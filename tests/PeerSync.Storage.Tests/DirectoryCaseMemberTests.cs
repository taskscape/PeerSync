using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed partial class CasePreservationTests
{
    [Fact]
    public async Task MemberIntentAndCursorSurviveReopenAndInstallationDoesNotCompleteTheSource()
    {
        DirectoryCaseMemberRow row; ApplyIntent intent;
        await using (var index = await Open())
        {
            var pending = await InstalledDirectoryRoot(index); row = CaseMember(pending); intent = new(Guid.NewGuid(), row.DestinationPath, null, [9]);
            await index.ReserveDirectoryCaseMemberAsync(pending, row, intent, Token);
            index.ReadDirectoryCaseProgress(row.SourceId, row.RootFileId).LastKey.ShouldBeNull();
            index.ReadDirectoryCaseMember(row.SourceId, row.RootFileId, row.MemberKey)!.InstalledFileId.ShouldBeNull();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var source = reopened.ReadPendingCasePreservations().Single();
        var frozen = reopened.ReadDirectoryCaseMember(row.SourceId, row.RootFileId, row.MemberKey)!; frozen.RecordHash[0] = 99;
        reopened.ReadDirectoryCaseMember(row.SourceId, row.RootFileId, row.MemberKey)!.RecordHash.ShouldBe(Id(3));
        await reopened.AccountDirectoryCaseMemberAsync(source, row, intent, Id(8)[..16], Token);
        reopened.ReadApplyIntents().ShouldBeEmpty(); reopened.ReadDirectoryCaseProgress(row.SourceId, row.RootFileId).LastKey.ShouldBe("FILE/A");
        await reopened.MarkDirectoryCaseMembersExhaustedAsync(source, Token); reopened.ReadDirectoryCaseProgress(row.SourceId, row.RootFileId).Exhausted.ShouldBeTrue();
        reopened.HasPendingCasePreservation("file").ShouldBeTrue(); reopened.ReadIndexHighWater().ShouldBe(0);
    }
    [Theory]
    [InlineData("row")]
    [InlineData("cursor")]
    [InlineData("intent")]
    public async Task MemberAccountingFailureRollsBackIdentityCursorAndIntentTogether(string boundary)
    {
        await using var index = await Open(); var pending = await InstalledDirectoryRoot(index); var row = CaseMember(pending); var intent = new ApplyIntent(Guid.NewGuid(), row.DestinationPath, null, [9]);
        await index.ReserveDirectoryCaseMemberAsync(pending, row, intent, Token);
        string trigger = boundary switch { "row" => "BEFORE UPDATE ON directory_case_members", "cursor" => "BEFORE INSERT ON directory_case_progress", _ => "BEFORE DELETE ON apply_intents" };
        MemberSql("CREATE TRIGGER fail_member " + trigger + " BEGIN SELECT RAISE(ABORT,'member failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.AccountDirectoryCaseMemberAsync(pending, row, intent, Id(8)[..16], Token));
        index.ReadDirectoryCaseMember(row.SourceId, row.RootFileId, row.MemberKey)!.InstalledFileId.ShouldBeNull(); index.ReadDirectoryCaseProgress(row.SourceId, row.RootFileId).LastKey.ShouldBeNull();
        index.ReadApplyIntents().Single().Id.ShouldBe(intent.Id); MemberSql("DROP TRIGGER fail_member;");
        await index.AccountDirectoryCaseMemberAsync(pending, row, intent, Id(8)[..16], Token); index.ReadApplyIntents().ShouldBeEmpty();
    }
    [Fact]
    public async Task MemberBindingRejectsCursorSkipsIncompleteCoverageAndResetWithActiveIntent()
    {
        await using var index = await Open(); var pending = await InstalledDirectoryRoot(index); var row = CaseMember(pending);
        await Should.ThrowAsync<InvalidOperationException>(() => index.MarkDirectoryCaseMembersExhaustedAsync(pending, Token));
        var wrong = row with { MemberKey = "FILE/Z" }; await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveDirectoryCaseMemberAsync(pending, wrong, new(Guid.NewGuid(), row.DestinationPath, null, [9]), Token));
        var intent = new ApplyIntent(Guid.NewGuid(), row.DestinationPath, null, [9]); await index.ReserveDirectoryCaseMemberAsync(pending, row, intent, Token);
        await Should.ThrowAsync<IOException>(() => index.ResetDirectoryCaseRootAsync(pending, Token)); index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBe(pending.InstalledFileId);
        await index.AbandonApplyAsync(intent, Token); await index.ResetDirectoryCaseRootAsync(pending, Token);
        index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBeNull(); index.ReadDirectoryCaseMember(row.SourceId, row.RootFileId, row.MemberKey).ShouldNotBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveDirectoryCaseMemberAsync(pending, row, intent, Token));
    }
    private static async Task<PendingCasePreservation> InstalledDirectoryRoot(FolderIndex index)
    {
        await Receive(index, [Source(1)]); var pending = index.ReadPendingCasePreservations().Single();
        await index.BeginDirectoryGraphAsync(Id(1), Root, "file", Id(2), 12, Token); var rootWork = index.ReadDirectoryGraphWork(Id(1))!;
        await index.CompleteDirectoryGraphWorkAsync(Id(1), rootWork.Id, [new("file", Id(3), 48, DirectoryGraphWorkKind.Record, 0)], cancellationToken: Token);
        var member = index.ReadDirectoryGraphWork(Id(1))!; await index.CompleteDirectoryGraphWorkAsync(Id(1), member.Id, [], new("FILE/A", Id(3), 48), Token);
        var intent = new ApplyIntent(Guid.NewGuid(), "file.case-conflict", null, [8]); await index.ReserveCaseCopyAsync(pending, intent, "file.case-conflict", Token);
        await index.AccountInstalledCaseCopyAsync(pending, intent, "file.case-conflict", Id(6)[..16], Token); return index.ReadPendingCasePreservations().Single();
    }
    private static DirectoryCaseMemberRow CaseMember(PendingCasePreservation pending) => new(pending.Source.SourceId, pending.InstalledFileId!, "FILE/A", Id(3), 48, Id(7), 0,
        pending.DestinationPath + "/a", null, 638000000000000000);
    private void MemberSql(string sql)
    { using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
}
