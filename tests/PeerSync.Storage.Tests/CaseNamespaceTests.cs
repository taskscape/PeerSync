using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed partial class CasePreservationTests
{
    [Fact]
    public async Task PendingSourceAndCopyProtectTheirWholeNamespacesWhileCoverageReleasesOnlyPublication()
    {
        await using var index = await Open(); var pending = await InstalledDirectoryRoot(index); string copy = pending.DestinationPath!;
        index.HasPendingCasePreservation("file/child/deep").ShouldBeTrue(); index.HasPendingCasePreservation(copy + "/a").ShouldBeTrue();
        index.HasPendingCasePreservation(copy + "-unrelated/a").ShouldBeFalse(); index.HasPendingCasePreservation("filex/a").ShouldBeFalse();
        index.HasIncompleteDirectoryCaseCopy(copy).ShouldBeTrue(); index.HasIncompleteDirectoryCaseCopy(copy + "/nested").ShouldBeTrue();
        index.HasIncompleteDirectoryCaseCopy("file").ShouldBeFalse();
        var row = CaseMember(pending); var intent = new ApplyIntent(Guid.NewGuid(), row.DestinationPath, null, [9]);
        await index.ReserveDirectoryCaseMemberAsync(pending, row, intent, Token); await index.AccountDirectoryCaseMemberAsync(pending, row, intent, Id(8)[..16], Token);
        await index.MarkDirectoryCaseMembersExhaustedAsync(pending, Token);
        index.HasIncompleteDirectoryCaseCopy(copy).ShouldBeFalse(); index.HasIncompleteDirectoryCaseCopy(copy + "/a").ShouldBeFalse();
        index.HasPendingCasePreservation(copy + "/a").ShouldBeTrue(); index.HasPendingCasePreservation("file/child").ShouldBeTrue();
    }
    [Fact]
    public async Task NamespaceChecksUseLiteralNamesExactAncestorsAndMissingAncestorCreationPolicy()
    {
        await using var index = await Open(); const string original = "parent/literal%_"; const string copy = "parent/copy%_";
        var source = new CasePreservationSource(Id(9), Device, original, Record(1));
        await index.RecomputeNeededAsync([original], snapshot => Needed(snapshot.PathKey, [source]), Token);
        var pending = index.ReadPendingCasePreservations().Single(); var intent = new ApplyIntent(Guid.NewGuid(), copy, null, [8]);
        await index.ReserveCaseCopyAsync(pending, intent, copy, Token);
        index.HasPendingCasePreservation("parent").ShouldBeTrue(); index.HasPendingCasePreservation("parent", includeDescendants: false).ShouldBeFalse();
        index.HasPendingCasePreservation(copy + "/child", includeDescendants: false).ShouldBeTrue(); index.HasPendingCasePreservation(original + "/child", false).ShouldBeTrue();
        index.HasPendingCasePreservation("parent/copyAB/child").ShouldBeFalse(); index.HasPendingCasePreservation("parent/literalAB/child").ShouldBeFalse();
        string deep = copy + '/' + string.Join('/', Enumerable.Repeat("x", 1800)); index.HasPendingCasePreservation(deep, false).ShouldBeTrue();
    }
    [Fact]
    public async Task FullOrdinaryApplyCommitRechecksTheCopyNamespaceAndKeepsItsIntentOnFailure()
    {
        await using var index = await Open(); var pending = await InstalledDirectoryRoot(index); string path = pending.DestinationPath + "/a";
        var intent = new ApplyIntent(Guid.NewGuid(), path, null, [1]); await index.ReserveApplyAsync(intent, Token);
        var row = new LocalFileRow(path, 0, 0, 638000000000000000, 0, true, 638000000000000000, null, new Dictionary<ulong, ulong> { [1] = 1 }, Record(1), null);
        await Should.ThrowAsync<IOException>(() => index.CommitApplyAsync(intent, row, [], _ => null, Token));
        index.ReadApplyIntents().Single().Id.ShouldBe(intent.Id); index.LocalFiles.Find(path).ShouldBeNull(); index.ReadIndexHighWater().ShouldBe(0);
    }
}
