using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSubtreeCopyBlocksIncomingFileOrDirectoryBeforeNativeMutation(bool directory)
    {
        WriteFiles("a", "b"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var applier = new FileApplier(Root, index, keys, device) { DirectoryCaseMemberWorkLimit = 1 };
        await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token); await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var pending = index.ReadPendingCasePreservations().Single(); string target = directory ? pending.DestinationPath! : pending.DestinationPath + "/a";
        string bytes = File.ReadAllText(Path.Combine(Root, pending.DestinationPath!, "a"));
        var body = VersionView.Open(Sign(target), keys, device).ExportBody(); body.ModifiedBy = ByteString.CopyFrom([9, .. new byte[31]]); body.Vv.Clear(); body.Vv.Add(new VvEntry { Device = 9, Counter = 1 });
        body.MtimeTicks = DateTime.UtcNow.AddMinutes(1).Ticks; body.ChangedTicks = body.MtimeTicks;
        if (directory) { body.Type = 1; body.FileHash = ByteString.Empty; body.Attributes = (uint)FileAttributes.Hidden; }
        await new NeededSet(index, keys, device).ApplyIndexUpdateAsync([9, .. new byte[31]], 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
        int fetches = 0; ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> hash, int length, CancellationToken token) { fetches++; throw new IOException("Blocked copy must not fetch."); }
        if (directory) (await new DirectoryApplier(Root, index, keys, device).ApplyAsync(target, Token)).Outcome.ShouldBe(FileApplyOutcome.Blocked);
        else (await applier.ApplyAsync(target, Fetch, Token)).ShouldBe(FileApplyOutcome.Blocked);
        fetches.ShouldBe(0); File.ReadAllText(Path.Combine(Root, pending.DestinationPath!, "a")).ShouldBe(bytes);
        (File.GetAttributes(Path.Combine(Root, pending.DestinationPath!)) & FileAttributes.Hidden).ShouldBe((FileAttributes)0); index.ReadApplyIntents().ShouldBeEmpty();
    }
    [Fact]
    public async Task LegacyAndSnapshotScansDoNotAdvertiseAnUnfinishedCopyButReleaseAfterMemberCoverage()
    {
        WriteFiles("a", "b"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var applier = new FileApplier(Root, index, keys, device) { DirectoryCaseMemberWorkLimit = 1 }; await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var pending = index.ReadPendingCasePreservations().Single(); string copy = pending.DestinationPath!;
        using (var scanner = new Scanner(Root, index, keys, device)) await scanner.ScanAsync(Token);
        index.LocalFiles.Find(copy).ShouldBeNull();
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "copy-publication", debugEnabled: true);
        using (var scanner = new Scanner(Root, index, keys, device, journal: journal) { StageDirectorySnapshots = true }) await scanner.ScanAsync(Token);
        index.ReadDirectoryPublication(copy).ShouldBeNull();
        for (int pass = 0; pass < 4 && !index.ReadDirectoryCaseProgress(pending.Source.SourceId, pending.InstalledFileId!).Exhausted; pass++) await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        index.HasIncompleteDirectoryCaseCopy(copy).ShouldBeFalse();
        using (var scanner = new Scanner(Root, index, keys, device) { StageDirectorySnapshots = true }) await scanner.ScanAsync(Token);
        index.ReadDirectoryPublication(copy).ShouldNotBeNull(); await Worker(index).RunAsync(token: Token);
        var published = index.LocalFiles.Find(copy)!; DirectorySnapshotBlocks.SnapshotOf(VersionView.Open(published.Record!, keys, device)).Hash.Length.ShouldBe(32);
        index.HasPendingCasePreservation(copy + "/a").ShouldBeTrue(); index.HasPendingCasePreservation("topic").ShouldBeTrue();
        journal.DroppedEvents.ShouldBe(0); File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")).ShouldContain("case_copy_publication_held");
    }
    [Fact]
    public async Task FullSnapshotAndPrimitivePublicationRecheckIncompleteCopyCoverageWithoutReleasingDrafts()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await new FileApplier(Root, index, keys, device).PrepareCaseCopiesAsync(FetchRootMetadata, Token); var pending = index.ReadPendingCasePreservations().Single(); string path = pending.DestinationPath!;
        long now = DateTime.UtcNow.Ticks; var body = new FileRecordBody { Path = path, Type = 1, MtimeTicks = Directory.GetLastWriteTimeUtc(Path.Combine(Root, path)).Ticks,
            ChangedTicks = now, ModifiedBy = ByteString.CopyFrom(device) }; body.Vv.Add(new VvEntry { Device = 1, Counter = 1 });
        var prior = new LocalFileRow(path, 1, 0, body.MtimeTicks, (uint)File.GetAttributes(Path.Combine(Root, path)), false, now, null,
            new Dictionary<ulong, ulong> { [1] = 1 }, SignedRecordFactory.Create(body, keys), pending.InstalledFileId);
        await index.LocalFiles.UpsertAsync([prior], Token); prior = index.LocalFiles.Find(path)!; // Retain an older signed row from before the new guard.
        using var gate = await FolderOperationGate.AcquireAsync(index, Token); var capture = new DirectorySnapshotCapture(Root, index, keys, device);
        var draft = await capture.AdvancePublishedUnderGateAsync(path, 1000, Token);
        using var fingerprint = new DirectoryChildFingerprint(path); var empty = fingerprint.Complete(index.ReadDirectoryCaptureEpoch());
        var queued = await index.StageDirectoryPublicationAsync(new(Guid.NewGuid(), path, draft.RootId, draft.NativeId, prior.LocalSequence, prior.Attributes,
            prior.Record!, empty.Hash, 0, now, now), Token);
        queued = await index.BindDirectoryPublicationKeysAsync(queued, draft.InputEpoch!.Value, now, Token);
        body.DirectorySnapshotHash = ByteString.CopyFrom(draft.Hash!); body.DirectorySnapshotLength = (uint)draft.Length!.Value;
        var publication = prior with { Record = SignedRecordFactory.Create(body, keys) };
        await Should.ThrowAsync<InvalidOperationException>(() => capture.CommitQueuedUnderGateAsync(queued, draft, publication, now, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => capture.PublishUnderGateAsync(draft, publication, prior.LocalSequence, Token));
        index.LocalFiles.Find(path)!.LocalSequence.ShouldBe(prior.LocalSequence); index.ReadDirectoryPublication(path)!.Version.ShouldBe(queued.Version);
        index.ReadDirectoryCapture(path)!.Hash.ShouldBe(draft.Hash); index.HasPendingCasePreservation("topic").ShouldBeTrue();
    }
}
