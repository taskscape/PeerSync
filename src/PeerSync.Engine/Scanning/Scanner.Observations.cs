using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Native;

namespace PeerSync.Engine.Scanning;

public sealed partial class Scanner
{
    // This database is only a scan observation, never an index or deletion
    // proof. A crash discards it; the next scan must enumerate the native tree.
    private sealed class ScanObservations : IDisposable
    {
        private readonly string path;
        private readonly string lockPath;
        private readonly byte[] rootIdentity;
        private readonly FileStream ownership;
        private readonly SqliteConnection connection;
        private readonly DiagnosticJournal? journal;
        private SqliteTransaction? write;
        private SqliteCommand? insert;
        private SqliteCommand? exists;
        private SqliteCommand? find;
        private readonly Dictionary<string, WalkEntry> unencodable = new(StringComparer.Ordinal);
        private bool disposed;
        internal int Count { get; private set; }

        internal ScanObservations(string indexPath, byte[] rootIdentity, Guid scanSession, DiagnosticJournal? journal)
        {
            if (rootIdentity is not { Length: 24 }) throw new ArgumentException("Invalid scan root identity.", nameof(rootIdentity));
            this.rootIdentity = rootIdentity.ToArray();
            this.journal = journal;
            string directory = Path.GetDirectoryName(indexPath) ?? throw new IOException("Index has no parent directory.");
            string owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(indexPath)))[..16];
            string prefix = "scan-observations-" + owner + "-";
            CleanupAbandoned(directory, prefix, journal);
            path = Path.Combine(directory, prefix + scanSession.ToString("N") + ".db");
            lockPath = Path.ChangeExtension(path, ".lock");
            ownership = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var builder = new SqliteConnectionStringBuilder { DataSource = path,
                    Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false };
                connection = new SqliteConnection(builder.ToString()); connection.Open();
                using (var schema = connection.CreateCommand())
                {
                    schema.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; "
                        + "CREATE TABLE observed (path_key TEXT PRIMARY KEY, path TEXT NOT NULL, name TEXT NOT NULL, "
                        + "attributes INTEGER NOT NULL, length INTEGER NOT NULL, last_write INTEGER NOT NULL, "
                        + "change_time INTEGER NOT NULL, file_id BLOB NOT NULL, unsyncable INTEGER NOT NULL, "
                        + "parent_key TEXT NOT NULL, depth INTEGER NOT NULL, sort_key BLOB NOT NULL) WITHOUT ROWID; "
                        + "CREATE INDEX observed_parent ON observed(parent_key,sort_key); "
                        + "CREATE TABLE scan_owner(root_identity BLOB NOT NULL);";
                    schema.ExecuteNonQuery();
                }
                using (var binding = connection.CreateCommand())
                {
                    binding.CommandText = "INSERT INTO scan_owner(root_identity) VALUES($root);";
                    binding.Parameters.AddWithValue("$root", this.rootIdentity); binding.ExecuteNonQuery();
                }
                write = connection.BeginTransaction();
                insert = connection.CreateCommand(); insert.Transaction = write;
                insert.CommandText = "INSERT OR IGNORE INTO observed VALUES($key,$path,$name,$attributes,$length,$last,$change,$file,$unsafe,$parent,$depth,$sort);";
                foreach (string name in new[] { "$key", "$path", "$name", "$attributes", "$length", "$last", "$change", "$file", "$unsafe", "$parent", "$depth", "$sort" })
                    insert.Parameters.AddWithValue(name, DBNull.Value);
                insert.Prepare();
            }
            catch
            {
                insert?.Dispose(); write?.Dispose(); connection?.Dispose(); ownership.Dispose();
                try { File.Delete(path); File.Delete(lockPath); }
                catch (IOException) { /* Keep the lock for a later owned cleanup. */ }
                catch (UnauthorizedAccessException) { /* Preserve the original failure. */ }
                throw;
            }
        }

        internal bool TryAdd(string key, WalkEntry entry)
        {
            if (write is null || insert is null) throw new InvalidOperationException("Observation walk is complete.");
            if (unencodable.ContainsKey(key)) return false;
            try { _ = new UTF8Encoding(false, true).GetByteCount(entry.Path); }
            catch (EncoderFallbackException)
            {
                using var duplicate = connection.CreateCommand(); duplicate.Transaction = write;
                duplicate.CommandText = "SELECT 1 FROM observed WHERE path_key=$key;";
                duplicate.Parameters.AddWithValue("$key", key);
                if (duplicate.ExecuteScalar() is not null) return false;
                unencodable.Add(key, entry); Count = checked(Count + 1); return true;
            }
            int separator = key.LastIndexOf('/');
            insert.Parameters["$key"].Value = key;
            insert.Parameters["$path"].Value = entry.Path;
            insert.Parameters["$name"].Value = entry.Native.Name;
            insert.Parameters["$attributes"].Value = (long)entry.Native.Attributes;
            insert.Parameters["$length"].Value = entry.Native.Length;
            insert.Parameters["$last"].Value = entry.Native.LastWriteFileTime;
            insert.Parameters["$change"].Value = entry.Native.ChangeFileTime;
            insert.Parameters["$file"].Value = entry.Native.FileId;
            insert.Parameters["$unsafe"].Value = entry.Unsyncable ? 1 : 0;
            insert.Parameters["$parent"].Value = separator < 0 ? "" : key[..separator];
            insert.Parameters["$depth"].Value = key.Count(c => c == '/');
            insert.Parameters["$sort"].Value = Encoding.BigEndianUnicode.GetBytes(key);
            bool added = insert.ExecuteNonQuery() == 1;
            if (added) Count = checked(Count + 1);
            return added;
        }

        internal void CompleteWalk()
        {
            if (write is null) throw new InvalidOperationException("Observation walk was already completed.");
            insert!.Dispose(); insert = null;
            write.Commit(); write.Dispose(); write = null;
            using (var binding = connection.CreateCommand())
            {
                binding.CommandText = "SELECT root_identity FROM scan_owner;";
                if (binding.ExecuteScalar() is not byte[] stored || !stored.AsSpan().SequenceEqual(rootIdentity))
                    throw new IOException("Scan observation root identity changed.");
            }
            exists = connection.CreateCommand(); exists.CommandText = "SELECT 1 FROM observed WHERE path_key=$key;";
            exists.Parameters.AddWithValue("$key", ""); exists.Prepare();
            find = connection.CreateCommand(); find.CommandText = "SELECT path,name,attributes,length,last_write,change_time,file_id,unsyncable FROM observed WHERE path_key=$key;";
            find.Parameters.AddWithValue("$key", ""); find.Prepare();
        }

        internal bool ContainsKey(string key)
        {
            if (unencodable.ContainsKey(key)) return true;
            exists!.Parameters["$key"].Value = key;
            return exists.ExecuteScalar() is not null;
        }

        internal bool TryGetValue(string key, out WalkEntry entry)
        {
            if (unencodable.TryGetValue(key, out entry!)) return true;
            find!.Parameters["$key"].Value = key;
            using var reader = find.ExecuteReader();
            if (!reader.Read()) { entry = null!; return false; }
            entry = ReadEntry(reader); return true;
        }

        internal IEnumerable<WalkEntry> ReadAll()
        {
            using var readerConnection = OpenReader(); using var command = readerConnection.CreateCommand();
            // Hashing has no name-order dependency; use the primary-key walk
            // to avoid a million-row temporary sort.
            command.CommandText = "SELECT path,name,attributes,length,last_write,change_time,file_id,unsyncable FROM observed ORDER BY path_key;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return ReadEntry(reader);
            foreach (var entry in unencodable.Values) yield return entry;
        }

        internal IEnumerable<KeyValuePair<string, WalkEntry>> ReadDirectoriesDeepestFirst()
        {
            using var readerConnection = OpenReader(); using var command = readerConnection.CreateCommand();
            command.CommandText = "SELECT path_key,path,name,attributes,length,last_write,change_time,file_id,unsyncable "
                + "FROM observed WHERE (attributes & 16) != 0 ORDER BY depth DESC,sort_key;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return new(reader.GetString(0), ReadEntry(reader, 1));
            foreach (var entry in unencodable)
                if (entry.Value.Native.IsDirectory) yield return entry;
        }

        internal IEnumerable<(string Key, bool IsDirectory, bool Unsyncable)> ReadChildren(string parent)
        {
            using var readerConnection = OpenReader(); using var command = readerConnection.CreateCommand();
            command.CommandText = "SELECT path_key,attributes,unsyncable FROM observed WHERE parent_key=$parent ORDER BY sort_key;";
            command.Parameters.AddWithValue("$parent", parent);
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return (reader.GetString(0), (reader.GetInt64(1) & 16) != 0, reader.GetInt64(2) != 0);
        }

        private SqliteConnection OpenReader()
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path,
                Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false };
            var reader = new SqliteConnection(builder.ToString()); reader.Open(); return reader;
        }

        private static WalkEntry ReadEntry(SqliteDataReader reader, int start = 0)
        {
            var native = new NativeEntry(reader.GetString(start + 1), (FileAttributes)reader.GetInt64(start + 2),
                reader.GetInt64(start + 3), reader.GetInt64(start + 4), reader.GetInt64(start + 5),
                (byte[])reader.GetValue(start + 6));
            return new(reader.GetString(start), native, reader.GetInt64(start + 7) != 0);
        }

        private static void CleanupAbandoned(string directory, string prefix, DiagnosticJournal? journal)
        {
            foreach (string oldLock in Directory.EnumerateFiles(directory, prefix + "*.lock"))
            {
                string stem = Path.GetFileNameWithoutExtension(oldLock);
                if (!stem.StartsWith(prefix, StringComparison.Ordinal)
                    || !Guid.TryParseExact(stem[prefix.Length..], "N", out _)) continue;
                try
                {
                    using (new FileStream(oldLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        string database = Path.ChangeExtension(oldLock, ".db");
                        if (File.Exists(database)) File.Delete(database);
                    }
                    File.Delete(oldLock);
                    journal?.TryWrite(new("scan.observation_discarded", "scan") { Level = DiagnosticLevel.Debug });
                }
                catch (IOException) { /* A live owner or deferred cleanup leaves no publication authority. */ }
                catch (UnauthorizedAccessException) { /* Preserve a protected foreign object. */ }
            }
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            insert?.Dispose(); write?.Dispose(); find?.Dispose(); exists?.Dispose(); connection.Dispose();
            bool removed = false;
            try { File.Delete(path); removed = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { journal?.TryWrite(new("scan.observation_cleanup_deferred", "scan")
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult }); }
            ownership.Dispose();
            if (removed) try { File.Delete(lockPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { journal?.TryWrite(new("scan.observation_cleanup_deferred", "scan")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult }); }
        }
    }
}
