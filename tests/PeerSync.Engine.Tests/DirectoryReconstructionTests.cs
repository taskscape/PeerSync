using PeerSync.Core.Settings;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Fact]
    public async Task IncomingRootReconstructionAllowsLateChildApplyAndOnlyThenReleasesTheExactRoot()
    {
        byte[] content = "late original bytes"u8.ToArray(); File.WriteAllBytes(Path.Combine(Root, "topic", "a"), content);
        await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer);
        var child = producer.LocalFiles.Find("topic/a")!.Record!;
        string receiverRoot = Path.Combine(fixture, "receiver"); Directory.CreateDirectory(receiverRoot);
        await using var receiver = await FolderIndex.OpenAsync(Path.Combine(fixture, "receiver.db"), Token); byte[] own = [2, .. new byte[31]];
        using var scanner = new Scanner(receiverRoot, receiver, keys, own) { StageDirectorySnapshots = true };
        await scanner.ScanAsync(Token); var needed = new NeededSet(receiver, keys, own);
        await needed.ApplyIndexUpdateAsync(device, 0, 1, [original.WithLocalSequence(1)], Token);
        (await new DirectoryApplier(receiverRoot, receiver, keys, own).ApplyAsync("topic", Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        await scanner.ScanAsync(Token);
        var queued = receiver.ReadDirectoryPublication("topic")!; queued.ReconstructionOnly.ShouldBeTrue();
        receiver.HasPendingDirectoryPublication("topic/a", true).ShouldBeTrue(); receiver.HasBlockingDirectoryPublication("topic/a", true).ShouldBeFalse();
        var worker = new DirectoryPublicationWorker(receiverRoot, receiver, keys, own, new FolderSettings(), TimeProvider.System);
        (await worker.RunAsync(token: Token)).Deferred.ShouldBe(1); receiver.ReadDirectoryPublicationCache("topic").ShouldBeNull();
        var before = receiver.LocalFiles.Find("topic")!; before.Record!.Body.ToArray().ShouldBe(original.Body.ToArray());
        (await new BlockReferences(receiverRoot, receiver, keys, own).RefreshAsync(Token)).WaitingForDirectorySnapshots.ShouldBeTrue();
        // The pending reconstruction must not reject application of its missing child.
        await needed.ApplyIndexUpdateAsync(device, 1, 2, [child.WithLocalSequence(2)], Token);
        (await new FileApplier(receiverRoot, receiver, keys, own).ApplyAsync("topic/a",
            (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(content), Token)).ShouldBe(FileApplyOutcome.Applied);
        await scanner.ScanAsync(Token);
        (await worker.RunAsync(token: Token)).Deferred.ShouldBe(0); receiver.HasPendingDirectoryPublications().ShouldBeFalse();
        var complete = receiver.LocalFiles.Find("topic")!; complete.LocalSequence.ShouldBe(before.LocalSequence);
        complete.Record!.Body.ToArray().ShouldBe(original.Body.ToArray()); complete.Record.Signature.ToArray().ShouldBe(original.Signature.ToArray());
        receiver.ReadDirectoryPublicationCache("topic")!.RecordSourceId.ShouldBe(CaseConflictPolicy.SourceId(VersionView.Open(original, keys, device)));
        File.ReadAllBytes(Path.Combine(receiverRoot, "topic", "a")).ShouldBe(content);
        // Once exact reconstruction is proven, an ordinary local edit can publish a new root.
        File.WriteAllText(Path.Combine(receiverRoot, "topic", "a"), "subsequent local edit"); await scanner.ScanAsync(Token);
        receiver.ReadDirectoryPublication("topic")!.ReconstructionOnly.ShouldBeFalse();
        (await worker.RunAsync(token: Token)).ChangedPaths.ShouldBe(["topic"]);
        VersionView.Open(receiver.LocalFiles.Find("topic")!.Record!, keys, own).ModifiedBy.ToArray().ShouldBe(own);
    }

    [Fact]
    public async Task ReconstructionCannotBeUsedToReplaceTheOriginalSignedRow()
    {
        WriteFiles("a"); await using var index = await Open(); var draft = await AdvanceIndex(index, 1000); var publication = Publication(index, draft);
        var initial = await StageQueue(index, draft, publication); var installed = await CommitQueue(index, initial, draft, publication);
        var next = await AdvanceIndex(index, 1000); var reconstruction = await StageQueue(index, next, installed.Row, receivedTemplate: true);
        await Should.ThrowAsync<InvalidDataException>(() => index.CommitDirectoryPublicationAsync(reconstruction, next, installed.Row, DateTime.UtcNow.Ticks, Token));
        index.ReadDirectoryPublication("topic")!.ReconstructionOnly.ShouldBeTrue(); index.ReadIndexHighWater().ShouldBe(installed.Row.LocalSequence);
        (await CommitQueue(index, reconstruction, next, null)).Changed.ShouldBeFalse();
    }
}
