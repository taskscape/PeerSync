using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record FolderPathSnapshot(string PathKey, LocalFileRow? Local, IReadOnlyList<RemoteFileRow> Remote)
{
    public LocalFileRow? BlockingFileAncestor { get; init; }
}
public sealed record RemoteIndexResult(bool Applied, long LastSequence, int AffectedPaths);

public sealed partial class FolderIndex
{
    /// <summary>The resolver runs synchronously inside the writer transaction. It must not
    /// perform I/O, retain the snapshot or re-enter this database. Authentication is the caller's responsibility.</summary>
    public Task<RemoteIndexResult> ApplyRemoteIndexAsync(byte[] deviceId, long afterSequence, long lastSequence,
        IReadOnlyList<RemoteFileRow> records, Func<FolderPathSnapshot, NeededRow?> resolver,
        CancellationToken cancellationToken = default)
        => ApplyRemoteIndexCoreAsync(deviceId, afterSequence, lastSequence, records, resolver, null, cancellationToken);

    /// <summary>Admission runs synchronously at writer entry and before commit;
    /// it must be bounded, perform no I/O and never reenter this database.</summary>
    public Task<RemoteIndexResult> ApplyRemoteIndexWithAdmissionAsync(byte[] deviceId, long afterSequence, long lastSequence,
        IReadOnlyList<RemoteFileRow> records, Func<FolderPathSnapshot, NeededRow?> resolver, Action admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return ApplyRemoteIndexCoreAsync(deviceId, afterSequence, lastSequence, records, resolver, admission, cancellationToken);
    }

