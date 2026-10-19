using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
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
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Tests;

public sealed class BlockReferencesTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-BlockReferences-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static byte[] Device(byte id) { byte[] value = new byte[32]; value[0] = id; return value; }
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Current => "current bytes"u8.ToArray();
    public BlockReferencesTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task WithheldSignedManifestProtectsBytesEvenWithoutMutableChunkHintsAndSurvivesReopening()
    {
        Guid id;
        await using (var index = await PrepareAsync())
        {
            var row = index.LocalFiles.Find("private-name")!;
            await index.LocalFiles.UpsertAsync([row with { Flags = LocalFileState.NeedsRescan, ContentReady = false }], Token);
            await index.Chunks.DeleteAsync([row.FileHash!, "private-name", 0L], Token); index.Chunks.Count().ShouldBe(0);
            var result = await Builder(index).RefreshAsync(Token); result.References.ShouldBe(1); result.AllKnownSourcesCovered.ShouldBeTrue(); id = result.SnapshotId!.Value;
            index.ReadBlockReferences().Single().Hash.ShouldBe(Hash(Current)); index.ReadBlockReferences().Single().Kinds.ShouldBe(BlockReferenceKind.Current);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        reopened.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(id); reopened.ReadBlockReferences().Single().Hash.ShouldBe(Hash(Current));
    }

    [Fact]
    public async Task UnsignedReadOnlyHintsAreProtectedWithoutCapturingOrPublishingLocalEdits()
    {
        File.WriteAllBytes(Disk("local-ro"), Current); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var ro = FolderKeys.FromText(keys.ExportReadOnlyText()); using (var scanner = new Scanner(Root, index, ro, Device(1))) await scanner.ScanAsync(Token);
        var result = await new BlockReferences(Root, index, ro, Device(1)).RefreshAsync(Token); result.References.ShouldBe(1);
        index.ReadIndexBatch(0).Records.ShouldBeEmpty(); index.ReadBlockObjects().ShouldBeEmpty(); Directory.Exists(Disk(".psp/blocks")).ShouldBeFalse();
    }

    [Fact]
    public async Task AllCandidatesAtNeededPathsAreProtectedButUnneededRemoteHintsAreExcluded()
    {
        await using var index = await PrepareAsync(); var needed = new NeededSet(index, keys, Device(1));
        byte[] first = "first remote"u8.ToArray(), second = "second remote"u8.ToArray(), unused = "unused remote"u8.ToArray();
        await needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record("incoming", 2, first)], Token);
        await needed.ApplyIndexUpdateAsync(Device(3), 0, 1, [Record("incoming", 3, second)], Token);
        var extra = Record("unused", 4, unused); var view = VersionView.Open(extra, keys, Device(4));
        await index.RemoteFiles.UpsertAsync([new(Device(4), "UNUSED", view.Vector, extra)], Token);
        var result = await Builder(index).RefreshAsync(Token); result.References.ShouldBe(3);
        var rows = index.ReadBlockReferences().ToDictionary(r => Convert.ToHexString(r.Hash));
        rows[Convert.ToHexString(Hash(first))].Kinds.ShouldBe(BlockReferenceKind.Needed); rows[Convert.ToHexString(Hash(second))].Kinds.ShouldBe(BlockReferenceKind.Needed);
        rows.ContainsKey(Convert.ToHexString(Hash(unused))).ShouldBeFalse();
    }

    [Fact]
    public async Task PendingCaseManifestRetainsReferencesAfterRemoteReplacementAndNeededRemoval()
    {
        await using var index = await PrepareAsync();
        byte[] bytes = "case source survives replacement"u8.ToArray();
        var original = Record("incoming", 2, bytes); var view = VersionView.Open(original, keys, Device(2));
        var source = new CasePreservationSource(CaseConflictPolicy.SourceId(view), Device(2), view.Path, original);
        await index.ApplyRemoteIndexAsync(Device(2), 0, 1, [new(Device(2), "INCOMING", view.Vector, original)],
            snapshot => new(snapshot.PathKey, Device(2), 0) { CasePreservations = [source] }, Token);
        var replacement = Record("incoming", 2, "replacement bytes"u8.ToArray()).WithLocalSequence(2);
        var next = VersionView.Open(replacement, keys, Device(2));
        await index.ApplyRemoteIndexAsync(Device(2), 1, 2, [new(Device(2), "INCOMING", next.Vector, replacement)], _ => null, Token);
        index.Needed.Count().ShouldBe(0);
        (await Builder(index).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
        index.ReadBlockReferences().Single(row => row.Hash.SequenceEqual(Hash(bytes))).Kinds.ShouldBe(BlockReferenceKind.Needed);
        index.ReadBlockReferences().Any(row => row.Hash.SequenceEqual(Hash("replacement bytes"u8))).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidCaseSourceIdentityOrSignatureCannotReplaceActiveReferences(bool corruptSignature)
    {
        await using var index = await PrepareAsync(); var before = await Builder(index).RefreshAsync(Token);
        var original = Record("incoming", 2, "private case bytes"u8.ToArray()); var view = VersionView.Open(original, keys, Device(2));
        var source = new CasePreservationSource(corruptSignature ? CaseConflictPolicy.SourceId(view) : new byte[32], Device(2), view.Path,
            corruptSignature ? new(original.Body, new byte[64]) : original);
        await index.ApplyRemoteIndexAsync(Device(2), 0, 1, [new(Device(2), "INCOMING", view.Vector, original)],
            snapshot => new(snapshot.PathKey, Device(2), 0) { CasePreservations = [source] }, Token);
        if (corruptSignature) await Should.ThrowAsync<CryptographicException>(() => Builder(index).RefreshAsync(Token));
        else await Should.ThrowAsync<InvalidDataException>(() => Builder(index).RefreshAsync(Token));
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value);
        index.ReadBlockReferences().Single().Hash.ShouldBe(Hash(Current));
    }

    [Fact]
    public async Task AuthenticatedHistoryRetainsOldChunksEvenWhenItsPhysicalArchiveUnexpectedlyDisappears()
    {
        await using var index = await PrepareAsync(); File.WriteAllText(Disk("private-name"), "new bytes");
        using (var scanner = new Scanner(Root, index, keys, Device(1))) await scanner.ScanAsync(Token);
        var first = await Builder(index).RefreshAsync(Token); first.References.ShouldBe(2); first.ArchivesWithoutManifest.ShouldBe(0);
        index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash(Current))).Kinds.ShouldBe(BlockReferenceKind.History);
        File.Delete(Disk(index.Archive.ReadAll().Single().ArchivePath)); await Builder(index).RefreshAsync(Token);
        index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash(Current))).Kinds.ShouldBe(BlockReferenceKind.History);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSignatureOrForeignRootHistoryKeepsThePreviousActiveSnapshot(bool foreignRoot)
    {
        await using var index = await PrepareAsync(); File.WriteAllText(Disk("private-name"), "new bytes");
        using (var scanner = new Scanner(Root, index, keys, Device(1))) await scanner.ScanAsync(Token);
        var before = await Builder(index).RefreshAsync(Token); var marker = index.Meta.ReadAll().Single(r => r.Key.StartsWith("_history-", StringComparison.Ordinal));
        byte[] changed = marker.Value!.ToArray(); changed[foreignRoot ? 9 : 73] ^= 1; await index.Meta.UpsertAsync([marker with { Value = changed }], Token);
        if (foreignRoot) await Should.ThrowAsync<IOException>(() => Builder(index).RefreshAsync(Token));
        else await Should.ThrowAsync<CryptographicException>(() => Builder(index).RefreshAsync(Token));
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value); index.ReadBlockReferences().Count().ShouldBe(2);
    }

    [Fact]
    public async Task CancellationAfterABoundedBatchPreservesPriorSnapshotAndRestartRebuildsCompletely()
    {
        Guid before;
        await using (var index = await PrepareAsync())
        {
            before = (await Builder(index).RefreshAsync(Token)).SnapshotId!.Value;
            await new NeededSet(index, keys, Device(1)).ApplyIndexUpdateAsync(Device(2), 0, 1, [ManyChunks(1001)], Token);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var builder = new BlockReferences(Root, index, keys, Device(1)) { Checkpoint = stage => { if (stage == "batch") stop.Cancel(); } };
            await Should.ThrowAsync<OperationCanceledException>(() => builder.RefreshAsync(stop.Token));
            index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before); index.ReadBlockReferences().Count().ShouldBe(1);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var result = await Builder(reopened).RefreshAsync(Token);
        result.References.ShouldBe(1002); result.Batches.ShouldBe(2); reopened.ReadBlockReferences().Count().ShouldBe(1002);
    }

    [Fact]
    public async Task CaptureRecoveryRunsBeforeWaitingForAnApplicationIntent()
    {
        await using var index = await PrepareAsync(); var before = await Builder(index).RefreshAsync(Token);
        byte[] bytes = "captured before crash"u8.ToArray(); using var tree = new SafeFileTree(Root);
        using (var blocks = new PublishedBlocks(tree) { Checkpoint = stage => { if (stage == "installed") throw new IOException("fault"); } })
            Should.Throw<IOException>(() => blocks.Put(bytes, Hash(bytes), Token));
        var intent = new ApplyIntent(Guid.NewGuid(), "pending", null, [1]); await index.ReserveApplyAsync(intent, Token);
        var result = await Builder(index).RefreshAsync(Token); result.WaitingForRecovery.ShouldBeTrue(); result.AllKnownSourcesCovered.ShouldBeFalse();
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value);
        index.ReadBlockObjects().Any(r => r.Hash.SequenceEqual(Hash(bytes))).ShouldBeTrue();
        await index.AbandonApplyAsync(intent, Token);
    }

    [Fact]
    public async Task UnmanifestedArchiveIsVerifiedBeforeReferenceCoverageAndNeverLosesItsData()
    {
        await using var index = await PrepareAsync(); Directory.CreateDirectory(Disk(".psp/archive")); File.WriteAllBytes(Disk(".psp/archive/raw"), Current);
        await index.Archive.UpsertAsync([new(0, "raw", ".psp/archive/raw", DateTime.UtcNow.Ticks, Current.Length, Hash(Current))], Token);
        var result = await Builder(index).RefreshAsync(Token); result.ArchivesWithoutManifest.ShouldBe(0); result.AllKnownSourcesCovered.ShouldBeTrue();
        File.ReadAllBytes(Disk(".psp/archive/raw")).ShouldBe(Current); index.ReadBlockObjects().Count().ShouldBe(1);
        var content = index.FindActiveArchiveContent(index.ReadArchiveProvenance().Single().SourceId)!;
        content.Size.ShouldBe(Current.Length); content.FileHash.ShouldBe(Hash(Current));
        index.ReadBlockReferences().Single().Kinds.ShouldBe(BlockReferenceKind.Current | BlockReferenceKind.Archive);
    }

    [Fact]
    public async Task ActivationFailureRetainsOldProtectionAndRetrySwitchesToTheCompleteSnapshot()
    {
        await using var index = await PrepareAsync(); var before = await Builder(index).RefreshAsync(Token);
        File.WriteAllText(Disk("new-file"), "new separate bytes"); using (var scanner = new Scanner(Root, index, keys, Device(1))) await scanner.ScanAsync(Token);
        Sql("CREATE TRIGGER reject_reference BEFORE UPDATE ON block_reference_snapshots WHEN NEW.active=1 BEGIN SELECT RAISE(ABORT,'private SQL text'); END;");
        await Should.ThrowAsync<SqliteException>(() => Builder(index).RefreshAsync(Token));
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value); index.ReadBlockReferences().Count().ShouldBe(1);
        Sql("DROP TRIGGER reject_reference;"); (await Builder(index).RefreshAsync(Token)).References.ShouldBe(2);
    }

    [Fact]
    public async Task NewRootCannotReplaceProtectionBelongingToTheOriginalTree()
    {
        await using var index = await PrepareAsync(); var before = await Builder(index).RefreshAsync(Token);
        Directory.Move(Root, Path.Combine(fixture, "old-tree")); Directory.CreateDirectory(Root);
        await Should.ThrowAsync<IOException>(() => Builder(index).RefreshAsync(Token));
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value);
        File.ReadAllBytes(Path.Combine(fixture, "old-tree", "private-name")).ShouldBe(Current);
    }

    [Fact]
    public async Task ReferenceMaintenanceExcludesReceiveUntilActivationWithoutBlockingLaterProgress()
    {
        await using var index = await PrepareAsync(); Task<RemoteIndexResult>? receive = null;
        var needed = new NeededSet(index, keys, Device(1));
        var builder = new BlockReferences(Root, index, keys, Device(1)) { Checkpoint = stage =>
        {
            if (stage != "before-activate") return;
            receive = needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record("incoming", 2, "incoming bytes"u8.ToArray())], Token);
            receive.IsCompleted.ShouldBeFalse();
        } };
        (await builder.RefreshAsync(Token)).References.ShouldBe(1); (receive is not null).ShouldBeTrue(); (await receive!).Applied.ShouldBeTrue();
        (await Builder(index).RefreshAsync(Token)).References.ShouldBe(2);
    }

    [Theory]
    [InlineData("receive")]
    [InlineData("recompute")]
    [InlineData("rebuild")]
    public async Task IndexReferenceMutationsWaitCancellablyForTheirOwnGate(string method)
    {
        await using var index = await PrepareAsync(); var needed = new NeededSet(index, keys, Device(1));
        using var held = await FolderReferenceGate.AcquireAsync(index, Token); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task pending = method switch
        {
            "receive" => needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record("incoming", 2, Current)], stop.Token),
            "recompute" => needed.RecomputeAsync(["incoming"], stop.Token), _ => needed.RebuildAsync(stop.Token),
        };
        pending.IsCompleted.ShouldBeFalse(); stop.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => pending);
        held.Dispose(); (await Builder(index).RefreshAsync(Token)).References.ShouldBe(1);
    }

    [Fact]
    public async Task WaitingIndexRequestsOwnTheirInputsBeforeTheCallerReusesCollections()
    {
        await using var index = await PrepareAsync(); var needed = new NeededSet(index, keys, Device(1));
        using (var held = await FolderReferenceGate.AcquireAsync(index, Token))
        {
            var records = new List<Envelope> { Record("incoming", 2, Current) }; Task<RemoteIndexResult> pending = needed.ApplyIndexUpdateAsync(Device(2), 0, 1, records, Token);
            records[0] = Record("changed-request", 2, Current); held.Dispose(); await pending;
        }
        index.RemotesForPath("incoming").Count().ShouldBe(1); index.RemotesForPath("changed-request").ShouldBeEmpty();
        await index.Needed.DeleteAsync(["incoming"], Token);
        using (var held = await FolderReferenceGate.AcquireAsync(index, Token))
        {
            var paths = new List<string> { "incoming" }; Task pending = needed.RecomputeAsync(paths, Token); paths[0] = "changed-request";
            held.Dispose(); await pending;
        }
        index.Needed.Find("incoming").ShouldNotBeNull();
    }

    [Fact]
    public async Task DiagnosticsReportCountsAndFailureCodesWithoutNamesHashesBytesOrSql()
    {
        await using var index = await PrepareAsync(); using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "references", debugEnabled: true);
        var builder = new BlockReferences(Root, index, keys, Device(1), journal); await builder.RefreshAsync(Token);
        Sql("CREATE TRIGGER reject_reference BEFORE UPDATE ON block_reference_snapshots WHEN NEW.active=1 BEGIN SELECT RAISE(ABORT,'private SQL text'); END;");
        await Should.ThrowAsync<SqliteException>(() => builder.RefreshAsync(Token));
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); log.ShouldContain("block.references_complete"); log.ShouldContain("block.references_failed");
        log.ShouldNotContain(Root); log.ShouldNotContain("private-name"); log.ShouldNotContain("current bytes"); log.ShouldNotContain("private SQL text");
        log.ShouldNotContain(Convert.ToHexString(Hash(Current)));
    }

    private BlockReferences Builder(FolderIndex index) => new(Root, index, keys, Device(1));
    private async Task<FolderIndex> PrepareAsync()
    {
        File.WriteAllBytes(Disk("private-name"), Current); var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, Device(1)); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private Envelope Record(string path, byte device, byte[] bytes)
    {
        var body = new FileRecordBody { Path = path, Type = 0, Size = (ulong)bytes.Length, MtimeTicks = DateTime.UtcNow.Ticks,
            ChangedTicks = DateTime.UtcNow.Ticks, ModifiedBy = ByteString.CopyFrom(Device(device)), FileHash = ByteString.CopyFrom(Hash(bytes)) };
        body.Vv.Add(new VvEntry { Device = device, Counter = 1 }); body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash(bytes)), Length = (uint)bytes.Length });
        return SignedRecordFactory.Create(body, keys, 1);
    }
    private Envelope ManyChunks(int count)
    {
        byte[] data = new byte[count * 4]; for (int i = 0; i < count; i++) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), i);
        var record = Record("many", 2, data); SignedRecordFactory.TryOpen(record, keys, out var body).ShouldBeTrue(); body.Chunks.Clear();
        for (int i = 0; i < count; i++) body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash(data.AsSpan(i * 4, 4))), Length = 4 });
        return SignedRecordFactory.Create(body, keys, 1);
    }
    private static byte[] Hash(ReadOnlySpan<byte> data) => Blake3.Hasher.Hash(data).AsSpan().ToArray();
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-BlockReferences-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
