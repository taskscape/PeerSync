using System.Buffers.Binary;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Engine.Native;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;
using SignedRecord = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Tests;

public sealed class DirectoryApplierTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryApply-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] own = Device(1), remote = Device(2);
    private readonly List<string> links = [];
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private ulong ShortDevice => BinaryPrimitives.ReadUInt64LittleEndian(own);
    public DirectoryApplierTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task ANewDirectoryCommitsItsSignedMetadataAndIdentityWithoutAnOwnWriteVersion()
    {
        await using var index = await PrepareAsync(); var record = await ReceiveAsync(index, "dir", attributes: (uint)FileAttributes.Hidden);
        var result = await Directories(index).ApplyAsync("dir", Token);
        result.ShouldBe(new(FileApplyOutcome.Applied)); Directory.Exists(Disk("dir")).ShouldBeTrue();
        var row = index.LocalFiles.Find("dir")!; row.Type.ShouldBe(1U); row.FileId!.Length.ShouldBe(16); row.FileHash.ShouldBeNull();
        (row.Attributes & (uint)FileAttributes.Hidden).ShouldBe((uint)FileAttributes.Hidden);
        row.Record!.Body.ToArray().ShouldBe(record.Body.ToArray()); index.Needed.Count().ShouldBe(0); index.ReadApplyIntents().ShouldBeEmpty();
        using var scanner = Scanner(index); (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
        (await Directories(index).ApplyAsync("dir", Token)).Outcome.ShouldBe(FileApplyOutcome.Unchanged);
    }

    [Fact]
    public async Task CaseAndAttributeMetadataChangesKeepTheExistingDirectoryAndChildBytes()
    {
        Directory.CreateDirectory(Disk("dir")); File.WriteAllText(Disk("dir/child"), "child data");
        await using var index = await PrepareAsync(); var before = index.LocalFiles.Find("dir")!;
        await ReceiveAsync(index, "DIR", attributes: (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden));
        (await Directories(index).ApplyAsync("dir", Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        Path.GetFileName(Directory.GetDirectories(Root).Single()).ShouldBe("DIR");
        File.ReadAllText(Disk("DIR/child")).ShouldBe("child data"); index.LocalFiles.Find("dir")!.FileId.ShouldBe(before.FileId);
        var attrs = File.GetAttributes(Disk("DIR")); (attrs & FileAttributes.Hidden).ShouldBe(FileAttributes.Hidden);
        (attrs & FileAttributes.ReadOnly).ShouldBe(FileAttributes.ReadOnly);
        long childSequence = index.LocalFiles.Find("dir/child")!.LocalSequence;
        using var scanner = Scanner(index); (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
        index.LocalFiles.Find("dir/child")!.LocalSequence.ShouldBe(childSequence);
    }

    [Fact]
    public async Task LocalParentCaseEditPublishesOnlyTheDirectoryButLeafCaseEditIsStillPublished()
    {
        Directory.CreateDirectory(Disk("dir")); File.WriteAllText(Disk("dir/child"), "data");
        await using var index = await PrepareAsync(); var child = index.LocalFiles.Find("dir/child")!;
        using (var tree = new SafeFileTree(Root))
        using (var parent = tree.Directory(""))
        using (var directory = parent.OpenNode("dir")) parent.MoveHere(directory, "DIR");
        using var scanner = Scanner(index); (await scanner.ScanAsync(Token)).Updated.ShouldBe(1);
        index.LocalFiles.Find("dir")!.Path.ShouldBe("DIR");
        index.LocalFiles.Find("dir/child")!.Record!.Body.ToArray().ShouldBe(child.Record!.Body.ToArray());
        index.LocalFiles.Find("dir/child")!.LocalSequence.ShouldBe(child.LocalSequence);
        var needed = new NeededSet(index, keys, own); await needed.RebuildAsync(Token); index.Needed.Count().ShouldBe(0);
        using (var tree = new SafeFileTree(Root))
        using (var parent = tree.Directory("DIR"))
        using (var file = parent.OpenFile("child", mutation: true)) parent.MoveHere(file, "CHILD");
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(1);
        index.LocalFiles.Find("dir/child")!.Vector[ShortDevice].ShouldBe(child.Vector[ShortDevice] + 1);
        index.LocalFiles.Find("dir/child")!.Path.ShouldBe("DIR/CHILD");
    }

    [Fact]
    public async Task EmptyDirectoryDeletionCommitsATombstoneAfterNativeRemoval()
    {
        Directory.CreateDirectory(Disk("dir")); await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        (await Directories(index).ApplyAsync("dir", Token)).ShouldBe(new(FileApplyOutcome.Applied));
        Directory.Exists(Disk("dir")).ShouldBeFalse(); index.LocalFiles.Find("dir")!.Deleted.ShouldBeTrue();
        index.LocalFiles.Find("dir")!.FileId.ShouldBeNull(); index.ReadApplyIntents().ShouldBeEmpty(); index.Needed.Count().ShouldBe(0);
        using var scanner = Scanner(index); (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    [Fact]
    public async Task AlreadyMissingDirectoryCanAdoptTheSignedDeletion()
    {
        await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        (await Directories(index).ApplyAsync("dir", Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        index.LocalFiles.Find("dir")!.Deleted.ShouldBeTrue(); index.Needed.Count().ShouldBe(0);
    }

    [Fact]
    public async Task SignedKnownChildDeletionPrecedesParentAndUnknownChildrenSurviveAndSyncBack()
    {
        Directory.CreateDirectory(Disk("dir")); File.WriteAllText(Disk("dir/known"), "known old version");
        await using var index = await PrepareAsync(); var known = index.LocalFiles.Find("dir/known")!;
        var parent = DirectoryRecord(index, "dir", deleted: true, sequence: 1);
        SignedRecordFactory.TryOpen(known.Record!, keys, out var child).ShouldBeTrue();
        child.Deleted = true; child.Size = 0; child.FileHash = ByteString.Empty; child.Chunks.Clear(); child.ModifiedBy = ByteString.CopyFrom(remote);
        child.ChangedTicks = DateTime.UtcNow.AddMinutes(1).Ticks;
        child.Vv.Clear(); child.Vv.AddRange(VersionVector.Increment(known.Vector, BinaryPrimitives.ReadUInt64LittleEndian(remote))
            .Select(v => new VvEntry { Device = v.Key, Counter = v.Value }));
        var deletedChild = SignedRecordFactory.Create(child, keys, 2);
        var needed = new NeededSet(index, keys, own);
        await needed.ApplyIndexUpdateAsync(remote, 0, 2, [parent, deletedChild], Token);
        File.WriteAllText(Disk("dir/unknown"), "new unscanned child");
        (await Files(index).ApplyAsync("dir/known", (_, _, _) => throw new InvalidOperationException("No download"), Token)).ShouldBe(FileApplyOutcome.Applied);
        var result = await Directories(index).ApplyAsync("dir", Token);
        result.ShouldBe(new(FileApplyOutcome.Applied, RequiresScan: true));
        Directory.Exists(Disk("dir")).ShouldBeTrue(); File.Exists(Disk("dir/known")).ShouldBeFalse();
        File.ReadAllText(Disk("dir/unknown")).ShouldBe("new unscanned child");
        File.ReadAllText(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe("known old version");
        index.LocalFiles.Find("dir")!.Deleted.ShouldBeTrue();
        using var scanner = Scanner(index); await scanner.ScanAsync(Token); await needed.RebuildAsync(Token);
        var revived = index.LocalFiles.Find("dir")!; revived.Deleted.ShouldBeFalse(); revived.Vector[ShortDevice].ShouldBe(2UL);
        revived.Vector[BinaryPrimitives.ReadUInt64LittleEndian(remote)].ShouldBe(1UL);
        index.LocalFiles.Find("dir/unknown")!.Record.ShouldNotBeNull(); index.Needed.Count().ShouldBe(0);
    }

    [Fact]
    public async Task AReadOnlyPeerKeepsNonemptyDirectoryAsAnUnpublishedLocalChange()
    {
        Directory.CreateDirectory(Disk("dir")); await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        File.WriteAllText(Disk("dir/unknown"), "local child"); using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        (await Directories(index, readOnly).ApplyAsync("dir", Token)).RequiresScan.ShouldBeTrue();
        using var scanner = Scanner(index, readOnly); await scanner.ScanAsync(Token);
        index.LocalFiles.Find("dir")!.Record.ShouldBeNull(); index.LocalFiles.Find("dir/unknown")!.Record.ShouldBeNull();
        index.ReadIndexBatch(0).Records.ShouldBeEmpty(); File.ReadAllText(Disk("dir/unknown")).ShouldBe("local child");
    }

    [Fact]
    public async Task AChildCreatedImmediatelyBeforeNativeDeletionIsKept()
    {
        Directory.CreateDirectory(Disk("dir")); await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        var applier = Directories(index); applier.Checkpoint = stage => { if (stage == "before-delete") File.WriteAllText(Disk("dir/new"), "concurrent"); };
        (await applier.ApplyAsync("dir", Token)).RequiresScan.ShouldBeTrue();
        File.ReadAllText(Disk("dir/new")).ShouldBe("concurrent"); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("created")]
    [InlineData("prepared")]
    [InlineData("metadata")]
    [InlineData("before-commit")]
    public async Task DirectoryCreationReopensThroughTheSharedRecoveryEntryPoint(string boundary)
    {
        await using (var index = await PrepareAsync())
        {
            await ReceiveAsync(index, "dir"); var applier = Directories(index);
            applier.Checkpoint = stage => { if (stage == boundary) throw new IOException("injected stop"); };
            await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("dir", Token));
            BinaryPrimitives.ReadInt32LittleEndian(index.ReadApplyIntents().Single().Payload).ShouldBe(3);
            using var scanner = Scanner(index); await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        (await Files(reopened).RecoverAsync(Token)).ShouldBe(1); (await Files(reopened).RecoverAsync(Token)).ShouldBe(0);
        reopened.ReadApplyIntents().ShouldBeEmpty();
        if (boundary is "staging" or "created")
        { using var scanner = Scanner(reopened); await scanner.ScanAsync(Token); }
        else
        {
            Directory.Exists(Disk("dir")).ShouldBeTrue(); reopened.LocalFiles.Find("dir")!.Type.ShouldBe(1U);
            reopened.Needed.Count().ShouldBe(0);
        }
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("before-delete")]
    [InlineData("deleted")]
    [InlineData("before-commit")]
    public async Task EmptyDeletionReopensAndCommitsExactlyOnce(string boundary)
    {
        Directory.CreateDirectory(Disk("dir"));
        await using (var index = await PrepareAsync())
        {
            await ReceiveAsync(index, "dir", deleted: true); var applier = Directories(index);
            applier.Checkpoint = stage => { if (stage == boundary) throw new IOException("injected stop"); };
            await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("dir", Token));
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        (await Files(reopened).RecoverAsync(Token)).ShouldBe(1); var sequence = reopened.LocalFiles.Find("dir")!.LocalSequence;
        (await Files(reopened).RecoverAsync(Token)).ShouldBe(0); reopened.LocalFiles.Find("dir")!.LocalSequence.ShouldBe(sequence);
        Directory.Exists(Disk("dir")).ShouldBeFalse(); reopened.LocalFiles.Find("dir")!.Deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task UnknownDirectoryRecreatedAfterDeletionIsPreservedAndRescanned()
    {
        Directory.CreateDirectory(Disk("dir")); await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        var applier = Directories(index); applier.Checkpoint = stage =>
        { if (stage == "deleted") { Directory.CreateDirectory(Disk("dir")); File.WriteAllText(Disk("dir/new"), "new tree"); } };
        (await applier.ApplyAsync("dir", Token)).ShouldBe(new(FileApplyOutcome.Deferred, RequiresScan: true));
        File.ReadAllText(Disk("dir/new")).ShouldBe("new tree"); index.ReadApplyIntents().ShouldBeEmpty();
        index.LocalFiles.Find("dir")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
    }

    [Fact]
    public async Task RecoveryPreservesAChangedDirectoryIdentityAndAnUnexpectedFile()
    {
        Directory.CreateDirectory(Disk("dir")); await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        var applier = Directories(index); applier.Checkpoint = stage => { if (stage == "prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("dir", Token));
        Directory.Move(Disk("dir"), Disk("preserved-dir")); File.WriteAllText(Disk("dir"), "new file");
        (await Files(index).RecoverAsync(Token)).ShouldBe(1); File.ReadAllText(Disk("dir")).ShouldBe("new file");
        Directory.Exists(Disk("preserved-dir")).ShouldBeTrue(); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplacedRootFailsRecoveryWithoutMutatingEitherTree()
    {
        Directory.CreateDirectory(Disk("dir")); await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        var applier = Directories(index); applier.Checkpoint = stage => { if (stage == "prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("dir", Token));
        string old = Path.Combine(fixture, "old-root"); Directory.Move(Root, old); Directory.CreateDirectory(Disk("dir"));
        await Should.ThrowAsync<IOException>(() => Files(index).RecoverAsync(Token));
        Directory.Exists(Path.Combine(old, "dir")).ShouldBeTrue(); Directory.Exists(Disk("dir")).ShouldBeTrue();
        index.ReadApplyIntents().Count().ShouldBe(1);
    }

    [Fact]
    public async Task ReparseDirectoryIsNeverTraversedOrDeleted()
    {
        await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        string outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "file"), "preserve");
        string link = Disk("dir"); Directory.CreateSymbolicLink(link, outside); links.Add(link);
        (await Record.ExceptionAsync(() => Directories(index).ApplyAsync("dir", Token))).ShouldNotBeNull();
        File.ReadAllText(Path.Combine(outside, "file")).ShouldBe("preserve"); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task FileDirectoryTypeConflictDefersWithoutDiscardingTheFileOrCreatingAFalseVersion()
    {
        File.WriteAllText(Disk("dir"), "current file"); await using var index = await PrepareAsync(); var before = index.LocalFiles.Find("dir")!;
        await ReceiveAsync(index, "dir"); (await Directories(index).ApplyAsync("dir", Token)).Outcome.ShouldBe(FileApplyOutcome.Deferred);
        File.ReadAllText(Disk("dir")).ShouldBe("current file"); index.ReadApplyIntents().ShouldBeEmpty();
        index.LocalFiles.Find("dir")!.LocalSequence.ShouldBe(before.LocalSequence);
    }

    [Fact]
    public async Task ADeleteSharingDirectoryReaderDefersCompletionUntilTheNativeNameDisappears()
    {
        Directory.CreateDirectory(Disk("dir")); await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        using (var reader = NativeFiles.CreateFile(Disk("dir"), 0x81, 7, 0, 3, 0x02200000, 0))
        {
            reader.IsInvalid.ShouldBeFalse();
            await Should.ThrowAsync<System.ComponentModel.Win32Exception>(() => Directories(index).ApplyAsync("dir", Token));
            index.ReadApplyIntents().Count().ShouldBe(1); index.LocalFiles.Find("dir")!.Deleted.ShouldBeFalse();
        }
        (await Files(index).RecoverAsync(Token)).ShouldBe(1); Directory.Exists(Disk("dir")).ShouldBeFalse();
        index.LocalFiles.Find("dir")!.Deleted.ShouldBeTrue(); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedOrUnauthenticatedDirectoryRecoveryCannotDeleteTheDirectory(bool signature)
    {
        Directory.CreateDirectory(Disk("dir")); await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir", deleted: true);
        var applier = Directories(index); applier.Checkpoint = stage => { if (stage == "prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("dir", Token));
        var intent = index.ReadApplyIntents().Single(); byte[] bytes = intent.Payload.ToArray();
        if (signature)
        {
            int offset = 5;
            for (int field = 0; field < 3; field++) offset += 4 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            bytes[offset + 4] ^= 1; // First signature byte after bounded root/source/body.
        }
        else BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), int.MaxValue);
        await index.RewriteApplyAsync(intent, bytes, Token);
        if (signature) await Should.ThrowAsync<System.Security.Cryptography.CryptographicException>(() => Files(index).RecoverAsync(Token));
        else await Should.ThrowAsync<InvalidDataException>(() => Files(index).RecoverAsync(Token));
        Directory.Exists(Disk("dir")).ShouldBeTrue(); index.ReadApplyIntents().Count().ShouldBe(1);
    }

    [Fact]
    public async Task UnavailableDiagnosticsCannotChangeCommittedDirectorySuccess()
    {
        await using var index = await PrepareAsync(); await ReceiveAsync(index, "dir");
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "directory", debugEnabled: true); journal.Dispose();
        var applier = new DirectoryApplier(Root, index, keys, own, journal: journal);
        (await applier.ApplyAsync("dir", Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
        Directory.Exists(Disk("dir")).ShouldBeTrue(); index.ReadApplyIntents().ShouldBeEmpty(); journal.DroppedEvents.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task FailureDiagnosticsUseStagesAndNativeCodesWithoutDirectoryNames()
    {
        Directory.CreateDirectory(Disk("private-name")); await using var index = await PrepareAsync(); await ReceiveAsync(index, "private-name", deleted: true);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "directory", debugEnabled: true);
        var applier = new DirectoryApplier(Root, index, keys, own, journal: journal);
        using (var reader = NativeFiles.CreateFile(Disk("private-name"), 0x81, 3, 0, 3, 0x02200000, 0))
        {
            reader.IsInvalid.ShouldBeFalse();
            await Should.ThrowAsync<System.ComponentModel.Win32Exception>(() => applier.ApplyAsync("private-name", Token));
        }
        string logs = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        logs.ShouldContain("directory.start"); logs.ShouldContain("directory.failed"); logs.ShouldContain("\"nativeError\":32");
        logs.ShouldNotContain("private-name"); journal.DroppedEvents.ShouldBe(0);
        index.ReadApplyIntents().ShouldBeEmpty(); (await applier.ApplyAsync("private-name", Token)).Outcome.ShouldBe(FileApplyOutcome.Applied);
    }

    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = Scanner(index); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private DirectoryApplier Directories(FolderIndex index, FolderKeys? access = null) => new(Root, index, access ?? keys, own);
    private FileApplier Files(FolderIndex index) => new(Root, index, keys, own);
    private Scanner Scanner(FolderIndex index, FolderKeys? access = null) => new(Root, index, access ?? keys, own);
    private async Task<SignedRecord> ReceiveAsync(FolderIndex index, string path, bool deleted = false, uint attributes = 0)
    {
        var record = DirectoryRecord(index, path, deleted, 1, attributes);
        await new NeededSet(index, keys, own).ApplyIndexUpdateAsync(remote, 0, 1, [record], Token); return record;
    }
    private SignedRecord DirectoryRecord(FolderIndex index, string path, bool deleted, ulong sequence, uint attributes = 0)
    {
        var known = index.LocalFiles.Find(path); long now = DateTime.UtcNow.AddMinutes(1).Ticks;
        var body = new FileRecordBody { Path = path, Type = 1, Deleted = deleted, Attributes = attributes, MtimeTicks = now, ChangedTicks = now,
            ModifiedBy = ByteString.CopyFrom(remote) };
        body.Vv.AddRange(VersionVector.Increment(known?.Vector ?? new Dictionary<ulong, ulong>(), BinaryPrimitives.ReadUInt64LittleEndian(remote))
            .Select(v => new VvEntry { Device = v.Key, Counter = v.Value }));
        return SignedRecordFactory.Create(body, keys, sequence);
    }
    private static byte[] Device(byte value) { byte[] result = new byte[32]; result[0] = value; return result; }
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-DirectoryApply-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        foreach (string link in links) Directory.Delete(link);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(resolved, "*", options)) File.SetAttributes(file, FileAttributes.Normal);
        foreach (string dir in Directory.EnumerateDirectories(resolved, "*", options)) File.SetAttributes(dir, FileAttributes.Directory);
        Directory.Delete(resolved, recursive: true);
    }
}
