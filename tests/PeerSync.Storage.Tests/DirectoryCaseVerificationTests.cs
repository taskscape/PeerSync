using Microsoft.Data.Sqlite;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed partial class CasePreservationTests
{
    [Fact]
    public async Task VerificationRequiresExactCoverageAndReadonlyRetentionOutlivesTheGenericCompactor()
    {
        await using var index = await Open(); var pending = await VerificationFixture(index, false); long now = 638000000000000000;
        var proof = await index.BeginDirectoryCaseVerificationAsync(pending, index.LocalFiles.Find(pending.DestinationPath!)!.LocalSequence, false, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.AdvanceDirectoryCaseVerificationAsync(pending, proof, [], true, Token));
        var row = index.LocalFiles.Find(pending.DestinationPath + "/a")!;
        var complete = await index.AdvanceDirectoryCaseVerificationAsync(pending, proof, [new("FILE/A", row.LocalSequence, row.FileId!)], true, Token);
        complete.Complete.ShouldBeTrue(); await index.CompleteDirectoryCasePreservationAsync(pending, complete, now, now + 2 * TimeSpan.TicksPerDay, Token);
        index.HasPendingCasePreservation("file").ShouldBeFalse(); index.ReadRetainedDirectoryCases(Root, now + TimeSpan.TicksPerDay).Single().Record.Body.ToArray().ShouldBe(new byte[] { 1 });
        (await index.CompactCompletedCaseSourcesAsync(Root, now + TimeSpan.TicksPerDay, cancellationToken: Token)).ShouldBe(0);
        index.ReadRetainedDirectoryCases(Root, now + 3 * TimeSpan.TicksPerDay).ShouldBeEmpty();
        (await index.CompactCompletedCaseSourcesAsync(Root, now + 3 * TimeSpan.TicksPerDay, cancellationToken: Token)).ShouldBe(1);
        index.LocalFiles.Find(pending.DestinationPath + "/a").ShouldNotBeNull();
    }
    [Fact]
    public async Task LocalEpochAndAuthorityModeChangesResetVerificationInsteadOfReusingAStalePrefix()
    {
        await using var index = await Open(); var pending = await VerificationFixture(index, true); long sequence = index.LocalFiles.Find(pending.DestinationPath!)!.LocalSequence;
        var proof = await index.BeginDirectoryCaseVerificationAsync(pending, sequence, true, Token); var child = index.LocalFiles.Find(pending.DestinationPath + "/a")!;
        await index.LocalFiles.UpsertAsync([child with { MtimeTicks = child.MtimeTicks + 1 }], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.AdvanceDirectoryCaseVerificationAsync(pending, proof, [new("FILE/A", child.LocalSequence, child.FileId!)], true, Token));
        var reset = await index.BeginDirectoryCaseVerificationAsync(pending, sequence, false, Token); reset.LastKey.ShouldBeNull(); reset.RequirePublication.ShouldBeFalse(); reset.Epoch.ShouldBeGreaterThan(proof.Epoch);
        child = index.LocalFiles.Find(child.Path)!; var complete = await index.AdvanceDirectoryCaseVerificationAsync(pending, reset, [new("FILE/A", child.LocalSequence, child.FileId!)], true, Token);
        var write = await index.BeginDirectoryCaseVerificationAsync(pending, sequence, true, Token); write.Complete.ShouldBeFalse(); write.LastKey.ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => index.CompleteDirectoryCasePreservationAsync(pending, complete, 638000000000000000, 638000000000000001, Token));
    }
    [Fact]
    public async Task FinalCaseReleaseFailureRetainsTheBarrierVerificationAndOriginalEnvelope()
    {
        await using var index = await Open(); var pending = await VerificationFixture(index, true); var child = index.LocalFiles.Find(pending.DestinationPath + "/a")!;
        var proof = await index.BeginDirectoryCaseVerificationAsync(pending, index.LocalFiles.Find(pending.DestinationPath!)!.LocalSequence, true, Token);
        proof = await index.AdvanceDirectoryCaseVerificationAsync(pending, proof, [new("FILE/A", child.LocalSequence, child.FileId!)], true, Token);
        MemberSql("CREATE TRIGGER fail_case_complete BEFORE UPDATE OF completed ON case_preservations BEGIN SELECT RAISE(ABORT,'case completion failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.CompleteDirectoryCasePreservationAsync(pending, proof, 638000000000000000, null, Token));
        index.HasPendingCasePreservation(pending.DestinationPath + "/a").ShouldBeTrue(); index.ReadPendingCasePreservations().Single().Source.Record.Body.ToArray().ShouldBe(new byte[] { 1 });
        MemberSql("DROP TRIGGER fail_case_complete;"); await index.CompleteDirectoryCasePreservationAsync(pending, proof, 638000000000000000, null, Token);
        index.HasPendingCasePreservation(pending.DestinationPath + "/a").ShouldBeFalse();
    }
    private static async Task<PendingCasePreservation> VerificationFixture(FolderIndex index, bool rw)
    {
        var pending = await InstalledDirectoryRoot(index); var row = CaseMember(pending); var intent = new ApplyIntent(Guid.NewGuid(), row.DestinationPath, null, [9]);
        await index.ReserveDirectoryCaseMemberAsync(pending, row, intent, Token); await index.AccountDirectoryCaseMemberAsync(pending, row, intent, Id(8)[..16], Token); await index.MarkDirectoryCaseMembersExhaustedAsync(pending, Token);
        await index.LocalFiles.UpsertAsync([
            new(pending.DestinationPath!, 1, 0, row.TouchedTicks, 16, false, row.TouchedTicks, null, new Dictionary<ulong, ulong>(), rw ? Record(1) : null, pending.InstalledFileId),
            new(row.DestinationPath, 0, 0, row.TouchedTicks, 0, false, row.TouchedTicks, new byte[32], new Dictionary<ulong, ulong>(), rw ? Record(1) : null, Id(8)[..16]) { ContentReady = rw }], Token);
        return pending;
    }
}
