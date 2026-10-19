using Google.Protobuf;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Fact]
    public async Task WorkerResumesBoundedCaptureAfterReopenWithoutAdvertisingAnIntermediateRoot()
    {
        WriteFiles("a", "b", "c"); long prior;
        await using (var index = await Open())
        {
            prior = index.LocalFiles.Find("topic")!.LocalSequence; await QueueForWorker(index, "topic");
            var pass = await Worker(index).RunAsync(1, token: Token);
            pass.ChangedPaths.ShouldBeEmpty(); pass.More.ShouldBeTrue(); index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(1);
            index.LocalFiles.Find("topic")!.LocalSequence.ShouldBe(prior);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        (await Worker(reopened).RunAsync(1, token: Token)).ChangedPaths.ShouldBeEmpty(); reopened.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(2);
        var final = await Worker(reopened).RunAsync(1, token: Token); final.ChangedPaths.ShouldBe(["topic"]); final.More.ShouldBeFalse();
        reopened.LocalFiles.Find("topic")!.LocalSequence.ShouldBeGreaterThan(prior);
        var root = DirectorySnapshotBlocks.SnapshotOf(VersionView.Open(reopened.LocalFiles.Find("topic")!.Record!, keys, device)); root.Hash.Length.ShouldBe(32);
        reopened.HasDirectoryCaptures().ShouldBeFalse(); reopened.HasPendingDirectoryPublications().ShouldBeFalse();
        reopened.ReadDirectoryPublicationCache("topic")!.MemberCount.ShouldBe(3);
    }
    [Fact]
    public async Task WorkerPublishesChildrenBeforeParentsAndIdenticalReconstructionDoesNotChurnVersions()
    {
        Directory.CreateDirectory(Path.Combine(Root, "topic", "nested")); File.WriteAllText(Path.Combine(Root, "topic", "nested", "a"), "bytes");
        await using var index = await Open(); await QueueForWorker(index, "topic"); await QueueForWorker(index, "topic/nested");
        var first = await Worker(index).RunAsync(token: Token); first.Deferred.ShouldBe(0); first.ChangedPaths.ShouldBe(["topic/nested", "topic"]);
        var rows = new[] { index.LocalFiles.Find("topic")!, index.LocalFiles.Find("topic/nested")! }; long high = index.ReadIndexHighWater();
        await QueueForWorker(index, "topic"); await QueueForWorker(index, "topic/nested");
        var same = await Worker(index).RunAsync(token: Token); same.ChangedPaths.ShouldBeEmpty(); same.Deferred.ShouldBe(0); same.More.ShouldBeFalse();
        index.ReadIndexHighWater().ShouldBe(high);
        foreach (var prior in rows) { var current = index.LocalFiles.Find(prior.Path)!; current.LocalSequence.ShouldBe(prior.LocalSequence); current.Record!.Body.ToArray().ShouldBe(prior.Record!.Body.ToArray()); }
    }
    [Fact]
    public async Task WorkerDefersMismatchedMembershipAndStillPublishesAnIndependentDirectory()
    {
        Directory.CreateDirectory(Path.Combine(Root, "zebra")); WriteFiles("a"); await using var index = await Open();
        var wrong = await QueueForWorker(index, "topic", wrongMembership: true); await QueueForWorker(index, "zebra");
        var result = await Worker(index).RunAsync(token: Token); result.ChangedPaths.ShouldBe(["zebra"]); result.Deferred.ShouldBe(1); result.More.ShouldBeFalse();
        var retry = index.ReadDirectoryPublication("topic")!; retry.Version.ShouldBe(wrong.Version); retry.Attempts.ShouldBe(1); retry.NextTicks.ShouldNotBeNull();
        index.ReadDirectoryCapture("topic").ShouldBeNull(); index.ReadDirectoryPublicationCache("topic").ShouldBeNull();
    }
    [Fact]
    public async Task WorkerHonorsPendingChildDependencyAndCancellationDoesNotConvertIntoRetry()
    {
        Directory.CreateDirectory(Path.Combine(Root, "topic", "nested")); await using var index = await Open();
        await QueueForWorker(index, "topic"); var child = await QueueForWorker(index, "topic/nested");
        await index.PauseDirectoryPublicationAsync(child, true, DateTime.UtcNow.Ticks, Token);
        var result = await Worker(index).RunAsync(token: Token); result.Deferred.ShouldBe(1); result.ChangedPaths.ShouldBeEmpty(); index.HasDirectoryCaptures().ShouldBeFalse();
        var before = index.ReadDirectoryPublication("topic")!; using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Worker(index).RunAsync(token: cancelled.Token));
        index.ReadDirectoryPublication("topic")!.Attempts.ShouldBe(before.Attempts);
    }
    [Fact]
    public async Task WorkerCanReconstructReadonlyButCannotSignAMismatchedRoot()
    {
        WriteFiles("a"); await using var index = await Open(); await QueueForWorker(index, "topic");
        (await Worker(index).RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
        var original = index.LocalFiles.Find("topic")!; long high = index.ReadIndexHighWater();
        await QueueForWorker(index, "topic", receivedTemplate: true);
        using var access = FolderKeys.FromText(keys.ExportReadOnlyText());
        var same = await Worker(index, access).RunAsync(token: Token); same.ChangedPaths.ShouldBeEmpty(); same.Deferred.ShouldBe(0); index.ReadIndexHighWater().ShouldBe(high);
        // A newly signed logical child changes the captured root. RO must keep
        // the authenticated current root and queue instead of inventing one.
        var body = VersionView.Open(index.LocalFiles.Find("topic/a")!.Record!, keys, device).ExportBody(); body.ChangedTicks++;
        var child = index.LocalFiles.Find("topic/a")!;
        await index.LocalFiles.UpsertAsync([child with { Record = SignedRecordFactory.Create(body, keys), ChangedTicks = body.ChangedTicks }], Token);
        await QueueForWorker(index, "topic", receivedTemplate: true);
        var pending = await Worker(index, access).RunAsync(token: Token); pending.Deferred.ShouldBe(1); pending.ChangedPaths.ShouldBeEmpty();
        index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(original.Record!.Body.ToArray()); index.HasDirectoryCaptures().ShouldBeTrue();
    }
    private DirectoryPublicationWorker Worker(FolderIndex index, FolderKeys? access = null, Action<string>? checkpoint = null) =>
        new(Root, index, access ?? keys, device, new FolderSettings(), TimeProvider.System) { Checkpoint = checkpoint };
    private async Task<DirectoryPublication> QueueForWorker(FolderIndex index, string path, bool wrongMembership = false, bool receivedTemplate = false)
    {
        var row = index.LocalFiles.Find(path)!; var inventory = index.ReadDirectoryChildInventory(path, Token);
        var body = VersionView.Open(row.Record!, keys, device).ExportBody();
        if (!receivedTemplate)
        {
            body.DirectorySnapshotHash = ByteString.Empty; body.DirectorySnapshotLength = 0; body.ChangedTicks++;
            foreach (var entry in body.Vv) if (entry.Device == 1) entry.Counter++;
        }
        long now = DateTime.UtcNow.Ticks;
        return await index.StageDirectoryPublicationAsync(new(Guid.NewGuid(), path, index.Meta.Find("_root-identity")!.Value!, row.FileId!, row.LocalSequence,
            row.Attributes, receivedTemplate ? row.Record! : SignedRecordFactory.Create(body, keys), wrongMembership ? new byte[32] : inventory.Hash, inventory.Count, now, now), Token);
    }
}
