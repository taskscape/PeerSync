using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryParentProof(string PathKey, byte[] RootId, byte[] NativeId, byte[] MetadataHash, byte[] InputHash,
    long InputEpoch, Guid QueueVersion, long LocalSequence, byte[] LocalSourceId, byte[] LocalHash, int LocalLength)
{
    public long ChildEpoch { get; init; }
    public long VerifiedChildEpoch { get; init; }
    public long RemoteRevision { get; init; }
    public long DiscoveryRevision { get; init; }
    public long Generation { get; init; }
    public byte[]? DiscoveryAfter { get; init; }
    public bool DiscoveryComplete { get; init; }
    public bool Resetting { get; init; }
    public IReadOnlyDictionary<ulong, ulong> Clock { get; init; } = new Dictionary<ulong, ulong>();
    public override string ToString() => "Directory parent proof (redacted)";
}
public sealed record DirectoryParentPreparation(DirectoryParentProof Proof, int ResetWork);
public sealed record DirectoryParentRoot(string PathKey, DirectoryMetadataSource Source, long Generation, string? Cursor, bool Complete)
{ public override string ToString() => "Directory parent root proof (redacted)"; }
public sealed record DirectoryParentPage(int Limit, long Revision, byte[]? After, byte[]? NextAfter, bool More, IReadOnlyList<RemoteFileRow> Rows);

public sealed partial class FolderIndex
{
    private const string ParentProofColumns = "path_key,root_id,native_id,metadata_hash,input_hash,input_epoch,queue_version,local_seq,local_source_id,local_hash,local_length,child_epoch,verified_child_epoch,remote_revision,discovery_revision,generation,discovery_after,discovery_complete,resetting,discovery_clock";

    public DirectoryParentProof? ReadDirectoryParentProof(string path)
    { using var connection = database.OpenReader(); return FindParentProof(connection, null, PathRules.Key(path)); }

    /// <summary>An authenticated ordinary or reconstruction parent graph has a
    /// still-unproven direct child at this path. A scheduled live receipt for
    /// that exact child may advance the parent proof; unrelated paths retain
    /// any ordinary ancestor publication barrier.</summary>
    public bool HasUnprovenDirectoryParentMember(string path)
    {
        string key = PathRules.Key(path), parent = CaptureParent(key);
        if (parent.Length == 0) return false;
        using var connection = database.OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM directory_publications q "
            + "JOIN directory_parent_proofs p ON p.path_key=q.path_key AND p.queue_version=q.version "
            + "AND p.root_id=q.root_id AND p.native_id=q.native_id AND p.local_seq=q.expected_local_seq "
            + "JOIN directory_parent_roots r ON r.path_key=p.path_key AND r.seen_generation=p.generation "
            + "JOIN directory_graphs g ON g.id=r.source_id AND g.complete=1 AND g.retiring=0 "
            + "AND g.root_id=q.root_id AND g.directory_path=r.source_path AND g.node_hash=r.node_hash AND g.node_length=r.node_length "
            + "JOIN directory_graph_members m ON m.graph_id=r.source_id AND m.path_key=$key "
            + "WHERE q.path_key=$parent AND q.paused=0 "
            + "AND p.discovery_complete=1 AND p.resetting=0 AND p.remote_revision=p.discovery_revision "
            + "AND r.complete=0 AND (r.cursor IS NULL OR m.path_key>r.cursor));";
        command.Parameters.AddWithValue("$parent", parent);
        command.Parameters.AddWithValue("$key", key);
        return (long)command.ExecuteScalar()! != 0;
    }

