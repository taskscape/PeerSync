using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
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

public sealed class BlockRetentionTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-BlockRetention-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly MutableClock clock = new(DateTimeOffset.UtcNow);
    private readonly List<string> links = [];
    private static byte[] Device => Enumerable.Repeat((byte)1, 32).ToArray();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static byte[] Bytes => "owned orphan bytes"u8.ToArray();
    private static byte[] Hash => Blake3.Hasher.Hash(Bytes).AsSpan().ToArray();
    public BlockRetentionTests() => Directory.CreateDirectory(Root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedUnreferencedBlocksRequireGraceAndExpireByAgeOrCapacityWhileCurrentAndUnknownFilesRemain(bool pressure)
    {
        await using var index = await PrepareAsync(); string orphan = await AddOrphanAsync(index); string unknown = Disk(".psp/blocks/unknown"); File.WriteAllText(unknown, "unknown bytes");
        var retention = Retention(index, pressure: pressure); (await retention.SweepAsync(Token)).Deleted.ShouldBe(0);
        clock.Now = clock.Now.AddHours(23); (await retention.SweepAsync(Token)).Deleted.ShouldBe(0);
        clock.Now = clock.Now.AddHours(2); var afterGrace = await retention.SweepAsync(Token); afterGrace.Deleted.ShouldBe(pressure ? 1 : 0);
        if (!pressure) { clock.Now = clock.Now.AddDays(30); (await retention.SweepAsync(Token)).Deleted.ShouldBe(1); }
        File.Exists(orphan).ShouldBeFalse(); File.ReadAllText(unknown).ShouldBe("unknown bytes"); File.ReadAllText(Disk("current")).ShouldBe("current bytes");
        index.ReadBlockDeletionIntents().ShouldBeEmpty(); index.ReadBlockObjects().Count().ShouldBe(1);
        (await Retention(index).SweepAsync(Token)).Deleted.ShouldBe(0);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("deleted")]
    [InlineData("completed")]
    public async Task EveryNativeDeletionBoundaryRecoversThroughReopenWithoutLosingCurrentData(string boundary)
    {
        string orphan;
        await using (var index = await PrepareAsync())
        {
            orphan = await AddOrphanAsync(index); await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31);
            var retention = new BlockRetention(Root, index, keys, Device, clock: clock) { Checkpoint = stage => { if (stage == boundary) throw new InvalidOperationException("process boundary"); } };
            await Should.ThrowAsync<InvalidOperationException>(() => retention.SweepAsync(Token));
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); await Retention(reopened).SweepAsync(Token);
        File.Exists(orphan).ShouldBeFalse(); reopened.ReadBlockDeletionIntents().ShouldBeEmpty(); reopened.ReadBlockObjects().Count().ShouldBe(1);
        File.ReadAllText(Disk("current")).ShouldBe("current bytes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewReferencesAfterACrashCancelPresentDeletionOrWithholdReadinessForAlreadyMissingBytes(bool alreadyDeleted)
    {
        await using var index = await PrepareAsync(); string orphan = await AddOrphanAsync(index); await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31);
        var retention = new BlockRetention(Root, index, keys, Device, clock: clock)
        { Checkpoint = stage => { if (stage == (alreadyDeleted ? "deleted" : "prepared")) throw new InvalidOperationException("process boundary"); } };
        await Should.ThrowAsync<InvalidOperationException>(() => retention.SweepAsync(Token));
        await AddNeededAsync(index); var result = await Retention(index).SweepAsync(Token);
        if (alreadyDeleted)
        {
            result.Missing.ShouldBe(1); File.Exists(orphan).ShouldBeFalse(); index.LocalFiles.Find("current")!.ContentReady.ShouldBeFalse(); index.ReadIndexBatch(0).Records.ShouldBeEmpty();
            // Ordinary scan recaptures independent current data and restores
            // readiness. The missing needed block remains a real pull request.
            using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token); index.LocalFiles.Find("current")!.ContentReady.ShouldBeTrue();
        }
        else { result.Cancelled.ShouldBe(1); File.ReadAllBytes(orphan).ShouldBe(Bytes); }
        index.Needed.Find("incoming").ShouldNotBeNull(); index.ReadBlockDeletionIntents().ShouldBeEmpty();
        index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash)).Kinds.ShouldBe(BlockReferenceKind.Needed);
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("locked")]
    [InlineData("linked")]
    [InlineData("reparse")]
    public async Task UnknownChangedLockedLinkedAndReparseObjectsArePreservedWithVisibleDeferral(string change)
    {
        await using var index = await PrepareAsync(); string orphan = await AddOrphanAsync(index); await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31);
        FileStream? held = null;
        if (change == "replaced") { File.Move(orphan, orphan + ".unknown"); File.WriteAllBytes(orphan, Bytes); }
        else if (change is "hash" or "length")
        { using var writer = File.Open(orphan, FileMode.Open, FileAccess.Write, FileShare.Read); writer.Write(new byte[Bytes.Length]); if (change == "length") writer.SetLength(1); }
        else if (change == "locked") held = File.Open(orphan, FileMode.Open, FileAccess.Read, FileShare.None);
        else if (change == "linked")
        {
            using var tree = new SafeFileTree(Root); using var parent = tree.Directory(Parent, privatePath: true); using var home = tree.Directory("");
            using var file = parent.OpenFile(Convert.ToHexString(Hash), mutation: true); home.LinkHere(file, "unknown-hardlink");
        }
        else if (change == "reparse")
        {
            string outside = Path.Combine(fixture, "outside"); Directory.Move(Disk(Parent), outside);
            Directory.CreateSymbolicLink(Disk(Parent), outside); links.Add(Disk(Parent));
        }
        try { var result = await Retention(index).SweepAsync(Token); result.Deleted.ShouldBe(0); result.Deferred.ShouldBeGreaterThan(0); index.ReadBlockDeletionIntents().ShouldBeEmpty(); }
        finally { held?.Dispose(); }
        File.Exists(orphan).ShouldBeTrue(); index.ReadBlockObjects().Any(r => r.Hash.SequenceEqual(Hash)).ShouldBeTrue();
        if (change is not ("hash" or "length")) File.ReadAllBytes(orphan).ShouldBe(Bytes);
        if (change == "linked") File.ReadAllBytes(Disk("unknown-hardlink")).ShouldBe(Bytes);
    }

    [Fact]
    public async Task RecapturedReplacementCancelsAnOldIntentAndReceivesANewGracePeriod()
    {
        await using var index = await PrepareAsync(); string orphan = await AddOrphanAsync(index); await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31);
        var retention = new BlockRetention(Root, index, keys, Device, clock: clock)
        { Checkpoint = stage => { if (stage == "prepared") throw new InvalidOperationException("process boundary"); } };
        await Should.ThrowAsync<InvalidOperationException>(() => retention.SweepAsync(Token)); File.Move(orphan, orphan + ".unknown");
        using (var tree = new SafeFileTree(Root)) using (var blocks = new PublishedBlocks(tree)) { blocks.Put(Bytes, Hash, Token); await blocks.FlushAsync(index, Token); }
        var result = await Retention(index).SweepAsync(Token); result.Cancelled.ShouldBe(1); result.Deleted.ShouldBe(0); File.ReadAllBytes(orphan).ShouldBe(Bytes);
        index.ReadBlockDeletionIntents().ShouldBeEmpty(); clock.Now = clock.Now.AddDays(31); (await Retention(index).SweepAsync(Token)).Deleted.ShouldBe(1);
        File.ReadAllBytes(orphan + ".unknown").ShouldBe(Bytes);
    }

    [Fact]
    public async Task UnpreparedMissingObjectsReconcileWithoutInventingAPolicyDeletion()
    {
        await using var index = await PrepareAsync(); string orphan = await AddOrphanAsync(index); await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31); File.Delete(orphan);
        var result = await Retention(index).SweepAsync(Token); result.Missing.ShouldBe(1); result.Deleted.ShouldBe(0); result.BytesRemoved.ShouldBe(0);
        index.ReadBlockDeletionIntents().ShouldBeEmpty(); index.ReadBlockObjects().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSqliteReservationOrCompletionFailureKeepsARecoverableTruthfulJournal(bool completion)
    {
        await using var index = await PrepareAsync(); string orphan = await AddOrphanAsync(index); await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31);
        Sql(completion ? "CREATE TRIGGER reject_sweep BEFORE DELETE ON block_objects BEGIN SELECT RAISE(ABORT,'private SQL text'); END;"
            : "CREATE TRIGGER reject_sweep BEFORE INSERT ON block_deletions BEGIN SELECT RAISE(ABORT,'private SQL text'); END;");
        using (var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "sweep", debugEnabled: true))
            await Should.ThrowAsync<SqliteException>(() => new BlockRetention(Root, index, keys, Device, clock: clock, journal: journal).SweepAsync(Token));
        File.Exists(orphan).ShouldBe(!completion); index.ReadBlockDeletionIntents().Count().ShouldBe(completion ? 1 : 0);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); log.ShouldContain("block.sweep_failed"); log.ShouldContain("1811");
        log.ShouldNotContain(Root); log.ShouldNotContain(Convert.ToHexString(Hash)); log.ShouldNotContain("private SQL text"); log.ShouldNotContain("owned orphan bytes");
        Sql("DROP TRIGGER reject_sweep;"); await Retention(index).SweepAsync(Token); File.Exists(orphan).ShouldBeFalse(); index.ReadBlockDeletionIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task UncoveredArchiveAndPendingApplyPreventSweepsAndCancellationReleasesBothGates()
    {
        await using var index = await PrepareAsync(); string orphan = await AddOrphanAsync(index); await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31);
        var pending = new ApplyIntent(Guid.NewGuid(), "pending", null, [1]); await index.ReserveApplyAsync(pending, Token);
        (await Retention(index).SweepAsync(Token)).WaitingForRecovery.ShouldBeTrue(); File.Exists(orphan).ShouldBeTrue(); await index.AbandonApplyAsync(pending, Token);
        await index.Archive.UpsertAsync([new(0, "unknown", ".psp/archive/unknown", clock.Now.UtcTicks, Bytes.Length, Hash)], Token);
        (await Retention(index).SweepAsync(Token)).WaitingForRecovery.ShouldBeTrue(); File.Exists(orphan).ShouldBeTrue();
        using (var held = await FolderReferenceGate.AcquireAsync(index, Token))
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Token))
        { Task<BlockSweepResult> request = Retention(index).SweepAsync(stop.Token); request.IsCompleted.ShouldBeFalse(); stop.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => request); }
        using var operation = await FolderOperationGate.AcquireAsync(index, Token); using var references = await FolderReferenceGate.AcquireAsync(index, Token);
    }

    [Fact]
    public async Task IncompleteOwnedCaptureDefersSweepingWithoutDiscardingItsPrefixOrOtherOrphans()
    {
        await using var index = await PrepareAsync(); string orphan = await AddOrphanAsync(index); await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31);
        Guid id = Guid.NewGuid(); byte[] expected = "incomplete captured data"u8.ToArray(), hash = Blake3.Hasher.Hash(expected).AsSpan().ToArray();
        string parentPath = ".psp/blocks/" + Convert.ToHexString(hash)[..2];
        using (var tree = new SafeFileTree(Root))
        using (var parent = tree.Directory(parentPath, create: true, privatePath: true))
        using (var home = tree.Directory(BlockCaptureProof.Directory, create: true, privatePath: true))
        using (var data = parent.CreateFile(id.ToString("N") + ".!psp", temporary: true))
        using (var marker = home.CreateFile(id.ToString("N") + ".capture"))
        {
            var proof = new BlockCaptureProof(id, tree.Identity.ToArray(), BlockCaptureProof.Identity(marker), BlockCaptureProof.Identity(data), hash, expected.Length, clock.Now.UtcTicks);
            RandomAccess.Write(marker, proof.Encode(), 0); RandomAccess.FlushToDisk(marker); RandomAccess.Write(data, expected.AsSpan(0, 2), 0); RandomAccess.FlushToDisk(data);
        }
        var result = await Retention(index).SweepAsync(Token); result.WaitingForRecovery.ShouldBeTrue(); result.Deleted.ShouldBe(0); result.Deferred.ShouldBeGreaterThan(0);
        File.Exists(orphan).ShouldBeTrue(); File.ReadAllBytes(Disk(parentPath + "/" + id.ToString("N") + ".!psp")).ShouldBe(expected[..2]);
    }

    [Fact]
    public async Task ProductionPassDeletesAtMostOneThousandOwnedObjectsThenContinuesOnTheNextPass()
    {
        await using var index = await PrepareAsync();
        using (var tree = new SafeFileTree(Root)) using (var blocks = new PublishedBlocks(tree))
        {
            for (int i = 0; i < 1001; i++)
            { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, i); blocks.Put(bytes, Blake3.Hasher.Hash(bytes).AsSpan(), Token); }
            await blocks.FlushAsync(index, Token);
        }
        (await Retention(index).SweepAsync(Token)).Deleted.ShouldBe(0); clock.Now = clock.Now.AddDays(31);
        var first = await Retention(index).SweepAsync(Token); first.Deleted.ShouldBe(1000); first.OwnedBytes.ShouldBe(17);
        (await Retention(index).SweepAsync(Token)).Deleted.ShouldBe(1); index.ReadBlockObjects().Count().ShouldBe(1);
    }

    [Fact]
    public async Task PreparedDeletionHoldsItsVerifiedNativeGuardUntilTheDurableIntentExists()
    {
        await using var index = await PrepareAsync(); string orphan = await AddOrphanAsync(index); await Retention(index).SweepAsync(Token); clock.Now = clock.Now.AddDays(31); bool held = false;
        var retention = new BlockRetention(Root, index, keys, Device, clock: clock) { Checkpoint = stage =>
        {
            if (stage != "prepared") return;
            index.ReadBlockDeletionIntents().Count().ShouldBe(1);
            Should.Throw<IOException>(() => File.Open(orphan, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)); held = true;
        } };
        (await retention.SweepAsync(Token)).Deleted.ShouldBe(1); held.ShouldBeTrue();
    }

    private BlockRetention Retention(FolderIndex index, bool pressure = false) => new(Root, index, keys, Device, clock: clock) { CapacityOverride = pressure ? () => 1UL : null };
    private async Task<FolderIndex> PrepareAsync()
    {
        File.WriteAllText(Disk("current"), "current bytes"); var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    private async Task<string> AddOrphanAsync(FolderIndex index)
    {
        using var tree = new SafeFileTree(Root); using var blocks = new PublishedBlocks(tree); blocks.Put(Bytes, Hash, Token); await blocks.FlushAsync(index, Token); return Disk(Parent + "/" + Convert.ToHexString(Hash));
    }
    private async Task AddNeededAsync(FolderIndex index)
    {
        byte[] peer = Enumerable.Repeat((byte)2, 32).ToArray(); var body = new FileRecordBody { Path = "incoming", Size = (ulong)Bytes.Length, MtimeTicks = DateTime.UtcNow.Ticks,
            ChangedTicks = DateTime.UtcNow.Ticks, ModifiedBy = ByteString.CopyFrom(peer), FileHash = ByteString.CopyFrom(Hash) };
        body.Vv.Add(new VvEntry { Device = BinaryPrimitives.ReadUInt64LittleEndian(peer), Counter = 1 }); body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash), Length = (uint)Bytes.Length });
        await new NeededSet(index, keys, Device).ApplyIndexUpdateAsync(peer, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
    }
    private static string Parent => ".psp/blocks/" + Convert.ToHexString(Hash)[..2];
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
            || !Path.GetFileName(resolved).StartsWith("PeerSync-BlockRetention-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        foreach (string link in links)
        { string path = Path.GetFullPath(link); if (!path.StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Link cleanup escaped fixture."); Directory.Delete(path); }
        Directory.Delete(resolved, recursive: true);
    }
}
