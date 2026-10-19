using System.Buffers.Binary;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class CaptureRetentionTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-CaptureRetention-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly MutableClock clock = new(DateTimeOffset.UtcNow);
    private readonly List<string> links = [];
    private static byte[] Device => Enumerable.Repeat((byte)1, 32).ToArray();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static byte[] Expected => "never published incomplete capture bytes"u8.ToArray();
    private static byte[] Hash => Blake3.Hasher.Hash(Expected).AsSpan().ToArray();
    public CaptureRetentionTests() => Directory.CreateDirectory(Root);

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task AgedUnreferencedStrictPrefixesRetireWithTheirProofWhileCurrentFilesRemain(int length)
    {
        await using var index = await PrepareAsync(); var proof = Stage(length);
        var result = await Retention(index).SweepAsync(Token); result.Deleted.ShouldBe(1); result.BytesRemoved.ShouldBe(length); result.WaitingForRecovery.ShouldBeFalse();
        File.Exists(Data(proof)).ShouldBeFalse(); File.Exists(Marker(proof)).ShouldBeFalse(); index.ReadCaptureRetirements().ShouldBeEmpty();
        File.ReadAllText(Disk("current")).ShouldBe("current bytes"); index.ReadBlockObjects().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task YoungOrNeededPrefixesRemainProtected(bool needed)
    {
        await using var index = await PrepareAsync(); var proof = Stage(3, ageHours: needed ? 25 : 23); if (needed) await AddNeededAsync(index);
        var result = await Retention(index).SweepAsync(Token); result.Deleted.ShouldBe(0); result.WaitingForRecovery.ShouldBeTrue();
        File.ReadAllBytes(Data(proof)).ShouldBe(Expected[..3]); File.Exists(Marker(proof)).ShouldBeTrue(); index.ReadCaptureRetirements().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("capture-reserved")]
    [InlineData("capture-data-deleted")]
    [InlineData("capture-proof-deleted")]
    [InlineData("capture-completed")]
    public async Task EveryCaptureRetirementBoundaryRecoversAfterDatabaseReopening(string boundary)
    {
        BlockCaptureProof proof;
        await using (var index = await PrepareAsync())
        {
            proof = Stage(3); await Should.ThrowAsync<InvalidOperationException>(() => CrashAt(index, boundary).SweepAsync(Token));
            if (boundary == "capture-reserved")
            {
                using var tree = new SafeFileTree(Root); var ordinary = await PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token);
                ordinary.Deferred.ShouldBe(1); File.ReadAllBytes(Data(proof)).ShouldBe(Expected[..3]); File.Exists(Marker(proof)).ShouldBeTrue();
            }
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); await Retention(reopened).SweepAsync(Token);
        File.Exists(Data(proof)).ShouldBeFalse(); File.Exists(Marker(proof)).ShouldBeFalse(); reopened.ReadCaptureRetirements().ShouldBeEmpty();
        File.ReadAllText(Disk("current")).ShouldBe("current bytes");
    }

    [Theory]
    [InlineData("capture-reserved", false)]
    [InlineData("capture-data-deleted", true)]
    [InlineData("capture-proof-deleted", true)]
    public async Task NewDemandCancelsPresentRetirementOrWithholdsPublicationIfPrefixAlreadyDisappeared(string boundary, bool missing)
    {
        await using var index = await PrepareAsync(); var proof = Stage(3);
        await Should.ThrowAsync<InvalidOperationException>(() => CrashAt(index, boundary).SweepAsync(Token)); await AddNeededAsync(index);
        var result = await Retention(index).SweepAsync(Token); index.ReadCaptureRetirements().ShouldBeEmpty(); index.Needed.Find("incoming").ShouldNotBeNull();
        if (missing)
        {
            result.Missing.ShouldBe(1); File.Exists(Data(proof)).ShouldBeFalse(); File.Exists(Marker(proof)).ShouldBeFalse();
            index.LocalFiles.Find("current")!.ContentReady.ShouldBeFalse(); index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        }
        else { result.Cancelled.ShouldBe(1); File.ReadAllBytes(Data(proof)).ShouldBe(Expected[..3]); File.Exists(Marker(proof)).ShouldBeTrue(); }
        index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash)).Kinds.ShouldBe(BlockReferenceKind.Needed);
    }

    [Theory]
    [InlineData("data-replaced")]
    [InlineData("full-corrupt")]
    [InlineData("oversized")]
    [InlineData("linked")]
    [InlineData("locked")]
    [InlineData("reparse")]
    [InlineData("proof-corrupt")]
    [InlineData("foreign-root")]
    [InlineData("proof-replaced")]
    public async Task UnknownChangedLinkedLockedReparseAndFullCorruptObjectsRemainUntouched(string change)
    {
        await using var index = await PrepareAsync(); var proof = Stage(change == "full-corrupt" ? Expected.Length : change == "oversized" ? Expected.Length + 1 : 3);
        FileStream? held = null;
        if (change == "data-replaced") { File.Move(Data(proof), Data(proof) + ".unknown"); File.WriteAllBytes(Data(proof), Expected[..3]); }
        else if (change == "linked")
        {
            using var tree = new SafeFileTree(Root); using var parent = tree.Directory(proof.Parent, privatePath: true); using var home = tree.Directory("");
            using var file = parent.OpenFile(proof.Temporary, mutation: true, temporary: true); home.LinkHere(file, "unknown-link");
        }
        else if (change == "locked") held = File.Open(Data(proof), FileMode.Open, FileAccess.Read, FileShare.None);
        else if (change == "reparse")
        {
            string outside = Path.Combine(fixture, "outside"); Directory.Move(Disk(proof.Parent), outside); Directory.CreateSymbolicLink(Disk(proof.Parent), outside); links.Add(Disk(proof.Parent));
        }
        else if (change == "proof-corrupt") { using var writer = File.Open(Marker(proof), FileMode.Open, FileAccess.Write); writer.WriteByte(0); }
        else if (change == "foreign-root")
        {
            var foreign = proof with { RootId = new byte[24] }; using var writer = File.Open(Marker(proof), FileMode.Open, FileAccess.Write); writer.Write(foreign.Encode());
        }
        else if (change == "proof-replaced") { File.Move(Marker(proof), Marker(proof) + ".unknown"); File.WriteAllBytes(Marker(proof), proof.Encode()); }
        try { var result = await Retention(index).SweepAsync(Token); result.Deleted.ShouldBe(0); result.WaitingForRecovery.ShouldBeTrue(); }
        finally { held?.Dispose(); }
        File.Exists(Data(proof)).ShouldBeTrue(); File.Exists(Marker(proof)).ShouldBeTrue(); index.ReadCaptureRetirements().ShouldBeEmpty();
        if (change == "linked") File.ReadAllBytes(Disk("unknown-link")).ShouldBe(Expected[..3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangesAfterReservationPreserveThePrefixAndItsDurableAuthority(bool markerChanged)
    {
        await using var index = await PrepareAsync(); var proof = Stage(3); await Should.ThrowAsync<InvalidOperationException>(() => CrashAt(index, "capture-reserved").SweepAsync(Token));
        if (markerChanged) { File.Move(Marker(proof), Marker(proof) + ".unknown"); File.WriteAllBytes(Marker(proof), proof.Encode()); }
        else { using var writer = File.Open(Data(proof), FileMode.Open, FileAccess.Write); writer.WriteByte(0); }
        var result = await Retention(index).SweepAsync(Token); result.WaitingForRecovery.ShouldBeTrue(); result.Deleted.ShouldBe(0);
        File.Exists(Data(proof)).ShouldBeTrue(); File.Exists(Marker(proof)).ShouldBeTrue(); index.ReadCaptureRetirements().Single().Id.ShouldBe(proof.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualReservationAndCompletionFailureLeaveTruthfulRecoveryAndPrivateDebugEvents(bool completion)
    {
        await using var index = await PrepareAsync(); var proof = Stage(3);
        Sql(completion ? "CREATE TRIGGER reject_capture BEFORE DELETE ON capture_retirements BEGIN SELECT RAISE(ABORT,'private SQL text'); END;"
            : "CREATE TRIGGER reject_capture BEFORE INSERT ON capture_retirements BEGIN SELECT RAISE(ABORT,'private SQL text'); END;");
        using (var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "capture-retention", debugEnabled: true))
            await Should.ThrowAsync<SqliteException>(() => new BlockRetention(Root, index, keys, Device, clock: clock, journal: journal).SweepAsync(Token));
        File.Exists(Data(proof)).ShouldBe(!completion); File.Exists(Marker(proof)).ShouldBe(!completion); index.ReadCaptureRetirements().Count().ShouldBe(completion ? 1 : 0);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); log.ShouldContain("capture.retirement_failed"); log.ShouldContain("1811");
        log.ShouldNotContain(Root); log.ShouldNotContain("private SQL text"); log.ShouldNotContain(Convert.ToHexString(Hash)); log.ShouldNotContain("never published incomplete");
        Sql("DROP TRIGGER reject_capture;"); await Retention(index).SweepAsync(Token); File.Exists(Data(proof)).ShouldBeFalse(); index.ReadCaptureRetirements().ShouldBeEmpty();
    }

    [Fact]
    public async Task CompletionFailureRollsBackReadinessWithholdingAndSuccessfulRetryWithholdsNewDemand()
    {
        await using var index = await PrepareAsync(); Stage(3); await Should.ThrowAsync<InvalidOperationException>(() => CrashAt(index, "capture-data-deleted").SweepAsync(Token)); await AddNeededAsync(index);
        Sql("CREATE TRIGGER reject_capture BEFORE DELETE ON capture_retirements BEGIN SELECT RAISE(ABORT,'test rollback'); END;");
        await Should.ThrowAsync<SqliteException>(() => Retention(index).SweepAsync(Token)); index.LocalFiles.Find("current")!.ContentReady.ShouldBeTrue(); index.ReadCaptureRetirements().Count().ShouldBe(1);
        Sql("DROP TRIGGER reject_capture;"); await Retention(index).SweepAsync(Token); index.LocalFiles.Find("current")!.ContentReady.ShouldBeFalse(); index.ReadCaptureRetirements().ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingProofDoesNotDiscardAuthorityWhileItsPrefixIsNeeded()
    {
        await using var index = await PrepareAsync(); var proof = Stage(3); await Should.ThrowAsync<InvalidOperationException>(() => CrashAt(index, "capture-reserved").SweepAsync(Token));
        File.Delete(Marker(proof)); await AddNeededAsync(index); var waiting = await Retention(index).SweepAsync(Token); waiting.WaitingForRecovery.ShouldBeTrue();
        File.ReadAllBytes(Data(proof)).ShouldBe(Expected[..3]); index.ReadCaptureRetirements().Single().Id.ShouldBe(proof.Id);
        await index.Needed.DeleteAsync(["incoming"], Token); await Retention(index).SweepAsync(Token); File.Exists(Data(proof)).ShouldBeFalse(); index.ReadCaptureRetirements().ShouldBeEmpty();
    }

    [Fact]
    public async Task DeferredCaptureFirstCancelsOldBlockDeletionThenAllowsFreshSweepingAfterItsOwnRetirement()
    {
        await using var index = await PrepareAsync(); byte[] other = "owned older orphan"u8.ToArray(), hash = Blake3.Hasher.Hash(other).AsSpan().ToArray();
        using (var tree = new SafeFileTree(Root)) using (var blocks = new PublishedBlocks(tree)) { blocks.Put(other, hash, Token); await blocks.FlushAsync(index, Token); }
        await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31);
        await Should.ThrowAsync<InvalidOperationException>(() => CrashAt(index, "prepared").SweepAsync(Token)); var proof = Stage(3);
        string oldBlock = Disk(".psp/blocks/" + Convert.ToHexString(hash)[..2] + "/" + Convert.ToHexString(hash)); bool checkedOrder = false;
        var retention = new BlockRetention(Root, index, keys, Device, clock: clock) { Checkpoint = stage =>
        { if (stage == "capture-proof-deleted") { File.Exists(oldBlock).ShouldBeTrue(); index.ReadBlockDeletionIntents().ShouldBeEmpty(); checkedOrder = true; } } };
        var result = await retention.SweepAsync(Token); result.Cancelled.ShouldBe(1); result.Deleted.ShouldBe(2); checkedOrder.ShouldBeTrue();
        File.Exists(oldBlock).ShouldBeFalse(); File.Exists(Data(proof)).ShouldBeFalse();
    }

    [Fact]
    public async Task CallerBudgetBoundsActualCaptureDataRetirementsAndBothGuardsAreHeldAtReservation()
    {
        await using var index = await PrepareAsync(); var first = Stage(3); var second = Stage(3);
        using var operation = await FolderOperationGate.AcquireAsync(index, Token); using var references = await FolderReferenceGate.AcquireAsync(index, Token); using var tree = new SafeFileTree(Root);
        var fresh = await new BlockReferences(Root, index, keys, Device).RefreshUnderGateAsync(tree, Token); int guarded = 0;
        var result = await PublishedBlocks.RetireCapturesAsync(tree, index, fresh.SnapshotId!.Value, clock.Now.UtcTicks, 1, null, stage =>
        {
            if (stage != "capture-reserved") return; var current = index.ReadCaptureRetirements().Single(); var proof = current.Id == first.Id ? first : second;
            Should.Throw<IOException>(() => File.Open(Data(proof), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
            Should.Throw<IOException>(() => File.Open(Marker(proof), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)); guarded++;
        }, Token);
        result.Deleted.ShouldBe(1); result.Visited.ShouldBe(1); guarded.ShouldBe(1); new[] { Data(first), Data(second) }.Count(File.Exists).ShouldBe(1);
    }

    [Fact]
    public async Task ReferencedDuplicatePrefixExpiresOnlyUnderItsVerifiedCatalogedReplacementGuard()
    {
        await using var index = await PrepareAsync(); var proof = Stage(3); await FillReplacementAsync(index); await AddNeededAsync(index); bool checkedGuard = false;
        var retention = new BlockRetention(Root, index, keys, Device, clock: clock) { Checkpoint = stage =>
        {
            if (stage != "capture-reserved") return;
            index.ReadCaptureRetirements().Single().ReplacementId.ShouldNotBeNull();
            Should.Throw<IOException>(() => File.Open(Final(proof), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)); checkedGuard = true;
        } };
        var result = await retention.SweepAsync(Token); result.Deleted.ShouldBe(1); result.WaitingForRecovery.ShouldBeFalse(); checkedGuard.ShouldBeTrue();
        File.Exists(Data(proof)).ShouldBeFalse(); File.Exists(Marker(proof)).ShouldBeFalse(); File.ReadAllBytes(Final(proof)).ShouldBe(Expected);
        index.LocalFiles.Find("current")!.ContentReady.ShouldBeTrue(); index.Needed.Find("incoming").ShouldNotBeNull();
        index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash)).Kinds.ShouldBe(BlockReferenceKind.Needed);
    }

    [Theory]
    [InlineData("capture-reserved")]
    [InlineData("capture-data-deleted")]
    public async Task LostReplacementPreservesPresentPrefixesOrWithholdsAlreadyMissingReadiness(string boundary)
    {
        await using var index = await PrepareAsync(); var proof = Stage(3); await FillReplacementAsync(index); await AddNeededAsync(index);
        await Should.ThrowAsync<InvalidOperationException>(() => CrashAt(index, boundary).SweepAsync(Token)); File.Move(Final(proof), Final(proof) + ".unknown");
        var result = await Retention(index).SweepAsync(Token);
        if (boundary == "capture-reserved") { result.Cancelled.ShouldBe(1); File.ReadAllBytes(Data(proof)).ShouldBe(Expected[..3]); File.Exists(Marker(proof)).ShouldBeTrue(); }
        else { result.Missing.ShouldBe(1); index.LocalFiles.Find("current")!.ContentReady.ShouldBeFalse(); }
        index.ReadCaptureRetirements().ShouldBeEmpty(); File.ReadAllBytes(Final(proof) + ".unknown").ShouldBe(Expected);
    }

    [Theory]
    [InlineData("missing-catalog")]
    [InlineData("changed")]
    [InlineData("replaced")]
    [InlineData("locked")]
    public async Task ReferencedDuplicateDoesNotExpireWithAnUnverifiableCompleteReplacement(string change)
    {
        await using var index = await PrepareAsync(); var proof = Stage(3); await FillReplacementAsync(index); await AddNeededAsync(index); FileStream? held = null;
        if (change == "missing-catalog") Sql("DELETE FROM block_objects;");
        else if (change == "changed") { using var writer = File.Open(Final(proof), FileMode.Open, FileAccess.Write); writer.WriteByte(0); }
        else if (change == "replaced") { File.Move(Final(proof), Final(proof) + ".unknown"); File.WriteAllBytes(Final(proof), Expected); }
        else held = File.Open(Final(proof), FileMode.Open, FileAccess.Read, FileShare.None);
        try { var result = await Retention(index).SweepAsync(Token); result.Deleted.ShouldBe(0); result.WaitingForRecovery.ShouldBeTrue(); }
        finally { held?.Dispose(); }
        File.ReadAllBytes(Data(proof)).ShouldBe(Expected[..3]); File.Exists(Marker(proof)).ShouldBeTrue(); index.ReadCaptureRetirements().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownArchiveOrPendingApplyPreventsPrefixRetirement(bool pending)
    {
        await using var index = await PrepareAsync(); var proof = Stage(3);
        if (pending) await index.ReserveApplyAsync(new(Guid.NewGuid(), "pending", null, [1]), Token);
        else await index.Archive.UpsertAsync([new(0, "unknown", ".psp/archive/unknown", clock.Now.UtcTicks, Expected.Length, Hash)], Token);
        (await Retention(index).SweepAsync(Token)).WaitingForRecovery.ShouldBeTrue(); File.Exists(Data(proof)).ShouldBeTrue(); File.Exists(Marker(proof)).ShouldBeTrue(); index.ReadCaptureRetirements().ShouldBeEmpty();
    }

    private BlockRetention Retention(FolderIndex index) => new(Root, index, keys, Device, clock: clock);
    private BlockRetention CrashAt(FolderIndex index, string boundary) => new(Root, index, keys, Device, clock: clock)
    { Checkpoint = stage => { if (stage == boundary) throw new InvalidOperationException("process boundary"); } };
    private async Task<FolderIndex> PrepareAsync()
    {
        File.WriteAllText(Disk("current"), "current bytes"); var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    private BlockCaptureProof Stage(int prefixLength, int ageHours = 25)
    {
        Guid id = Guid.NewGuid(); using var tree = new SafeFileTree(Root);
        using var parent = tree.Directory(".psp/blocks/" + Convert.ToHexString(Hash)[..2], create: true, privatePath: true);
        using var home = tree.Directory(BlockCaptureProof.Directory, create: true, privatePath: true);
        using var data = parent.CreateFile(id.ToString("N") + ".!psp", temporary: true); using var marker = home.CreateFile(id.ToString("N") + ".capture");
        var proof = new BlockCaptureProof(id, tree.Identity.ToArray(), BlockCaptureProof.Identity(marker), BlockCaptureProof.Identity(data), Hash, Expected.Length, clock.Now.AddHours(-ageHours).UtcTicks);
        RandomAccess.Write(marker, proof.Encode(), 0); RandomAccess.FlushToDisk(marker);
        byte[] bytes = prefixLength < Expected.Length ? Expected[..prefixLength] : new byte[prefixLength]; RandomAccess.Write(data, bytes, 0); RandomAccess.FlushToDisk(data); return proof;
    }
    private async Task AddNeededAsync(FolderIndex index)
    {
        byte[] peer = Enumerable.Repeat((byte)2, 32).ToArray(); var body = new FileRecordBody { Path = "incoming", Size = (ulong)Expected.Length, MtimeTicks = DateTime.UtcNow.Ticks,
            ChangedTicks = DateTime.UtcNow.Ticks, ModifiedBy = ByteString.CopyFrom(peer), FileHash = ByteString.CopyFrom(Hash) };
        body.Vv.Add(new VvEntry { Device = BinaryPrimitives.ReadUInt64LittleEndian(peer), Counter = 1 }); body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash), Length = (uint)Expected.Length });
        await new NeededSet(index, keys, Device).ApplyIndexUpdateAsync(peer, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
    }
    private async Task FillReplacementAsync(FolderIndex index)
    {
        using var operation = await FolderOperationGate.AcquireAsync(index, Token); using var tree = new SafeFileTree(Root); using var blocks = new PublishedBlocks(tree);
        blocks.Put(Expected, Hash, Token); await blocks.FlushAsync(index, Token);
    }
    private string Data(BlockCaptureProof proof) => Disk(proof.Parent + "/" + proof.Temporary);
    private string Marker(BlockCaptureProof proof) => Disk(BlockCaptureProof.Directory + "/" + proof.Name);
    private string Final(BlockCaptureProof proof) => Disk(proof.Parent + "/" + proof.Final);
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-CaptureRetention-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        foreach (string link in links)
        { string path = Path.GetFullPath(link); if (!path.StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Link cleanup escaped fixture."); Directory.Delete(path); }
        Directory.Delete(resolved, recursive: true);
    }
}