    /// <summary>Rebind only after the caller has revalidated the exact indexed input. Direct-child changes invalidate prior causal work.</summary>
    public Task<DirectoryParentPreparation> BeginDirectoryParentProofAsync(DirectoryPublication queued, DirectoryCaptureState draft,
        DirectoryMetadataSource original, byte[] metadataHash, long nowTicks, int maximumResetWork = 1000, CancellationToken cancellationToken = default)
    {
        var publication = FreezePublication(queued); var capture = FreezeCapture(draft); var source = FreezeMetadataSources([original])[0];
        FolderMaps.RequireBytes(metadataHash, 32); byte[] metadata = metadataHash.ToArray(); RequireStagingNow(nowTicks);
        if (maximumResetWork is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumResetWork));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireParentInputs(connection, transaction, publication, capture); var originalClock = RequireParentOriginal(connection, transaction, publication, source);
            string key = PathRules.Key(publication.Path); var old = FindParentProof(connection, transaction, key);
            if (old is not null && !old.RootId.AsSpan().SequenceEqual(publication.RootId)) throw new InvalidDataException("Parent proof belongs to another folder root.");
            bool reset = old is not null && (old.Resetting || !old.NativeId.AsSpan().SequenceEqual(publication.NativeId) || !old.MetadataHash.AsSpan().SequenceEqual(metadata));
            int work = 0;
            if (reset)
            {
                using var retire = connection.CreateCommand(); retire.Transaction = transaction;
                retire.CommandText = "DELETE FROM directory_parent_roots WHERE source_id IN (SELECT source_id FROM directory_parent_roots WHERE path_key=$path ORDER BY source_id LIMIT $limit);";
                retire.Parameters.AddWithValue("$path", key); retire.Parameters.AddWithValue("$limit", maximumResetWork); work = retire.ExecuteNonQuery();
                retire.CommandText = "SELECT EXISTS(SELECT 1 FROM directory_parent_roots WHERE path_key=$path);";
                if ((long)retire.ExecuteScalar()! != 0)
                {
                    retire.CommandText = "UPDATE directory_parent_proofs SET resetting=1,touched_ticks=$now WHERE path_key=$path;";
                    retire.Parameters.AddWithValue("$now", nowTicks); retire.ExecuteNonQuery();
                    return new DirectoryParentPreparation(FindParentProof(connection, transaction, key)!, work);
                }
                retire.CommandText = "DELETE FROM directory_parent_proofs WHERE path_key=$path;"; retire.ExecuteNonQuery(); old = null;
            }
            bool discovery = old is null || !old.LocalHash.AsSpan().SequenceEqual(source.Hash) || old.LocalLength != source.Length || old.RemoteRevision != old.DiscoveryRevision;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO directory_parent_proofs(path_key,root_id,native_id,metadata_hash,input_hash,input_epoch,queue_version,local_seq,local_source_id,local_hash,local_length,touched_ticks) "
                + "VALUES($path,$root,$native,$metadata,$input,$epoch,$version,$seq,$source,$hash,$length,$now) ON CONFLICT(path_key) DO UPDATE SET "
                + "input_hash=excluded.input_hash,input_epoch=excluded.input_epoch,verified_child_epoch=directory_parent_proofs.child_epoch,"
                + "queue_version=excluded.queue_version,local_seq=excluded.local_seq,local_source_id=excluded.local_source_id,local_hash=excluded.local_hash,local_length=excluded.local_length,touched_ticks=excluded.touched_ticks;";
            command.Parameters.AddWithValue("$path", key); command.Parameters.AddWithValue("$root", publication.RootId); command.Parameters.AddWithValue("$native", publication.NativeId);
            command.Parameters.AddWithValue("$metadata", metadata); command.Parameters.AddWithValue("$input", capture.InputHash); command.Parameters.AddWithValue("$epoch", capture.InputEpoch!.Value);
            command.Parameters.AddWithValue("$version", publication.Version.ToString("N")); command.Parameters.AddWithValue("$seq", publication.ExpectedLocalSequence);
            command.Parameters.AddWithValue("$source", source.SourceId); command.Parameters.AddWithValue("$hash", source.Hash); command.Parameters.AddWithValue("$length", source.Length);
            command.Parameters.AddWithValue("$now", nowTicks); command.ExecuteNonQuery();
            if (discovery && old is not null)
            {
                command.CommandText = "UPDATE directory_parent_proofs SET discovery_revision=remote_revision,generation=generation+1,discovery_after=NULL,discovery_complete=0 WHERE path_key=$path;";
                command.ExecuteNonQuery();
            }
            var proof = FindParentProof(connection, transaction, key)!;
            var clock = discovery ? originalClock : VersionVector.Merge(proof.Clock, originalClock);
            command.CommandText = "UPDATE directory_parent_proofs SET discovery_clock=$clock WHERE path_key=$path;";
            command.Parameters.AddWithValue("$clock", VersionVector.Serialize(clock)); command.ExecuteNonQuery();
            proof = FindParentProof(connection, transaction, key)!;
            AddParentRoot(connection, transaction, proof, source, nowTicks);
            cancellationToken.ThrowIfCancellationRequested(); return new DirectoryParentPreparation(proof, work);
        }, cancellationToken);
    }

    private static void RequireParentInputs(SqliteConnection connection, SqliteTransaction transaction, DirectoryPublication queued, DirectoryCaptureState draft)
    {
        var actual = RequirePublicationExpected(connection, transaction, queued); RequireCaptureExpected(connection, transaction, draft);
        if (actual.Paused || draft.Hash is null || draft.InputEpoch is null || queued.KeysEpoch != draft.InputEpoch || draft.ParentPath != queued.Path
            || !draft.RootId.AsSpan().SequenceEqual(queued.RootId) || !draft.NativeId.AsSpan().SequenceEqual(queued.NativeId))
            throw new InvalidOperationException("Parent proof capture or queue is incomplete.");
        RequireDirectoryCaptureEpoch(connection, transaction, draft.InputEpoch.Value);
    }
    private static Dictionary<ulong, ulong> RequireParentOriginal(SqliteConnection connection, SqliteTransaction transaction, DirectoryPublication queued, DirectoryMetadataSource source)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT local_seq,record_body,signature,vv FROM local_files WHERE path_key=$path AND type=1 AND deleted=0;";
        command.Parameters.AddWithValue("$path", PathRules.Key(queued.Path)); using var reader = command.ExecuteReader();
        if (PathRules.Key(source.Path) != PathRules.Key(queued.Path) || !reader.Read() || reader.GetInt64(0) != queued.ExpectedLocalSequence
            || reader.IsDBNull(1) || !source.Record.Body.SequenceEqual(reader.GetFieldValue<byte[]>(1)) || !source.Record.Signature.SequenceEqual(reader.GetFieldValue<byte[]>(2)))
            throw new InvalidOperationException("Parent proof original changed.");
        return VersionVector.Deserialize(reader.GetFieldValue<byte[]>(3));
    }
    private static DirectoryParentProof? FindParentProof(SqliteConnection connection, SqliteTransaction? transaction, string path)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT " + ParentProofColumns + " FROM directory_parent_proofs WHERE path_key=$path;";
        command.Parameters.AddWithValue("$path", path); using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<byte[]>(4), reader.GetInt64(5), Guid.ParseExact(reader.GetString(6), "N"), reader.GetInt64(7), reader.GetFieldValue<byte[]>(8), reader.GetFieldValue<byte[]>(9), reader.GetInt32(10))
        {
            ChildEpoch = reader.GetInt64(11), VerifiedChildEpoch = reader.GetInt64(12), RemoteRevision = reader.GetInt64(13), DiscoveryRevision = reader.GetInt64(14),
            Generation = reader.GetInt64(15), DiscoveryAfter = reader.IsDBNull(16) ? null : reader.GetFieldValue<byte[]>(16), DiscoveryComplete = reader.GetBoolean(17), Resetting = reader.GetBoolean(18), Clock = VersionVector.Deserialize(reader.GetFieldValue<byte[]>(19))
        } : null;
    }
}
