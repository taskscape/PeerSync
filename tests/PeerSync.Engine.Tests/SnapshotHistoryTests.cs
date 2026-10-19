using System.Text;
using System.Security.Cryptography;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class SnapshotHistoryTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-SnapshotHistory-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] device = new byte[32];
    private readonly List<string> links = [];
    private static byte[] Old => "old published private bytes"u8.ToArray();
    private static byte[] Current => "new current user bytes, longer"u8.ToArray();
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public SnapshotHistoryTests() { device[0] = 1; Directory.CreateDirectory(Root); }

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("directory")]
    public async Task ContentChangesRetainTheOldPublishedSnapshotBeforeCommittingItsSuccessor(string change)
    {
        await using var index = await PrepareAsync(); var before = index.LocalFiles.Find("file")!;
        Change(change); using var scanner = Scan(index); var result = await scanner.ScanAsync(Token);
        result.Problems.ShouldBeEmpty(); var row = index.Archive.ReadAll().Single(); row.OriginalPath.ShouldBe("file");
        File.ReadAllBytes(Disk(row.ArchivePath)).ShouldBe(Old); row.FileHash.ShouldBe(before.FileHash);
        File.GetLastWriteTimeUtc(Disk(row.ArchivePath)).Ticks.ShouldBe(before.MtimeTicks);
        index.LocalFiles.Find("file")!.LocalSequence.ShouldBeGreaterThan(before.LocalSequence); index.ReadApplyIntents().ShouldBeEmpty();
        if (change == "edit") File.ReadAllBytes(Disk("file")).ShouldBe(Current);
        else if (change == "delete") File.Exists(Disk("file")).ShouldBeFalse();
        else File.ReadAllText(Disk("file/child")).ShouldBe("child survives");
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0); index.Archive.Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("history-reserved")]
    [InlineData("history-created")]
    [InlineData("history-owned")]
    [InlineData("history-copied")]
    [InlineData("history-prepared")]
    [InlineData("history-installed")]
    [InlineData("history-before-commit")]
    [InlineData("history-committed")]
    [InlineData("before-scan-commit")]
    public async Task ReopeningAtEveryBoundaryPreservesCurrentUserDataAndCreatesExactlyOneArchive(string boundary)
    {
        long oldSequence;
        await using (var index = await PrepareAsync())
        {
            oldSequence = index.LocalFiles.Find("file")!.LocalSequence; Change("edit");
            using var scanner = Scan(index); scanner.Checkpoint = stage => { if (stage == boundary) throw new IOException("stop"); };
            await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
            index.LocalFiles.Find("file")!.LocalSequence.ShouldBe(oldSequence); File.ReadAllBytes(Disk("file")).ShouldBe(Current);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        // Recovery reconstructs history only. A subsequent user edit must remain current.
        byte[] latest = "even newer user data after interruption"u8.ToArray(); WriteCurrent(latest);
        await new FileApplier(Root, reopened, keys, device).RecoverAsync(Token);
        File.ReadAllBytes(Disk("file")).ShouldBe(latest); reopened.LocalFiles.Find("file")!.LocalSequence.ShouldBe(oldSequence);
        using var retry = Scan(reopened); await retry.ScanAsync(Token);
        File.ReadAllBytes(Disk(reopened.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
        File.ReadAllBytes(Disk("file")).ShouldBe(latest); reopened.ReadApplyIntents().ShouldBeEmpty();
        (await retry.ScanAsync(Token)).Updated.ShouldBe(0); reopened.Archive.Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepBothAndOversizeVersionsBecomePublicCopiesWithNormalRwPublication(bool oversize)
    {
        await using var index = await PrepareAsync(); Change("edit");
        var settings = oversize ? new FolderSettings { ArchiveMaxFileBytes = Old.Length - 1 } : new FolderSettings { KeepBothCopies = true };
        using var scanner = Scan(index, settings: settings); var result = await scanner.ScanAsync(Token);
        result.RequiresFollowupScan.ShouldBeTrue(); index.Archive.Count().ShouldBe(0);
        string copy = Directory.GetFiles(Root, "*.sync-conflict-*").Single(); File.ReadAllBytes(copy).ShouldBe(Old);
        index.LocalFiles.Find(Path.GetFileName(copy)).ShouldBeNull(); await scanner.ScanAsync(Token);
        index.LocalFiles.Find(Path.GetFileName(copy))!.Record.ShouldNotBeNull();
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0); Directory.GetFiles(Root, "*.sync-conflict-*").Length.ShouldBe(1);
    }

    [Fact]
    public async Task ReadOnlyLocalEditsRetainPriorForwardedBytesAndKeepConflictCopiesUnpublished()
    {
        await using var index = await PrepareAsync(); Change("edit"); using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        using var scanner = Scan(index, readOnly, new() { KeepBothCopies = true }); await scanner.ScanAsync(Token); await scanner.ScanAsync(Token);
        string copy = Directory.GetFiles(Root, "*.sync-conflict-*").Single(); File.ReadAllBytes(copy).ShouldBe(Old);
        index.LocalFiles.Find("file")!.Record.ShouldBeNull(); index.LocalFiles.Find(Path.GetFileName(copy))!.Record.ShouldBeNull();
        index.ReadIndexBatch(0).Records.ShouldBeEmpty(); index.Archive.Count().ShouldBe(0);
    }

    [Fact]
    public async Task MetadataOnlyChangesAndUncapturedLocalRoChangesDoNotInventDuplicateContentHistories()
    {
        await using var index = await PrepareAsync(); File.SetLastWriteTimeUtc(Disk("file"), DateTime.UtcNow.AddMinutes(2));
        using var scanner = Scan(index); (await scanner.ScanAsync(Token)).Updated.ShouldBe(1); index.Archive.Count().ShouldBe(0);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText()); using var roScanner = Scan(index, readOnly);
        Change("edit"); await roScanner.ScanAsync(Token); index.Archive.Count().ShouldBe(1);
        WriteCurrent("another unsigned RO edit"u8.ToArray()); await roScanner.ScanAsync(Token); index.Archive.Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptOldBlocksKeepTheSourceRowAndPendingHistoryAndWithholdPublication(bool corrupt)
    {
        await using var index = await PrepareAsync(); var before = index.LocalFiles.Find("file")!;
        byte[] hash = Blake3.Hasher.Hash(Old).AsSpan().ToArray(); string name = Convert.ToHexString(hash);
        string cache = Disk(".psp/blocks/" + name[..2] + "/" + name);
        if (corrupt) { using var writer = new FileStream(cache, FileMode.Open, FileAccess.Write); writer.Write(new byte[Old.Length]); }
        else File.Delete(cache);
        Change("edit"); using var scanner = Scan(index);
        var error = await Record.ExceptionAsync(() => scanner.ScanAsync(Token));
        if (corrupt) error.ShouldBeOfType<CryptographicException>(); else error.ShouldBeOfType<IOException>();
        index.LocalFiles.Find("file")!.LocalSequence.ShouldBe(before.LocalSequence);
        index.LocalFiles.Find("file")!.Flags.ShouldBe(LocalFileState.NeedsRescan); index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        index.ReadApplyIntents().Count().ShouldBe(1); index.Archive.Count().ShouldBe(0); File.ReadAllBytes(Disk("file")).ShouldBe(Current);
        using (var writer = new FileStream(cache, corrupt ? FileMode.Open : FileMode.CreateNew, FileAccess.Write)) { writer.Write(Old); writer.Flush(true); }
        // Scanner recovers its own kind-4 intents before walking again.
        await scanner.ScanAsync(Token); index.ReadApplyIntents().ShouldBeEmpty();
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
    }

    [Fact]
    public async Task NameCollisionsPreserveUnknownBytesAndSelectAnotherDurableSlot()
    {
        await using var index = await PrepareAsync(); Change("edit"); string? collision = null;
        using var scanner = Scan(index); scanner.Checkpoint = stage =>
        {
            if (stage != "history-prepared") return; var state = HistoryState.Decode(index.ReadApplyIntents().Single(), keys);
            collision = state.Destination; File.WriteAllText(Disk(collision), "unknown collision");
        };
        await scanner.ScanAsync(Token); File.ReadAllText(Disk(collision!)).ShouldBe("unknown collision");
        var archive = index.Archive.ReadAll().Single(); archive.ArchivePath.ShouldNotBe(collision); File.ReadAllBytes(Disk(archive.ArchivePath)).ShouldBe(Old);
    }

    [Theory]
    [InlineData("history-copied")]
    [InlineData("history-installed")]
    public async Task CancellationKeepsRecoverableHistoryAndNeverRollsBackTheUserEdit(string boundary)
    {
        await using var index = await PrepareAsync(); Change("edit"); using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var scanner = Scan(index); scanner.Checkpoint = stage => { if (stage == boundary) cancelled.Cancel(); };
        await Should.ThrowAsync<OperationCanceledException>(() => scanner.ScanAsync(cancelled.Token));
        File.ReadAllBytes(Disk("file")).ShouldBe(Current); index.ReadApplyIntents().Count().ShouldBe(1);
        using var retry = Scan(index); await retry.ScanAsync(Token); index.Archive.Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("history-installed")]
    [InlineData("history-before-commit")]
    public async Task ReadOnlyHiddenSourceAttributesAndMtimeSurviveRecovery(string boundary)
    {
        File.WriteAllBytes(Disk("file"), Old); File.SetAttributes(Disk("file"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        await using var index = await FolderIndex.OpenAsync(Database, Token); using (var initial = Scan(index)) await initial.ScanAsync(Token);
        long mtime = index.LocalFiles.Find("file")!.MtimeTicks; File.SetAttributes(Disk("file"), FileAttributes.Hidden); Change("edit");
        using var scanner = Scan(index); scanner.Checkpoint = stage => { if (stage == boundary) throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        using var retry = Scan(index); await retry.ScanAsync(Token); string archive = Disk(index.Archive.ReadAll().Single().ArchivePath);
        File.GetLastWriteTimeUtc(archive).Ticks.ShouldBe(mtime);
        (File.GetAttributes(archive) & (FileAttributes.ReadOnly | FileAttributes.Hidden)).ShouldBe(FileAttributes.ReadOnly | FileAttributes.Hidden);
        File.ReadAllBytes(archive).ShouldBe(Old); File.ReadAllBytes(Disk("file")).ShouldBe(Current);
    }

    [Fact]
    public async Task ReparseArchiveParentsCannotReceiveReconstructedUserData()
    {
        await using var index = await PrepareAsync(); Change("edit"); string outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Disk(".psp/archive"), outside); links.Add(Disk(".psp/archive")); using var scanner = Scan(index);
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        Directory.GetFileSystemEntries(outside).ShouldBeEmpty(); File.ReadAllBytes(Disk("file")).ShouldBe(Current); index.Archive.Count().ShouldBe(0);
    }

    [Fact]
    public async Task AReplacedRootCannotRecoverHistoryAgainstNewUserData()
    {
        await using var index = await PrepareAsync(); Change("edit"); using var scanner = Scan(index);
        scanner.Checkpoint = stage => { if (stage == "history-prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        Directory.Move(Root, Path.Combine(fixture, "old-root")); Directory.CreateDirectory(Root); File.WriteAllText(Disk("file"), "new root data");
        await Should.ThrowAsync<IOException>(() => new FileApplier(Root, index, keys, device).RecoverAsync(Token));
        File.ReadAllText(Disk("file")).ShouldBe("new root data"); index.ReadApplyIntents().Count().ShouldBe(1);
    }

    [Fact]
    public async Task DiagnosticsReportHistoryStagesWithoutSourceNamesBytesOrHashes()
    {
        await using var index = await PrepareAsync(); Change("edit"); using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "history", debugEnabled: true);
        using var scanner = new Scanner(Root, index, keys, device, journal: journal); await scanner.ScanAsync(Token);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("history.prepared"); log.ShouldContain("history.installed"); log.ShouldContain("history.committed");
        log.ShouldNotContain(Root); log.ShouldNotContain(Encoding.UTF8.GetString(Old)); log.ShouldNotContain(Convert.ToHexString(Blake3.Hasher.Hash(Old).AsSpan()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TamperedOrTruncatedHistoryCannotChangeCurrentUserData(bool signature)
    {
        await using var index = await PrepareAsync(); Change("edit"); using var scanner = Scan(index);
        scanner.Checkpoint = stage => { if (stage == "history-prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token)); var intent = index.ReadApplyIntents().Single(); byte[] payload = intent.Payload.ToArray();
        if (signature)
        {
            int offset = 5; for (int field = 0; field < 3; field++) offset += 4 + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
            payload[offset + 4] ^= 1;
        }
        else payload = payload[..^1];
        await index.RewriteApplyAsync(intent, payload, Token);
        var error = await Record.ExceptionAsync(() => new FileApplier(Root, index, keys, device).RecoverAsync(Token));
        if (signature) error.ShouldBeOfType<CryptographicException>(); else error.ShouldBeOfType<EndOfStreamException>();
        File.ReadAllBytes(Disk("file")).ShouldBe(Current); index.Archive.Count().ShouldBe(0); index.ReadApplyIntents().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrReplacedPreparedStagingRebuildsFromBlocksAndPreservesUnknownObjects(bool replace)
    {
        await using var index = await PrepareAsync(); Change("edit"); using var scanner = Scan(index);
        scanner.Checkpoint = stage => { if (stage == "history-prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token)); var state = HistoryState.Decode(index.ReadApplyIntents().Single(), keys);
        string temporary = Disk(state.Temporary);
        File.Move(temporary, Disk(".psp/archive/saved-prepared"));
        if (replace) File.WriteAllText(temporary, "unknown replacement");
        using var retry = Scan(index); await retry.ScanAsync(Token);
        if (replace) File.ReadAllText(temporary).ShouldBe("unknown replacement");
        File.ReadAllBytes(Disk(".psp/archive/saved-prepared")).ShouldBe(Old);
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old); File.ReadAllBytes(Disk("file")).ShouldBe(Current);
    }

    [Fact]
    public async Task EmptyPublishedVersionIsArchivedWithoutRequestingAnyBlocks()
    {
        await using var index = await PrepareAsync([]); Change("edit"); using var scanner = Scan(index); await scanner.ScanAsync(Token);
        var row = index.Archive.ReadAll().Single(); row.Size.ShouldBe(0); File.ReadAllBytes(Disk(row.ArchivePath)).ShouldBeEmpty();
    }

    [Fact]
    public async Task KeepBothDoesNotReuseAPrivateArchiveWhenAPublicCopyIsRequired()
    {
        await using var index = await PrepareAsync(); Directory.CreateDirectory(Disk(".psp/archive"));
        const string archived = ".psp/archive/earlier"; File.WriteAllBytes(Disk(archived), Old);
        await index.Archive.UpsertAsync([new(0, "file", archived, DateTime.UtcNow.Ticks, Old.Length, Blake3.Hasher.Hash(Old).AsSpan().ToArray())], Token);
        Change("edit"); using var scanner = Scan(index, settings: new() { KeepBothCopies = true }); await scanner.ScanAsync(Token);
        File.ReadAllBytes(Directory.GetFiles(Root, "*.sync-conflict-*").Single()).ShouldBe(Old);
        index.Archive.Count().ShouldBe(1); File.ReadAllBytes(Disk(archived)).ShouldBe(Old);
    }

    [Fact]
    public async Task ExistingArchiveReuseRecoversWithoutDuplicateAccountingOrChangingRetainedMetadata()
    {
        await using var index = await PrepareAsync(); Directory.CreateDirectory(Disk(".psp/archive")); const string archived = ".psp/archive/previous";
        File.WriteAllBytes(Disk(archived), Old); DateTime time = DateTime.UtcNow.AddYears(-2); File.SetLastWriteTimeUtc(Disk(archived), time);
        File.SetAttributes(Disk(archived), FileAttributes.ReadOnly | FileAttributes.Hidden);
        await index.Archive.UpsertAsync([new(0, "file", archived, DateTime.UtcNow.Ticks, Old.Length, Blake3.Hasher.Hash(Old).AsSpan().ToArray())], Token);
        Change("edit"); using var scanner = Scan(index); scanner.Checkpoint = stage => { if (stage == "history-before-commit") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        using var retry = Scan(index); await retry.ScanAsync(Token); index.Archive.Count().ShouldBe(1);
        File.GetLastWriteTimeUtc(Disk(archived)).ShouldBe(time); (File.GetAttributes(Disk(archived)) & (FileAttributes.ReadOnly | FileAttributes.Hidden))
            .ShouldBe(FileAttributes.ReadOnly | FileAttributes.Hidden);
        File.ReadAllBytes(Disk(archived)).ShouldBe(Old); File.ReadAllBytes(Disk("file")).ShouldBe(Current);
    }

    [Fact]
    public async Task AnUnverifiedHardlinkedArchiveIsPreservedWhileAnIndependentHistoryCopyIsBuilt()
    {
        await using var index = await PrepareAsync(); const string archived = ".psp/archive/linked";
        using (var tree = new SafeFileTree(Root))
        using (var destination = tree.Directory(".psp/archive", create: true, privatePath: true))
        using (var parent = tree.Directory(""))
        using (var file = parent.OpenFile("file", mutation: true)) destination.LinkHere(file, "linked");
        await index.Archive.UpsertAsync([new(0, "file", archived, DateTime.UtcNow.Ticks, Old.Length, Blake3.Hasher.Hash(Old).AsSpan().ToArray())], Token);
        Change("edit"); using var scanner = Scan(index); await scanner.ScanAsync(Token);
        File.ReadAllBytes(Disk(archived)).ShouldBe(Current); File.ReadAllBytes(Disk("file")).ShouldBe(Current);
        var fresh = index.Archive.ReadAll().Single(row => row.ArchivePath != archived);
        File.ReadAllBytes(Disk(fresh.ArchivePath)).ShouldBe(Old);
    }

    [Fact]
    public async Task RecoveryCanChooseAnotherSlotWhileAnUnknownCollisionIsLocked()
    {
        await using var index = await PrepareAsync(); Change("edit"); using var scanner = Scan(index);
        scanner.Checkpoint = stage => { if (stage == "history-prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token)); var state = HistoryState.Decode(index.ReadApplyIntents().Single(), keys);
        File.WriteAllText(Disk(state.Destination), "unknown locked bytes");
        using (var locked = new FileStream(Disk(state.Destination), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            (await new FileApplier(Root, index, keys, device).RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllText(Disk(state.Destination)).ShouldBe("unknown locked bytes");
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old); File.ReadAllBytes(Disk("file")).ShouldBe(Current);
    }

    private async Task<FolderIndex> PrepareAsync(byte[]? source = null)
    {
        File.WriteAllBytes(Disk("file"), source ?? Old); var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = Scan(index); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private Scanner Scan(FolderIndex index, FolderKeys? access = null, FolderSettings? settings = null) => new(Root, index, access ?? keys, device, settings: settings);
    private void Change(string change)
    {
        if (change == "edit") WriteCurrent(Current);
        else { File.Delete(Disk("file")); if (change == "directory") { Directory.CreateDirectory(Disk("file")); File.WriteAllText(Disk("file/child"), "child survives"); } }
    }
    private void WriteCurrent(byte[] bytes)
    {
        using var file = new FileStream(Disk("file"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.SetLength(bytes.Length); file.Write(bytes); file.Flush(true);
        File.SetLastWriteTimeUtc(Disk("file"), DateTime.UtcNow.AddMinutes(1));
    }
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-SnapshotHistory-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        foreach (string link in links) Directory.Delete(link);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(resolved, "*", options)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(resolved, recursive: true);
    }
}
