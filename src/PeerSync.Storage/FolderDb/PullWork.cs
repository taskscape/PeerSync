using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Settings;

namespace PeerSync.Storage.FolderDb;

public sealed record RemoteIndexRange(byte[] DeviceId, long TargetSequence, long ReceivedSequence)
{
    public bool Complete => ReceivedSequence >= TargetSequence;
    public override string ToString() => "Remote index range (redacted)";
}
public sealed record PullRetry(string PathKey, byte[] RootId, byte[] Fingerprint, int Attempts, long FailedTicks, long NextTicks)
{
    public override string ToString() => "Pull retry (redacted)";
}

public sealed partial class FolderIndex
{
    /// <summary>Current sender index generation only. A rollback/reset needs
    /// explicit transport reconciliation; never lower an outstanding target.</summary>
    public Task<RemoteIndexRange> AnnounceRemoteIndexRangeAsync(byte[] deviceId, long targetSequence, CancellationToken cancellationToken = default)
        => AnnounceRemoteIndexRangeCoreAsync(deviceId, targetSequence, null, cancellationToken);

    /// <summary>Admission runs inside the writer transaction at entry and before
    /// commit. It must be pure, bounded and never reenter storage.</summary>
    public Task<RemoteIndexRange> AnnounceRemoteIndexRangeWithAdmissionAsync(byte[] deviceId, long targetSequence, Action admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission); return AnnounceRemoteIndexRangeCoreAsync(deviceId, targetSequence, admission, cancellationToken);
    }

    private Task<RemoteIndexRange> AnnounceRemoteIndexRangeCoreAsync(byte[] deviceId, long targetSequence, Action? admission, CancellationToken cancellationToken)
    {
        FolderMaps.RequireBytes(deviceId, 32); ArgumentOutOfRangeException.ThrowIfNegative(targetSequence); byte[] device = deviceId.ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            admission?.Invoke();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT last_seq_received FROM devices WHERE device_id=$device;"; command.Parameters.AddWithValue("$device", device);
            long received = command.ExecuteScalar() is long cursor ? cursor : 0;
            if (targetSequence < received) throw new InvalidOperationException("Remote index target precedes its received cursor.");
            command.CommandText = "INSERT OR IGNORE INTO devices(device_id) VALUES($device); INSERT INTO remote_index_ranges(device_id,target_sequence) VALUES($device,$target) "
                + "ON CONFLICT(device_id) DO UPDATE SET target_sequence=MAX(remote_index_ranges.target_sequence,excluded.target_sequence);";
            command.Parameters.AddWithValue("$target", targetSequence); command.ExecuteNonQuery();
            command.CommandText = "SELECT target_sequence FROM remote_index_ranges WHERE device_id=$device;";
            long target = (long)command.ExecuteScalar()!; admission?.Invoke();
            return new RemoteIndexRange(device.ToArray(), target, received);
        }, cancellationToken);
    }

    public RemoteIndexRange? ReadRemoteIndexRange(byte[] deviceId)
    {
        FolderMaps.RequireBytes(deviceId, 32); using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT r.target_sequence,d.last_seq_received FROM remote_index_ranges r JOIN devices d ON d.device_id=r.device_id WHERE r.device_id=$device;";
        command.Parameters.AddWithValue("$device", deviceId); using var reader = command.ExecuteReader();
        return reader.Read() ? new(deviceId.ToArray(), reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    /// <summary>Bounded scheduling metadata only. Caller revalidates work and
    /// range/target state inside the applier before performing native mutation.</summary>
    public IReadOnlyList<NeededRow> ReadDuePullWork(byte[] rootId, long nowTicks, TransferPriority priority = TransferPriority.Default, int limit = 1000)
        => ReadDuePullWorkCore(rootId, nowTicks, priority, limit, null);

    /// <summary>Filter incomplete deletions and live work without installed
    /// parents before applying the batch limit. The applier still rechecks
    /// range and signed work under its gates.</summary>
    public IReadOnlyList<NeededRow> ReadScheduledPullWork(byte[] rootId, byte[] localDevice, long nowTicks,
        TransferPriority priority = TransferPriority.Default, int limit = 1000)
    {
        FolderMaps.RequireBytes(localDevice, 32); byte[] own = localDevice.ToArray();
        byte[] root = FreezeSweep(rootId, nowTicks, 1, limit);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        RequireBlockReferenceRoot(connection, transaction, root);
        var rows = new List<NeededRow>(limit);
        for (int stage = 0; stage < 4 && rows.Count < limit; stage++)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT n.path_key,n.source_device,n.priority,n.work_stage,n.work_size,n.work_mtime,n.work_hash FROM needed n "
                + "LEFT JOIN pull_retries r ON r.path_key=n.path_key AND r.work_hash=n.work_hash AND r.root_id=$root "
                + "WHERE n.work_stage=$stage AND (r.path_key IS NULL OR r.failed_ticks>$now OR r.next_ticks<=$now) "
                + PullRangeFilter + PullParentFilter + "ORDER BY " + StageOrder(stage, priority) + " LIMIT $limit;";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$own", own);
            command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$stage", stage);
            command.Parameters.AddWithValue("$limit", limit - rows.Count);
            using var reader = command.ExecuteReader(); while (reader.Read()) rows.Add(FolderMaps.ReadNeeded(reader));
        }
        return rows.AsReadOnly();
    }

    private static string StageOrder(int stage, TransferPriority priority) => stage switch
    {
        0 => "length(n.path_key)-length(replace(n.path_key,'/','')),n.priority DESC,n.path_key",
        1 => priority switch
        {
            TransferPriority.SmallerFirst => "n.priority DESC,n.work_size,n.path_key",
            TransferPriority.LargerFirst => "n.priority DESC,n.work_size DESC,n.path_key",
            TransferPriority.OlderFirst => "n.priority DESC,n.work_mtime,n.path_key",
            TransferPriority.NewerFirst => "n.priority DESC,n.work_mtime DESC,n.path_key",
            _ => "n.priority DESC,n.path_key",
        },
        2 => "n.priority DESC,n.path_key",
        _ => "length(n.path_key)-length(replace(n.path_key,'/','')) DESC,n.priority DESC,n.path_key",
    };

    private const string PullRangeFilter = " AND ($own IS NULL OR n.work_stage<2 OR n.source_device=$own OR EXISTS("
        + "SELECT 1 FROM remote_index_ranges q JOIN devices d ON d.device_id=q.device_id WHERE q.device_id=n.source_device AND d.last_seq_received>=q.target_sequence)) ";

    // Signed file records can precede their parent directory records in a
    // large index. Do not repeatedly attempt native work whose direct parent
    // has not been installed. Parent installation updates local_files and
    // makes the child eligible on the next scheduling pass.
    private const string PullParentFilter = " AND ($own IS NULL OR n.work_stage NOT IN (0,1) OR instr(n.path_key,'/')=0 OR EXISTS("
        + "SELECT 1 FROM local_files p WHERE p.path_key=rtrim(rtrim(n.path_key,replace(n.path_key,'/','')),'/') "
        + "AND p.type=1 AND p.deleted=0 AND p.flags=0 AND p.ntfs_file_id IS NOT NULL)) ";

    private System.Collections.ObjectModel.ReadOnlyCollection<NeededRow> ReadDuePullWorkCore(byte[] rootId, long nowTicks, TransferPriority priority, int limit, byte[]? own)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, limit); if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true); RequireBlockReferenceRoot(connection, transaction, root);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT n.path_key,n.source_device,n.priority,n.work_stage,n.work_size,n.work_mtime,n.work_hash FROM needed n "
            + "LEFT JOIN pull_retries r ON r.path_key=n.path_key AND r.work_hash=n.work_hash AND r.root_id=$root "
            + "WHERE n.work_stage IS NOT NULL AND (r.path_key IS NULL OR r.failed_ticks>$now OR r.next_ticks<=$now) " + PullRangeFilter + PullParentFilter
            + "ORDER BY n.work_stage,CASE WHEN n.work_stage=0 THEN length(n.path_key)-length(replace(n.path_key,'/','')) END,"
            + "CASE WHEN n.work_stage=3 THEN length(n.path_key)-length(replace(n.path_key,'/','')) END DESC,n.priority DESC,"
            + "CASE WHEN n.work_stage=1 AND $priority=1 THEN n.work_size END,CASE WHEN n.work_stage=1 AND $priority=2 THEN n.work_size END DESC,"
            + "CASE WHEN n.work_stage=1 AND $priority=3 THEN n.work_mtime END,CASE WHEN n.work_stage=1 AND $priority=4 THEN n.work_mtime END DESC,n.path_key LIMIT $limit;";
        command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$priority", (int)priority); command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$own", (object?)own ?? DBNull.Value);
        var rows = new List<NeededRow>(limit); using var reader = command.ExecuteReader(); while (reader.Read()) rows.Add(FolderMaps.ReadNeeded(reader)); return rows.AsReadOnly();
    }

    public long? ReadNextScheduledPullTicks(byte[] rootId, byte[] localDevice, long nowTicks)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, 1000); FolderMaps.RequireBytes(localDevice, 32);
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true); RequireBlockReferenceRoot(connection, transaction, root);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        // Due work only needs an existence check. If none is due, future
        // deadlines live in the much smaller retry table, avoiding a second
        // aggregate over every cached file on each million-file pass.
        command.CommandText = "SELECT 1 FROM needed n LEFT JOIN pull_retries r ON r.path_key=n.path_key AND r.work_hash=n.work_hash AND r.root_id=$root "
            + "WHERE n.work_stage IS NOT NULL AND (r.path_key IS NULL OR r.failed_ticks>$now OR r.next_ticks<=$now) "
            + PullRangeFilter + PullParentFilter + "LIMIT 1;";
        command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$own", localDevice);
        if (command.ExecuteScalar() is long) return nowTicks;
        command.CommandText = "SELECT MIN(r.next_ticks) FROM pull_retries r JOIN needed n ON n.path_key=r.path_key AND n.work_hash=r.work_hash "
            + "WHERE r.root_id=$root AND n.work_stage IS NOT NULL AND r.failed_ticks<=$now AND r.next_ticks>$now "
            + PullRangeFilter + PullParentFilter;
        return command.ExecuteScalar() is long ticks ? ticks : null;
    }

    public PullRetry? ReadPullRetry(string path)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT path_key,root_id,work_hash,attempts,failed_ticks,next_ticks FROM pull_retries WHERE path_key=$path;";
        command.Parameters.AddWithValue("$path", PathRules.Key(path)); using var reader = command.ExecuteReader(); return reader.Read() ? ReadRetry(reader) : null;
    }

    /// <summary>Persist backoff only for the current exact needed fingerprint.
    /// New work is immediately eligible; clock rollback restarts bounded delay.</summary>
    public Task<PullRetry?> RecordPullFailureAsync(string path, byte[] rootId, byte[] fingerprint, long nowTicks, CancellationToken cancellationToken = default)
    {
        string key = PathRules.Key(path); byte[] root = FreezeSweep(rootId, nowTicks, 1, 1000); FolderMaps.RequireBytes(fingerprint, 32); byte[] hash = fingerprint.ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.Parameters.AddWithValue("$path", key); command.CommandText = "SELECT path_key,root_id,work_hash,attempts,failed_ticks,next_ticks FROM pull_retries WHERE path_key=$path;";
            PullRetry? prior; using (var reader = command.ExecuteReader()) prior = reader.Read() ? ReadRetry(reader) : null;
            if (prior is not null && !prior.RootId.AsSpan().SequenceEqual(root)) throw new InvalidOperationException("Pull retry belongs to another root.");
            int attempts = prior is not null && prior.Fingerprint.AsSpan().SequenceEqual(hash) && prior.FailedTicks <= nowTicks ? Math.Min(7, prior.Attempts + 1) : 1;
            long delay = Math.Min(600, 10 << Math.Min(6, attempts - 1)) * TimeSpan.TicksPerSecond;
            long next = nowTicks + Math.Min(DateTime.MaxValue.Ticks - nowTicks, delay);
            command.CommandText = "INSERT INTO pull_retries(path_key,root_id,work_hash,attempts,failed_ticks,next_ticks) SELECT path_key,$root,$hash,$attempts,$now,$next FROM needed "
                + "WHERE path_key=$path AND work_hash=$hash ON CONFLICT(path_key) DO UPDATE SET root_id=excluded.root_id,work_hash=excluded.work_hash,attempts=excluded.attempts,failed_ticks=excluded.failed_ticks,next_ticks=excluded.next_ticks;";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$attempts", attempts); command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$next", next);
            return command.ExecuteNonQuery() == 1 ? new PullRetry(key, root.ToArray(), hash.ToArray(), attempts, nowTicks, next) : null;
        }, cancellationToken);
    }

    public Task<bool> ClearPullRetryAsync(string path, byte[] fingerprint, CancellationToken cancellationToken = default)
    {
        string key = PathRules.Key(path); FolderMaps.RequireBytes(fingerprint, 32); byte[] hash = fingerprint.ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DELETE FROM pull_retries WHERE path_key=$path AND work_hash=$hash;";
            command.Parameters.AddWithValue("$path", key); command.Parameters.AddWithValue("$hash", hash); return command.ExecuteNonQuery() == 1;
        }, cancellationToken);
    }

    private static PullRetry ReadRetry(SqliteDataReader reader) => new(reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5));
}
