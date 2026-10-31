using Google.Protobuf;
using PeerSync.Core.Ignore;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Fact]
    public async Task ScannerObservationInventoryKeepsUtf16OrdinalChildOrder()
    {
        Directory.CreateDirectory(Path.Combine(Root, "topic"));
        File.WriteAllText(Path.Combine(Root, "topic", "\uE000"), "one");
        File.WriteAllText(Path.Combine(Root, "topic", "\U0001F600"), "two");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        (await PreviewScan(index)).Problems.ShouldBeEmpty();
        index.ReadDirectoryPublication("topic")!.ExpectedChildren.ShouldBe(2);
        (await Worker(index).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
        (await PreviewScan(index)).Updated.ShouldBe(0);
        index.ReadDirectoryPublication("topic").ShouldBeNull();
    }

    [Fact]
    public async Task LocalOnlyLeafCacheSurvivesUnrelatedPublicationButDetectsChangedSignedChild()
    {
        Directory.CreateDirectory(Path.Combine(Root, "alpha"));
        Directory.CreateDirectory(Path.Combine(Root, "beta"));
        File.WriteAllText(Path.Combine(Root, "alpha", "file"), "first");
        File.WriteAllText(Path.Combine(Root, "beta", "file"), "second");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        (await PreviewScan(index)).Problems.ShouldBeEmpty();
        (await Worker(index).RunAsync(token: Token)).ChangedPaths.ShouldContain("alpha");
        index.ReadDirectoryPublicationCache("alpha")!.InputEpoch.ShouldBeLessThan(index.ReadDirectoryCaptureEpoch());
        long high = index.ReadIndexHighWater();

        string logs = Path.Combine(fixture, "leaf-cache-logs");
        using (var journal = new PeerSync.Core.Diagnostics.DiagnosticJournal(logs, "leaf-cache", debugEnabled: true))
        using (var scanner = new Scanner(Root, index, keys, device, journal: journal) { StageDirectorySnapshots = true })
            (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
        index.ReadDirectoryPublication("alpha").ShouldBeNull();
        index.ReadDirectoryPublication("beta").ShouldBeNull();
        index.ReadIndexHighWater().ShouldBe(high);
        string events = File.ReadAllText(Path.Combine(logs, "debug.jsonl"));
        events.ShouldContain("directory.signed_leaf_cache_hit");
        events.ShouldNotContain(Root);
        events.ShouldNotContain("alpha/file");

        File.WriteAllText(Path.Combine(Root, "alpha", "file"), "changed");
        (await PreviewScan(index)).Updated.ShouldBe(1);
        index.ReadDirectoryPublication("alpha").ShouldNotBeNull();
        index.ReadDirectoryPublication("beta").ShouldBeNull();
    }

    [Fact]
    public async Task RemoteStateKeepsStaleLeafCacheOnConservativePublicationPath()
    {
        Directory.CreateDirectory(Path.Combine(Root, "alpha"));
        Directory.CreateDirectory(Path.Combine(Root, "beta"));
        File.WriteAllText(Path.Combine(Root, "alpha", "file"), "first");
        File.WriteAllText(Path.Combine(Root, "beta", "file"), "second");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await PreviewScan(index); await Worker(index).RunAsync(token: Token);
        index.ReadDirectoryPublicationCache("alpha")!.InputEpoch.ShouldBeLessThan(index.ReadDirectoryCaptureEpoch());
        var existing = index.LocalFiles.Find("beta/file")!;
        byte[] remote = new byte[32]; remote[0] = 77;
        await index.RemoteFiles.UpsertAsync([new(remote, existing.Path, existing.Vector, existing.Record!)], Token);

        await PreviewScan(index);
        index.ReadDirectoryPublication("alpha").ShouldNotBeNull();
    }

    [Fact]
    public async Task ScannerPreviewPublishesFilesFirstAndNestedSnapshotsConvergeWithoutMtimeFeedback()
    {
        Directory.CreateDirectory(Path.Combine(Root, "topic", "nested")); File.WriteAllText(Path.Combine(Root, "topic", "nested", "a"), "bytes");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        (await PreviewScan(index)).Problems.ShouldBeEmpty(); index.LocalFiles.Find("topic").ShouldBeNull(); index.LocalFiles.Find("topic/nested").ShouldBeNull();
        index.LocalFiles.Find("topic/nested/a")!.Record.ShouldNotBeNull();
        var staged = index.ReadDirectoryPublication("topic")!; staged.ExpectedChildren.ShouldBe(1);
        VersionView.Open(staged.Template, keys, device).ExportBody().DirectorySnapshotHash.Length.ShouldBe(0);
        (await Worker(index).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic/nested", "topic"]);
        var parent = index.LocalFiles.Find("topic")!; var child = index.LocalFiles.Find("topic/nested")!; long high = index.ReadIndexHighWater();
        // The global epoch can require one no-op revalidation of earlier child
        // caches after parent publication. It must never publish another version.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var scan = await PreviewScan(index); scan.Updated.ShouldBe(0); (await Worker(index).RunAsync(token: Token)).ChangedPaths.ShouldBeEmpty();
        }
        index.HasPendingDirectoryPublications().ShouldBeFalse(); index.ReadIndexHighWater().ShouldBe(high);
        File.WriteAllText(Path.Combine(Root, "topic", "nested", "a"), "changed bytes");
        (await PreviewScan(index)).Updated.ShouldBe(1);
        index.LocalFiles.Find("topic")!.LocalSequence.ShouldBe(parent.LocalSequence); index.LocalFiles.Find("topic/nested")!.LocalSequence.ShouldBe(child.LocalSequence);
        var content = await Worker(index).RunAsync(token: Token); content.ChangedPaths.ShouldBe(["topic/nested", "topic"]);
        index.LocalFiles.Find("topic")!.MtimeTicks.ShouldBe(parent.MtimeTicks); index.LocalFiles.Find("topic/nested")!.MtimeTicks.ShouldBe(child.MtimeTicks);
    }
    [Fact]
    public async Task ScannerPreviewRestagingRetainsPrivateVersionTimeAndVectorUntilInputChanges()
    {
        WriteFiles("a", "b"); await using var index = await FolderIndex.OpenAsync(Database, Token); await PreviewScan(index);
        var first = index.ReadDirectoryPublication("topic")!; await PreviewScan(index); var same = index.ReadDirectoryPublication("topic")!;
        same.Version.ShouldBe(first.Version); same.Template.Body.ToArray().ShouldBe(first.Template.Body.ToArray());
        (await Worker(index).RunAsync(1, token: Token)).More.ShouldBeTrue(); index.LocalFiles.Find("topic").ShouldBeNull();
        WriteFiles("c"); await PreviewScan(index); var changed = index.ReadDirectoryPublication("topic")!;
        changed.Version.ShouldNotBe(first.Version); changed.ExpectedChildren.ShouldBe(3);
        (await Worker(index).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]); index.ReadDirectoryPublicationCache("topic")!.MemberCount.ShouldBe(3);
    }
    [Fact]
    public async Task CompleteWalkReconcilesDeletedDirectoryQueueAndDraftWithoutDeletingAnyOtherBytes()
    {
        WriteFiles("a", "b"); await using var index = await FolderIndex.OpenAsync(Database, Token); await PreviewScan(index);
        await Worker(index).RunAsync(1, token: Token); index.HasDirectoryCaptures().ShouldBeTrue();
        // An external rename preserves these user bytes outside the old name.
        Directory.Move(Path.Combine(Root, "topic"), Path.Combine(Root, "renamed"));
        await PreviewScan(index); index.ReadDirectoryPublication("topic").ShouldBeNull(); index.ReadDirectoryCapture("topic").ShouldBeNull();
        File.Exists(Path.Combine(Root, "renamed", "a")).ShouldBeTrue(); File.Exists(Path.Combine(Root, "renamed", "b")).ShouldBeTrue();
        index.ReadDirectoryPublication("renamed").ShouldNotBeNull(); (await Worker(index).RunAsync(token: Token)).ChangedPaths.ShouldBe(["renamed"]);
    }
    [Fact]
    public async Task ExclusionRemovesObsoletePrivateQueueButKeepsBytesAndReadonlyPreviewDoesNotStage()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); await PreviewScan(index);
        using (var excluded = new Scanner(Root, index, keys, device, ignore: new IgnoreMatcher("topic/")) { StageDirectorySnapshots = true })
            await excluded.ScanAsync(Token);
        index.HasPendingDirectoryPublications().ShouldBeFalse(); File.Exists(Path.Combine(Root, "topic", "a")).ShouldBeTrue();
        using var access = FolderKeys.FromText(keys.ExportReadOnlyText()); await PreviewScan(index, access);
        index.HasPendingDirectoryPublications().ShouldBeFalse(); index.LocalFiles.Find("topic")!.Record.ShouldBeNull();
    }
    [Fact]
    public async Task UncachedIncomingSnapshotIsNeverRestagedAsAnEmptyLocallyAuthoredDirectory()
    {
        await using var index = await Open(); var draft = await AdvanceIndex(index, 1000); var row = Publication(index, draft);
        await QueueForWorker(index, "topic"); index.HasPendingDirectoryPublications().ShouldBeTrue();
        var body = VersionView.Open(row.Record!, keys, device).ExportBody(); body.ModifiedBy = ByteString.CopyFrom([2, .. new byte[31]]);
        body.Vv.Clear(); body.Vv.Add(new PeerSync.Protocol.Records.VvEntry { Device = 2, Counter = 1 });
        await index.LocalFiles.UpsertAsync([row with { Record = SignedRecordFactory.Create(body, keys), Vector = new Dictionary<ulong, ulong> { [2] = 1 } }], Token);
        var previous = index.LocalFiles.Find("topic")!; await PreviewScan(index);
        var reconstruction = index.ReadDirectoryPublication("topic")!; reconstruction.ReconstructionOnly.ShouldBeTrue();
        reconstruction.Template.Body.ToArray().ShouldBe(previous.Record!.Body.ToArray());
        index.HasBlockingDirectoryPublication("topic/late-child", true).ShouldBeFalse();
        index.LocalFiles.Find("topic")!.LocalSequence.ShouldBe(previous.LocalSequence);
    }
    private async Task<ScanResult> PreviewScan(FolderIndex index, FolderKeys? access = null)
    {
        using var scanner = new Scanner(Root, index, access ?? keys, device) { StageDirectorySnapshots = true };
        return await scanner.ScanAsync(Token);
    }

    [Fact]
    public async Task IncompleteWalkPausesTheExactQueueAndAFreshCompleteWalkResumesItWithCorrelatedLogs()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var journal = new PeerSync.Core.Diagnostics.DiagnosticJournal(Path.Combine(fixture, "logs"), "scanner-preview", debugEnabled: true);
        using var scanner = new Scanner(Root, index, keys, device, journal: journal) { StageDirectorySnapshots = true };
        await scanner.ScanAsync(Token); var before = index.ReadDirectoryPublication("topic")!;
        Directory.SetLastWriteTimeUtc(Path.Combine(Root, "topic"), DateTime.UtcNow.AddHours(1));
        int enumeration = 0;
        scanner.Checkpoint = stage => { if (stage == "before-enumerate" && ++enumeration == 2) throw new System.ComponentModel.Win32Exception(5); };
        var incomplete = await scanner.ScanAsync(Token); incomplete.Problems.Single().Code.ShouldBe("enumeration-failed");
        var paused = index.ReadDirectoryPublication("topic")!; paused.Version.ShouldBe(before.Version); paused.Paused.ShouldBeTrue();
        (await Worker(index).RunAsync(token: Token)).Visited.ShouldBe(0); index.LocalFiles.Find("topic").ShouldBeNull();
        scanner.Checkpoint = null; await scanner.ScanAsync(Token); var resumed = index.ReadDirectoryPublication("topic")!;
        resumed.ExpectedLocalSequence.ShouldBe(before.ExpectedLocalSequence); resumed.NativeId.ShouldBe(before.NativeId); resumed.Attributes.ShouldBe(before.Attributes);
        resumed.ExpectedKeysHash.ShouldBe(before.ExpectedKeysHash); resumed.ExpectedChildren.ShouldBe(before.ExpectedChildren);
        VersionView.Open(resumed.Template, keys, device).MtimeTicks.ShouldBe(VersionView.Open(before.Template, keys, device).MtimeTicks);
        resumed.Version.ShouldBe(before.Version); resumed.Paused.ShouldBeFalse(); (await Worker(index).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
        journal.DroppedEvents.ShouldBe(0); string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("directory.queue_staged"); log.ShouldContain("directory.queue_paused"); log.ShouldContain("directory.queue_retained");
        log.ShouldContain(before.Version.ToString()); log.ShouldNotContain(Root); log.ShouldNotContain("topic");
    }
}
