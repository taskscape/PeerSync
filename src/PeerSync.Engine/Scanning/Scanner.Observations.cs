using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

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
                        + "CREATE TABLE staged_updates (id INTEGER PRIMARY KEY, path_sort BLOB NOT NULL, "
                        + "path TEXT NOT NULL, type INTEGER NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, "
                        + "attributes INTEGER NOT NULL, deleted INTEGER NOT NULL, changed INTEGER NOT NULL, "
                        + "file_hash BLOB, vector BLOB NOT NULL, body BLOB, signature BLOB, file_id BLOB, "
                        + "sequence INTEGER NOT NULL, flags INTEGER NOT NULL, ready INTEGER NOT NULL, "
                        + "change_time INTEGER, chunks BLOB, conflict_copy INTEGER NOT NULL); "
                        + "CREATE INDEX staged_order ON staged_updates(path_sort,id); "
                        + "CREATE INDEX staged_conflict ON staged_updates(file_id,file_hash,path_sort) WHERE conflict_copy=1; "
                        + "CREATE TABLE rename_candidates (id INTEGER PRIMARY KEY, file_id BLOB NOT NULL, path TEXT NOT NULL); "
                        + "CREATE INDEX rename_by_file ON rename_candidates(file_id,id); "
                        + "CREATE TABLE staged_cache (id INTEGER PRIMARY KEY, path TEXT NOT NULL, sequence INTEGER NOT NULL, "
                        + "file_id BLOB NOT NULL, change_time INTEGER NOT NULL); "
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

        // Close every observation reader before the hash workers stage their
        // signed rows. SQLite cannot upgrade a long-lived read cursor here.
        internal IEnumerable<WalkEntry[]> ReadPages()
        {
            string after = "";
            while (true)
            {
                var page = new List<WalkEntry>(RowRepository<LocalFileRow>.BatchSize);
                using (var readerConnection = OpenReader())
                using (var command = readerConnection.CreateCommand())
                {
                    command.CommandText = "SELECT path_key,path,name,attributes,length,last_write,change_time,file_id,unsyncable "
                        + "FROM observed WHERE path_key>$after ORDER BY path_key LIMIT $limit;";
                    command.Parameters.AddWithValue("$after", after);
                    command.Parameters.AddWithValue("$limit", RowRepository<LocalFileRow>.BatchSize);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        after = reader.GetString(0);
                        page.Add(ReadEntry(reader, 1));
                    }
                }
                if (page.Count == 0) break;
                yield return page.ToArray();
            }
            foreach (var page in unencodable.Values.Chunk(RowRepository<LocalFileRow>.BatchSize)) yield return page;
        }

        internal int UpdateCount { get; private set; }
        internal int RenameCandidateCount { get; private set; }
        internal int CacheUpdateCount { get; private set; }

        internal void StageCacheUpdates(IEnumerable<NativeChangeTimeUpdate> rows)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO staged_cache(path,sequence,file_id,change_time) VALUES($path,$sequence,$file,$change);";
            command.Parameters.Add("$path", SqliteType.Text); command.Parameters.Add("$sequence", SqliteType.Integer);
            command.Parameters.Add("$file", SqliteType.Blob); command.Parameters.Add("$change", SqliteType.Integer);
            command.Prepare();
            foreach (var row in rows)
            {
                command.Parameters[0].Value = row.Path; command.Parameters[1].Value = row.ExpectedLocalSequence;
                command.Parameters[2].Value = row.FileId; command.Parameters[3].Value = row.ChangeFileTime;
                command.ExecuteNonQuery(); CacheUpdateCount = checked(CacheUpdateCount + 1);
            }
            transaction.Commit();
        }

        internal IEnumerable<NativeChangeTimeUpdate> ReadCacheUpdates()
        {
            using var readerConnection = OpenReader(); using var command = readerConnection.CreateCommand();
            command.CommandText = "SELECT path,sequence,file_id,change_time FROM staged_cache ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) yield return new(reader.GetString(0), reader.GetInt64(1),
                reader.GetFieldValue<byte[]>(2), reader.GetInt64(3));
        }

        internal void StageRenameCandidates(IEnumerable<LocalFileRow> rows)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO rename_candidates(file_id,path) VALUES($file,$path);";
            command.Parameters.Add("$file", SqliteType.Blob); command.Parameters.Add("$path", SqliteType.Text);
            command.Prepare();
            foreach (var row in rows)
            {
                command.Parameters[0].Value = row.FileId!;
                command.Parameters[1].Value = row.Path;
                command.ExecuteNonQuery(); RenameCandidateCount = checked(RenameCandidateCount + 1);
            }
            transaction.Commit();
        }

        internal Dictionary<string, LocalFileRow[]> ReadRenameCandidates(IReadOnlyList<WalkEntry> page, FolderIndex index)
        {
            if (RenameCandidateCount == 0) return new Dictionary<string, LocalFileRow[]>(StringComparer.Ordinal);
            var ids = page.Where(entry => !entry.Native.IsDirectory && !entry.Unsyncable)
                .Select(entry => Convert.ToHexString(entry.Native.FileId)).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) return new Dictionary<string, LocalFileRow[]>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            var parameters = new string[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                parameters[i] = "$id" + i;
                command.Parameters.AddWithValue(parameters[i], Convert.FromHexString(ids[i]));
            }
            command.CommandText = "SELECT file_id,path FROM rename_candidates WHERE file_id IN ("
                + string.Join(',', parameters) + ") ORDER BY id;";
            var candidates = new List<(string File, string Path)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) candidates.Add((Convert.ToHexString(reader.GetFieldValue<byte[]>(0)), reader.GetString(1)));
            var rowsByPath = new Dictionary<string, LocalFileRow>(StringComparer.Ordinal);
            foreach (var paths in candidates.Select(candidate => candidate.Path).Chunk(RowRepository<LocalFileRow>.BatchSize))
                foreach (var pair in index.ReadLocalFilesByPaths(paths)) rowsByPath.Add(pair.Key, pair.Value);
            var groups = new Dictionary<string, List<LocalFileRow>>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (!rowsByPath.TryGetValue(candidate.Path.ToUpperInvariant(), out var row))
                    throw new IOException("Rename candidate disappeared during the operation gate.");
                if (!groups.TryGetValue(candidate.File, out var group)) groups.Add(candidate.File, group = []);
                group.Add(row);
            }
            return groups.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        }

        internal void StageUpdates(IEnumerable<LocalFileRow> rows)
        {
            using var transaction = connection.BeginTransaction();
            using var insertUpdate = connection.CreateCommand(); insertUpdate.Transaction = transaction;
            insertUpdate.CommandText = "INSERT INTO staged_updates(path_sort,path,type,size,mtime,attributes,deleted,changed,file_hash,vector,body,signature,file_id,sequence,flags,ready,change_time,chunks,conflict_copy) "
                + "VALUES($sort,$path,$type,$size,$mtime,$attributes,$deleted,$changed,$hash,$vector,$body,$signature,$file,$sequence,$flags,$ready,$change,$chunks,$conflict);";
            foreach (string name in new[] { "$sort", "$path", "$type", "$size", "$mtime", "$attributes", "$deleted", "$changed", "$hash", "$vector", "$body", "$signature", "$file", "$sequence", "$flags", "$ready", "$change", "$chunks", "$conflict" })
                insertUpdate.Parameters.AddWithValue(name, DBNull.Value);
            insertUpdate.Prepare();
            foreach (var row in rows)
            {
                insertUpdate.Parameters["$sort"].Value = Encoding.BigEndianUnicode.GetBytes(row.Path);
                insertUpdate.Parameters["$path"].Value = row.Path;
                insertUpdate.Parameters["$type"].Value = (long)row.Type;
                insertUpdate.Parameters["$size"].Value = row.Size;
                insertUpdate.Parameters["$mtime"].Value = row.MtimeTicks;
                insertUpdate.Parameters["$attributes"].Value = (long)row.Attributes;
                insertUpdate.Parameters["$deleted"].Value = row.Deleted ? 1 : 0;
                insertUpdate.Parameters["$changed"].Value = row.ChangedTicks;
                insertUpdate.Parameters["$hash"].Value = (object?)row.FileHash ?? DBNull.Value;
                insertUpdate.Parameters["$vector"].Value = VersionVector.Serialize(row.Vector);
                insertUpdate.Parameters["$body"].Value = (object?)row.Record?.Body.ToArray() ?? DBNull.Value;
                insertUpdate.Parameters["$signature"].Value = (object?)row.Record?.Signature.ToArray() ?? DBNull.Value;
                insertUpdate.Parameters["$file"].Value = (object?)row.FileId ?? DBNull.Value;
                insertUpdate.Parameters["$sequence"].Value = row.LocalSequence;
                insertUpdate.Parameters["$flags"].Value = (int)row.Flags;
                insertUpdate.Parameters["$ready"].Value = row.ContentReady ? 1 : 0;
                insertUpdate.Parameters["$change"].Value = (object?)row.ChangeFileTime ?? DBNull.Value;
                insertUpdate.Parameters["$chunks"].Value = (object?)EncodeChunks(row.IndexedChunks) ?? DBNull.Value;
                insertUpdate.Parameters["$conflict"].Value = !row.Deleted && row.Type == 0 && row.FileId is not null
                    && row.FileHash is not null && row.Path.Split('/').Any(p => p.Contains(".sync-conflict-", StringComparison.Ordinal)) ? 1 : 0;
                insertUpdate.ExecuteNonQuery(); UpdateCount = checked(UpdateCount + 1);
            }
            transaction.Commit();
        }

        internal string? FindRetainedConflictCopy(byte[] fileId, byte[] hash)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path FROM staged_updates WHERE file_id=$file AND file_hash=$hash AND conflict_copy=1 ORDER BY path_sort,id LIMIT 1;";
            command.Parameters.AddWithValue("$file", fileId); command.Parameters.AddWithValue("$hash", hash);
            return command.ExecuteScalar() as string;
        }

        internal IEnumerable<LocalFileRow> ReadUpdates()
        {
            using var readerConnection = OpenReader(); using var command = readerConnection.CreateCommand();
            command.CommandText = "SELECT path,type,size,mtime,attributes,deleted,changed,file_hash,vector,body,signature,file_id,sequence,flags,ready,change_time,chunks "
                + "FROM staged_updates ORDER BY path_sort,id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                byte[]? body = reader.IsDBNull(9) ? null : reader.GetFieldValue<byte[]>(9);
                byte[]? signature = reader.IsDBNull(10) ? null : reader.GetFieldValue<byte[]>(10);
                if ((body is null) != (signature is null)) throw new InvalidDataException("Incomplete staged signed record.");
                long sequence = reader.GetInt64(12);
                yield return new LocalFileRow(reader.GetString(0), checked((uint)reader.GetInt64(1)), reader.GetInt64(2), reader.GetInt64(3),
                    checked((uint)reader.GetInt64(4)), reader.GetInt64(5) != 0, reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7), VersionVector.Deserialize(reader.GetFieldValue<byte[]>(8)),
                    body is null ? null : new SignedRecord(body, signature!, checked((ulong)sequence)),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11), sequence, (LocalFileState)reader.GetInt64(13))
                {
                    ContentReady = reader.GetInt64(14) != 0,
                    ChangeFileTime = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    IndexedChunks = reader.IsDBNull(16) ? null : DecodeChunks(reader.GetFieldValue<byte[]>(16)),
                };
            }
        }

        private static byte[]? EncodeChunks(IReadOnlyList<LocalChunk>? chunks)
        {
            if (chunks is null) return null;
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(chunks.Count);
            foreach (var chunk in chunks)
            {
                writer.Write(chunk.Offset); writer.Write(chunk.Length); writer.Write(chunk.Hash.Length); writer.Write(chunk.Hash);
            }
            return stream.ToArray();
        }

        private static List<LocalChunk> DecodeChunks(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
            int count = reader.ReadInt32();
            if (count < 0 || count > (bytes.Length - sizeof(int)) / 48) throw new InvalidDataException("Invalid staged chunk count.");
            var chunks = new List<LocalChunk>(count);
            for (int i = 0; i < count; i++)
            {
                long offset = reader.ReadInt64(); int length = reader.ReadInt32(); int hashLength = reader.ReadInt32();
                if (hashLength != 32) throw new InvalidDataException("Invalid staged chunk hash.");
                byte[] hash = reader.ReadBytes(hashLength);
                if (hash.Length != 32) throw new EndOfStreamException("Truncated staged chunk hash.");
                chunks.Add(new(offset, length, hash));
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing staged chunk data.");
            return chunks;
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
