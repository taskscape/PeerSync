using System.Buffers.Binary;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
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

public sealed class DirectorySnapshotGraphTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryGraph-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] device = [1, .. new byte[31]];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DiagnosticsExposeProgressAndFailureWithoutPrivatePathsHashesOrSignedBodies()
    {
        await using var index = await Bind("source"); using var blocks = Store("source", index);
        var root = await blocks.CaptureDirectoryAsync("private-topic", [Sign("private-topic/private-child", 0)], Token);
        var directory = Sign("private-topic", 1, root);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "graph", debugEnabled: true);
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        var graph = new DirectorySnapshotGraph(Root("source"), index, keys, device, journal);
        (await graph.AdvanceUnderGateAsync(directory, NoFetch, 0, Token)).Complete.ShouldBeFalse();
        (await graph.AdvanceUnderGateAsync(directory, NoFetch, 1000, Token)).Complete.ShouldBeTrue();
        var malformed = await blocks.PutAsync(DirectorySnapshotNode.Blob(new byte[36]), Token); await blocks.FlushAsync(Token);
        await Should.ThrowAsync<InvalidDataException>(() => graph.AdvanceUnderGateAsync(Sign("private-bad", 1, malformed), NoFetch, 1000, Token));
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("directory.graph_pending"); log.ShouldContain("directory.graph_complete"); log.ShouldContain("directory.graph_failed");
        log.ShouldNotContain("private-topic"); log.ShouldNotContain("private-child"); log.ShouldNotContain("private-bad"); log.ShouldNotContain(fixture);
        log.ShouldNotContain(Convert.ToHexString(root.Hash)); log.ShouldNotContain(Convert.ToBase64String(directory.Body));
        log.ShouldNotContain(keys.ExportText());
    }

    [Fact]
    public async Task NewlyNeededRetiringGraphWaitsAcrossRestartThenRebuildsFromIndependentMetadata()
    {
        SignedRecord directory; byte[] id;
        await using (var index = await Bind("source"))
        {
            using var blocks = Store("source", index);
            var root = await blocks.CaptureDirectoryAsync("topic", [Sign("topic/file", 0)], Token); directory = Sign("topic", 1, root);
            var complete = await Advance("source", index, directory, NoFetch, 1000); complete.Complete.ShouldBeTrue(); id = complete.Id;
            Age();
            var state = index.ReadDirectoryGraph(id)!;
            (await index.PruneDirectoryGraphsAsync(state.RootId, DateTime.UtcNow.Ticks, true, 1, Token)).Mutations.ShouldBe(1);
            index.ReadDirectoryGraph(id)!.Retiring.ShouldBeTrue();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database("source"), Token);
        var waiting = await Advance("source", reopened, directory, NoFetch, 1000); waiting.Complete.ShouldBeFalse(); waiting.Processed.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => reopened.ReadDirectoryGraphReferences(id).ToArray());
        byte[] physical = reopened.ReadDirectoryGraph(id)!.RootId; int passes = 0;
        DirectoryGraphPrune cleanup;
        do
        {
            cleanup = await reopened.PruneDirectoryGraphsAsync(physical, DateTime.UtcNow.Ticks, false, 2, Token);
            cleanup.Mutations.ShouldBeLessThanOrEqualTo(2); passes++; passes.ShouldBeLessThan(10);
        } while (cleanup.MoreWork);
        (await Advance("source", reopened, directory, NoFetch, 1000)).Complete.ShouldBeTrue();
        reopened.ReadDirectoryGraphReferences(id).Count().ShouldBe(3); reopened.ReadBlockObjects().Count().ShouldBe(3);
        reopened.ReadBlockDeletionIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task FreshReferenceActivationTouchesCurrentGraphsAndRetiresOnlyUnusedCacheMetadata()
    {
        await using var index = await Bind("source"); using var blocks = Store("source", index);
        var empty = await blocks.CaptureDirectoryAsync("topic", [], Token);
        var current = Sign("topic", 1, empty); var view = VersionView.Open(current, keys, device);
        await index.LocalFiles.UpsertAsync([new("topic", 1, 0, view.MtimeTicks, 0, false, view.ChangedTicks, null, view.Vector, current, null)], Token);
        var builder = new BlockReferences(Root("source"), index, keys, device); (await builder.RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
        var unused = await Advance("source", index, Sign("unused", 1, empty), NoFetch, 1); unused.Complete.ShouldBeTrue(); Age();
        (await builder.RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
        index.ReadDirectoryGraph(unused.Id).ShouldBeNull();
        index.ReadDirectoryGraph(CaseConflictPolicy.SourceId(view))!.Retiring.ShouldBeFalse();
        index.ReadBlockReferences().Count().ShouldBe(1); index.ReadBlockObjects().Count().ShouldBe(1); index.ReadBlockDeletionIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task LegacyPendingDirectoryNeverClaimsEmptyTreeCoverage()
    {
        await using var index = await Bind("source");
        var builder = new BlockReferences(Root("source"), index, keys, device); var before = await builder.RefreshAsync(Token);
        var directory = Sign("topic", 1).WithLocalSequence(1); var view = VersionView.Open(directory, keys, device);
        var source = new CasePreservationSource(CaseConflictPolicy.SourceId(view), device, view.Path, directory);
        await index.ApplyRemoteIndexAsync(device, 0, 1, [new(device, "TOPIC", view.Vector, directory)],
            snapshot => new(snapshot.PathKey, device, 0) { CasePreservations = [source] }, Token);
        var result = await builder.RefreshAsync(Token); result.WaitingForDirectorySnapshots.ShouldBeTrue(); result.AllKnownSourcesCovered.ShouldBeFalse();
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value);
        (await new BlockRetention(Root("source"), index, keys, device).SweepAsync(Token)).WaitingForRecovery.ShouldBeTrue();
        index.ReadPendingCasePreservations().Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReferenceRefreshResumesItsSharedBudgetAndActivatesOnlyAfterAllGraphWorkCompletes()
    {
        await using var index = await Bind("source"); using var blocks = Store("source", index);
        var initial = await new BlockReferences(Root("source"), index, keys, device).RefreshAsync(Token);
        var root = await blocks.CaptureDirectoryAsync("topic", [Sign("topic/a", 0), Sign("topic/b", 0)], Token);
        var directory = Sign("topic", 1, root); var view = VersionView.Open(directory, keys, device);
        await index.LocalFiles.UpsertAsync([new("topic", 1, 0, view.MtimeTicks, 0, false, view.ChangedTicks, null, view.Vector, directory, null)], Token);
        var builder = new BlockReferences(Root("source"), index, keys, device) { DirectoryGraphWorkLimit = 1 };
        for (int pass = 0; pass < 2; pass++)
        {
            var pending = await builder.RefreshAsync(Token); pending.WaitingForDirectorySnapshots.ShouldBeTrue(); pending.AllKnownSourcesCovered.ShouldBeFalse();
            index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(initial.SnapshotId!.Value);
        }
        var completed = await builder.RefreshAsync(Token); completed.AllKnownSourcesCovered.ShouldBeTrue(); completed.References.ShouldBe(5);
        index.ReadBlockReferences().All(row => row.Kinds == BlockReferenceKind.Current).ShouldBeTrue();
        (await builder.RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
    }

    [Fact]
    public async Task PendingDirectorySourceRetainsMetadataAndDescendantChunksAfterRemoteReplacement()
    {
        await using var index = await Bind("source"); using var blocks = Store("source", index);
        var root = await blocks.CaptureDirectoryAsync("topic", [ManyChunks(1025)], Token);
        var directory = Sign("topic", 1, root).WithLocalSequence(1); var view = VersionView.Open(directory, keys, device);
        var source = new CasePreservationSource(CaseConflictPolicy.SourceId(view), device, view.Path, directory);
        await index.ApplyRemoteIndexAsync(device, 0, 1, [new(device, "TOPIC", view.Vector, directory)],
            snapshot => new(snapshot.PathKey, device, 0) { CasePreservations = [source] }, Token);
        var replacement = Sign("topic", 1).WithLocalSequence(2); var next = VersionView.Open(replacement, keys, device);
        await index.ApplyRemoteIndexAsync(device, 1, 2, [new(device, "TOPIC", next.Vector, replacement)], _ => null, Token);
        index.Needed.Count().ShouldBe(0);
        var result = await new BlockReferences(Root("source"), index, keys, device).RefreshAsync(Token);
        result.AllKnownSourcesCovered.ShouldBeTrue(); result.References.ShouldBe(1028);
        index.ReadBlockReferences().All(row => row.Kinds == BlockReferenceKind.Needed).ShouldBeTrue();
        index.ReadPendingCasePreservations().Count.ShouldBe(1);
    }

    [Fact]
    public async Task BudgetAndRestartResumeNestedAuthenticatedMembersWithCompleteReferenceCoverage()
    {
        SignedRecord directory; byte[] id;
        await using (var index = await Bind("source"))
        {
            using var blocks = Store("source", index);
            var nested = await blocks.CaptureDirectoryAsync("topic/nested", [Sign("topic/nested/file", 0)], Token);
            var root = await blocks.CaptureDirectoryAsync("topic", [Sign("topic/a", 0), Sign("topic/nested", 1, nested)], Token);
            directory = Sign("topic", 1, root);
            var progress = await Advance("source", index, directory, NoFetch, 1); progress.Complete.ShouldBeFalse(); progress.Processed.ShouldBe(1); id = progress.Id;
            Should.Throw<InvalidOperationException>(() => index.ReadDirectoryGraphReferences(id).ToArray());
        }
        await using var reopened = await FolderIndex.OpenAsync(Database("source"), Token);
        var first = await Advance("source", reopened, directory, NoFetch, 2); first.Complete.ShouldBeFalse(); first.Processed.ShouldBe(2);
        var last = await Advance("source", reopened, directory, NoFetch, 2); last.Complete.ShouldBeTrue(); last.Processed.ShouldBe(2);
        reopened.ReadDirectoryGraphMembers(id).Select(row => row.PathKey).ShouldBe(["TOPIC/A", "TOPIC/NESTED", "TOPIC/NESTED/FILE"]);
        reopened.ReadDirectoryGraphReferences(id).Count().ShouldBe(8);
        var cached = await Advance("source", reopened, directory.WithLocalSequence(99), NoFetch, 1);
        cached.Complete.ShouldBeTrue(); cached.Processed.ShouldBe(0); cached.Id.ShouldBe(id);
    }

    [Fact]
    public async Task ReceiverRetainsProgressAcrossMissingMetadataThenFetchesAndAccountsTheGraph()
    {
        await using var source = await Bind("source"); await using var receiver = await Bind("receiver");
        using var blocks = Store("source", source);
        var root = await blocks.CaptureDirectoryAsync("topic", [Sign("topic/file", 0)], Token); var directory = Sign("topic", 1, root);
        using var cache = new PublishedBlocks(Root("source"), source); int requests = 0;
        ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> hash, int length, CancellationToken token)
        { requests++; return ValueTask.FromResult<ReadOnlyMemory<byte>>(cache.TryRead(hash.Span, length, token)!); }
        var progress = await Advance("receiver", receiver, directory, Fetch, 1); progress.Complete.ShouldBeFalse(); requests.ShouldBe(1);
        long pending = receiver.ReadDirectoryGraphWork(progress.Id)!.Id;
        await Should.ThrowAsync<IOException>(() => Advance("receiver", receiver, directory, NoFetch, 1));
        receiver.ReadDirectoryGraphWork(progress.Id)!.Id.ShouldBe(pending);
        var completed = await Advance("receiver", receiver, directory, Fetch, 1); completed.Complete.ShouldBeTrue(); requests.ShouldBe(3);
        receiver.ReadBlockObjects().Count().ShouldBe(3); receiver.ReadDirectoryGraphReferences(progress.Id).Count().ShouldBe(3);
    }

    [Fact]
    public async Task DifferentlySignedCaseVariantsForOneChildKeyCannotCompleteAValidGraph()
    {
        await using var index = await Bind("source"); using var blocks = Store("source", index);
        var a = await blocks.CaptureMemberAsync("topic", Sign("topic/file", 0), Token);
        var b = await blocks.CaptureMemberAsync("topic", Sign("topic/FILE", 0), Token);
        var root = await blocks.PutAsync(DirectorySnapshotNode.Entries([a, b]), Token); await blocks.FlushAsync(Token);
        var directory = Sign("topic", 1, root); byte[] id = CaseConflictPolicy.SourceId(VersionView.Open(directory, keys, device));
        await Should.ThrowAsync<InvalidDataException>(() => Advance("source", index, directory, NoFetch, 1000));
        index.ReadDirectoryGraph(id)!.Complete.ShouldBeFalse(); index.ReadDirectoryGraphMembers(id).Count().ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => index.ReadDirectoryGraphReferences(id).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicatePageStructureOrExcessiveNestingCannotBecomeCoverage(bool deep)
    {
        await using var index = await Bind("source"); using var blocks = Store("source", index);
        var empty = await blocks.PutAsync(DirectorySnapshotNode.Entries([]), Token); DirectorySnapshotLink root;
        if (deep)
        {
            root = empty;
            for (int depth = 0; depth < 10; depth++) root = await blocks.PutAsync(DirectorySnapshotNode.Branch([root]), Token);
        }
        else
        {
            var member = await blocks.CaptureMemberAsync("topic", Sign("topic/file", 0), Token);
            var page = await blocks.PutAsync(DirectorySnapshotNode.Entries([member]), Token);
            var a = await blocks.PutAsync(DirectorySnapshotNode.Branch([empty]), Token);
            var b = await blocks.PutAsync(DirectorySnapshotNode.Branch([empty, page]), Token);
            root = await blocks.PutAsync(DirectorySnapshotNode.Branch([a, b]), Token);
        }
        await blocks.FlushAsync(Token); var directory = Sign("topic", 1, root);
        await Should.ThrowAsync<InvalidDataException>(() => Advance("source", index, directory, NoFetch, 1000));
        byte[] id = CaseConflictPolicy.SourceId(VersionView.Open(directory, keys, device)); index.ReadDirectoryGraph(id)!.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task RootWithValidSignedHashButWrongNodeKindIsRejected()
    {
        await using var index = await Bind("source"); using var blocks = Store("source", index);
        var root = await blocks.PutAsync(DirectorySnapshotNode.Blob(new byte[36]), Token); await blocks.FlushAsync(Token);
        await Should.ThrowAsync<InvalidDataException>(() => Advance("source", index, Sign("topic", 1, root), NoFetch, 1000));
    }

    [Theory]
    [InlineData("references")]
    [InlineData("before-progress")]
    public async Task CancelledLargeChunkPrefixResumesWithoutLosingOrDuplicatingAnyReference(string stopAt)
    {
        SignedRecord directory; byte[] id;
        await using (var index = await Bind("source"))
        {
            using var blocks = Store("source", index);
            var root = await blocks.CaptureDirectoryAsync("topic", [ManyChunks(1025)], Token); directory = Sign("topic", 1, root);
            var first = await Advance("source", index, directory, NoFetch, 1); id = first.Id; long pending = index.ReadDirectoryGraphWork(id)!.Id;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            await Should.ThrowAsync<OperationCanceledException>(() => Advance("source", index, directory, NoFetch, 1,
                stage => { if (stage == stopAt) stop.Cancel(); }, stop.Token));
            index.ReadDirectoryGraphWork(id)!.Id.ShouldBe(pending); index.ReadDirectoryGraph(id)!.Complete.ShouldBeFalse();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database("source"), Token);
        (await Advance("source", reopened, directory, NoFetch, 1)).Complete.ShouldBeTrue();
        reopened.ReadDirectoryGraphReferences(id).Count().ShouldBe(1028);
        reopened.ReadDirectoryGraphMembers(id).Single().PathKey.ShouldBe("TOPIC/LARGE");
    }

    private async Task<DirectoryGraphAdvance> Advance(string name, FolderIndex index, SignedRecord directory,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, int budget,
        Action<string>? checkpoint = null, CancellationToken? token = null)
    {
        var cancellation = token ?? Token; using var gate = await FolderOperationGate.AcquireAsync(index, cancellation);
        return await new DirectorySnapshotGraph(Root(name), index, keys, device) { Checkpoint = checkpoint }.AdvanceUnderGateAsync(directory, fetch, budget, cancellation);
    }
    private SignedRecord Sign(string path, uint type, DirectorySnapshotLink? root = null)
    {
        var body = Body(path, type);
        if (root is not null) { body.DirectorySnapshotHash = ByteString.CopyFrom(root.Hash); body.DirectorySnapshotLength = (uint)root.EncodedLength; }
        return SignedRecordFactory.Create(body, keys);
    }
    private SignedRecord ManyChunks(int count)
    {
        var body = Body("topic/large", 0); byte[] bytes = new byte[count * 4];
        for (int value = 0; value < count; value++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(value * 4), value);
            body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash(bytes.AsSpan(value * 4, 4))), Length = 4 });
        }
        body.Size = (ulong)bytes.Length; body.FileHash = ByteString.CopyFrom(Hash(bytes)); return SignedRecordFactory.Create(body, keys);
    }
    private FileRecordBody Body(string path, uint type)
    {
        var body = new FileRecordBody { Path = path, Type = type, MtimeTicks = 638000000000000000, ChangedTicks = 638000000000000000,
            ModifiedBy = ByteString.CopyFrom(device) };
        body.Vv.Add(new VvEntry { Device = 1, Counter = 1 }); if (type == 0) body.FileHash = ByteString.CopyFrom(Hash([])); return body;
    }
    private DirectorySnapshotBlocks Store(string name, FolderIndex index) => new(Root(name), index, keys, device);
    private void Age()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database("source"), Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE directory_graphs SET last_used_ticks=1;"; command.ExecuteNonQuery();
    }
    private async Task<FolderIndex> Bind(string name)
    {
        Directory.CreateDirectory(Root(name)); var index = await FolderIndex.OpenAsync(Database(name), Token);
        try { using var scanner = new Scanner(Root(name), index, keys, device); (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty(); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private string Root(string name) => Path.Combine(fixture, name);
    private string Database(string name) => Path.Combine(fixture, name + ".db");
    private static byte[] Hash(ReadOnlySpan<byte> bytes) => Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    private static ValueTask<ReadOnlyMemory<byte>> NoFetch(ReadOnlyMemory<byte> hash, int length, CancellationToken token) => throw new IOException("Metadata unavailable.");
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-DirectoryGraph-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
