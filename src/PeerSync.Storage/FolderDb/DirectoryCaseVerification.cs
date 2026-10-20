using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryCaseVerification(long Epoch, long RootSequence, bool RequirePublication, string? LastKey, bool Complete);
public sealed record DirectoryCaseObservation(string MemberKey, long LocalSequence, byte[] FileId);

public sealed partial class FolderIndex
{
    public Task<DirectoryCaseVerification> BeginDirectoryCaseVerificationAsync(PendingCasePreservation expected, long rootSequence, bool requirePublication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentOutOfRangeException.ThrowIfNegative(rootSequence);
        byte[] root = expected.InstalledFileId ?? throw new ArgumentException("Case root is not installed.", nameof(expected));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireDirectoryCaseRoot(connection, transaction, expected, root);
            if (!ReadCaseProgress(connection, transaction, expected.Source.SourceId, root).Exhausted) throw new InvalidOperationException("Case installation is incomplete.");
            RequireVerifiedRootRow(connection, transaction, expected, rootSequence, requirePublication);
            long epoch = ReadDirectoryCaptureEpoch(connection, transaction); var before = ReadCaseVerification(connection, transaction, expected.Source.SourceId, root);
            if (before is not null && before.Epoch == epoch && before.RootSequence == rootSequence && before.RequirePublication == requirePublication) return before;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_case_progress SET verify_epoch=$epoch,verify_root_seq=$seq,verify_rw=$rw,verify_key=NULL,verify_complete=0 WHERE source_id=$id AND root_file_id=$root;";
            command.Parameters.AddWithValue("$epoch", epoch); command.Parameters.AddWithValue("$seq", rootSequence); command.Parameters.AddWithValue("$rw", requirePublication ? 1 : 0);
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); command.Parameters.AddWithValue("$root", root); command.ExecuteNonQuery();
            return new DirectoryCaseVerification(epoch, rootSequence, requirePublication, null, false);
        }, cancellationToken);
    }
    public Task<DirectoryCaseVerification> AdvanceDirectoryCaseVerificationAsync(PendingCasePreservation expected, DirectoryCaseVerification verification,
        IReadOnlyList<DirectoryCaseObservation> observations, bool complete, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(verification); ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count > 1000 || observations.Count == 0 && !complete) throw new ArgumentException("Invalid verification batch.", nameof(observations));
        var rows = observations.Select(row => row with { FileId = row.FileId.ToArray(), MemberKey = PathRules.Key(row.MemberKey) }).ToArray();
        foreach (var row in rows) { FolderMaps.RequireBytes(row.FileId, 16); ArgumentOutOfRangeException.ThrowIfNegative(row.LocalSequence); }
        byte[] root = expected.InstalledFileId ?? throw new ArgumentException("Case root is not installed.", nameof(expected));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireDirectoryCaseVerification(connection, transaction, expected, verification); string? cursor = verification.LastKey;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested(); var next = NextCaseGraphMember(connection, transaction, expected.Source.SourceId, cursor);
                var member = ReadCaseMember(connection, transaction, expected.Source.SourceId, root, row.MemberKey);
                if (next is null || next.PathKey != row.MemberKey || member?.InstalledFileId is null || !member.InstalledFileId.AsSpan().SequenceEqual(row.FileId)) throw new InvalidOperationException("Case observation prefix or identity changed.");
                var local = ReadDirectoryCaseLocal(connection, transaction, member.DestinationPath);
                if (local is null || local.LocalSequence != row.LocalSequence || local.Deleted || local.Flags != LocalFileState.None || local.Type != member.Type
                    || local.FileId is null || !local.FileId.AsSpan().SequenceEqual(row.FileId) || verification.RequirePublication && (local.Record is null || local.Type == 0 && !local.ContentReady))
                    throw new IOException("Observed case publication changed.");
                cursor = row.MemberKey;
            }
            if (complete && NextCaseGraphMember(connection, transaction, expected.Source.SourceId, cursor) is not null) throw new InvalidOperationException("Case verification has remaining members.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_case_progress SET verify_key=$key,verify_complete=$complete WHERE source_id=$id AND root_file_id=$root;";
            command.Parameters.AddWithValue("$key", (object?)cursor ?? DBNull.Value); command.Parameters.AddWithValue("$complete", complete ? 1 : 0);
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); command.Parameters.AddWithValue("$root", root); command.ExecuteNonQuery();
            return verification with { LastKey = cursor, Complete = complete };
        }, cancellationToken);
    }
    public Task CompleteDirectoryCasePreservationAsync(PendingCasePreservation expected, DirectoryCaseVerification verification, long nowTicks, long? retainUntilTicks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(verification); RequireStagingNow(nowTicks);
        if (!verification.Complete || (verification.RequirePublication ? retainUntilTicks is not null : retainUntilTicks is null || retainUntilTicks < nowTicks || retainUntilTicks > DateTime.MaxValue.Ticks))
            throw new ArgumentException("Invalid case completion proof or retention.", nameof(verification));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireDirectoryCaseVerification(connection, transaction, expected, verification);
            if (NextCaseGraphMember(connection, transaction, expected.Source.SourceId, verification.LastKey) is not null) throw new InvalidOperationException("Case members remain unverified.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM apply_intents);"; if ((long)command.ExecuteScalar()! != 0) throw new IOException("Case completion waits for recovery.");
            string key = PathRules.Key(expected.DestinationPath!);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM directory_publications WHERE path_key=$key OR path_key >= $low AND path_key < $high);";
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$low", key + '/'); command.Parameters.AddWithValue("$high", key + '0');
            if ((long)command.ExecuteScalar()! != 0) throw new IOException("Case completion waits for publication.");
            command.CommandText = "UPDATE case_preservations SET completed=1,completed_ticks=$now,retained_until_ticks=$until WHERE source_id=$id;";
            command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$until", (object?)retainUntilTicks ?? DBNull.Value); command.Parameters.AddWithValue("$id", expected.Source.SourceId);
            command.ExecuteNonQuery(); ClearCaseRetry(connection, transaction, expected.Source.SourceId); return 0;
        }, cancellationToken);
    }
    public IEnumerable<CasePreservationSource> ReadRetainedDirectoryCases(byte[] rootId, long nowTicks)
    {
        FolderMaps.RequireBytes(rootId, 24); RequireStagingNow(nowTicks); byte[] root = rootId.ToArray(); return Read();
        IEnumerable<CasePreservationSource> Read()
        {
            using var connection = database.OpenReader(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_id,source_device,path_key,record_body,signature FROM case_preservations WHERE root_id=$root AND completed=1 AND retained_until_ticks>$now AND record_body IS NOT NULL ORDER BY retained_until_ticks,source_id;";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", nowTicks); using var reader = command.ExecuteReader();
            while (reader.Read()) yield return new(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetString(2), new(reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4)));
        }
    }
    private static void RequireDirectoryCaseVerification(SqliteConnection connection, SqliteTransaction transaction, PendingCasePreservation expected, DirectoryCaseVerification proof)
    {
        byte[] root = expected.InstalledFileId ?? throw new InvalidOperationException("Case root is missing."); RequireDirectoryCaseRoot(connection, transaction, expected, root);
        if (!ReadCaseProgress(connection, transaction, expected.Source.SourceId, root).Exhausted) throw new InvalidOperationException("Case installation changed.");
        RequireDirectoryCaptureEpoch(connection, transaction, proof.Epoch); RequireVerifiedRootRow(connection, transaction, expected, proof.RootSequence, proof.RequirePublication);
        if (ReadCaseVerification(connection, transaction, expected.Source.SourceId, root) != proof) throw new InvalidOperationException("Case verification witness changed.");
    }
    private static DirectoryCaseVerification? ReadCaseVerification(SqliteConnection connection, SqliteTransaction transaction, byte[] id, byte[] root)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT verify_epoch,verify_root_seq,verify_rw,verify_key,verify_complete FROM directory_case_progress WHERE source_id=$id AND root_file_id=$root;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$root", root); using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4)) : null;
    }
    private static void RequireVerifiedRootRow(SqliteConnection connection, SqliteTransaction transaction, PendingCasePreservation expected, long sequence, bool rw)
    {
        var row = ReadDirectoryCaseLocal(connection, transaction, expected.DestinationPath!);
        if (row is not { Type: 1, Deleted: false, Flags: LocalFileState.None, FileId: not null } || row.LocalSequence != sequence
            || !row.FileId.AsSpan().SequenceEqual(expected.InstalledFileId) || rw && row.Record is null) throw new IOException("Copied root publication is unavailable or changed.");
    }
    private static LocalFileRow? ReadDirectoryCaseLocal(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT " + FolderMaps.LocalColumns + " FROM local_files WHERE path_key=$key;"; command.Parameters.AddWithValue("$key", PathRules.Key(path));
        using var reader = command.ExecuteReader(); return reader.Read() ? FolderMaps.ReadLocal(reader) : null;
    }
}
