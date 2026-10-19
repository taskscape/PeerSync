using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class ArchiveRestorerTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Restore-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] own = Device(1), remote = Device(2);
    private readonly TimeProvider clock = new RestoreClock();
    private readonly List<string> links = [];
    private static readonly byte[] Archived = "archived version bytes"u8.ToArray(), Current = "current target bytes"u8.ToArray();
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private ulong ShortDevice => BinaryPrimitives.ReadUInt64LittleEndian(own);
    public ArchiveRestorerTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task RestoreKeepsCurrentAndSourceVersionsThenScannerCreatesExactlyOneFreshRwVersion()
    {
        await using var index = await PrepareAsync(); var before = index.LocalFiles.Find("file.txt")!;
        var source = await ArchiveAsync(index); var applier = Applier(index); var restorer = new ArchiveRestorer(applier);
        (await restorer.RestoreAsync(source.Id, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Archived); File.ReadAllBytes(Disk(source.ArchivePath)).ShouldBe(Archived);
        var retained = index.Archive.ReadAll().Single(r => r.Id != source.Id);
        File.ReadAllBytes(Disk(retained.ArchivePath)).ShouldBe(Current); retained.FileHash.ShouldBe(Hash(Current));
        index.ReadApplyIntents().ShouldBeEmpty(); index.LocalFiles.Find("file.txt")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
        index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        using var scanner = Scanner(index); (await scanner.ScanAsync(Token)).Updated.ShouldBe(1);
        var restored = index.LocalFiles.Find("file.txt")!;
        restored.Vector[ShortDevice].ShouldBe(before.Vector[ShortDevice] + 1);
        restored.FileHash.ShouldBe(Hash(Archived)); restored.Record.ShouldNotBeNull();
        SignedRecordFactory.TryOpen(restored.Record, keys, out var body).ShouldBeTrue();
        body.ModifiedBy.ToByteArray().ShouldBe(own); body.MtimeTicks.ShouldBe(clock.GetUtcNow().UtcTicks);
        restored.Record.Body.ToArray().ShouldNotBe(before.Record!.Body.ToArray());
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0); index.ReadIndexBatch(0).Records.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReadOnlyRestoreRemainsAnUnpublishedLocalChangeAndNormalOverwritePolicyStillApplies()
    {
        await using var index = await PrepareAsync(); var original = index.LocalFiles.Find("file.txt")!;
        var source = await ArchiveAsync(index); using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        var needed = new NeededSet(index, readOnly, own);
        await needed.ApplyIndexUpdateAsync(remote, 0, 1, [original.Record!.WithLocalSequence(1)], Token);
        var restorer = new ArchiveRestorer(Applier(index, readOnly));
        (await restorer.RestoreAsync(source.Id, Token)).ShouldBe(FileApplyOutcome.Applied);
        using var scanner = Scanner(index, readOnly); (await scanner.ScanAsync(Token)).Updated.ShouldBe(1);
        var local = index.LocalFiles.Find("file.txt")!; local.Record.ShouldBeNull(); local.Vector[ShortDevice].ShouldBe(original.Vector[ShortDevice]);
        local.FileHash.ShouldBe(Hash(Archived)); index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        var decision = needed.Decide(new("FILE.TXT", local, index.RemotesForPath("file.txt").ToArray()));
        decision.BlockedReason.ShouldBe("read-only-local-change");
    }

    [Fact]
    public async Task UnscannedCurrentEditsAreRetainedInsteadOfBeingDiscardedByRestore()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        File.WriteAllText(Disk("file.txt"), "an unscanned user edit");
        (await new ArchiveRestorer(Applier(index)).RestoreAsync(source.Id, Token)).ShouldBe(FileApplyOutcome.Applied);
        var retained = index.Archive.ReadAll().Single(r => r.Id != source.Id);
        File.ReadAllText(Disk(retained.ArchivePath)).ShouldBe("an unscanned user edit");
        retained.FileHash.ShouldBe(Hash(Encoding.UTF8.GetBytes("an unscanned user edit")));
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Archived);
    }

    [Fact]
    public async Task RestoreAsCopyCreatesParentsAndNeverChangesTheOriginalOrItsSignedRecord()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var before = index.LocalFiles.Find("file.txt")!;
        (await new ArchiveRestorer(Applier(index)).RestoreAsCopyAsync(source.Id, "copies/restored.txt", Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Disk("copies/restored.txt")).ShouldBe(Archived); File.ReadAllBytes(Disk("file.txt")).ShouldBe(Current);
        index.LocalFiles.Find("file.txt")!.Record!.Body.ToArray().ShouldBe(before.Record!.Body.ToArray());
        index.Archive.Count().ShouldBe(1);
        using var scanner = Scanner(index); await scanner.ScanAsync(Token);
        index.LocalFiles.Find("copies/restored.txt")!.Vector[ShortDevice].ShouldBe(1UL);
        index.LocalFiles.Find("file.txt")!.Vector[ShortDevice].ShouldBe(before.Vector[ShortDevice]);
    }

    [Fact]
    public async Task CopyRefusesAnOccupiedDestinationBeforeCreatingAnIntentOrTemporary()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        (await new ArchiveRestorer(Applier(index)).RestoreAsCopyAsync(source.Id, "file.txt", Token)).ShouldBe(FileApplyOutcome.Blocked);
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Current); index.ReadApplyIntents().ShouldBeEmpty();
        Directory.GetFiles(Root, "*.!psp").ShouldBeEmpty(); index.Archive.Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("restore-copied")]
    public async Task InterruptedStagingDoesNotChangeTheTargetAndCanBeRecoveredBeforeScanning(string boundary)
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == boundary) throw new IOException("injected stop"); };
        await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(applier).RestoreAsync(source.Id, Token));
        BinaryPrimitives.ReadInt32LittleEndian(index.ReadApplyIntents().Single().Payload).ShouldBe(2);
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Current);
        (await Applier(index).RecoverAsync(Token)).ShouldBe(1); index.ReadApplyIntents().ShouldBeEmpty();
        using var scanner = Scanner(index); (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("original-cleared")]
    [InlineData("archive-moved")]
    [InlineData("archived")]
    [InlineData("install-ready")]
    [InlineData("installed")]
    [InlineData("before-commit")]
    public async Task ReopeningAtEveryPreparedMutationBoundaryCompletesAsANormalLocalEdit(string boundary)
    {
        long sourceId;
        await using (var index = await PrepareAsync())
        {
            var source = await ArchiveAsync(index); sourceId = source.Id;
            var applier = Applier(index); applier.Checkpoint = stage => { if (stage == boundary) throw new IOException("injected stop"); };
            await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(applier).RestoreAsync(sourceId, Token));
            var state = ApplyState.Decode(index.ReadApplyIntents().Single(), keys); state.IsRestore.ShouldBeTrue(); state.Version.ShouldBeNull();
            using var scanner = Scanner(index); await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        (await Applier(reopened).RecoverAsync(Token)).ShouldBe(1); (await Applier(reopened).RecoverAsync(Token)).ShouldBe(0);
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Archived);
        File.ReadAllBytes(Disk(reopened.Archive.Find(sourceId)!.ArchivePath)).ShouldBe(Archived);
        File.ReadAllBytes(Disk(reopened.Archive.ReadAll().Single(r => r.Id != sourceId).ArchivePath)).ShouldBe(Current);
        reopened.ReadIndexBatch(0).Records.ShouldBeEmpty();
        using var rescan = Scanner(reopened); (await rescan.ScanAsync(Token)).Updated.ShouldBe(1);
        reopened.LocalFiles.Find("file.txt")!.Vector[ShortDevice].ShouldBe(2UL);
    }

    [Fact]
    public async Task KeepBothOrOversizePolicyPreservesTheCurrentVersionAsASyncableConflict()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index, settings: new FolderSettings { ArchiveMaxFileBytes = 1 });
        (await new ArchiveRestorer(applier).RestoreAsync(source.Id, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Directory.GetFiles(Root, "*.sync-conflict-*.txt").Single()).ShouldBe(Current);
        index.Archive.Count().ShouldBe(1); File.ReadAllBytes(Disk(source.ArchivePath)).ShouldBe(Archived);
    }

    [Fact]
    public async Task CopyPreservesAFileCreatedAfterCopyingAndBeforePreparation()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "restore-copied") File.WriteAllText(Disk("copy.txt"), "new concurrent file"); };
        (await new ArchiveRestorer(applier).RestoreAsCopyAsync(source.Id, "copy.txt", Token)).ShouldBe(FileApplyOutcome.Blocked);
        File.ReadAllText(Disk("copy.txt")).ShouldBe("new concurrent file"); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task ConcurrentTargetInTheRestoreGapIsPreservedAlongWithTheCurrentAndSourceArchives()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "archived") File.WriteAllText(Disk("file.txt"), "concurrent gap edit"); };
        (await new ArchiveRestorer(applier).RestoreAsync(source.Id, Token)).ShouldBe(FileApplyOutcome.Deferred);
        File.ReadAllText(Disk("file.txt")).ShouldBe("concurrent gap edit");
        File.ReadAllBytes(Disk(source.ArchivePath)).ShouldBe(Archived);
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single(r => r.Id != source.Id).ArchivePath)).ShouldBe(Current);
        index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task PreparedRecoveryNeedsNoSourceArchiveAndWorksAfterAnRwToRoRoleChange()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "archived") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(applier).RestoreAsync(source.Id, Token));
        File.Delete(Disk(source.ArchivePath)); using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        (await Applier(index, readOnly).RecoverAsync(Token)).ShouldBe(1);
        using var scanner = Scanner(index, readOnly); await scanner.ScanAsync(Token);
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Archived); index.LocalFiles.Find("file.txt")!.Record.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongSourceHashesCannotChangeTheTarget(bool missingHash)
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        await index.Archive.UpsertAsync([source with { FileHash = missingHash ? null : new byte[32] }], Token);
        var error = await Record.ExceptionAsync(() => new ArchiveRestorer(Applier(index)).RestoreAsync(source.Id, Token));
        if (missingHash) error.ShouldBeOfType<InvalidDataException>(); else error.ShouldBeOfType<CryptographicException>();
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Current); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task LocalIntentBytesAreNotAcceptedAsAnAuthenticatedRemoteRecord()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "staging") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(applier).RestoreAsync(source.Id, Token));
        var intent = index.ReadApplyIntents().Single();
        var forged = new SignedRecord(intent.Payload, new byte[64], 1);
        await Should.ThrowAsync<CryptographicException>(() => new NeededSet(index, keys, own).ApplyIndexUpdateAsync(remote, 0, 1, [forged], Token));
        index.RemoteFiles.Count().ShouldBe(0); index.Devices.Find(remote).ShouldBeNull();
    }

    [Fact]
    public async Task ReadOnlyAndHiddenSourceFlagsAreRestoredWithFreshLocalMtime()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        File.SetAttributes(Disk(source.ArchivePath), FileAttributes.ReadOnly | FileAttributes.Hidden);
        (await new ArchiveRestorer(Applier(index)).RestoreAsync(source.Id, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.GetAttributes(Disk("file.txt")).ShouldBe(FileAttributes.ReadOnly | FileAttributes.Hidden);
        File.GetLastWriteTimeUtc(Disk("file.txt")).Ticks.ShouldBe(clock.GetUtcNow().UtcTicks);
        File.ReadAllBytes(Disk(source.ArchivePath)).ShouldBe(Archived);
    }

    [Fact]
    public async Task ReplacementPreservesTheCurrentTargetsProtectedDaclAndCreationTime()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        using var currentIdentity = WindowsIdentity.GetCurrent(); var identity = currentIdentity.User!;
        var security = new FileSecurity(); security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(Disk("file.txt")).SetAccessControl(security);
        DateTime created = new(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(Disk("file.txt"), created);
        string expected = new FileInfo(Disk("file.txt")).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        (await new ArchiveRestorer(Applier(index)).RestoreAsync(source.Id, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.GetCreationTimeUtc(Disk("file.txt")).ShouldBe(created);
        var actual = new FileInfo(Disk("file.txt")).GetAccessControl(); actual.AreAccessRulesProtected.ShouldBeTrue();
        actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access).ShouldBe(expected);
    }

    [Fact]
    public async Task ReusedStagingForANewTargetInheritsItsParentInsteadOfThePriorTargetsProtectedDacl()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity(); security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(Disk("file.txt")).SetAccessControl(security);
        var first = Applier(index); first.Checkpoint = stage => { if (stage == "metadata-prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(first).RestoreAsync(source.Id, Token));
        (await Applier(index).RecoverAsync(Token)).ShouldBe(1);
        new FileInfo(Directory.GetFiles(Root, "*.!psp").Single()).GetAccessControl().AreAccessRulesProtected.ShouldBeTrue();
        File.Delete(Disk("file.txt")); using var scanner = Scanner(index); await scanner.ScanAsync(Token);
        (await new ArchiveRestorer(Applier(index)).RestoreAsCopyAsync(source.Id, "file.txt", Token)).ShouldBe(FileApplyOutcome.Applied);
        new FileInfo(Disk("file.txt")).GetAccessControl().AreAccessRulesProtected.ShouldBeFalse();
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Archived);
    }

    [Fact]
    public async Task AnEmptyArchiveRestoresAsAnEmptyLocalFile()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        using (var empty = new FileStream(Disk(source.ArchivePath), FileMode.Open, FileAccess.Write)) empty.SetLength(0);
        await index.Archive.UpsertAsync([source with { Size = 0, FileHash = Hash([]) }], Token);
        (await new ArchiveRestorer(Applier(index)).RestoreAsync(source.Id, Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Disk("file.txt")).ShouldBeEmpty();
        using var scanner = Scanner(index); await scanner.ScanAsync(Token);
        index.LocalFiles.Find("file.txt")!.FileHash.ShouldBe(Hash([]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptPreparedStagingRestoresTheRetainedCurrentVersion(bool corrupt)
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "archived") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(applier).RestoreAsync(source.Id, Token));
        string temporary = Directory.GetFiles(Root, "*.!psp").Single();
        if (corrupt) { using var edit = new FileStream(temporary, FileMode.Open, FileAccess.Write); edit.WriteByte(0); }
        else File.Delete(temporary);
        (await Applier(index).RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Current); File.ReadAllBytes(Disk(source.ArchivePath)).ShouldBe(Archived);
        index.Archive.Count().ShouldBe(1); index.ReadApplyIntents().ShouldBeEmpty();
        using var scanner = Scanner(index); (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    [Fact]
    public async Task SourceArchiveReparseTraversalCannotWriteToTheTargetOrTheLinkedFixture()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        string moved = Path.Combine(fixture, "source-outside"); Directory.Move(Disk(".psp/archive"), moved);
        string link = Disk(".psp/archive"); Directory.CreateSymbolicLink(link, moved); links.Add(link);
        var error = await Record.ExceptionAsync(() => new ArchiveRestorer(Applier(index)).RestoreAsync(source.Id, Token));
        error.ShouldNotBeNull(); File.ReadAllBytes(Disk("file.txt")).ShouldBe(Current);
        File.ReadAllBytes(Directory.GetFiles(moved).Single()).ShouldBe(Archived); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplacedRootCannotRecoverAPreparedLocalRestore()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(applier).RestoreAsync(source.Id, Token));
        string previous = Path.Combine(fixture, "previous"); Directory.Move(Root, previous); Directory.CreateDirectory(Root);
        File.WriteAllText(Disk("file.txt"), "new root data");
        await Should.ThrowAsync<IOException>(() => Applier(index).RecoverAsync(Token));
        File.ReadAllText(Disk("file.txt")).ShouldBe("new root data");
        File.ReadAllBytes(Path.Combine(previous, "file.txt")).ShouldBe(Current); index.ReadApplyIntents().Count().ShouldBe(1);
    }

    [Fact]
    public async Task MalformedLocalRecoveryFieldsFailBeforeAnyDestructiveWork()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(applier).RestoreAsync(source.Id, Token));
        var intent = index.ReadApplyIntents().Single(); byte[] payload = intent.Payload.ToArray();
        // First field after kind/prepared is the root's bounded length.
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5), int.MaxValue);
        await index.RewriteApplyAsync(intent, payload, Token);
        await Should.ThrowAsync<InvalidDataException>(() => Applier(index).RecoverAsync(Token));
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Current); index.ReadApplyIntents().Count().ShouldBe(1);
    }

    [Fact]
    public async Task CopyRecoveryPreservesAConcurrentDestinationCreatedImmediatelyBeforeInstallation()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        var applier = Applier(index); applier.Checkpoint = stage => { if (stage == "prepared") throw new IOException("stop"); };
        await Should.ThrowAsync<IOException>(() => new ArchiveRestorer(applier).RestoreAsCopyAsync(source.Id, "copy.txt", Token));
        var recovery = Applier(index); recovery.Checkpoint = stage => { if (stage == "install-ready") File.WriteAllText(Disk("copy.txt"), "concurrent copy destination"); };
        (await recovery.RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllText(Disk("copy.txt")).ShouldBe("concurrent copy destination");
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Current); File.ReadAllBytes(Disk(source.ArchivePath)).ShouldBe(Archived);
        index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task LockedCurrentTargetLogsNativeFailureWithoutPrivateNamesAndRecoversStaging()
    {
        await using var index = await PrepareAsync(); var source = await ArchiveAsync(index);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "restore", debugEnabled: true);
        var applier = new FileApplier(Root, index, keys, own, journal: journal, clock: clock);
        using (File.Open(Disk("file.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Should.ThrowAsync<System.ComponentModel.Win32Exception>(() => new ArchiveRestorer(applier).RestoreAsync(source.Id, Token));
        string logs = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        logs.ShouldContain("\"nativeError\":32"); logs.ShouldContain("apply.failed"); logs.ShouldContain("restore");
        logs.ShouldNotContain("file.txt"); logs.ShouldNotContain(Encoding.UTF8.GetString(Archived)); logs.ShouldNotContain(Convert.ToHexString(Hash(Archived)));
        journal.DroppedEvents.ShouldBe(0); (await applier.RecoverAsync(Token)).ShouldBe(1);
        File.ReadAllBytes(Disk("file.txt")).ShouldBe(Current);
    }

    private async Task<FolderIndex> PrepareAsync()
    {
        File.WriteAllBytes(Disk("file.txt"), Current); var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = Scanner(index); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private FileApplier Applier(FolderIndex index, FolderKeys? access = null, FolderSettings? settings = null) =>
        new(Root, index, access ?? keys, own, settings, clock: clock);
    private Scanner Scanner(FolderIndex index, FolderKeys? access = null) => new(Root, index, access ?? keys, own, timeProvider: clock);
    private async Task<ArchiveRow> ArchiveAsync(FolderIndex index)
    {
        string path = ArchiveNames.Candidate("file.txt", clock.GetUtcNow().AddDays(-2));
        Directory.CreateDirectory(Path.GetDirectoryName(Disk(path))!); File.WriteAllBytes(Disk(path), Archived);
        await index.Archive.UpsertAsync([new(0, "file.txt", path, clock.GetUtcNow().AddDays(-2).UtcTicks, Archived.Length, Hash(Archived))], Token);
        return index.Archive.ReadAll().Single();
    }
    private static byte[] Hash(byte[] bytes) => Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    private static byte[] Device(byte value) { byte[] result = new byte[32]; result[0] = value; return result; }
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private sealed class RestoreClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero); }
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-Restore-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string link in links) Directory.Delete(link);
        foreach (string file in Directory.EnumerateFiles(resolved, "*", options)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(resolved, recursive: true);
    }
}
