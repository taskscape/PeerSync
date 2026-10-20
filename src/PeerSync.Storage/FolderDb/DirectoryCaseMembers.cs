using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryCaseProgress(string? LastKey, bool Exhausted);
public sealed record DirectoryCaseMemberRow(byte[] SourceId, byte[] RootFileId, string MemberKey, byte[] RecordHash, int RecordLength,
    byte[] MemberSourceId, uint Type, string DestinationPath, byte[]? InstalledFileId, long TouchedTicks)
{ public override string ToString() => "Directory case member (redacted)"; }

public sealed partial class FolderIndex
{
    public DirectoryCaseProgress ReadDirectoryCaseProgress(byte[] sourceId, byte[] rootFileId)
    {
        FolderMaps.RequireBytes(sourceId, 32); FolderMaps.RequireBytes(rootFileId, 16);
        using var connection = database.OpenReader(); return ReadCaseProgress(connection, null, sourceId, rootFileId);
    }
    public DirectoryCaseMemberRow? ReadDirectoryCaseMember(byte[] sourceId, byte[] rootFileId, string memberKey)
    {
        FolderMaps.RequireBytes(sourceId, 32); FolderMaps.RequireBytes(rootFileId, 16);
        using var connection = database.OpenReader(); return ReadCaseMember(connection, null, sourceId, rootFileId, PathRules.Key(memberKey));
    }
    // Engine authenticated the original envelope/projection and pinned all
    // native parents. Only the exact next graph member can acquire an intent.
    public Task ReserveDirectoryCaseMemberAsync(PendingCasePreservation expected, DirectoryCaseMemberRow member, ApplyIntent intent, CancellationToken cancellationToken = default)
    {
        var row = FreezeCaseMember(member); ValidateCaseMember(expected, row); ArgumentNullException.ThrowIfNull(intent);
        if (intent.PathKey != PathRules.Key(row.DestinationPath) || row.InstalledFileId is not null) throw new ArgumentException("Invalid member reservation.", nameof(member));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireDirectoryCaseRoot(connection, transaction, expected, row.RootFileId); RequireNextCaseMember(connection, transaction, row);
            RequireExpectedSequence(connection, transaction, intent); var prior = ReadCaseMember(connection, transaction, row.SourceId, row.RootFileId, row.MemberKey);
            if (prior is not null) RequireSameCaseMember(prior, row);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO directory_case_members(source_id,root_file_id,member_key,record_hash,record_length,member_source_id,type,destination_path,touched_ticks) "
                + "VALUES($id,$root,$key,$hash,$length,$member,$type,$destination,$now) ON CONFLICT(source_id,root_file_id,member_key) DO NOTHING;";
            BindCaseMember(command, row); command.ExecuteNonQuery(); command.Parameters.Clear();
            command.CommandText = "INSERT INTO apply_intents(id,path_key,expected_local_seq,payload) VALUES($intent,$path,$seq,$payload);";
            command.Parameters.AddWithValue("$intent", intent.Id.ToString("N")); command.Parameters.AddWithValue("$path", intent.PathKey);
            command.Parameters.AddWithValue("$seq", (object?)intent.ExpectedLocalSequence ?? DBNull.Value); command.Parameters.AddWithValue("$payload", intent.Payload.ToArray());
            cancellationToken.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); return 0;
        }, cancellationToken);
    }
    public Task AccountDirectoryCaseMemberAsync(PendingCasePreservation expected, DirectoryCaseMemberRow member, ApplyIntent intent, byte[] fileId, CancellationToken cancellationToken = default)
    {
        var row = FreezeCaseMember(member); ValidateCaseMember(expected, row); FolderMaps.RequireBytes(fileId, 16); byte[] native = fileId.ToArray();
        ArgumentNullException.ThrowIfNull(intent); if (intent.PathKey != PathRules.Key(row.DestinationPath)) throw new ArgumentException("Member destination changed.", nameof(intent));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireDirectoryCaseRoot(connection, transaction, expected, row.RootFileId); RequireNextCaseMember(connection, transaction, row);
            RequireSameIntent(ReadIntent(connection, transaction, intent.Id), intent); RequireSameCaseMember(ReadCaseMember(connection, transaction, row.SourceId, row.RootFileId, row.MemberKey), row);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_case_members SET installed_file_id=$file,touched_ticks=$now WHERE source_id=$id AND root_file_id=$root AND member_key=$key;";
            BindCaseMember(command, row); command.Parameters.AddWithValue("$file", native); command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO directory_case_progress(source_id,root_file_id,last_key) VALUES($id,$root,$key) ON CONFLICT(source_id,root_file_id) DO UPDATE SET last_key=excluded.last_key;";
            command.ExecuteNonQuery(); cancellationToken.ThrowIfCancellationRequested(); RemoveIntent(connection, transaction, intent.Id); ClearCaseRetry(connection, transaction, row.SourceId); return 0;
        }, cancellationToken);
    }
    public Task MarkDirectoryCaseMembersExhaustedAsync(PendingCasePreservation expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); byte[] root = expected.InstalledFileId ?? throw new ArgumentException("Root is not installed.", nameof(expected));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireDirectoryCaseRoot(connection, transaction, expected, root); var progress = ReadCaseProgress(connection, transaction, expected.Source.SourceId, root);
            if (NextCaseGraphMember(connection, transaction, expected.Source.SourceId, progress.LastKey) is not null) throw new InvalidOperationException("Members remain uninstalled.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO directory_case_progress(source_id,root_file_id,exhausted) VALUES($id,$root,1) ON CONFLICT(source_id,root_file_id) DO UPDATE SET exhausted=1;";
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); command.Parameters.AddWithValue("$root", root); command.ExecuteNonQuery(); return 0;
        }, cancellationToken);
    }
    // Metadata only: retain the prior tree and all old identity-bound witnesses.
    public Task ResetDirectoryCaseRootAsync(PendingCasePreservation expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); byte[] root = expected.InstalledFileId ?? throw new ArgumentException("Root is not installed.", nameof(expected));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireDirectoryCaseRoot(connection, transaction, expected, root); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM apply_intents);"; if ((long)command.ExecuteScalar()! != 0) throw new IOException("Recover active intents before changing the case root.");
            command.CommandText = "UPDATE case_preservations SET destination_key=NULL,destination_path=NULL,installed_file_id=NULL WHERE source_id=$id;";
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); command.ExecuteNonQuery(); ClearCaseRetry(connection, transaction, expected.Source.SourceId); return 0;
        }, cancellationToken);
    }
    private static void RequireDirectoryCaseRoot(SqliteConnection connection, SqliteTransaction transaction, PendingCasePreservation expected, byte[] root)
    {
        RequirePendingCase(connection, transaction, expected);
        if (expected.DestinationPath is null || expected.InstalledFileId is null || !root.AsSpan().SequenceEqual(expected.InstalledFileId)) throw new InvalidOperationException("Case native root changed.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT destination_path,installed_file_id FROM case_preservations WHERE source_id=$id;"; command.Parameters.AddWithValue("$id", expected.Source.SourceId);
        using (var reader = command.ExecuteReader())
            if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetString(0) != expected.DestinationPath || !reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(root))
                throw new InvalidOperationException("Case destination changed.");
        var graph = FindDirectoryGraph(connection, transaction, expected.Source.SourceId);
        if (graph is null || !graph.Complete || graph.Retiring || !graph.RootId.AsSpan().SequenceEqual(expected.RootId) || PathRules.Key(graph.DirectoryPath) != expected.Source.PathKey)
            throw new InvalidOperationException("Case graph is unavailable or incomplete.");
    }
    private static void RequireNextCaseMember(SqliteConnection connection, SqliteTransaction transaction, DirectoryCaseMemberRow row)
    {
        var progress = ReadCaseProgress(connection, transaction, row.SourceId, row.RootFileId); var next = NextCaseGraphMember(connection, transaction, row.SourceId, progress.LastKey);
        if (progress.Exhausted || next is null || next.PathKey != row.MemberKey || next.RecordLength != row.RecordLength || !next.RecordHash.AsSpan().SequenceEqual(row.RecordHash)) throw new InvalidOperationException("Case cursor or graph member changed.");
    }
    private static DirectoryGraphMember? NextCaseGraphMember(SqliteConnection connection, SqliteTransaction transaction, byte[] id, string? after)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT path_key,record_hash,record_length FROM directory_graph_members WHERE graph_id=$id " + (after is null ? "" : "AND path_key>$after ") + "ORDER BY path_key LIMIT 1;";
        command.Parameters.AddWithValue("$id", id); if (after is not null) command.Parameters.AddWithValue("$after", after);
        using var reader = command.ExecuteReader(); return reader.Read() ? new(reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetInt32(2)) : null;
    }
    private static DirectoryCaseProgress ReadCaseProgress(SqliteConnection connection, SqliteTransaction? transaction, byte[] id, byte[] root)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT last_key,exhausted FROM directory_case_progress WHERE source_id=$id AND root_file_id=$root;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$root", root); using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetBoolean(1)) : new(null, false);
    }
    private static DirectoryCaseMemberRow? ReadCaseMember(SqliteConnection connection, SqliteTransaction? transaction, byte[] id, byte[] root, string key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT record_hash,record_length,member_source_id,type,destination_path,installed_file_id,touched_ticks FROM directory_case_members WHERE source_id=$id AND root_file_id=$root AND member_key=$key;";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$key", key); using var reader = command.ExecuteReader();
        return reader.Read() ? new(id.ToArray(), root.ToArray(), key, reader.GetFieldValue<byte[]>(0), reader.GetInt32(1), reader.GetFieldValue<byte[]>(2), (uint)reader.GetInt64(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5), reader.GetInt64(6)) : null;
    }
    private static void ValidateCaseMember(PendingCasePreservation expected, DirectoryCaseMemberRow row)
    {
        ArgumentNullException.ThrowIfNull(expected); FolderMaps.RequireBytes(row.SourceId, 32); FolderMaps.RequireBytes(row.RootFileId, 16); FolderMaps.RequireBytes(row.RecordHash, 32); FolderMaps.RequireBytes(row.MemberSourceId, 32);
        if (row.InstalledFileId is not null) FolderMaps.RequireBytes(row.InstalledFileId, 16); RequireStagingNow(row.TouchedTicks);
        if (!row.SourceId.AsSpan().SequenceEqual(expected.Source.SourceId) || PathRules.Key(row.MemberKey) != row.MemberKey || !row.MemberKey.StartsWith(expected.Source.PathKey + '/', StringComparison.Ordinal)
            || expected.DestinationPath is null || !PathRules.Key(row.DestinationPath).StartsWith(PathRules.Key(expected.DestinationPath) + '/', StringComparison.Ordinal)
            || row.RecordLength is < 48 or > 624 || (row.RecordLength - 12) % 36 != 0 || row.Type > 1) throw new ArgumentException("Invalid case member.", nameof(row));
    }
    private static DirectoryCaseMemberRow FreezeCaseMember(DirectoryCaseMemberRow row)
    {
        ArgumentNullException.ThrowIfNull(row); return row with { SourceId = row.SourceId.ToArray(), RootFileId = row.RootFileId.ToArray(), RecordHash = row.RecordHash.ToArray(), MemberSourceId = row.MemberSourceId.ToArray(), InstalledFileId = row.InstalledFileId?.ToArray() };
    }
    private static void RequireSameCaseMember(DirectoryCaseMemberRow? actual, DirectoryCaseMemberRow row)
    {
        if (actual is null || actual.MemberKey != row.MemberKey || actual.Type != row.Type || actual.DestinationPath != row.DestinationPath || actual.RecordLength != row.RecordLength
            || !actual.SourceId.AsSpan().SequenceEqual(row.SourceId) || !actual.RootFileId.AsSpan().SequenceEqual(row.RootFileId) || !actual.RecordHash.AsSpan().SequenceEqual(row.RecordHash)
            || !actual.MemberSourceId.AsSpan().SequenceEqual(row.MemberSourceId) || actual.InstalledFileId is not null || row.InstalledFileId is not null) throw new InvalidOperationException("Case member changed.");
    }
    private static void BindCaseMember(SqliteCommand command, DirectoryCaseMemberRow row)
    {
        command.Parameters.AddWithValue("$id", row.SourceId); command.Parameters.AddWithValue("$root", row.RootFileId); command.Parameters.AddWithValue("$key", row.MemberKey); command.Parameters.AddWithValue("$hash", row.RecordHash);
        command.Parameters.AddWithValue("$length", row.RecordLength); command.Parameters.AddWithValue("$member", row.MemberSourceId); command.Parameters.AddWithValue("$type", row.Type); command.Parameters.AddWithValue("$destination", row.DestinationPath); command.Parameters.AddWithValue("$now", row.TouchedTicks);
    }
}
