using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryRebase(byte[] GraphId, byte[] InputHash, long InputEpoch, string? Cursor, bool Complete)
{ public override string ToString() => "Directory rebase proof (redacted)"; }

public sealed partial class FolderIndex
{
    public Task<DirectoryRebase> BeginDirectoryRebaseAsync(DirectoryPublication queued, DirectoryCaptureState draft, byte[] graphId, CancellationToken cancellationToken = default)
    {
        var publication = FreezePublication(queued); var capture = FreezeCapture(draft); FolderMaps.RequireBytes(graphId, 32); byte[] graph = graphId.ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireRebaseInputs(connection, transaction, publication, capture, graph);
            var old = FindDirectoryRebase(connection, transaction, publication.Path);
            if (old is not null && old.InputEpoch == capture.InputEpoch && old.GraphId.AsSpan().SequenceEqual(graph) && old.InputHash.AsSpan().SequenceEqual(capture.InputHash)) return old;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO directory_rebases(path_key,graph_id,input_hash,input_epoch,cursor,complete) VALUES($path,$graph,$hash,$epoch,NULL,0) "
                + "ON CONFLICT(path_key) DO UPDATE SET graph_id=excluded.graph_id,input_hash=excluded.input_hash,input_epoch=excluded.input_epoch,cursor=NULL,complete=0;";
            command.Parameters.AddWithValue("$path", PathRules.Key(publication.Path)); command.Parameters.AddWithValue("$graph", graph);
            command.Parameters.AddWithValue("$hash", capture.InputHash); command.Parameters.AddWithValue("$epoch", capture.InputEpoch!.Value); command.ExecuteNonQuery();
            return new DirectoryRebase(graph, capture.InputHash, capture.InputEpoch.Value, null, false);
        }, cancellationToken);
    }

    /// <summary>Engine authenticated this exact next prefix; local mutation invalidates every accepted member.</summary>
    public Task<DirectoryRebase> AdvanceDirectoryRebaseAsync(DirectoryPublication queued, DirectoryCaptureState draft, DirectoryRebase expected,
        IReadOnlyList<DirectoryGraphMember> verified, CancellationToken cancellationToken = default)
    {
        var publication = FreezePublication(queued); var capture = FreezeCapture(draft);
        var proof = expected with { GraphId = expected.GraphId.ToArray(), InputHash = expected.InputHash.ToArray() };
        if (verified.Count > 1000) throw new ArgumentOutOfRangeException(nameof(verified));
        var members = verified.Select(row => row with { RecordHash = row.RecordHash.ToArray() }).ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireRebaseInputs(connection, transaction, publication, capture, proof.GraphId);
            var actual = FindDirectoryRebase(connection, transaction, publication.Path);
            if (actual is null || actual.InputEpoch != proof.InputEpoch || actual.InputEpoch != capture.InputEpoch || actual.Cursor != proof.Cursor || actual.Complete != proof.Complete
                || !actual.GraphId.AsSpan().SequenceEqual(proof.GraphId) || !actual.InputHash.AsSpan().SequenceEqual(proof.InputHash)
                || !actual.InputHash.AsSpan().SequenceEqual(capture.InputHash)) throw new InvalidOperationException("Directory rebase proof changed.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT path_key,record_hash,record_length FROM directory_graph_members WHERE graph_id=$graph "
                + (proof.Cursor is null ? "" : "AND path_key>$after ") + "ORDER BY path_key LIMIT $limit;";
            command.Parameters.AddWithValue("$graph", proof.GraphId); command.Parameters.AddWithValue("$limit", members.Length + 1);
            if (proof.Cursor is not null) command.Parameters.AddWithValue("$after", proof.Cursor);
            bool complete; string? cursor = proof.Cursor;
            using (var reader = command.ExecuteReader())
            {
                foreach (var member in members)
                {
                    if (!reader.Read() || reader.GetString(0) != member.PathKey || reader.GetInt32(2) != member.RecordLength
                        || !reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(member.RecordHash)) throw new InvalidOperationException("Directory rebase prefix changed.");
                    cursor = member.PathKey;
                }
                complete = !reader.Read();
            }
            command.CommandText = "UPDATE directory_rebases SET cursor=$cursor,complete=$complete WHERE path_key=$path;";
            command.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value); command.Parameters.AddWithValue("$complete", complete ? 1 : 0);
            command.Parameters.AddWithValue("$path", PathRules.Key(publication.Path)); command.ExecuteNonQuery();
            return actual with { Cursor = cursor, Complete = complete };
        }, cancellationToken);
    }
    private static DirectoryRebase? FindDirectoryRebase(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT graph_id,input_hash,input_epoch,cursor,complete FROM directory_rebases WHERE path_key=$path;";
        command.Parameters.AddWithValue("$path", PathRules.Key(path)); using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4) != 0) : null;
    }
    private static void RequireRebaseInputs(SqliteConnection connection, SqliteTransaction transaction, DirectoryPublication queued, DirectoryCaptureState draft, byte[] graph)
    {
        RequirePublicationExpected(connection, transaction, queued); RequireCaptureExpected(connection, transaction, draft);
        if (!queued.ReconstructionOnly || draft.Hash is null || draft.InputEpoch is null || queued.KeysEpoch != draft.InputEpoch || draft.ParentPath != queued.Path
            || !draft.RootId.AsSpan().SequenceEqual(queued.RootId) || !draft.NativeId.AsSpan().SequenceEqual(queued.NativeId)) throw new InvalidOperationException("Directory rebase inputs are incomplete.");
        RequireDirectoryCaptureEpoch(connection, transaction, draft.InputEpoch.Value);
        using var digest = Blake3.Hasher.New(); digest.Update("PeerSync case source v1"u8); digest.Update(queued.Template.Body); digest.Update(queued.Template.Signature);
        if (!digest.Finalize().AsSpan().SequenceEqual(graph)) throw new InvalidDataException("Directory rebase graph does not match its original template.");
        var original = FindDirectoryGraph(connection, transaction, graph);
        if (original is null || !original.Complete || original.Retiring || original.DirectoryPath != queued.Path || !original.RootId.AsSpan().SequenceEqual(queued.RootId))
            throw new InvalidOperationException("Directory rebase original graph is unavailable.");
    }
}
