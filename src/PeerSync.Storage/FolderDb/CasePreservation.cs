using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

public sealed class CasePreservationCapacityException : IOException
{
    public CasePreservationCapacityException() : base("Case preservation pending capacity exceeded.") { }
    public CasePreservationCapacityException(string? message) : base(message) { }
    public CasePreservationCapacityException(string? message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>Immutable authenticated source supplied by the engine. Storage
/// does not authenticate signatures. Never serialize this value to diagnostics.</summary>
public sealed class CasePreservationSource
{
    private readonly byte[] id, device;
    public byte[] SourceId => id.ToArray();
    public byte[] SourceDevice => device.ToArray();
    public string PathKey { get; }
    public SignedRecord Record { get; }
    public CasePreservationSource(byte[] sourceId, byte[] sourceDevice, string path, SignedRecord record)
    {
        FolderMaps.RequireBytes(sourceId, 32); FolderMaps.RequireBytes(sourceDevice, 32);
        ArgumentNullException.ThrowIfNull(record);
        id = sourceId.ToArray(); device = sourceDevice.ToArray(); PathKey = PathRules.Key(path);
        Record = record.WithLocalSequence(0);
    }
    public override string ToString() => "Case preservation source (redacted)";
}

public sealed class PendingCasePreservation
{
    private readonly byte[] root;
    private readonly byte[]? installed;
    public byte[] RootId => root.ToArray();
    public CasePreservationSource Source { get; }
    public string? DestinationPath { get; }
    public byte[]? InstalledFileId => installed?.ToArray();
    internal PendingCasePreservation(byte[] rootId, CasePreservationSource source, string? destination = null, byte[]? installedFileId = null)
    { root = rootId.ToArray(); Source = source; DestinationPath = destination; installed = installedFileId?.ToArray(); }
    public override string ToString() => "Pending case preservation (redacted)";
}

public sealed partial class FolderIndex
{
    public const int MaximumPendingCaseSources = 1000;

    /// <summary>A bounded immutable page, ordered by portable source identity.
    /// The coordinator must retain the pending barrier until visible publication.</summary>
    public IReadOnlyList<PendingCasePreservation> ReadPendingCasePreservations(int limit = MaximumPendingCaseSources)
        => ReadCasePreservationsCore(null, null, limit);

    public IReadOnlyList<PendingCasePreservation> ReadDueCasePreservations(byte[] rootId, long nowTicks, int limit = MaximumPendingCaseSources)
        => ReadCasePreservationsCore(FreezeSweep(rootId, nowTicks, 1, limit), nowTicks, limit);

    private System.Collections.ObjectModel.ReadOnlyCollection<PendingCasePreservation> ReadCasePreservationsCore(byte[]? root, long? now, int limit)
    {
        if (limit is < 1 or > MaximumPendingCaseSources) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        if (root is not null) RequireBlockReferenceRoot(connection, transaction, root);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.root_id,c.source_id,c.source_device,c.path_key,c.record_body,c.signature,c.destination_path,c.installed_file_id FROM case_preservations c "
            + "LEFT JOIN case_preservation_retries r ON r.source_id=c.source_id WHERE c.completed=0 "
            + "AND ($root IS NULL OR c.root_id=$root) AND ($now IS NULL OR r.source_id IS NULL OR r.failed_ticks>$now OR r.next_ticks<=$now) ORDER BY c.source_id LIMIT $limit;";
        command.Parameters.AddWithValue("$root", (object?)root ?? DBNull.Value); command.Parameters.AddWithValue("$now", (object?)now ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<PendingCasePreservation>(); using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new(reader.GetFieldValue<byte[]>(0), new(reader.GetFieldValue<byte[]>(1),
            reader.GetFieldValue<byte[]>(2), reader.GetString(3), new(reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<byte[]>(5))),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7)));
        return rows.AsReadOnly();
    }

    public bool HasPendingCasePreservation(string path, bool includeDescendants = true)
    {
        string key = PathRules.Key(path);
        using var connection = database.OpenReader();
        return HasPendingCase(connection, null, key, includeDescendants);
    }

    private static bool HasPendingCase(SqliteConnection connection, SqliteTransaction? transaction, string key, bool includeDescendants = true)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "WITH RECURSIVE ancestors(key) AS (VALUES($path) UNION ALL SELECT rtrim(rtrim(key,replace(key,'/','')),'/') FROM ancestors WHERE instr(key,'/')>0) "
            + "SELECT EXISTS(SELECT 1 FROM ancestors a JOIN case_preservations c ON c.path_key=a.key WHERE c.completed=0 UNION ALL "
            + "SELECT 1 FROM ancestors a JOIN case_preservations c ON c.destination_key=a.key WHERE c.completed=0 UNION ALL "
            + "SELECT 1 FROM case_preservations WHERE completed=0 AND $descendants=1 AND path_key >= $low AND path_key < $high UNION ALL "
            + "SELECT 1 FROM case_preservations WHERE completed=0 AND $descendants=1 AND destination_key >= $low AND destination_key < $high);";
        command.Parameters.AddWithValue("$descendants", includeDescendants ? 1 : 0);
        command.Parameters.AddWithValue("$path", key); command.Parameters.AddWithValue("$low", key + "/"); command.Parameters.AddWithValue("$high", key + "0");
        return (long)command.ExecuteScalar()! != 0;
    }

