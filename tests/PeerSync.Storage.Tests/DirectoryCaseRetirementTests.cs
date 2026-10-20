using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed partial class CasePreservationTests
{
    [Fact]
    public async Task WitnessRetirementKeepsCurrentPendingRootsAndBoundsSupersededRowsAcrossReopen()
    {
        PendingCasePreservation pending; long now = 638000000000000000 + 2 * TimeSpan.TicksPerDay;
        await using (var index = await Open())
        {
            pending = await VerificationFixture(index, false);
            (await index.RetireDirectoryCaseWitnessesAsync(Root, now, cancellationToken: Token)).ShouldBe(0);
            await index.ResetDirectoryCaseRootAsync(pending, Token);
            (await index.RetireDirectoryCaseWitnessesAsync(Root, now, 1, Token)).ShouldBe(1);
            index.ReadDirectoryCaseMember(pending.Source.SourceId, pending.InstalledFileId!, "FILE/A").ShouldBeNull();
            index.ReadDirectoryCaseProgress(pending.Source.SourceId, pending.InstalledFileId!).Exhausted.ShouldBeTrue();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        (await reopened.RetireDirectoryCaseWitnessesAsync(Root, now, 1, Token)).ShouldBe(1);
        reopened.ReadDirectoryCaseProgress(pending.Source.SourceId, pending.InstalledFileId!).Exhausted.ShouldBeFalse();
        reopened.ReadPendingCasePreservations().Count.ShouldBe(1); reopened.LocalFiles.Find(pending.DestinationPath + "/a").ShouldNotBeNull();
        (await reopened.RetireDirectoryCaseWitnessesAsync(Root, now, 1, Token)).ShouldBe(0);
    }

    [Fact]
    public async Task CompletedWitnessRetirementWaitsADayAndDoesNotReleaseReadonlySourceRetention()
    {
        await using var index = await Open(); var pending = await VerificationFixture(index, false); long now = 638000000000000000;
        var child = index.LocalFiles.Find(pending.DestinationPath + "/a")!;
        var proof = await index.BeginDirectoryCaseVerificationAsync(pending, index.LocalFiles.Find(pending.DestinationPath!)!.LocalSequence, false, Token);
        proof = await index.AdvanceDirectoryCaseVerificationAsync(pending, proof, [new("FILE/A", child.LocalSequence, child.FileId!)], true, Token);
        await index.CompleteDirectoryCasePreservationAsync(pending, proof, now, now + 30 * TimeSpan.TicksPerDay, Token);
        (await index.RetireDirectoryCaseWitnessesAsync(Root, now + TimeSpan.TicksPerDay - 1, cancellationToken: Token)).ShouldBe(0);
        (await index.RetireDirectoryCaseWitnessesAsync(Root, now + TimeSpan.TicksPerDay, cancellationToken: Token)).ShouldBe(2);
        index.ReadRetainedDirectoryCases(Root, now + 2 * TimeSpan.TicksPerDay).Single().SourceId.ShouldBe(pending.Source.SourceId);
        index.LocalFiles.Find(child.Path)!.FileId.ShouldBe(child.FileId);
    }

    [Fact]
    public async Task RecoveryIntentsAndRecentMembersProtectObsoleteWitnesses()
    {
        await using var index = await Open(); var pending = await VerificationFixture(index, false); long now = 638000000000000000 + 2 * TimeSpan.TicksPerDay;
        await index.ResetDirectoryCaseRootAsync(pending, Token);
        var intent = new ApplyIntent(Guid.NewGuid(), "unrelated", null, [1]); await index.ReserveApplyAsync(intent, Token);
        (await index.RetireDirectoryCaseWitnessesAsync(Root, now, cancellationToken: Token)).ShouldBe(0);
        await index.AbandonApplyAsync(intent, Token);
        MemberSql("UPDATE directory_case_members SET touched_ticks=" + now.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";");
        (await index.RetireDirectoryCaseWitnessesAsync(Root, now, cancellationToken: Token)).ShouldBe(0);
        (await index.RetireDirectoryCaseWitnessesAsync(Root, now + TimeSpan.TicksPerDay, cancellationToken: Token)).ShouldBe(2);
    }

    [Fact]
    public async Task WitnessRetirementFailureRollsBackBothTablesAndRejectsWrongRootAndUnboundedWork()
    {
        await using var index = await Open(); var pending = await VerificationFixture(index, false); long now = 638000000000000000 + 2 * TimeSpan.TicksPerDay;
        await index.ResetDirectoryCaseRootAsync(pending, Token);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => index.RetireDirectoryCaseWitnessesAsync(Root, now, 1001, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => index.RetireDirectoryCaseWitnessesAsync([1, .. new byte[23]], now, cancellationToken: Token));
        MemberSql("CREATE TRIGGER fail_witness_retirement BEFORE DELETE ON directory_case_progress BEGIN SELECT RAISE(ABORT,'retirement failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.RetireDirectoryCaseWitnessesAsync(Root, now, cancellationToken: Token));
        index.ReadDirectoryCaseMember(pending.Source.SourceId, pending.InstalledFileId!, "FILE/A").ShouldNotBeNull();
        index.ReadDirectoryCaseProgress(pending.Source.SourceId, pending.InstalledFileId!).Exhausted.ShouldBeTrue();
        MemberSql("DROP TRIGGER fail_witness_retirement;");
        (await index.RetireDirectoryCaseWitnessesAsync(Root, now, cancellationToken: Token)).ShouldBe(2);
    }
}
