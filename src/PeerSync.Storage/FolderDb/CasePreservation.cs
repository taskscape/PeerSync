using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

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
    public byte[] RootId => root.ToArray();
    public CasePreservationSource Source { get; }
    internal PendingCasePreservation(byte[] rootId, CasePreservationSource source)
    { root = rootId.ToArray(); Source = source; }
    public override string ToString() => "Pending case preservation (redacted)";
}

public sealed partial class FolderIndex
{
    public const int MaximumPendingCaseSources = 1000;

    /// <summary>A bounded immutable page, ordered by portable source identity.
    /// The coordinator must retain the pending barrier until visible publication.</summary>
    public IReadOnlyList<PendingCasePreservation> ReadPendingCasePreservations(int limit = MaximumPendingCaseSources)
    {
        if (limit is < 1 or > MaximumPendingCaseSources) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT root_id,source_id,source_device,path_key,record_body,signature FROM case_preservations WHERE completed=0 ORDER BY source_id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<PendingCasePreservation>(); using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new(reader.GetFieldValue<byte[]>(0), new(reader.GetFieldValue<byte[]>(1),
            reader.GetFieldValue<byte[]>(2), reader.GetString(3), new(reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<byte[]>(5)))));
        return rows.AsReadOnly();
    }

    public bool HasPendingCasePreservation(string path)
    {
        string key = PathRules.Key(path);
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM case_preservations WHERE path_key=$path AND completed=0);";
        command.Parameters.AddWithValue("$path", key); return (long)command.ExecuteScalar()! != 0;
    }

    private static void AdmitCaseSources(SqliteConnection connection, SqliteTransaction transaction, string path,
        IReadOnlyList<CasePreservationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) return;
        if (sources.Count > MaximumPendingCaseSources) throw new InvalidOperationException("Case preservation pending capacity exceeded.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT value FROM meta WHERE key='_root-identity';";
        if (command.ExecuteScalar() is not byte[] { Length: 24 } root) throw new InvalidOperationException("Case preservation requires a bound root.");
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.PathKey != path) throw new InvalidOperationException("Case source belongs to another path.");
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", source.SourceId);
            command.CommandText = "SELECT root_id,path_key,record_body,signature FROM case_preservations WHERE source_id=$id;";
            bool exists;
            using (var reader = command.ExecuteReader())
            {
                exists = reader.Read();
                // Forwarding identity can change. The portable signed source cannot.
                if (exists && (!root.AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(0)) || reader.GetString(1) != path
                    || !source.Record.Body.SequenceEqual(reader.GetFieldValue<byte[]>(2)) || !source.Record.Signature.SequenceEqual(reader.GetFieldValue<byte[]>(3))))
                    throw new InvalidDataException("Case source identity changed.");
            }
            if (exists) continue;
            command.CommandText = "SELECT COUNT(*) FROM case_preservations WHERE completed=0;";
            if ((long)command.ExecuteScalar()! >= MaximumPendingCaseSources) throw new InvalidOperationException("Case preservation pending capacity exceeded.");
            command.CommandText = "INSERT INTO case_preservations(source_id,root_id,path_key,source_device,record_body,signature) VALUES($id,$root,$path,$device,$body,$signature);";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$device", source.SourceDevice); command.Parameters.AddWithValue("$body", source.Record.Body.ToArray());
            command.Parameters.AddWithValue("$signature", source.Record.Signature.ToArray()); command.ExecuteNonQuery();
        }
    }
}