    /// <summary>Reserve an authenticated case copy and its native recovery intent
    /// together. The engine holds the filesystem/reference gates and verifies the
    /// source, destination policy and native root before calling.</summary>
    public Task ReserveCaseCopyAsync(PendingCasePreservation expected, ApplyIntent intent, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(intent);
        string key = PathRules.Key(destination);
        if (key == expected.Source.PathKey || key != intent.PathKey || intent.ExpectedLocalSequence is not null)
            throw new ArgumentException("Invalid case copy destination.", nameof(destination));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequirePendingCase(connection, transaction, expected); RequireExpectedSequence(connection, transaction, intent);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO apply_intents(id,path_key,expected_local_seq,payload) VALUES($intent,$path,NULL,$payload);";
            command.Parameters.AddWithValue("$intent", intent.Id.ToString("N")); command.Parameters.AddWithValue("$path", key);
            command.Parameters.AddWithValue("$payload", intent.Payload.ToArray()); command.ExecuteNonQuery();
            command.CommandText = "UPDATE case_preservations SET destination_key=$path,destination_path=$destination,installed_file_id=NULL WHERE source_id=$id;";
            command.Parameters.AddWithValue("$destination", destination); command.Parameters.AddWithValue("$id", expected.Source.SourceId);
            cancellationToken.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); return true;
        }, cancellationToken);
    }

    public Task AccountInstalledCaseCopyAsync(PendingCasePreservation expected, ApplyIntent intent, string destination, byte[] fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(intent); FolderMaps.RequireBytes(fileId, 16);
        byte[] identity = fileId.ToArray(); string key = PathRules.Key(destination);
        if (key != intent.PathKey) throw new ArgumentException("Case copy does not match its intent.", nameof(destination));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequirePendingCase(connection, transaction, expected); RequireSameIntent(ReadIntent(connection, transaction, intent.Id), intent);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE case_preservations SET installed_file_id=$file WHERE source_id=$id AND destination_key=$path AND destination_path=$destination;";
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); command.Parameters.AddWithValue("$path", key);
            command.Parameters.AddWithValue("$destination", destination); command.Parameters.AddWithValue("$file", identity);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Case destination changed.");
            cancellationToken.ThrowIfCancellationRequested(); RemoveIntent(connection, transaction, intent.Id);
            ClearCaseRetry(connection, transaction, expected.Source.SourceId); return true;
        }, cancellationToken);
    }

    /// <summary>Account guarded visible bytes at an already authenticated ready
    /// local publication. This observation confers no filesystem deletion rights.</summary>
    public Task AccountObservedCaseCopyAsync(PendingCasePreservation expected, LocalFileRow observed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(observed);
        var frozen = Freeze(observed); FolderMaps.Local.Validate(frozen); string key = PathRules.Key(frozen.Path);
        if (key == expected.Source.PathKey || frozen is not { Type: 0, Deleted: false, Flags: LocalFileState.None, ContentReady: true, Record: not null, FileId: { Length: 16 }, FileHash: { Length: 32 } })
            throw new ArgumentException("Case observation requires a ready authenticated regular file.", nameof(observed));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequirePendingCase(connection, transaction, expected);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT " + FolderMaps.LocalColumns + " FROM local_files WHERE path_key=$path;";
            command.Parameters.AddWithValue("$path", key);
            LocalFileRow? current; using (var reader = command.ExecuteReader()) current = reader.Read() ? FolderMaps.ReadLocal(reader) : null;
            if (current is not { Type: 0, Deleted: false, Flags: LocalFileState.None, ContentReady: true, Record: not null, FileId: not null, FileHash: not null }
                || current.LocalSequence != frozen.LocalSequence || !current.FileId.AsSpan().SequenceEqual(frozen.FileId)
                || !current.FileHash.AsSpan().SequenceEqual(frozen.FileHash) || !current.Record.Body.SequenceEqual(frozen.Record.Body)
                || !current.Record.Signature.SequenceEqual(frozen.Record.Signature)) throw new IOException("Case publication observation changed.");
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM apply_intents WHERE path_key=$path);";
            if ((long)command.ExecuteScalar()! != 0) throw new IOException("Case publication has pending application.");
            command.CommandText = "UPDATE case_preservations SET destination_key=$path,destination_path=$destination,installed_file_id=$file WHERE source_id=$id;";
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); command.Parameters.AddWithValue("$destination", frozen.Path);
            command.Parameters.AddWithValue("$file", frozen.FileId); cancellationToken.ThrowIfCancellationRequested(); command.ExecuteNonQuery();
            ClearCaseRetry(connection, transaction, expected.Source.SourceId); return true;
        }, cancellationToken);
    }

    /// <summary>After native byte verification, complete only the same observed
    /// local version. RW additionally requires ready signed publication.</summary>
    public Task CompleteCasePreservationAsync(PendingCasePreservation expected, long localSequence, bool requirePublication, long completedTicks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        RequireStagingNow(completedTicks);
        if (expected.DestinationPath is null || expected.InstalledFileId is null) throw new ArgumentException("Case copy is not installed.", nameof(expected));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequirePendingCase(connection, transaction, expected);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT " + FolderMaps.LocalColumns + " FROM local_files WHERE path_key=$path;";
            command.Parameters.AddWithValue("$path", PathRules.Key(expected.DestinationPath));
            LocalFileRow? local; using (var reader = command.ExecuteReader()) local = reader.Read() ? FolderMaps.ReadLocal(reader) : null;
            if (local is null || local.LocalSequence != localSequence || local.Deleted || local.Flags != LocalFileState.None
                || local.FileId is null || !local.FileId.AsSpan().SequenceEqual(expected.InstalledFileId)
                || requirePublication && (local.Record is null || !local.ContentReady)) throw new IOException("Case copy publication changed.");
            command.CommandText = "UPDATE case_preservations SET completed=1,completed_ticks=$completed WHERE source_id=$id AND destination_path=$destination AND installed_file_id=$file;";
            command.Parameters.AddWithValue("$completed", completedTicks);
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); command.Parameters.AddWithValue("$destination", expected.DestinationPath);
            command.Parameters.AddWithValue("$file", expected.InstalledFileId);
            cancellationToken.ThrowIfCancellationRequested();
            if (command.ExecuteNonQuery() != 1) throw new IOException("Case copy observation changed.");
            ClearCaseRetry(connection, transaction, expected.Source.SourceId); return true;
        }, cancellationToken);
    }

    private static void RequirePendingCase(SqliteConnection connection, SqliteTransaction transaction, PendingCasePreservation expected)
    {
        RequireBlockReferenceRoot(connection, transaction, expected.RootId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT root_id,path_key,record_body,signature FROM case_preservations WHERE source_id=$id AND completed=0;";
        command.Parameters.AddWithValue("$id", expected.Source.SourceId); using var reader = command.ExecuteReader();
        if (!reader.Read() || !reader.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(expected.RootId) || reader.GetString(1) != expected.Source.PathKey
            || !expected.Source.Record.Body.SequenceEqual(reader.GetFieldValue<byte[]>(2)) || !expected.Source.Record.Signature.SequenceEqual(reader.GetFieldValue<byte[]>(3)))
            throw new InvalidOperationException("Pending case source changed.");
    }

    private static void AdmitCaseSources(SqliteConnection connection, SqliteTransaction transaction, string path,
        IReadOnlyList<CasePreservationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) return;
        if (sources.Count > MaximumPendingCaseSources) throw new CasePreservationCapacityException();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT value FROM meta WHERE key='_root-identity';";
        if (command.ExecuteScalar() is not byte[] { Length: 24 } root) throw new InvalidOperationException("Case preservation requires a bound root.");
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.PathKey != path) throw new InvalidOperationException("Case source belongs to another path.");
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", source.SourceId);
            command.CommandText = "SELECT root_id,path_key,record_body,signature,completed FROM case_preservations WHERE source_id=$id;";
            bool exists;
            using (var reader = command.ExecuteReader())
            {
                exists = reader.Read();
                // Forwarding identity can change. The portable signed source cannot.
                if (exists)
                {
                    bool compact = reader.GetInt32(4) == 1 && reader.IsDBNull(2);
                    if (!root.AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(0)) || reader.GetString(1) != path
                        || !compact && (!source.Record.Body.SequenceEqual(reader.GetFieldValue<byte[]>(2)) || !source.Record.Signature.SequenceEqual(reader.GetFieldValue<byte[]>(3))))
                        throw new InvalidDataException("Case source identity changed.");
                }
            }
            if (exists) continue;
            command.CommandText = "SELECT COUNT(*) FROM case_preservations WHERE completed=0;";
            if ((long)command.ExecuteScalar()! >= MaximumPendingCaseSources) throw new CasePreservationCapacityException();
            command.CommandText = "INSERT INTO case_preservations(source_id,root_id,path_key,source_device,record_body,signature) VALUES($id,$root,$path,$device,$body,$signature);";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$device", source.SourceDevice); command.Parameters.AddWithValue("$body", source.Record.Body.ToArray());
            command.Parameters.AddWithValue("$signature", source.Record.Signature.ToArray()); command.ExecuteNonQuery();
        }
    }
}
