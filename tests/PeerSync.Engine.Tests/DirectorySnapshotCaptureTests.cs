using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Directories;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;
using SignedRecord = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryCapture-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] device = [1, .. new byte[31]];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    public DirectorySnapshotCaptureTests() => Directory.CreateDirectory(Path.Combine(Root, "topic"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPublicationBlocksIncomingFileOrDirectoryMutationBeforeNativeChanges(bool directory)
    {
        WriteFiles("a"); await using var index = await Open(); var local = index.LocalFiles.Find("topic")!;
        var queued = new DirectoryPublication(Guid.NewGuid(), "topic", index.Meta.Find("_root-identity")!.Value!, local.FileId!, local.LocalSequence,
            local.Attributes, local.Record!, new byte[32], 1, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks);
        await index.StageDirectoryPublicationAsync(queued, Token);
        byte[] remote = [2, .. new byte[31]], content = "remote content"u8.ToArray();
        var body = new FileRecordBody { Path = directory ? "topic" : "topic/a", Type = directory ? 1u : 0u,
            ModifiedBy = ByteString.CopyFrom(remote), MtimeTicks = DateTime.UtcNow.AddMinutes(1).Ticks, ChangedTicks = DateTime.UtcNow.Ticks,
            Attributes = directory ? (uint)FileAttributes.Hidden : 0 };
        body.Vv.Add(new VvEntry { Device = 2, Counter = 1 });
        if (!directory)
        {
            body.Size = (ulong)content.Length; body.FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash(content).AsSpan());
            body.Chunks.Add(new Chunk { Hash = body.FileHash, Length = (uint)content.Length });
        }
        await new NeededSet(index, keys, device).ApplyIndexUpdateAsync(remote, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
        index.HasPendingCasePreservation(body.Path).ShouldBeFalse();
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "queue-barrier", debugEnabled: true);
        if (directory)
            (await new DirectoryApplier(Root, index, keys, device, journal: journal).ApplyAsync(body.Path, Token)).Outcome.ShouldBe(FileApplyOutcome.Blocked);
        else
            (await new FileApplier(Root, index, keys, device, journal: journal).ApplyAsync(body.Path,
                (_, _, _) => throw new InvalidOperationException("Blocked work must not fetch bytes."), Token)).ShouldBe(FileApplyOutcome.Blocked);
        File.ReadAllBytes(Path.Combine(Root, "topic", "a")).ShouldBeEmpty();
        ((File.GetAttributes(Path.Combine(Root, "topic")) & FileAttributes.Hidden) != 0).ShouldBeFalse();
        index.ReadApplyIntents().ShouldBeEmpty(); index.ReadDirectoryPublication("topic")!.Version.ShouldBe(queued.Version);
        File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")).ShouldContain("publication-pending");
    }

    [Fact]
    public async Task ParentProofMayReceiveItsMissingDirectChildWithoutPublishingAnOldSnapshot()
    {
        WriteFiles("a"); await using var index = await Open();
        var originalDraft = await AdvanceIndex(index, 1000);
        var originalParent = index.LocalFiles.Find("topic")!;
        await Publish(index, originalDraft, Publication(index, originalDraft), originalParent.LocalSequence);
        var signedParent = index.LocalFiles.Find("topic")!.Record!;
        using (var published = new PublishedBlocks(Root, index))
        using (await FolderOperationGate.AcquireAsync(index, Token))
            (await new DirectorySnapshotGraph(Root, index, keys, device).AdvanceUnderGateAsync(
                signedParent, (hash, length, token) => ValueTask.FromResult<ReadOnlyMemory<byte>>(
                    published.TryRead(hash.Span, length, token) ?? throw new IOException("Published graph block is missing.")),
                1000, Token)).Complete.ShouldBeTrue();

        File.WriteAllText(Path.Combine(Root, "topic", "b"), "local change");
        using (var scanner = new Scanner(Root, index, keys, device) { StageDirectorySnapshots = true })
            (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty();
        index.ReadDirectoryPublication("topic")!.ReconstructionOnly.ShouldBeFalse();
        // Model a signed child whose bytes have not yet been installed. The
        // parent proof must wait for the receipt instead of absorbing its clock.
        Sql("UPDATE local_files SET content_ready=0 WHERE path_key='TOPIC/A';");
        byte[] remote = [2, .. new byte[31]], content = "child recovered"u8.ToArray();
        var body = VersionView.Open(index.LocalFiles.Find("topic/a")!.Record!, keys, device).ExportBody();
        body.ModifiedBy = ByteString.CopyFrom(remote); body.ChangedTicks++;
        body.Size = (ulong)content.Length;
        body.FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash(content).AsSpan());
        body.Chunks.Clear(); body.Chunks.Add(new Chunk { Hash = body.FileHash, Length = (uint)content.Length });
        body.Vv.Add(new VvEntry { Device = 2, Counter = 1 });
        var needed = new NeededSet(index, keys, device);
        await needed.ApplyIndexUpdateAsync(remote, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
        await needed.AnnounceIndexRangeAsync(remote, 1, Token);
        await new DirectoryPublicationWorker(Root, index, keys, device, new(), TimeProvider.System).RunAsync(token: Token);
        index.HasUnprovenDirectoryParentMember("topic/a").ShouldBeTrue();
        index.HasUnprovenDirectoryParentMember("topic/b").ShouldBeFalse();
        var waiting = index.ReadDirectoryPublication("topic")!;
        waiting.NextTicks.ShouldNotBeNull();
        long beforeEpoch = index.ReadDirectoryCaptureEpoch();
        var outcome = await new FileApplier(Root, index, keys, device).ApplyScheduledAsync("topic/a",
            (_, _, length, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(content), Token);
        outcome.Outcome.ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Path.Combine(Root, "topic", "a")).ShouldBe(content);
        index.ReadDirectoryCaptureEpoch().ShouldBeGreaterThan(beforeEpoch);
        var woken = index.ReadDirectoryPublication("topic")!;
        woken.NextTicks.ShouldBeNull();
        woken.Attempts.ShouldBe(0);
        woken.Version.ShouldBe(waiting.Version);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DominatingDescendantTombstonePassesOnlyAnAncestorPublication(bool directory, bool interrupted)
    {
        string childPath = directory ? "topic/nested" : "topic/a";
        if (directory) Directory.CreateDirectory(Path.Combine(Root, "topic", "nested"));
        else WriteFiles("a");
        await using var index = await Open();
        var parent = index.LocalFiles.Find("topic")!;
        var child = index.LocalFiles.Find(childPath)!;
        long now = DateTime.UtcNow.Ticks;
        var parentQueue = new DirectoryPublication(Guid.NewGuid(), "topic", index.Meta.Find("_root-identity")!.Value!,
            parent.FileId!, parent.LocalSequence, parent.Attributes, parent.Record!, new byte[32], 1, now, now);
        await index.StageDirectoryPublicationAsync(parentQueue, Token);

        SignedRecordFactory.TryOpen(child.Record!, keys, out var body).ShouldBeTrue();
        byte[] remote = [2, .. new byte[31]];
        body.Deleted = true; body.Size = 0; body.FileHash = ByteString.Empty; body.Chunks.Clear();
        body.DirectorySnapshotHash = ByteString.Empty; body.DirectorySnapshotLength = 0;
        body.ModifiedBy = ByteString.CopyFrom(remote); body.ChangedTicks = now + 1;
        body.Vv.Add(new VvEntry { Device = 2, Counter = 1 });
        var needed = new NeededSet(index, keys, device);
        await needed.ApplyIndexUpdateAsync(remote, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
        await needed.AnnounceIndexRangeAsync(remote, 1, Token);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "ancestor-tombstone", debugEnabled: true);

        if (directory)
        {
            var applier = new DirectoryApplier(Root, index, keys, device, journal: journal);
            if (interrupted) applier.Checkpoint = stage => { if (stage == "prepared") throw new IOException("injected"); };
            if (interrupted)
            {
                await Should.ThrowAsync<IOException>(() => applier.ApplyScheduledAsync(childPath, Token));
                (await new FileApplier(Root, index, keys, device).RecoverAsync(Token)).ShouldBe(1);
            }
            else (await applier.ApplyScheduledAsync(childPath, Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        }
        else
        {
            var applier = new FileApplier(Root, index, keys, device, journal: journal);
            if (interrupted) applier.Checkpoint = stage => { if (stage == "prepared") throw new IOException("injected"); };
            if (interrupted)
            {
                await Should.ThrowAsync<IOException>(() => applier.ApplyScheduledAsync(childPath,
                    (_, _, _, _) => throw new InvalidOperationException("Tombstone must not fetch bytes."), Token));
                (await new FileApplier(Root, index, keys, device).RecoverAsync(Token)).ShouldBe(1);
            }
            else (await applier.ApplyScheduledAsync(childPath,
                (_, _, _, _) => throw new InvalidOperationException("Tombstone must not fetch bytes."), Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        }
        Directory.Exists(Path.Combine(Root, "topic")).ShouldBeTrue();
        (directory ? Directory.Exists(Path.Combine(Root, "topic", "nested")) : File.Exists(Path.Combine(Root, "topic", "a"))).ShouldBeFalse();
        index.LocalFiles.Find(childPath)!.Deleted.ShouldBeTrue();
        index.ReadDirectoryPublication("topic")!.Version.ShouldBe(parentQueue.Version);
        File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")).ShouldContain("ancestor-publication-bypassed");
    }

    [Fact]
    public async Task ConcurrentDescendantTombstoneStillWaitsBehindAncestorPublication()
    {
        WriteFiles("a"); await using var index = await Open();
        var parent = index.LocalFiles.Find("topic")!;
        var child = index.LocalFiles.Find("topic/a")!;
        long now = DateTime.UtcNow.Ticks;
        await index.StageDirectoryPublicationAsync(new(Guid.NewGuid(), "topic", index.Meta.Find("_root-identity")!.Value!,
            parent.FileId!, parent.LocalSequence, parent.Attributes, parent.Record!, new byte[32], 1, now, now), Token);
        SignedRecordFactory.TryOpen(child.Record!, keys, out var body).ShouldBeTrue();
        byte[] remote = [2, .. new byte[31]];
        body.Deleted = true; body.Size = 0; body.FileHash = ByteString.Empty; body.Chunks.Clear();
        body.ModifiedBy = ByteString.CopyFrom(remote); body.ChangedTicks = now + TimeSpan.TicksPerMinute;
        body.Vv.Clear(); body.Vv.Add(new VvEntry { Device = 2, Counter = 1 });
        var needed = new NeededSet(index, keys, device);
        await needed.ApplyIndexUpdateAsync(remote, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
        await needed.AnnounceIndexRangeAsync(remote, 1, Token);
        var decision = needed.Decide(new("TOPIC/A", child, index.RemotesForPath("topic/a").ToArray()));
        decision.Winner!.Deleted.ShouldBeTrue();
        VersionVector.Compare(decision.Winner.Vector, child.Vector).ShouldBe(VvOrder.Concurrent);
        (await new FileApplier(Root, index, keys, device).ApplyScheduledAsync("topic/a",
            (_, _, _, _) => throw new InvalidOperationException("Blocked work must not fetch bytes."), Token)).Outcome.ShouldBe(FileApplyOutcome.Blocked);
        File.Exists(Path.Combine(Root, "topic", "a")).ShouldBeTrue();
    }

    [Fact]
    public async Task DominatingDirectoryTombstoneCannotPassItsOwnOrdinaryPublication()
    {
        Directory.CreateDirectory(Path.Combine(Root, "topic", "nested"));
        await using var index = await Open(); var child = index.LocalFiles.Find("topic/nested")!;
        long now = DateTime.UtcNow.Ticks;
        await index.StageDirectoryPublicationAsync(new(Guid.NewGuid(), child.Path, index.Meta.Find("_root-identity")!.Value!,
            child.FileId!, child.LocalSequence, child.Attributes, child.Record!, new byte[32], 0, now, now), Token);
        SignedRecordFactory.TryOpen(child.Record!, keys, out var body).ShouldBeTrue();
        byte[] remote = [2, .. new byte[31]];
        body.Deleted = true; body.DirectorySnapshotHash = ByteString.Empty; body.DirectorySnapshotLength = 0;
        body.ModifiedBy = ByteString.CopyFrom(remote); body.ChangedTicks = now + 1;
        body.Vv.Add(new VvEntry { Device = 2, Counter = 1 });
        var needed = new NeededSet(index, keys, device);
        await needed.ApplyIndexUpdateAsync(remote, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
        await needed.AnnounceIndexRangeAsync(remote, 1, Token);
        (await new DirectoryApplier(Root, index, keys, device).ApplyScheduledAsync(child.Path, Token)).Outcome.ShouldBe(FileApplyOutcome.Blocked);
        Directory.Exists(Path.Combine(Root, "topic", "nested")).ShouldBeTrue();
    }

    [Fact]
    public async Task IndexedCaptureResumesAfterRestartWithoutRevalidatingUnchangedFullInput()
    {
        WriteFiles("a", "b", "c"); byte[] expected; DirectoryCaptureState first;
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "index-capture", debugEnabled: true);
        await using (var index = await Open())
        {
            using var blocks = new DirectorySnapshotBlocks(Root, index, keys, device);
            expected = (await blocks.CaptureDirectoryAsync("topic", index.ReadDirectorySnapshotChildren("topic").Rows.Select(row => row.Record!), Token)).Hash;
            first = await AdvanceIndex(index, 1, journal: journal); first.Accepted.ShouldBe(1); first.InputEpoch.ShouldBe(index.ReadDirectoryCaptureEpoch());
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var second = await AdvanceIndex(reopened, 1, journal: journal); second.Accepted.ShouldBe(2); second.InputHash.ShouldBe(first.InputHash);
        var third = await AdvanceIndex(reopened, 1, journal: journal); third.Accepted.ShouldBe(3); third.Hash.ShouldBe(expected);
        (await AdvanceIndex(reopened, 1, journal: journal)).Hash.ShouldBe(expected);
        File.ReadAllLines(Path.Combine(fixture, "logs", "debug.jsonl")).Count(line => line.Contains("directory.capture_input_revalidated", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task ChangedEpochWithIdenticalManifestPreservesAcceptedPrefix()
    {
        WriteFiles("a", "b", "c"); await using var index = await Open(); var first = await AdvanceIndex(index, 1);
        await index.LocalFiles.UpsertAsync([index.LocalFiles.Find("topic/a")!], Token); // Sequence/epoch changes; original signed bytes do not.
        var next = await AdvanceIndex(index, 1); next.Accepted.ShouldBe(2); next.InputHash.ShouldBe(first.InputHash); next.InputEpoch.ShouldNotBe(first.InputEpoch);
        (await AdvanceIndex(index, 1)).Hash.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReadinessRepairRetainsAuthenticatedMembershipAndAdvancesTheSamePrefix()
    {
        WriteFiles("a", "b", "c"); await using var index = await Open(); var first = await AdvanceIndex(index, 1);
        long sequence = index.ReadIndexHighWater(); Sql("UPDATE local_files SET content_ready=0 WHERE path_key='TOPIC/C';");
        index.ReadIndexHighWater().ShouldBe(sequence);
        var next = await AdvanceIndex(index, 1); next.Accepted.ShouldBe(2); next.MemberCount.ShouldBe(3); next.InputHash.ShouldBe(first.InputHash);
        var done = await AdvanceIndex(index, 1); done.Accepted.ShouldBe(3); done.Hash.ShouldNotBeNull();
        index.LocalFiles.Find("topic/c")!.ContentReady.ShouldBeFalse("metadata capture does not claim payload availability");
    }

    [Fact]
    public async Task NewPublishedChildChangesTheManifestAndRestartsItsDraft()
    {
        WriteFiles("a", "b", "c"); await using var index = await Open(); var first = await AdvanceIndex(index, 1);
        WriteFiles("d"); using (var scanner = new Scanner(Root, index, keys, device)) (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty();
        var reset = await AdvanceIndex(index, 1); reset.Accepted.ShouldBe(1); reset.MemberCount.ShouldBe(4); reset.InputHash.ShouldNotBe(first.InputHash);
        (await AdvanceIndex(index, 1000)).Accepted.ShouldBe(4);
    }

    [Theory]
    [InlineData("before-input-bind")]
    [InlineData("captured")]
    public async Task MutationBetweenInputCheckAndCommitNeverBindsOrAdvancesAStaleWitness(string boundary)
    {
        WriteFiles("a", "b", "c"); await using var index = await Open();
        if (boundary == "captured") await AdvanceIndex(index, 1);
        await Should.ThrowAsync<InvalidOperationException>(() => AdvanceIndex(index, 1, stage =>
        {
            if (stage == boundary) Sql("UPDATE local_files SET change_filetime=COALESCE(change_filetime,0)+1 WHERE path_key='TOPIC/A';");
        }));
        var saved = index.ReadDirectoryCapture("topic")!; saved.Accepted.ShouldBe(1); saved.Hash.ShouldBeNull();
        if (boundary == "before-input-bind") saved.InputEpoch.ShouldBeNull();
        (await AdvanceIndex(index, 1000)).Accepted.ShouldBe(3);
    }

    [Fact]
    public async Task CorruptStoredMemberCountCannotCompleteWhileIndexedMembersRemain()
    {
        WriteFiles("a", "b", "c"); await using var index = await Open(); await AdvanceIndex(index, 1);
        Sql("UPDATE directory_captures SET member_count=2;");
        await Should.ThrowAsync<InvalidDataException>(() => AdvanceIndex(index, 1));
        index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(1); index.ReadDirectoryCapture("topic")!.Hash.ShouldBeNull();
    }

    private void WriteFiles(params string[] names) { foreach (string name in names) File.WriteAllBytes(Path.Combine(Root, "topic", name), []); }
    private async Task<DirectoryCaptureState> AdvanceIndex(FolderIndex index, int budget, Action<string>? checkpoint = null, DiagnosticJournal? journal = null)
    {
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        return await new DirectorySnapshotCapture(Root, index, keys, device, journal) { Checkpoint = checkpoint }.AdvancePublishedUnderGateAsync("topic", budget, Token);
    }

    [Fact]
    public async Task PublicationDiagnosticsExposeCommitStageAndNativeCodeWithoutSqlOrSourceDetails()
    {
        await using var index = await Open(); var draft = await Advance(index, [], 1000); var prior = index.LocalFiles.Find("topic")!;
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "publish", debugEnabled: true);
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        var capture = new DirectorySnapshotCapture(Root, index, keys, device, journal); var publication = Publication(index, draft);
        Sql("CREATE TRIGGER reject_capture_publication BEFORE DELETE ON directory_captures BEGIN SELECT RAISE(ABORT,'private publication failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => capture.PublishUnderGateAsync(draft, publication, prior.LocalSequence, Token));
        Sql("DROP TRIGGER reject_capture_publication;"); await capture.PublishUnderGateAsync(draft, publication, prior.LocalSequence, Token);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("directory.publish_start"); log.ShouldContain("directory.publish_complete"); log.ShouldContain("directory.publish_failed");
        log.ShouldContain("commit-row-and-draft.SqliteException"); log.ShouldContain("1811");
        log.ShouldNotContain("private publication failure"); log.ShouldNotContain("topic"); log.ShouldNotContain(fixture);
        log.ShouldNotContain(Convert.ToHexString(draft.Hash!)); log.ShouldNotContain(Convert.ToBase64String(publication.Record!.Body));
    }

    [Fact]
    public async Task PublicationInstallsTheSignedRootAndReleasesTheDraftInOneDurableStep()
    {
        File.WriteAllText(Path.Combine(Root, "topic", "a"), "published child bytes");
        await using var index = await Open(); var child = index.LocalFiles.Find("topic/a")!.Record!;
        var draft = await Advance(index, [child], 1000); var prior = index.LocalFiles.Find("topic")!;
        var published = await Publish(index, draft, Publication(index, draft), prior.LocalSequence);
        published.LocalSequence.ShouldBeGreaterThan(prior.LocalSequence); published.Record.ShouldNotBeNull();
        DirectorySnapshotBlocks.SnapshotOf(VersionView.Open(published.Record!, keys, device)).Hash.ShouldBe(draft.Hash!);
        index.HasDirectoryCaptures().ShouldBeFalse(); index.ReadDirectoryCapture("topic").ShouldBeNull();
        var references = await new BlockReferences(Root, index, keys, device).RefreshAsync(Token);
        references.AllKnownSourcesCovered.ShouldBeTrue(); references.References.ShouldBe(4);
        File.ReadAllText(Path.Combine(Root, "topic", "a")).ShouldBe("published child bytes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlFailureRollsBackBothPublicationAndDraftRelease(bool afterRow)
    {
        await using var index = await Open(); var draft = await Advance(index, [], 1000); var prior = index.LocalFiles.Find("topic")!;
        long high = index.ReadIndexHighWater(); var publication = Publication(index, draft);
        Sql(afterRow
            ? "CREATE TRIGGER reject_capture_publication BEFORE DELETE ON directory_captures BEGIN SELECT RAISE(ABORT,'private publication failure'); END;"
            : "CREATE TRIGGER reject_capture_publication BEFORE UPDATE ON local_files WHEN NEW.path_key='TOPIC' BEGIN SELECT RAISE(ABORT,'private publication failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => Publish(index, draft, publication, prior.LocalSequence));
        index.HasDirectoryCaptures().ShouldBeTrue(); index.ReadIndexHighWater().ShouldBe(high);
        index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(prior.Record!.Body.ToArray());
        Sql("DROP TRIGGER reject_capture_publication;");
        (await Publish(index, draft, publication, prior.LocalSequence)).Record.ShouldNotBeNull(); index.HasDirectoryCaptures().ShouldBeFalse();
    }

    [Fact]
    public async Task StaleLocalSequenceCannotReleaseAnUnpublishedDraft()
    {
        await using var index = await Open(); var draft = await Advance(index, [], 1000); var prior = index.LocalFiles.Find("topic")!;
        var publication = Publication(index, draft); await index.LocalFiles.UpsertAsync([prior], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => Publish(index, draft, publication, prior.LocalSequence));
        index.HasDirectoryCaptures().ShouldBeTrue();
    }

    [Fact]
    public async Task NativeReplacementOrWrongSignedRootCannotPublishOrOverwriteUserData()
    {
        await using var index = await Open(); var draft = await Advance(index, [], 1000); var prior = index.LocalFiles.Find("topic")!;
        var publication = Publication(index, draft); SignedRecordFactory.TryOpen(publication.Record!, keys, out var body).ShouldBeTrue();
        byte[] wrong = draft.Hash!.ToArray(); wrong[0] ^= 1; body.DirectorySnapshotHash = ByteString.CopyFrom(wrong);
        await Should.ThrowAsync<InvalidDataException>(() => Publish(index, draft, publication with { Record = SignedRecordFactory.Create(body, keys) }, prior.LocalSequence));
        Directory.Move(Path.Combine(Root, "topic"), Path.Combine(Root, "old-topic")); Directory.CreateDirectory(Path.Combine(Root, "topic"));
        File.WriteAllText(Path.Combine(Root, "topic", "user"), "new directory user bytes");
        await Should.ThrowAsync<IOException>(() => Publish(index, draft, publication, prior.LocalSequence));
        File.ReadAllText(Path.Combine(Root, "topic", "user")).ShouldBe("new directory user bytes"); index.HasDirectoryCaptures().ShouldBeTrue();
    }

    [Fact]
    public async Task CancellationBeforePublicationCommitRetainsTheDraftAndLocalRow()
    {
        await using var index = await Open(); var draft = await Advance(index, [], 1000); var prior = index.LocalFiles.Find("topic")!;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        var capture = new DirectorySnapshotCapture(Root, index, keys, device) { Checkpoint = stage => { if (stage == "before-publication") stop.Cancel(); } };
        await Should.ThrowAsync<OperationCanceledException>(() => capture.PublishUnderGateAsync(draft, Publication(index, draft), prior.LocalSequence, stop.Token));
        index.HasDirectoryCaptures().ShouldBeTrue(); index.LocalFiles.Find("topic")!.LocalSequence.ShouldBe(prior.LocalSequence);
    }

    private LocalFileRow Publication(FolderIndex index, DirectoryCaptureState draft)
    {
        var prior = index.LocalFiles.Find("topic")!; var vector = VersionVector.Increment(prior.Vector, 1);
        var body = new FileRecordBody { Path = "topic", Type = 1, MtimeTicks = prior.MtimeTicks, ChangedTicks = DateTime.UtcNow.Ticks,
            Attributes = prior.Attributes & (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden), ModifiedBy = ByteString.CopyFrom(device),
            DirectorySnapshotHash = ByteString.CopyFrom(draft.Hash!), DirectorySnapshotLength = (uint)draft.Length!.Value };
        body.Vv.AddRange(vector.Select(entry => new VvEntry { Device = entry.Key, Counter = entry.Value }));
        return prior with { Vector = vector, Record = SignedRecordFactory.Create(body, keys), ChangedTicks = body.ChangedTicks, ContentReady = true };
    }
    private async Task<LocalFileRow> Publish(FolderIndex index, DirectoryCaptureState draft, LocalFileRow publication, long sequence)
    {
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        return await new DirectorySnapshotCapture(Root, index, keys, device).PublishUnderGateAsync(draft, publication, sequence, Token);
    }
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    [Fact]
    public async Task InconsistentCheckpointCountCannotOmitAcceptedMembers()
    {
        await using var index = await Open(); var children = Children(3); await Advance(index, children, 1);
        byte[] empty = new DirectorySnapshotBuilder(static (_, _) => throw new InvalidOperationException()).ExportCheckpoint();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE directory_captures SET builder_state=$state;";
            command.Parameters.AddWithValue("$state", empty); command.ExecuteNonQuery();
        }
        await Should.ThrowAsync<InvalidDataException>(() => Advance(index, children, 1000));
        index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(1); index.ReadDirectoryCapture("topic")!.Hash.ShouldBeNull();
        (await Advance(index, Children(2), 1000)).Hash.ShouldNotBeNull("a new input starts an independent draft");
    }

    [Fact]
    public async Task RootReplacementCannotReuseAnotherTreesDraftOrCaptureIntoIt()
    {
        await using var index = await Open(); var children = Children(3); var prior = await Advance(index, children, 1);
        Directory.Move(Root, Path.Combine(fixture, "old-tree")); Directory.CreateDirectory(Path.Combine(Root, "topic"));
        await Should.ThrowAsync<IOException>(() => Advance(index, children, 1000));
        index.ReadDirectoryCapture("topic")!.InputHash.ShouldBe(prior.InputHash); index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(1);
        Directory.Exists(Path.Combine(Root, ".psp")).ShouldBeFalse();
    }

    [Fact]
    public async Task DuplicateKeysAndForeignEnvelopesCannotCreateACaptureDraft()
    {
        await using var index = await Open();
        await Should.ThrowAsync<InvalidDataException>(() => Advance(index, [Sign("topic/a"), Sign("topic/A")], 1000));
        using var foreign = FolderKeys.CreateNew(); SignedRecordFactory.TryOpen(Sign("topic/a"), keys, out var body).ShouldBeTrue();
        await Should.ThrowAsync<System.Security.Cryptography.CryptographicException>(() => Advance(index, [SignedRecordFactory.Create(body!, foreign)], 1000));
        index.HasDirectoryCaptures().ShouldBeFalse();
    }

    [Fact]
    public async Task CaptureDiagnosticsRetainCountsAndSanitizedFailuresWithoutPrivateState()
    {
        await using var index = await Open(); using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "capture", debugEnabled: true);
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        var capture = new DirectorySnapshotCapture(Root, index, keys, device, journal); var children = Children(3);
        await capture.AdvanceUnderGateAsync("topic", children, 1, Token);
        var completed = await capture.AdvanceUnderGateAsync("topic", children, 1000, Token);
        await Should.ThrowAsync<InvalidDataException>(() => capture.AdvanceUnderGateAsync("topic", [Sign("other/private-file")], 1000, Token));
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("directory.capture_pending"); log.ShouldContain("directory.capture_complete"); log.ShouldContain("directory.capture_failed");
        log.ShouldNotContain("topic"); log.ShouldNotContain("file000"); log.ShouldNotContain("private-file"); log.ShouldNotContain(fixture);
        log.ShouldNotContain(Convert.ToHexString(completed.Hash!)); log.ShouldNotContain(Convert.ToBase64String(children[0].Body)); log.ShouldNotContain(keys.ExportText());
    }

    [Theory]
    [InlineData("member")]
    [InlineData("captured")]
    [InlineData("progress")]
    public async Task InterruptedCaptureResumesDurablePrefixAndMatchesUninterruptedSnapshot(string boundary)
    {
        var children = Children(129); DirectorySnapshotLink expected;
        await using (var index = await Open())
        {
            using var blocks = new DirectorySnapshotBlocks(Root, index, keys, device);
            expected = await blocks.CaptureDirectoryAsync("topic", children, Token);
            await Should.ThrowAsync<Stop>(() => Advance(index, children, 128, stage => { if (stage == boundary) throw new Stop(); }));
            index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(boundary == "progress" ? 128 : 0);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var complete = await Advance(reopened, children, 1000); complete.Hash.ShouldBe(expected.Hash); complete.Length.ShouldBe(expected.EncodedLength);
        complete.Accepted.ShouldBe(129); complete.BuilderState.ShouldBeNull(); reopened.HasDirectoryCaptures().ShouldBeTrue();
        (await Advance(reopened, children, 1)).Hash.ShouldBe(expected.Hash);
    }

    [Fact]
    public async Task ChangedDirectoryIdentityResetsDraftWhileLocalSequenceChangesDoNot()
    {
        await using var index = await Open(); var children = Children(3);
        var first = await Advance(index, children, 1); first.Accepted.ShouldBe(1);
        var second = await Advance(index, children.Select(record => record.WithLocalSequence(99)), 1); second.Accepted.ShouldBe(2); second.InputHash.ShouldBe(first.InputHash);
        string topic = Path.Combine(Root, "topic"), old = Path.Combine(Root, "old-topic"); Directory.Move(topic, old); Directory.CreateDirectory(topic);
        var reset = await Advance(index, children, 1); reset.Accepted.ShouldBe(1); reset.NativeId.ShouldNotBe(first.NativeId);
        (await Advance(index, children, 1000)).Hash.ShouldNotBeNull();
    }

    [Fact]
    public async Task ChangingEnumerationCannotCommitAMixedSnapshotAndStableRetryCompletes()
    {
        await using var index = await Open(); int enumeration = 0;
        IEnumerable<SignedRecord> Unstable()
        {
            enumeration++; yield return Sign("topic/a"); yield return Sign(enumeration % 2 == 1 ? "topic/b" : "topic/c");
        }
        await Should.ThrowAsync<InvalidDataException>(() => Advance(index, Unstable(), 1000));
        index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(0); index.ReadDirectoryCapture("topic")!.Hash.ShouldBeNull();
        var stable = new[] { Sign("topic/a"), Sign("topic/b") }; var completed = await Advance(index, stable, 1000);
        using var blocks = new DirectorySnapshotBlocks(Root, index, keys, device);
        completed.Hash.ShouldBe((await blocks.CaptureDirectoryAsync("topic", stable, Token)).Hash);
    }

    [Fact]
    public async Task CancellationAfterMetadataFlushPreservesOldPrefixAndIndependentBytesForRetry()
    {
        await using var index = await Open(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var children = Children(3);
        await Should.ThrowAsync<OperationCanceledException>(() => Advance(index, children, 1, stage => { if (stage == "captured") stop.Cancel(); }, stop.Token));
        index.ReadDirectoryCapture("topic")!.Accepted.ShouldBe(0); index.ReadBlockObjects().ShouldNotBeEmpty();
        (await Advance(index, children, 1000)).Hash.ShouldNotBeNull();
    }

    [Fact]
    public async Task PinnedNativeDirectoryCannotBeReplacedDuringCapture()
    {
        await using var index = await Open(); bool attempted = false;
        var result = await Advance(index, Children(1), 1, stage =>
        {
            if (stage != "member") return; attempted = true;
            Should.Throw<IOException>(() => Directory.Move(Path.Combine(Root, "topic"), Path.Combine(Root, "replacement")));
        });
        attempted.ShouldBeTrue(); result.Hash.ShouldNotBeNull(); Directory.Exists(Path.Combine(Root, "replacement")).ShouldBeFalse();
    }

    [Fact]
    public async Task DifferentManifestRestartsRatherThanReusingAnotherChildPrefix()
    {
        await using var index = await Open(); var before = await Advance(index, Children(3), 1);
        var changed = new[] { Sign("topic/a"), Sign("topic/z") }; var reset = await Advance(index, changed, 1);
        reset.Accepted.ShouldBe(1); reset.InputHash.ShouldNotBe(before.InputHash); reset.LastKey.ShouldBe("TOPIC/A");
        var complete = await Advance(index, changed, 1); complete.Hash.ShouldNotBeNull(); complete.Accepted.ShouldBe(2);
    }

    [Fact]
    public async Task EmptyDirectoryProducesTheCanonicalEmptyRootAndKeepsItsUnpublishedBarrier()
    {
        await using var index = await Open(); var result = await Advance(index, [], 1);
        result.Hash.ShouldBe(DirectorySnapshotNode.Entries([]).CalculateHash()); result.Length.ShouldBe(12); result.Accepted.ShouldBe(0);
        index.HasDirectoryCaptures().ShouldBeTrue();
    }

    private async Task<DirectoryCaptureState> Advance(FolderIndex index, IEnumerable<SignedRecord> children, int budget,
        Action<string>? checkpoint = null, CancellationToken? token = null)
    {
        var cancellation = token ?? Token; using var gate = await FolderOperationGate.AcquireAsync(index, cancellation);
        return await new DirectorySnapshotCapture(Root, index, keys, device) { Checkpoint = checkpoint }.AdvanceUnderGateAsync("topic", children, budget, cancellation);
    }
    private SignedRecord[] Children(int count) => Enumerable.Range(0, count).Select(value => Sign("topic/file" + value.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
    private SignedRecord Sign(string path)
    {
        var body = new FileRecordBody { Path = path, Type = 0, MtimeTicks = 638000000000000000, ChangedTicks = 638000000000000000,
            ModifiedBy = ByteString.CopyFrom(device), FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash([]).AsSpan()) };
        body.Vv.Add(new VvEntry { Device = 1, Counter = 1 }); return SignedRecordFactory.Create(body, keys);
    }
    private async Task<FolderIndex> Open()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, device); (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty(); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private sealed class Stop : Exception;
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-DirectoryCapture-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
