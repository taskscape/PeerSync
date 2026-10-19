using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using Google.Protobuf;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class TypeTransitionTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-TypeTransition-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] own = Device(1), remote = Device(2);
    private readonly List<string> links = [];
    private static byte[] Old => "old file bytes"u8.ToArray();
    private static byte[] New => "new file bytes"u8.ToArray();
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public TypeTransitionTests() => Directory.CreateDirectory(Root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothKindsPreserveOldObjectsAndDoNotCreateAnOwnWriteVersion(bool directoryWins)
    {
        Seed(directoryWins); await using var index = await PrepareAsync(); var before = index.LocalFiles.Find("node")!;
        await ReceiveAsync(index, directoryWins); if (!directoryWins) File.WriteAllText(Disk("node/unknown"), "unscanned bytes");
        (await Applier(index).ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        var row = index.LocalFiles.Find("node")!; row.Type.ShouldBe(directoryWins ? 1U : 0U); row.FileId.ShouldNotBe(before.FileId);
        long sequence = row.LocalSequence; var record = row.Record!.Body.ToArray();
        AssertRetained(index, directoryWins); index.ReadApplyIntents().ShouldBeEmpty(); index.Needed.Find("node").ShouldBeNull();
        using var scanner = new Scanner(Root, index, keys, own); await scanner.ScanAsync(Token);
        index.LocalFiles.Find("node")!.LocalSequence.ShouldBe(sequence); index.LocalFiles.Find("node")!.Record!.Body.ToArray().ShouldBe(record);
        if (!directoryWins)
        {
            var copied = index.LocalFiles.ReadAll().Single(r => r.Path.EndsWith("/unknown", StringComparison.Ordinal) && !r.Deleted);
            copied.Record.ShouldNotBeNull(); index.LocalFiles.Find("node/known")!.Deleted.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(false, "staging")]
    [InlineData(false, "prepared")]
    [InlineData(false, "original-cleared")]
    [InlineData(false, "archive-moved")]
    [InlineData(false, "archived")]
    [InlineData(false, "install-ready")]
    [InlineData(false, "installed")]
    [InlineData(false, "before-commit")]
    [InlineData(false, "committed")]
    [InlineData(true, "staging")]
    [InlineData(true, "prepared")]
    [InlineData(true, "original-cleared")]
    [InlineData(true, "archive-moved")]
    [InlineData(true, "archived")]
    [InlineData(true, "install-ready")]
    [InlineData(true, "installed")]
    [InlineData(true, "before-commit")]
    [InlineData(true, "committed")]
    public async Task EveryBoundaryRecoversAfterReopeningTheDatabase(bool directoryWins, string boundary)
    {
        Seed(directoryWins);
        await using (var index = await PrepareAsync())
        {
            await ReceiveAsync(index, directoryWins);
            var applier = Applier(index); applier.Checkpoint = stage => { if (stage == boundary) throw new IOException("injected stop"); };
            await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("node", Fetch, Token));
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var recovery = Applier(reopened); await recovery.RecoverAsync(Token);
        if (boundary == "staging") (await recovery.ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        AssertRetained(reopened, directoryWins); reopened.ReadApplyIntents().ShouldBeEmpty();
        (await recovery.RecoverAsync(Token)).ShouldBe(0);
        reopened.LocalFiles.Find("node")!.Type.ShouldBe(directoryWins ? 1U : 0U);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingStagingRestoresTheWholeOldObjectWithoutChangingItsPublishedMetadata(bool directoryWins)
    {
        Seed(directoryWins); await using var index = await PrepareAsync(); var before = index.LocalFiles.Find("node")!;
        await ReceiveAsync(index, directoryWins); var applier = Applier(index);
        applier.Checkpoint = stage => { if (stage == "archived") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("node", Fetch, Token));
        string temp = Directory.GetFileSystemEntries(Root, "*.!psp").Single();
        if (directoryWins) Directory.Delete(temp); else File.Delete(temp);
        (await Applier(index).RecoverAsync(Token)).ShouldBe(1);
        index.LocalFiles.Find("node")!.LocalSequence.ShouldBe(before.LocalSequence); index.ReadApplyIntents().ShouldBeEmpty();
        if (directoryWins) File.ReadAllBytes(Disk("node")).ShouldBe(Old);
        else File.ReadAllText(Disk("node/known")).ShouldBe("known bytes");
        index.Archive.Count().ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConcurrentTargetIsPreservedAndTheRetainedVersionIsAccountedFor(bool directoryWins)
    {
        Seed(directoryWins); await using var index = await PrepareAsync(); await ReceiveAsync(index, directoryWins);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "install-ready") File.WriteAllText(Disk("node"), "concurrent"); };
        (await applier.ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Deferred);
        File.ReadAllText(Disk("node")).ShouldBe("concurrent"); AssertOldRetained(index, directoryWins);
        index.LocalFiles.Find("node")!.Flags.ShouldBe(LocalFileState.NeedsRescan); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task AChildWrittenDuringPreparationMovesWithTheWholeDirectoryAndStaleDescendantsAreWithheld()
    {
        Seed(false); await using var index = await PrepareAsync(); await ReceiveAsync(index, false);
        await index.Needed.UpsertAsync([new("node/known", remote, 1), new("node2/unrelated", remote, 1)], Token);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "prepared") File.WriteAllText(Disk("node/late"), "late child"); };
        (await applier.ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        string retained = Directory.GetDirectories(Root, "*.sync-conflict-*").Single();
        File.ReadAllText(Path.Combine(retained, "late")).ShouldBe("late child");
        index.LocalFiles.Find("node/known")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
        index.ReadIndexBatch(0).Records.Any(r => r.Path == "node/known").ShouldBeFalse();
        index.Needed.Find("node/known").ShouldBeNull(); index.Needed.Find("node2/unrelated").ShouldNotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictCollisionsNeverOverwriteExistingObjects(bool directoryWins)
    {
        Seed(directoryWins); await using var index = await PrepareAsync(); await ReceiveAsync(index, directoryWins);
        var applier = Applier(index, new() { KeepBothCopies = true }); string? collision = null;
        applier.Checkpoint = stage =>
        {
            if (stage != "prepared") return;
            var state = ApplyState.Decode(index.ReadApplyIntents().Single(), keys); collision = state.Retained;
            File.WriteAllText(Disk(collision), "collision bytes");
        };
        (await applier.ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllText(Disk(collision!)).ShouldBe("collision bytes");
        if (directoryWins) File.ReadAllBytes(Directory.GetFiles(Root, "*.sync-conflict-*").Single(p => p != Disk(collision!))).ShouldBe(Old);
        else AssertOldRetained(index, false);
    }

    [Fact]
    public async Task AReadOnlyPeerPreservesTheDirectoryTreeWithoutPublishingTheConflictCopy()
    {
        Seed(false); await using var index = await PrepareAsync(); await ReceiveAsync(index, false);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        (await new FileApplier(Root, index, readOnly, own).ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        using var scanner = new Scanner(Root, index, readOnly, own); await scanner.ScanAsync(Token);
        var copied = index.LocalFiles.ReadAll().Where(r => r.Path.Contains(".sync-conflict-", StringComparison.Ordinal)).ToArray();
        copied.ShouldNotBeEmpty(); copied.All(r => r.Record is null).ShouldBeTrue(); AssertRetained(index, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementKeepsDaclProtectionCreationTimeAndOldAttributesAcrossRecovery(bool directoryWins)
    {
        Seed(directoryWins); string path = Disk("node");
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        if (directoryWins) new FileInfo(path).SetAccessControl(acl);
        else
        {
            var directoryAcl = new DirectorySecurity(); directoryAcl.SetAccessRuleProtection(true, false);
            directoryAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(directoryAcl);
        }
        FileSystemSecurity before = directoryWins ? new FileInfo(path).GetAccessControl() : new DirectoryInfo(path).GetAccessControl();
        DateTime created = DateTime.UtcNow.AddYears(-1);
        if (directoryWins) File.SetCreationTimeUtc(path, created); else Directory.SetCreationTimeUtc(path, created);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly | FileAttributes.Hidden);
        await using var index = await PrepareAsync(); await ReceiveAsync(index, directoryWins);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "archive-moved") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("node", Fetch, Token));
        (await Applier(index).RecoverAsync(Token)).ShouldBe(1); AssertRetained(index, directoryWins);
        (directoryWins ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path)).ShouldBe(created);
        FileSystemSecurity after = directoryWins ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        after.AreAccessRulesProtected.ShouldBeTrue();
        // Windows removes directory-only inheritance flags when setting a file DACL.
        // Effective trustees, rights, allow/deny and protection remain the same.
        Rules(after).ShouldBe(Rules(before));
        string retained = directoryWins ? Disk(index.Archive.ReadAll().Single().ArchivePath) : Directory.GetDirectories(Root, "*.sync-conflict-*").Single();
        FileSystemSecurity retainedAcl = directoryWins ? new FileInfo(retained).GetAccessControl() : new DirectoryInfo(retained).GetAccessControl();
        retainedAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access).ShouldBe(before.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        (File.GetAttributes(retained) & (FileAttributes.ReadOnly | FileAttributes.Hidden)).ShouldBe(FileAttributes.ReadOnly | FileAttributes.Hidden);
        (File.GetAttributes(path) & (FileAttributes.ReadOnly | FileAttributes.Hidden)).ShouldBe((FileAttributes)0);

        static string[] Rules(FileSystemSecurity security) => security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => $"{r.IdentityReference.Value}:{r.FileSystemRights}:{r.AccessControlType}:{r.IsInherited}").ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterRetainingTheOldNodeRecoversBeforeScanning(bool directoryWins)
    {
        Seed(directoryWins); await using var index = await PrepareAsync(); await ReceiveAsync(index, directoryWins);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "archived") cancellation.Cancel(); };
        await Should.ThrowAsync<OperationCanceledException>(() => applier.ApplyAsync("node", Fetch, cancellation.Token));
        using var scanner = new Scanner(Root, index, keys, own);
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        (await Applier(index).RecoverAsync(Token)).ShouldBe(1); AssertRetained(index, directoryWins);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChangedNodeKindDuringStagingIsDeferredWithoutMovingEitherObject(bool directoryWins)
    {
        Seed(directoryWins); await using var index = await PrepareAsync(); await ReceiveAsync(index, directoryWins);
        var applier = Applier(index); applier.Checkpoint = stage =>
        {
            if (stage != "staging") return;
            if (directoryWins) { File.Move(Disk("node"), Disk("original")); Directory.CreateDirectory(Disk("node")); }
            else { Directory.Move(Disk("node"), Disk("original")); File.WriteAllText(Disk("node"), "other file"); }
        };
        (await applier.ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Deferred);
        index.Archive.Count().ShouldBe(0); index.ReadApplyIntents().ShouldBeEmpty();
        if (directoryWins) { File.ReadAllBytes(Disk("original")).ShouldBe(Old); Directory.Exists(Disk("node")).ShouldBeTrue(); }
        else { File.ReadAllText(Disk("original/known")).ShouldBe("known bytes"); File.ReadAllText(Disk("node")).ShouldBe("other file"); }
    }

    [Fact]
    public async Task ADirectoryContainingAReparseChildMovesIntactWithoutFollowingIt()
    {
        Seed(false); string external = Path.Combine(fixture, "external"); Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "secret"), "outside bytes");
        string link = Disk("node/link"); Directory.CreateSymbolicLink(link, external);
        await using var index = await PrepareAsync(); await ReceiveAsync(index, false);
        try
        {
            (await Applier(index).ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
            string moved = Path.Combine(Directory.GetDirectories(Root, "*.sync-conflict-*").Single(), "link");
            links.Add(moved); (File.GetAttributes(moved) & FileAttributes.ReparsePoint).ShouldBe(FileAttributes.ReparsePoint);
            File.ReadAllText(Path.Combine(external, "secret")).ShouldBe("outside bytes");
        }
        finally { if (Directory.Exists(link)) links.Add(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletionWithTheOtherSignedKindPreservesTheOldNode(bool directoryRecord)
    {
        Seed(directoryRecord); await using var index = await PrepareAsync(); await ReceiveAsync(index, directoryRecord, deleted: true);
        (await Applier(index).ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Applied);
        Directory.Exists(Disk("node")).ShouldBeFalse(); File.Exists(Disk("node")).ShouldBeFalse();
        index.LocalFiles.Find("node")!.Deleted.ShouldBeTrue(); AssertOldRetained(index, directoryRecord);
        index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATargetCreatedInTheDeletionGapIsDeferredAndNeverDeleted(bool directoryRecord)
    {
        Seed(directoryRecord); await using var index = await PrepareAsync(); await ReceiveAsync(index, directoryRecord, deleted: true);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "archived") File.WriteAllText(Disk("node"), "new target"); };
        (await applier.ApplyAsync("node", Fetch, Token)).ShouldBe(FileApplyOutcome.Deferred);
        File.ReadAllText(Disk("node")).ShouldBe("new target"); AssertOldRetained(index, directoryRecord);
        index.ReadApplyIntents().ShouldBeEmpty(); index.LocalFiles.Find("node")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReplacedRootCannotConsumeAnIntentBoundToTheOldRoot(bool directoryWins)
    {
        Seed(directoryWins); await using var index = await PrepareAsync(); await ReceiveAsync(index, directoryWins);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => applier.ApplyAsync("node", Fetch, Token));
        Directory.Move(Root, Path.Combine(fixture, "old-root")); Directory.CreateDirectory(Root); File.WriteAllText(Disk("node"), "new root bytes");
        await Should.ThrowAsync<IOException>(() => Applier(index).RecoverAsync(Token));
        File.ReadAllText(Disk("node")).ShouldBe("new root bytes"); index.ReadApplyIntents().Count().ShouldBe(1);
        if (directoryWins) File.ReadAllBytes(Path.Combine(fixture, "old-root", "node")).ShouldBe(Old);
        else File.ReadAllText(Path.Combine(fixture, "old-root", "node", "known")).ShouldBe("known bytes");
    }

    [Fact]
    public async Task AReparseTargetIsRejectedWithoutFollowingOrMutatingIt()
    {
        Seed(true); await using var index = await PrepareAsync(); await ReceiveAsync(index, true);
        string external = Path.Combine(fixture, "external"); Directory.CreateDirectory(external); File.WriteAllText(Path.Combine(external, "secret"), "outside");
        File.Move(Disk("node"), Disk("original")); Directory.CreateSymbolicLink(Disk("node"), external); links.Add(Disk("node"));
        await Should.ThrowAsync<IOException>(() => Applier(index).ApplyAsync("node", Fetch, Token));
        File.ReadAllText(Path.Combine(external, "secret")).ShouldBe("outside"); File.ReadAllBytes(Disk("original")).ShouldBe(Old);
        index.ReadApplyIntents().Count().ShouldBe(1); (await Applier(index).RecoverAsync(Token)).ShouldBe(1);
        Directory.Exists(Disk("node")).ShouldBeTrue(); index.Archive.Count().ShouldBe(0);
    }

    [Fact]
    public async Task LocalArchiveRestoreOverADirectoryRetainsTheWholeTreeAndPublishesOnlyViaScanning()
    {
        Seed(false); await using var index = await PrepareAsync(); Directory.CreateDirectory(Disk(".psp/archive"));
        const string archivePath = ".psp/archive/source"; File.WriteAllBytes(Disk(archivePath), New);
        await index.Archive.UpsertAsync([new(0, "node", archivePath, DateTime.UtcNow.Ticks, New.Length, Blake3.Hasher.Hash(New).AsSpan().ToArray())], Token);
        var source = index.Archive.ReadAll().Single(); var applier = Applier(index);
        applier.Checkpoint = stage => { if (stage == "archived") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(applier).RestoreAsync(source.Id, Token));
        (await Applier(index).RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllBytes(Disk("node")).ShouldBe(New); File.ReadAllBytes(Disk(archivePath)).ShouldBe(New);
        File.ReadAllText(Path.Combine(Directory.GetDirectories(Root, "*.sync-conflict-*").Single(), "known")).ShouldBe("known bytes");
        index.Archive.Count().ShouldBe(1); index.LocalFiles.Find("node/known")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
        index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        using var scanner = new Scanner(Root, index, keys, own); await scanner.ScanAsync(Token);
        index.LocalFiles.Find("node")!.Type.ShouldBe(0U); index.LocalFiles.Find("node/known")!.Deleted.ShouldBeTrue();
    }

    private void Seed(bool directoryWins)
    {
        if (directoryWins) File.WriteAllBytes(Disk("node"), Old);
        else { Directory.CreateDirectory(Disk("node")); File.WriteAllText(Disk("node/known"), "known bytes"); }
    }
    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, own); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private async Task ReceiveAsync(FolderIndex index, bool directoryWins, bool deleted = false)
    {
        long now = DateTime.UtcNow.AddMinutes(1).Ticks;
        var body = new FileRecordBody { Path = "node", Type = directoryWins ? 1U : 0U, Size = directoryWins || deleted ? 0UL : (ulong)New.Length,
            MtimeTicks = now, ChangedTicks = now, Deleted = deleted, ModifiedBy = ByteString.CopyFrom(remote) };
        body.Vv.AddRange(VersionVector.Increment(index.LocalFiles.Find("node")!.Vector, BinaryPrimitives.ReadUInt64LittleEndian(remote))
            .Select(v => new VvEntry { Device = v.Key, Counter = v.Value }));
        if (!directoryWins && !deleted)
        {
            body.FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash(New).AsSpan());
            body.Chunks.Add(new Chunk { Hash = body.FileHash, Length = (uint)New.Length });
        }
        await new NeededSet(index, keys, own).ApplyIndexUpdateAsync(remote, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
    }
    private void AssertRetained(FolderIndex index, bool directoryWins)
    {
        if (directoryWins) Directory.Exists(Disk("node")).ShouldBeTrue();
        else File.ReadAllBytes(Disk("node")).ShouldBe(New);
        AssertOldRetained(index, directoryWins);
    }
    private void AssertOldRetained(FolderIndex index, bool directoryWins)
    {
        if (directoryWins) File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old);
        else
        {
            index.Archive.Count().ShouldBe(0);
            File.ReadAllText(Path.Combine(Directory.GetDirectories(Root, "*.sync-conflict-*").Single(), "known")).ShouldBe("known bytes");
        }
    }
    private FileApplier Applier(FolderIndex index, FolderSettings? settings = null) => new(Root, index, keys, own, settings);
    private static ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> _, int length, CancellationToken token)
    { token.ThrowIfCancellationRequested(); length.ShouldBe(New.Length); return ValueTask.FromResult<ReadOnlyMemory<byte>>(New); }
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private static byte[] Device(byte value) { byte[] result = new byte[32]; result[0] = value; return result; }
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-TypeTransition-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        foreach (string link in links.Distinct()) Directory.Delete(link);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(resolved, "*", options)) File.SetAttributes(file, FileAttributes.Normal);
        foreach (string dir in Directory.EnumerateDirectories(resolved, "*", options)) File.SetAttributes(dir, FileAttributes.Directory);
        Directory.Delete(resolved, recursive: true);
    }
}