    private Task<RemoteIndexResult> ApplyRemoteIndexCoreAsync(byte[] deviceId, long afterSequence, long lastSequence,
        IReadOnlyList<RemoteFileRow> records, Func<FolderPathSnapshot, NeededRow?> resolver, Action? admission, CancellationToken cancellationToken)
    {
        FolderMaps.RequireBytes(deviceId, 32);
        ArgumentNullException.ThrowIfNull(records); ArgumentNullException.ThrowIfNull(resolver);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (lastSequence < afterSequence || records.Count > 1000) throw new ArgumentOutOfRangeException(nameof(lastSequence));
        byte[] device = deviceId.ToArray();
        var frozen = new List<RemoteFileRow>(records.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        ulong sequence = (ulong)afterSequence;
        foreach (var row in records)
        {
            FolderMaps.Remote.Validate(row);
            string path = PathRules.Key(row.PathKey);
            if (!row.DeviceId.AsSpan().SequenceEqual(device) || !paths.Add(path)
                || row.Record.LocalSequence <= sequence || row.Record.LocalSequence > (ulong)lastSequence)
                throw new ArgumentException("Invalid remote batch path, source or sequence.", nameof(records));
            sequence = row.Record.LocalSequence;
            frozen.Add(new(device.ToArray(), path, new ReadOnlyDictionary<ulong, ulong>(row.Vector.ToDictionary()), row.Record));
        }
        return database.WriteDurableAsync((connection, transaction) =>
        {
            admission?.Invoke();
            using var cursor = connection.CreateCommand(); cursor.Transaction = transaction;
            cursor.CommandText = "SELECT last_seq_received FROM devices WHERE device_id=$device;";
            cursor.Parameters.AddWithValue("$device", device);
            long current = (long?)cursor.ExecuteScalar() ?? 0;
            if (lastSequence <= current) return new RemoteIndexResult(false, current, 0);
            if (afterSequence != current) throw new InvalidOperationException("Remote index cursor mismatch.");
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO remote_files(device_id,path_key,vv,record_body,signature) VALUES($device,$path,$vv,$body,$signature) ON CONFLICT(device_id,path_key) DO UPDATE SET vv=excluded.vv,record_body=excluded.record_body,signature=excluded.signature;";
            insert.Parameters.AddWithValue("$device", device);
            insert.Parameters.Add("$path", SqliteType.Text); insert.Parameters.Add("$vv", SqliteType.Blob);
            insert.Parameters.Add("$body", SqliteType.Blob); insert.Parameters.Add("$signature", SqliteType.Blob); insert.Prepare();
            foreach (var row in frozen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                insert.Parameters[1].Value = row.PathKey;
                insert.Parameters[2].Value = Core.Sync.VersionVector.Serialize(row.Vector);
                insert.Parameters[3].Value = row.Record.Body.ToArray(); insert.Parameters[4].Value = row.Record.Signature.ToArray();
                insert.ExecuteNonQuery();
            }
            using var needed = new NeededRewrite(connection, transaction, resolver);
            foreach (string path in paths) { cancellationToken.ThrowIfCancellationRequested(); needed.Update(path); }
            cursor.CommandText = "INSERT INTO devices(device_id,last_seq_received) VALUES($device,$sequence) ON CONFLICT(device_id) DO UPDATE SET last_seq_received=excluded.last_seq_received;";
            cursor.Parameters.AddWithValue("$sequence", lastSequence); cursor.ExecuteNonQuery();
            admission?.Invoke();
            return new RemoteIndexResult(true, lastSequence, paths.Count);
        }, cancellationToken);
    }

    public async Task<int> RecomputeNeededAsync(IEnumerable<string> paths, Func<FolderPathSnapshot, NeededRow?> resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths); ArgumentNullException.ThrowIfNull(resolver);
        // Freeze caller-owned incremental inputs before awaiting the writer.
        // Scanner-scale rebuilds take the streamed index path below.
        string[] unique = paths.Select(PathRules.Key).Distinct(StringComparer.Ordinal).ToArray();
        return await RecomputeNeededCoreAsync(unique, resolver, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> RecomputeAllNeededAsync(Func<FolderPathSnapshot, NeededRow?> resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return RecomputeNeededCoreAsync(ReadAllNeededPathKeys(), resolver, cancellationToken);
    }

    private async Task<int> RecomputeNeededCoreAsync(IEnumerable<string> unique, Func<FolderPathSnapshot, NeededRow?> resolver,
        CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (var batch in unique.Chunk(1000))
        {
            var result = await database.WriteDurableAsync<(int Updated, IOException? Capacity)>((connection, transaction) =>
            {
                using var needed = new NeededRewrite(connection, transaction, resolver);
                int updated = 0;
                foreach (string path in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // One path can admit multiple sources. Never leave a partial path
                    // when capacity is reached, but let the valid prefix drain first.
                    transaction.Save("needed_path");
                    try { needed.Update(path); }
                    catch (IOException capacity) when (capacity is CasePreservationCapacityException or DirectoryMetadataCapacityException)
                    {
                        transaction.Rollback("needed_path"); transaction.Release("needed_path");
                        return (updated, capacity);
                    }
                    transaction.Release("needed_path"); updated++;
                }
                return (updated, null);
            }, cancellationToken).ConfigureAwait(false);
            count += result.Updated;
            // The writer commits FULL before exposing recoverable backpressure.
            // All other failures/cancellation still roll back the whole current batch.
            if (result.Capacity is not null) throw result.Capacity;
        }
        return count;
    }

    private sealed class NeededRewrite : IDisposable
    {
        private readonly SqliteConnection connection;
        private readonly SqliteTransaction transaction;
        private readonly SqliteCommand local, remote, store, remove, ancestor;
        private readonly Func<FolderPathSnapshot, NeededRow?> resolver;
        public NeededRewrite(SqliteConnection connection, SqliteTransaction transaction, Func<FolderPathSnapshot, NeededRow?> resolver)
        {
            this.connection = connection; this.transaction = transaction;
            this.resolver = resolver;
            local = Make("SELECT " + FolderMaps.LocalColumns + " FROM local_files WHERE path_key=$path;");
            remote = Make("SELECT device_id,path_key,vv,record_body,signature FROM remote_files WHERE path_key=$path ORDER BY device_id;");
            ancestor = Make("WITH RECURSIVE ancestors(key) AS (VALUES($path) UNION ALL SELECT rtrim(rtrim(key,replace(key,'/','')),'/') FROM ancestors WHERE instr(key,'/')>0) "
                + "SELECT " + string.Join(',', FolderMaps.LocalColumns.Split(',').Select(column => "l." + column.Trim()))
                + " FROM ancestors a JOIN local_files l ON l.path_key=a.key WHERE a.key<>$path AND l.type=0 AND l.deleted=0 AND l.flags=0 AND l.record_body IS NOT NULL AND l.ntfs_file_id IS NOT NULL ORDER BY length(a.key) DESC LIMIT 1;");
            store = Make("INSERT INTO needed(path_key,source_device,priority,work_stage,work_size,work_mtime,work_hash) VALUES($path,$source,$priority,$stage,$size,$mtime,$hash) "
                + "ON CONFLICT(path_key) DO UPDATE SET source_device=excluded.source_device,priority=excluded.priority,work_stage=excluded.work_stage,work_size=excluded.work_size,work_mtime=excluded.work_mtime,work_hash=excluded.work_hash;");
            store.Parameters.Add("$source", SqliteType.Blob); store.Parameters.Add("$priority", SqliteType.Integer);
            store.Parameters.Add("$stage", SqliteType.Integer); store.Parameters.Add("$size", SqliteType.Integer); store.Parameters.Add("$mtime", SqliteType.Integer); store.Parameters.Add("$hash", SqliteType.Blob); store.Prepare();
            remove = Make("DELETE FROM needed WHERE path_key=$path;");
            SqliteCommand Make(string sql)
            {
                var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
                command.Parameters.Add("$path", SqliteType.Text); return command;
            }
        }
        public void Update(string path)
        {
            local.Parameters[0].Value = path;
            LocalFileRow? localRow;
            using (var reader = local.ExecuteReader()) localRow = reader.Read() ? FolderMaps.ReadLocal(reader) : null;
            remote.Parameters[0].Value = path;
            var remoteRows = new List<RemoteFileRow>();
            using (var reader = remote.ExecuteReader())
                while (reader.Read()) remoteRows.Add(new(reader.GetFieldValue<byte[]>(0), reader.GetString(1),
                    Core.Sync.VersionVector.Deserialize(reader.GetFieldValue<byte[]>(2)), new(reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4))));
            ancestor.Parameters[0].Value = path; LocalFileRow? blocking;
            using (var reader = ancestor.ExecuteReader()) blocking = reader.Read() ? FolderMaps.ReadLocal(reader) : null;
            var row = resolver(new(path, localRow, remoteRows.AsReadOnly()) { BlockingFileAncestor = blocking });
            if (row is null) { remove.Parameters[0].Value = path; remove.ExecuteNonQuery(); return; }
            FolderMaps.Needed.Validate(row);
            if (PathRules.Key(row.PathKey) != path) throw new InvalidOperationException("Needed resolver returned another path.");
            AdmitCaseSources(connection, transaction, path, row.CasePreservations);
            if (row.DirectoryMetadataSources.Count != 0)
            {
                var metadata = FreezeMetadataSources(row.DirectoryMetadataSources);
                if (metadata.Any(source => PathRules.Key(source.Path) != path)) throw new InvalidDataException("Metadata admission returned another path.");
                using var binding = connection.CreateCommand(); binding.Transaction = transaction; binding.CommandText = "SELECT value FROM meta WHERE key='_root-identity';";
                byte[] root = binding.ExecuteScalar() as byte[] ?? throw new InvalidOperationException("Metadata source root is unbound.");
                FolderMaps.RequireBytes(root, 24); AdmitMetadataSources(connection, transaction, root, metadata, DateTime.UtcNow.Ticks);
            }
            if (row.PreservationAdmissionOnly) { remove.Parameters[0].Value = path; remove.ExecuteNonQuery(); return; }
            store.Parameters[0].Value = path; store.Parameters[1].Value = row.SourceDevice; store.Parameters[2].Value = row.Priority;
            store.Parameters[3].Value = (object?)row.Work?.Stage ?? DBNull.Value; store.Parameters[4].Value = (object?)row.Work?.Size ?? DBNull.Value;
            store.Parameters[5].Value = (object?)row.Work?.MtimeTicks ?? DBNull.Value; store.Parameters[6].Value = (object?)row.Work?.Fingerprint ?? DBNull.Value;
            store.ExecuteNonQuery();
        }
        public void Dispose() { local.Dispose(); remote.Dispose(); store.Dispose(); remove.Dispose(); ancestor.Dispose(); }
    }
}
