using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Crypto.Chunking;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class FileApplierTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Apply-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] own = Device(1), remote = Device(2);
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] Old = "original bytes that must survive"u8.ToArray(), New = "verified incoming data"u8.ToArray();
    public FileApplierTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task ReplacementArchivesExactOldBytesAndCommitsVerifiedIdentityChunksAndNeededTogether()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        File.SetAttributes(Path.Combine(Root, "file.txt"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        await using var index = await PrepareAsync("file.txt");
        var before = index.LocalFiles.Find("file.txt")!;
        await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own);
        (await applier.ApplyAsync("file.txt", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(New);
        var archive = index.Archive.ReadAll().Single(); File.ReadAllBytes(Disk(archive.ArchivePath)).ShouldBe(Old);
        archive.Size.ShouldBe(Old.Length); archive.FileHash.ShouldBe(Hash(Old));
        File.GetAttributes(Disk(archive.ArchivePath)).ShouldBe((FileAttributes)before.Attributes);
        var applied = index.LocalFiles.Find("file.txt")!; applied.FileId.ShouldNotBe(before.FileId);
        applied.FileHash.ShouldBe(Hash(New)); applied.LocalSequence.ShouldBeGreaterThan(before.LocalSequence);
        index.FindChunks(Hash(New)).Single().Length.ShouldBe(New.Length);
        index.Needed.Count().ShouldBe(0); index.ReadApplyIntents().ShouldBeEmpty();
        using var scanner = new Scanner(Root, index, keys, own);
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
        (await applier.ApplyAsync("file.txt", Fetch, Token)).ShouldBe(FileApplyOutcome.Unchanged);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("archived")]
    [InlineData("installed")]
    [InlineData("before-commit")]
    public async Task ReopeningAfterEachDestructiveBoundaryCompletesWithoutLosingEitherVersion(string boundary)
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using (var index = await PrepareAsync("file.txt"))
        {
            await ReceiveAsync(index, "file.txt", New);
            var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == boundary) throw new IOException("Injected stop"); } };
            await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
            index.ReadApplyIntents().Count().ShouldBe(1);
            using var scanner = new Scanner(Root, index, keys, own);
            await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
            if (boundary == "archived") File.Exists(Path.Combine(Root, "file.txt")).ShouldBeFalse();
        }
        await using (var reopened = await FolderIndex.OpenAsync(Database, Token))
        {
            var applier = new FileApplier(Root, reopened, keys, own);
            (await applier.RecoverAsync(Token)).ShouldBe(1); (await applier.RecoverAsync(Token)).ShouldBe(0);
            File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(New);
            File.ReadAllBytes(Disk(reopened.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
            reopened.Needed.Count().ShouldBe(0); reopened.ReadApplyIntents().ShouldBeEmpty();
            using var scanner = new Scanner(Root, reopened, keys, own); (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
        }
    }

    [Fact]
    public async Task MissingTemporaryAfterArchivalRestoresTheOldHandleBeforeScanning()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == "archived") throw new IOException("Injected stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        File.Delete(Directory.GetFiles(Root, "*.!psp").Single());
        (await new FileApplier(Root, index, keys, own).RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(Old); index.ReadApplyIntents().ShouldBeEmpty();
        index.Archive.Count().ShouldBe(0); index.Needed.Count().ShouldBe(1);
        using var scanner = new Scanner(Root, index, keys, own); (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    [Fact]
    public async Task UnknownConcurrentTargetAfterArchivalIsPreservedAndRetainedOriginalIsAccountedFor()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == "archived") throw new IOException("Injected stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        File.WriteAllText(Path.Combine(Root, "file.txt"), "concurrent local change");
        (await new FileApplier(Root, index, keys, own).RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllText(Path.Combine(Root, "file.txt")).ShouldBe("concurrent local change");
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
        File.ReadAllBytes(Directory.GetFiles(Root, "*.!psp").Single()).ShouldBe(New);
        index.ReadApplyIntents().ShouldBeEmpty(); index.LocalFiles.Find("file.txt")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
        index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        using var scanner = new Scanner(Root, index, keys, own); (await scanner.ScanAsync(Token)).Hashed.ShouldBe(1);
        index.LocalFiles.Find("file.txt")!.FileHash.ShouldBe(Hash(Encoding.UTF8.GetBytes("concurrent local change")));
    }

    [Fact]
    public async Task TargetEditedDuringDownloadIsRescannedInsteadOfBeingOverwritten()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own);
        (await applier.ApplyAsync("file.txt", (_, _, _) =>
        {
            File.WriteAllText(Path.Combine(Root, "file.txt"), "edited during transfer"); return ValueTask.FromResult<ReadOnlyMemory<byte>>(New);
        }, Token)).ShouldBe(FileApplyOutcome.Deferred);
        File.ReadAllText(Path.Combine(Root, "file.txt")).ShouldBe("edited during transfer");
        index.Archive.Count().ShouldBe(0); index.ReadApplyIntents().ShouldBeEmpty();
        using var scanner = new Scanner(Root, index, keys, own); (await scanner.ScanAsync(Token)).Hashed.ShouldBe(1);
    }

    [Fact]
    public async Task VerifiedStagingSurvivesCancellationAndIsReusedAfterRecoveryWithoutNetwork()
    {
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var applier = new FileApplier(Root, index, keys, own);
        await Should.ThrowAsync<OperationCanceledException>(() => applier.ApplyAsync("file.txt", (_, _, _) =>
        { cancel.Cancel(); return ValueTask.FromResult<ReadOnlyMemory<byte>>(New); }, cancel.Token));
        // The chunk may have been written before cancellation was observed. Seed
        // the owned temporary before recovery records its content fingerprint,
        // testing restart reuse independently of that timing.
        using (var seed = new FileStream(Directory.GetFiles(Root, "*.!psp").Single(), FileMode.Open, FileAccess.Write))
        { seed.Write(New); seed.SetLength(New.Length); }
        (await applier.RecoverAsync(Token)).ShouldBe(1);
        (await applier.ApplyAsync("file.txt", (_, _, _) => throw new InvalidOperationException("No fetch expected"), Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(New);
    }

    [Fact]
    public async Task RemoteDeletionArchivesTheOldVersionAndPublishesOnlyAfterTheMove()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", [], deleted: true);
        (await new FileApplier(Root, index, keys, own).ApplyAsync("file.txt", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.Exists(Path.Combine(Root, "file.txt")).ShouldBeFalse();
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
        index.LocalFiles.Find("file.txt")!.Deleted.ShouldBeTrue(); index.Chunks.Count().ShouldBe(0); index.Needed.Count().ShouldBe(0);
    }

    [Fact]
    public async Task KeepBothPreservesTheOriginalAsASyncableConflictCopy()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own, new FolderSettings { KeepBothCopies = true });
        (await applier.ApplyAsync("file.txt", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        string conflict = Directory.GetFiles(Root, "*.sync-conflict-*.txt").Single();
        conflict.ShouldContain("-0100.txt"); File.ReadAllBytes(conflict).ShouldBe(Old);
        index.Archive.Count().ShouldBe(0);
        using var scanner = new Scanner(Root, index, keys, own); (await scanner.ScanAsync(Token)).Updated.ShouldBe(1);
        index.LocalFiles.Count().ShouldBe(2);
    }

    [Fact]
    public async Task AConcurrentCreationDuringRecoveryInstallationPreservesTheNewTargetAndRetainedOriginal()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == "archived") throw new IOException("stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        var recovery = new FileApplier(Root, index, keys, own) { Checkpoint = stage =>
        { if (stage == "install-ready") File.WriteAllText(Path.Combine(Root, "file.txt"), "appeared during recovery"); } };
        (await recovery.RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllText(Path.Combine(Root, "file.txt")).ShouldBe("appeared during recovery");
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
        index.ReadApplyIntents().ShouldBeEmpty(); index.LocalFiles.Find("file.txt")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
    }

    [Fact]
    public async Task ArchiveDirectoryAndOriginalStayPinnedUntilTheIndexCommitIncludingRecovery()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        int pinChecks = 0;
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage =>
        {
            if (stage == "archived")
            {
                Should.Throw<IOException>(() => Directory.Move(Disk(".psp/archive"), Path.Combine(fixture, "moved-archive")));
                pinChecks++;
            }
            if (stage == "installed") throw new IOException("stop");
        } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        var recovery = new FileApplier(Root, index, keys, own) { Checkpoint = stage =>
        {
            if (stage != "before-commit") return;
            Should.Throw<IOException>(() => Directory.Move(Disk(".psp/archive"), Path.Combine(fixture, "moved-archive")));
            string retained = Directory.GetFiles(Disk(".psp/archive")).Single();
            Should.Throw<IOException>(() => File.Open(retained, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
            pinChecks++;
        } };
        (await recovery.RecoverAsync(Token)).ShouldBe(1); pinChecks.ShouldBe(2);
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
    }

    [Fact]
    public async Task EmptyFileInstallsWithoutFetchingAnyChunk()
    {
        await using var index = await PrepareAsync("empty.txt"); await ReceiveAsync(index, "empty.txt", []);
        (await new FileApplier(Root, index, keys, own).ApplyAsync("empty.txt", (_, _, _) => throw new InvalidOperationException("No chunks"), Token))
            .ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Path.Combine(Root, "empty.txt")).ShouldBeEmpty(); index.LocalFiles.Find("empty.txt")!.Size.ShouldBe(0);
        index.Needed.Count().ShouldBe(0); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task ANewerIndexReceivedDuringAssemblyRemainsNeededAfterTheOlderInstallCommits()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var first = index.RemotesForPath("file.txt").Single().Record;
        SignedRecordFactory.TryOpen(first, keys, out var body).ShouldBeTrue();
        byte[] latest = "an even newer remote edit"u8.ToArray();
        body.Size = (ulong)latest.Length; body.FileHash = ByteString.CopyFrom(Hash(latest)); body.Chunks.Clear();
        body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash(latest)), Length = (uint)latest.Length });
        body.Vv.Single(v => v.Device == BinaryPrimitives.ReadUInt64LittleEndian(remote)).Counter++;
        body.MtimeTicks++; body.ChangedTicks++;
        var next = SignedRecordFactory.Create(body, keys, 2); var receiver = new NeededSet(index, keys, own);
        var applier = new FileApplier(Root, index, keys, own);
        (await applier.ApplyAsync("file.txt", async (_, _, token) =>
        {
            await receiver.ApplyIndexUpdateAsync(remote, 1, 2, [next], token); return New;
        }, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(New); index.Needed.Count().ShouldBe(1);
        (await applier.ApplyAsync("file.txt", (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(latest), Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(latest); index.Needed.Count().ShouldBe(0);
        var retained = index.Archive.ReadAll().Select(a => File.ReadAllBytes(Disk(a.ArchivePath))).ToArray();
        retained.ShouldContain(bytes => bytes.SequenceEqual(Old)); retained.ShouldContain(bytes => bytes.SequenceEqual(New));
    }

    [Fact]
    public async Task CancellationInTheMissingTargetIntervalLeavesAnIntentThatRecoversWithoutNetwork()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == "archived") cancelled.Cancel(); } };
        await Should.ThrowAsync<OperationCanceledException>(() => applier.ApplyAsync("file.txt", Fetch, cancelled.Token));
        File.Exists(Path.Combine(Root, "file.txt")).ShouldBeFalse(); index.ReadApplyIntents().Count().ShouldBe(1);
        (await new FileApplier(Root, index, keys, own).RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(New);
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
    }

    [Fact]
    public async Task TargetAppearingInsideTheInstallationGapIsKeptWithoutStrandingRecovery()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage =>
        { if (stage == "archived") File.WriteAllText(Path.Combine(Root, "file.txt"), "appeared in gap"); } };
        (await applier.ApplyAsync("file.txt", Fetch, Token)).ShouldBe(FileApplyOutcome.Deferred);
        File.ReadAllText(Path.Combine(Root, "file.txt")).ShouldBe("appeared in gap");
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
        File.ReadAllBytes(Directory.GetFiles(Root, "*.!psp").Single()).ShouldBe(New);
        index.ReadApplyIntents().ShouldBeEmpty(); index.LocalFiles.Find("file.txt")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
    }

    [Fact]
    public async Task ArchiveCollisionIsReservedWithoutOverwriteAndPersistsTheNewDestination()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        string? occupied = null;
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage =>
        {
            if (stage != "prepared") return;
            occupied = ApplyState.Decode(index.ReadApplyIntents().Single(), keys).Retained;
            Directory.CreateDirectory(Path.GetDirectoryName(Disk(occupied))!); File.WriteAllText(Disk(occupied), "already retained");
        } };
        (await applier.ApplyAsync("file.txt", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllText(Disk(occupied!)).ShouldBe("already retained");
        var retained = index.Archive.ReadAll().Single(); retained.ArchivePath.ShouldContain("-2.txt");
        File.ReadAllBytes(Disk(retained.ArchivePath)).ShouldBe(Old);
    }

    [Fact]
    public async Task CorruptTemporaryAfterArchivalRestoresOriginalAndLeavesOwnedBytesForVerifiedRetry()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == "archived") throw new IOException("stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        string temporary = Directory.GetFiles(Root, "*.!psp").Single();
        using (var corrupt = new FileStream(temporary, FileMode.Open, FileAccess.Write)) corrupt.WriteByte(0);
        (await new FileApplier(Root, index, keys, own).RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(Old);
        File.Exists(temporary).ShouldBeTrue(); index.ReadApplyIntents().ShouldBeEmpty(); index.Archive.Count().ShouldBe(0);
        (await new FileApplier(Root, index, keys, own).ApplyAsync("file.txt", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
    }

    [Fact]
    public async Task UserEditToInstalledObjectBeforeRecoveryIsPreservedWithItsOriginalArchive()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == "installed") throw new IOException("stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        using (var edit = new FileStream(Path.Combine(Root, "file.txt"), FileMode.Open, FileAccess.Write)) edit.WriteByte(0);
        (await new FileApplier(Root, index, keys, own).RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllBytes(Path.Combine(Root, "file.txt"))[0].ShouldBe((byte)0);
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
        index.ReadApplyIntents().ShouldBeEmpty(); index.LocalFiles.Find("file.txt")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
    }

    [Fact]
    public async Task ReplacementOfParkedTemporaryDoesNotGrantOwnershipOfTheNewFile()
    {
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == "staging") throw new IOException("stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        await new FileApplier(Root, index, keys, own).RecoverAsync(Token);
        string temporary = Directory.GetFiles(Root, "*.!psp").Single();
        File.Move(temporary, Path.Combine(Root, "abandoned.!psp")); File.WriteAllText(temporary, "not owned");
        (await new FileApplier(Root, index, keys, own).ApplyAsync("file.txt", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllText(temporary).ShouldBe("not owned"); File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(New);
    }

    [Theory]
    [InlineData("original-cleared")]
    [InlineData("archive-moved")]
    public async Task ReadOnlyOnlyOriginalRecoversWhenStoppedBeforeItsAttributesAreRestored(string boundary)
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old); File.SetAttributes(Path.Combine(Root, "file.txt"), FileAttributes.ReadOnly);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == boundary) throw new IOException("stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        (await new FileApplier(Root, index, keys, own).RecoverAsync(Token)).ShouldBe(1);
        var archive = index.Archive.ReadAll().Single(); File.ReadAllBytes(Disk(archive.ArchivePath)).ShouldBe(Old);
        File.GetAttributes(Disk(archive.ArchivePath)).ShouldBe(FileAttributes.ReadOnly);
        File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(New);
    }

    [Theory]
    [InlineData("case-renamed")]
    [InlineData("before-commit")]
    public async Task SameContentCaseAndMetadataRecoveryIsIdempotent(string boundary)
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), New);
        await using var index = await PrepareAsync("file.txt"); var original = index.LocalFiles.Find("file.txt")!;
        await ReceiveAsync(index, "FILE.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == boundary) throw new IOException("stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        (await new FileApplier(Root, index, keys, own).RecoverAsync(Token)).ShouldBe(1);
        Path.GetFileName(Directory.GetFiles(Root).Single()).ShouldBe("FILE.txt");
        index.LocalFiles.Find("file.txt")!.FileId.ShouldBe(original.FileId); index.Archive.Count().ShouldBe(0);
        using var scanner = new Scanner(Root, index, keys, own); (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    [Fact]
    public async Task ReplacedRootCannotRecoverAnIntentFromTheOriginalRoot()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == "archived") throw new IOException("stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        Directory.Move(Root, Path.Combine(fixture, "old-root")); Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "file.txt"), "new root data");
        await Should.ThrowAsync<IOException>(() => new FileApplier(Root, index, keys, own).RecoverAsync(Token));
        File.ReadAllText(Path.Combine(Root, "file.txt")).ShouldBe("new root data"); index.ReadApplyIntents().Count().ShouldBe(1);
        Directory.GetFiles(Path.Combine(fixture, "old-root", ".psp", "archive")).Select(File.ReadAllBytes).Single().ShouldBe(Old);
    }

    [Fact]
    public async Task LockedTargetLeavesIntentAndLogsCorrelatedNativeErrorWithoutNamesOrPayloads()
    {
        const string name = "private-do-not-log.txt";
        File.WriteAllBytes(Path.Combine(Root, name), Old);
        await using var index = await PrepareAsync(name); await ReceiveAsync(index, name, New);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "applier", debugEnabled: true);
        var applier = new FileApplier(Root, index, keys, own, journal: journal);
        using (File.Open(Path.Combine(Root, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { await Should.ThrowAsync<System.ComponentModel.Win32Exception>(() => applier.ApplyAsync(name, Fetch, Token)); }
        index.ReadApplyIntents().Count().ShouldBe(1); File.ReadAllBytes(Path.Combine(Root, name)).ShouldBe(Old);
        string logs = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        logs.ShouldContain("apply.failed"); logs.ShouldContain("\"nativeError\":32"); logs.ShouldContain("apply.staging");
        logs.ShouldNotContain(name); logs.ShouldNotContain(Encoding.UTF8.GetString(Old)); logs.ShouldNotContain(Convert.ToHexString(Hash(New)));
        journal.DroppedEvents.ShouldBe(0);
        (await applier.RecoverAsync(Token)).ShouldBe(1); (await applier.ApplyAsync(name, Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
    }

    [Fact]
    public async Task TruncatedRecoveryStateIsRejectedBeforeFilesystemMutation()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), Old);
        await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage => { if (stage == "prepared") throw new IOException("stop"); } };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("file.txt", Fetch, Token));
        var intent = index.ReadApplyIntents().Single(); await index.RewriteApplyAsync(intent, intent.Payload[..^1].ToArray(), Token);
        await Should.ThrowAsync<EndOfStreamException>(() => new FileApplier(Root, index, keys, own).RecoverAsync(Token));
        File.ReadAllBytes(Path.Combine(Root, "file.txt")).ShouldBe(Old); index.Archive.Count().ShouldBe(0); index.ReadApplyIntents().Count().ShouldBe(1);
    }

    [Fact]
    public async Task SameContentCaseAndMetadataChangeRequiresNoNetworkAndKeepsIdentity()
    {
        File.WriteAllBytes(Path.Combine(Root, "file.txt"), New);
        await using var index = await PrepareAsync("file.txt"); var original = index.LocalFiles.Find("file.txt")!;
        await ReceiveAsync(index, "FILE.txt", New);
        var applier = new FileApplier(Root, index, keys, own);
        (await applier.ApplyAsync("file.txt", (_, _, _) => throw new InvalidOperationException("No fetch expected"), Token)).ShouldBe(FileApplyOutcome.Applied);
        Path.GetFileName(Directory.GetFiles(Root).Single()).ShouldBe("FILE.txt");
        index.LocalFiles.Find("file.txt")!.FileId.ShouldBe(original.FileId); index.Archive.Count().ShouldBe(0);
        using var scanner = new Scanner(Root, index, keys, own); (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledDeletionWaitsForDeclaredCompleteIndexWithoutReservingOrMovingData(bool announce)
    {
        File.WriteAllBytes(Disk("file.txt"), Old); await using var index = await PrepareAsync("file.txt");
        await ReceiveAsync(index, "file.txt", [], deleted: true); var needed = new NeededSet(index, keys, own);
        if (announce) await needed.AnnounceIndexRangeAsync(remote, 2, Token);
        var applier = new FileApplier(Root, index, keys, own);
        var waiting = await applier.ApplyScheduledAsync("file.txt", (_, _, _, _) => throw new InvalidOperationException("No download"), Token);
        waiting.ShouldBe(new(FileApplyOutcome.Deferred, false)); index.ReadApplyIntents().ShouldBeEmpty();
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Old); index.Archive.Count().ShouldBe(0);
        await needed.AnnounceIndexRangeAsync(remote, 2, Token); await needed.ApplyIndexUpdateAsync(remote, 1, 2, [], Token);
        (await applier.ApplyScheduledAsync("file.txt", (_, _, _, _) => throw new InvalidOperationException("No download"), Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        File.Exists(Disk("file.txt")).ShouldBeFalse(); File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
    }

    [Fact]
    public async Task ScheduledDeletionHoldsRangeGuardThroughNativeMovesAndCommit()
    {
        File.WriteAllBytes(Disk("file.txt"), Old); await using var index = await PrepareAsync("file.txt");
        await ReceiveAsync(index, "file.txt", [], deleted: true); var needed = new NeededSet(index, keys, own);
        await needed.AnnounceIndexRangeAsync(remote, 1, Token); Task<RemoteIndexRange>? announcement = null;
        var applier = new FileApplier(Root, index, keys, own) { Checkpoint = stage =>
        {
            if (stage != "prepared") return; announcement = needed.AnnounceIndexRangeAsync(remote, 2, Token); announcement.IsCompleted.ShouldBeFalse();
        } };
        (await applier.ApplyScheduledAsync("file.txt", (_, _, _, _) => throw new InvalidOperationException(), Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        (await announcement!.WaitAsync(TimeSpan.FromSeconds(5), Token)).Complete.ShouldBeFalse(); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task ScheduledFetchRoutesActualWinnerAndKeepsIndexReceiptAvailableDuringLiveDownload()
    {
        File.WriteAllBytes(Disk("file.txt"), Old); await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var needed = new NeededSet(index, keys, own); byte[] selected = Device(3);
        var body = VersionView.Open(index.RemotesForPath("file.txt").Single().Record, keys, remote).ExportBody();
        body.ModifiedBy = ByteString.CopyFrom(selected); body.Vv.Add(new VvEntry { Device = 3, Counter = 1 });
        await needed.ApplyIndexUpdateAsync(selected, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
        await needed.AnnounceIndexRangeAsync(selected, 2, Token); // A live transfer does not wait for the rest of this range.
        var result = await new FileApplier(Root, index, keys, own).ApplyScheduledAsync("file.txt", async (peer, hash, length, token) =>
        {
            peer.ToArray().ShouldBe(selected); hash.ToArray().ShouldBe(Hash(New)); length.ShouldBe(New.Length);
            await needed.ApplyIndexUpdateAsync(selected, 1, 2, [], token).WaitAsync(TimeSpan.FromSeconds(5), token);
            return New;
        }, Token);
        result.ShouldBe(new(FileApplyOutcome.Applied, false)); File.ReadAllBytes(Disk("file.txt")).ShouldBe(New);
        index.ReadRemoteIndexRange(selected)!.Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task ScheduledSelectionRechecksAfterWaitingAndReleasesReferenceGuardIfWinnerBecomesLive()
    {
        File.WriteAllBytes(Disk("file.txt"), Old); await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", [], deleted: true);
        var needed = new NeededSet(index, keys, own);
        var body = VersionView.Open(index.RemotesForPath("file.txt").Single().Record, keys, remote).ExportBody();
        body.Deleted = false; body.Size = (ulong)New.Length; body.FileHash = ByteString.CopyFrom(Hash(New)); body.ChunkClass = ChunkParameters.ClassFor(New.Length);
        body.Chunks.Add(new Chunk { Hash = body.FileHash, Length = (uint)New.Length }); body.Vv.Single(v => v.Device == 2).Counter++;
        Task<RemoteIndexResult> update; Task<ScheduledFileApplyResult> apply;
        using (await FolderReferenceGate.AcquireAsync(index, Token))
        {
            update = needed.ApplyIndexUpdateAsync(remote, 1, 2, [SignedRecordFactory.Create(body, keys, 2)], Token);
            apply = new FileApplier(Root, index, keys, own).ApplyScheduledAsync("file.txt", async (_, _, _, token) =>
            {
                await needed.AnnounceIndexRangeAsync(remote, 2, token).WaitAsync(TimeSpan.FromSeconds(5), token); return New;
            }, Token);
            apply.IsCompleted.ShouldBeFalse();
        }
        await update; (await apply.WaitAsync(TimeSpan.FromSeconds(10), Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(New);
    }

    [Fact]
    public async Task ScheduledResultRequestsScanForPublicPreservedCopy()
    {
        File.WriteAllBytes(Disk("file.txt"), Old); await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var applier = new FileApplier(Root, index, keys, own, new FolderSettings { KeepBothCopies = true });
        var result = await applier.ApplyScheduledAsync("file.txt", (_, hash, length, token) => Fetch(hash, length, token), Token);
        result.ShouldBe(new(FileApplyOutcome.Applied, true)); Directory.GetFiles(Root).Select(File.ReadAllBytes).ShouldContain(data => data.SequenceEqual(Old));
        index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task ScheduledResultRequestsScanWhenUserChangesTargetDuringDownload()
    {
        File.WriteAllBytes(Disk("file.txt"), Old); await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", New);
        var result = await new FileApplier(Root, index, keys, own).ApplyScheduledAsync("file.txt", (_, hash, length, token) =>
        {
            File.WriteAllText(Disk("file.txt"), "changed by user"); return Fetch(hash, length, token);
        }, Token);
        result.ShouldBe(new(FileApplyOutcome.Deferred, true)); File.ReadAllText(Disk("file.txt")).ShouldBe("changed by user");
        index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellingScheduledDeletionWhileWaitingForReferencesLeavesNoIntentAndReleasesFilesystemGate()
    {
        File.WriteAllBytes(Disk("file.txt"), Old); await using var index = await PrepareAsync("file.txt"); await ReceiveAsync(index, "file.txt", [], deleted: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token); var applier = new FileApplier(Root, index, keys, own);
        using (await FolderReferenceGate.AcquireAsync(index, Token))
        {
            var waiting = applier.ApplyScheduledAsync("file.txt", (_, _, _, _) => throw new InvalidOperationException(), cancellation.Token);
            waiting.IsCompleted.ShouldBeFalse(); cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        }
        index.ReadApplyIntents().ShouldBeEmpty(); File.ReadAllBytes(Disk("file.txt")).ShouldBe(Old);
        await new NeededSet(index, keys, own).AnnounceIndexRangeAsync(remote, 1, Token);
        (await applier.ApplyScheduledAsync("file.txt", (_, _, _, _) => throw new InvalidOperationException(), Token)
            .WaitAsync(TimeSpan.FromSeconds(5), Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("archived")]
    public async Task ScheduledDeletionRecoveryRetainsItsBarrierAcrossANewerSenderTarget(string boundary)
    {
        File.WriteAllBytes(Disk("file.txt"),Old); await using var index=await PrepareAsync("file.txt");
        await ReceiveAsync(index,"file.txt",[],deleted:true); var needed=new NeededSet(index,keys,own); await needed.AnnounceIndexRangeAsync(remote,1,Token);
        var interrupted=new FileApplier(Root,index,keys,own) { Checkpoint=stage=>{ if(stage==boundary) throw new IOException("injected"); } };
        await Should.ThrowAsync<IOException>(()=>interrupted.ApplyScheduledAsync("file.txt",(_,_,_,_)=>throw new InvalidOperationException(),Token));
        var intent=index.ReadApplyIntents().Single(); BinaryPrimitives.ReadInt32LittleEndian(intent.Payload).ShouldBe(5);
        ApplyState.Decode(intent,keys).ScheduledDeletion.ShouldBeTrue();
        await needed.AnnounceIndexRangeAsync(remote,2,Token);
        var recovery=new FileApplier(Root,index,keys,own);
        await Should.ThrowAsync<IndexRangeRecoveryPendingException>(()=>recovery.RecoverAsync(Token)); index.ReadApplyIntents().Count().ShouldBe(1);
        File.Exists(Disk("file.txt")).ShouldBe(boundary=="prepared");
        if(boundary=="prepared") File.ReadAllBytes(Disk("file.txt")).ShouldBe(Old);
        else File.ReadAllBytes(Disk(ApplyState.Decode(intent,keys).Retained)).ShouldBe(Old);
        await needed.ApplyIndexUpdateAsync(remote,1,2,[],Token); Task<RemoteIndexRange>? queued=null;
        recovery.Checkpoint=stage=>
        {
            if(stage!="recover-complete") return; queued=needed.AnnounceIndexRangeAsync(remote,3,Token); queued.IsCompleted.ShouldBeFalse();
        };
        (await recovery.RecoverAsync(Token)).ShouldBe(1); (await queued!.WaitAsync(TimeSpan.FromSeconds(5),Token)).Complete.ShouldBeFalse();
        File.Exists(Disk("file.txt")).ShouldBeFalse(); index.ReadApplyIntents().ShouldBeEmpty();
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
    }

    [Fact]
    public async Task UnpreparedScheduledDeletionCanBeAbandonedWhileANewerRangeIsIncomplete()
    {
        File.WriteAllBytes(Disk("file.txt"),Old); await using var index=await PrepareAsync("file.txt");
        await ReceiveAsync(index,"file.txt",[],deleted:true); var needed=new NeededSet(index,keys,own); await needed.AnnounceIndexRangeAsync(remote,1,Token);
        var applier=new FileApplier(Root,index,keys,own) { Checkpoint=stage=>{ if(stage=="staging") throw new IOException("injected"); } };
        await Should.ThrowAsync<IOException>(()=>applier.ApplyScheduledAsync("file.txt",(_,_,_,_)=>throw new InvalidOperationException(),Token));
        await needed.AnnounceIndexRangeAsync(remote,2,Token); (await new FileApplier(Root,index,keys,own).RecoverAsync(Token)).ShouldBe(1);
        index.ReadApplyIntents().ShouldBeEmpty(); File.ReadAllBytes(Disk("file.txt")).ShouldBe(Old);
    }

    [Fact]
    public async Task RecoveryRangeReadFailureReleasesReferenceGateForIndexReceipt()
    {
        File.WriteAllBytes(Disk("file.txt"),Old); await using var index=await PrepareAsync("file.txt");
        await ReceiveAsync(index,"file.txt",[],deleted:true); var needed=new NeededSet(index,keys,own); await needed.AnnounceIndexRangeAsync(remote,1,Token);
        var applier=new FileApplier(Root,index,keys,own) { Checkpoint=stage=>{ if(stage=="prepared") throw new IOException("injected"); } };
        await Should.ThrowAsync<IOException>(()=>applier.ApplyScheduledAsync("file.txt",(_,_,_,_)=>throw new InvalidOperationException(),Token));
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource=Database,Pooling=false }.ToString()))
        {
            connection.Open(); using var command=connection.CreateCommand(); command.CommandText="DROP TABLE remote_index_ranges;"; command.ExecuteNonQuery();
        }
        await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(()=>new FileApplier(Root,index,keys,own).RecoverAsync(Token));
        (await needed.ApplyIndexUpdateAsync(remote,1,2,[],Token).WaitAsync(TimeSpan.FromSeconds(5),Token)).Applied.ShouldBeTrue();
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Old); index.ReadApplyIntents().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentParentTombstoneRecoveryRetainsRangeBarrierAndConcurrentChild(bool concurrentChild)
    {
        await using var index=await PrepareAsync("parent/child"); await ReceiveAsync(index,"parent/child",[],deleted:true);
        var needed=new NeededSet(index,keys,own); await needed.AnnounceIndexRangeAsync(remote,1,Token);
        var applier=new FileApplier(Root,index,keys,own) { Checkpoint=stage=>{ if(stage=="prepared") throw new IOException("injected"); } };
        await Should.ThrowAsync<IOException>(()=>applier.ApplyScheduledAsync("parent/child",(_,_,_,_)=>throw new InvalidOperationException(),Token));
        if(concurrentChild) { Directory.CreateDirectory(Disk("parent")); File.WriteAllBytes(Disk("parent/child"),New); }
        await needed.AnnounceIndexRangeAsync(remote,2,Token);
        await Should.ThrowAsync<IndexRangeRecoveryPendingException>(()=>new FileApplier(Root,index,keys,own).RecoverAsync(Token));
        index.ReadApplyIntents().Count().ShouldBe(1);
        await needed.ApplyIndexUpdateAsync(remote,1,2,[],Token); (await new FileApplier(Root,index,keys,own).RecoverAsync(Token)).ShouldBe(1);
        index.ReadApplyIntents().ShouldBeEmpty();
        if(concurrentChild) File.ReadAllBytes(Disk("parent/child")).ShouldBe(New);
        else { Directory.Exists(Disk("parent")).ShouldBeFalse(); index.LocalFiles.Find("parent/child")!.Deleted.ShouldBeTrue(); }
    }

    [Fact]
    public async Task MissingAncestorDefersKnownLiveMetadataUntilScannerArchivesPublishedBytes()
    {
        Directory.CreateDirectory(Disk("parent")); File.WriteAllBytes(Disk("parent/child"),Old);
        await using var index=await PrepareAsync("parent/child"); await ReceiveAsync(index,"parent/child",[],deleted:true);
        var needed=new NeededSet(index,keys,own); await needed.AnnounceIndexRangeAsync(remote,1,Token);
        Directory.Delete(Disk("parent"),true); File.WriteAllText(Disk("parent"),"keep ancestor");
        var result=await new FileApplier(Root,index,keys,own).ApplyScheduledAsync("parent/child",(_,_,_,_)=>throw new InvalidOperationException(),Token);
        result.Outcome.ShouldBe(FileApplyOutcome.Deferred); result.RequiresScan.ShouldBeTrue(); index.LocalFiles.Find("parent/child")!.Deleted.ShouldBeFalse();
        using(var scanner=new Scanner(Root,index,keys,own)) await scanner.ScanAsync(Token);
        index.Archive.ReadAll().Any(row=>File.ReadAllBytes(Disk(row.ArchivePath)).AsSpan().SequenceEqual(Old)).ShouldBeTrue();
        await needed.RebuildAsync(Token); await new FileApplier(Root,index,keys,own).ApplyScheduledAsync("parent/child",(_,_,_,_)=>throw new InvalidOperationException(),Token);
        index.Needed.Find("parent/child").ShouldBeNull(); File.ReadAllText(Disk("parent")).ShouldBe("keep ancestor");
    }

    private async Task<FolderIndex> PrepareAsync(string path)
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, own); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private async Task ReceiveAsync(FolderIndex index, string path, byte[] bytes, bool deleted = false)
    {
        var vector = index.LocalFiles.Find(path)?.Vector ?? new Dictionary<ulong, ulong>();
        var body = new FileRecordBody { Path = path, Type = 0, Deleted = deleted, Size = (ulong)bytes.Length,
            MtimeTicks = DateTime.UtcNow.AddMinutes(1).Ticks, ChangedTicks = DateTime.UtcNow.AddMinutes(1).Ticks,
            ModifiedBy = ByteString.CopyFrom(remote), ChunkClass = ChunkParameters.ClassFor(bytes.Length) };
        foreach (var (device, count) in VersionVector.Increment(vector, BinaryPrimitives.ReadUInt64LittleEndian(remote)))
            body.Vv.Add(new VvEntry { Device = device, Counter = count });
        if (!deleted)
        {
            body.FileHash = ByteString.CopyFrom(Hash(bytes));
            if (bytes.Length != 0) body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash(bytes)), Length = (uint)bytes.Length });
        }
        var record = SignedRecordFactory.Create(body, keys, 1);
        await new NeededSet(index, keys, own).ApplyIndexUpdateAsync(remote, 0, 1, [record], Token);
    }
    private static ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> _, int length, CancellationToken token)
    { token.ThrowIfCancellationRequested(); length.ShouldBe(New.Length); return ValueTask.FromResult<ReadOnlyMemory<byte>>(New); }
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private static byte[] Hash(byte[] bytes) => Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    private static byte[] Device(byte value) { var result = new byte[32]; result[0] = value; return result; }
    public void Dispose()
    {
        keys.Dispose();
        foreach (string file in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fixture, recursive: true);
    }
}
