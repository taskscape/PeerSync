using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Crypto.Chunking;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Net;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Protocol.Wire;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class ChunkAssemblerTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Assemble-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private string Root => Path.Combine(fixture, "tree");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ChunkAssemblerTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task VerifiedTemporaryAndLocalChunksPrecedeNetworkAndTrailingTemporaryBytesAreRemoved()
    {
        byte[][] chunks = ["temporary chunk"u8.ToArray(), "local chunk"u8.ToArray(), "remote chunk"u8.ToArray()];
        var version = Version(chunks);
        await using var index = await OpenBoundIndexAsync();
        File.WriteAllBytes(Path.Combine(Root, "source"), [0, 0, 0, .. chunks[1]]);
        await index.Chunks.UpsertAsync([new(Hash(chunks[1]), "source", 3, chunks[1].Length)], Token);
        using var tree = new SafeFileTree(Root);
        using var temporary = new MemoryStream();
        temporary.Write(chunks[0]); temporary.Write(new byte[100]);
        int calls = 0;
        var result = await new ChunkAssembler(tree, index).AssembleAsync(version, temporary, (hash, length, _) =>
        {
            calls++; hash.ToArray().ShouldBe(Hash(chunks[2])); length.ShouldBe(chunks[2].Length);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(chunks[2]);
        }, Token);
        calls.ShouldBe(1); result.ShouldBe(new(chunks[0].Length, chunks[1].Length, chunks[2].Length, 3));
        temporary.ToArray().ShouldBe(chunks.SelectMany(c => c).ToArray());
        index.LocalFiles.Count().ShouldBe(0); // Assembly never publishes or installs anything.
    }

    [Fact]
    public async Task CorruptAndMissingIndexedCandidatesAreHintsAndAnIntactLaterCandidateAvoidsNetwork()
    {
        byte[] bytes = "a valid local chunk"u8.ToArray();
        await using var index = await OpenBoundIndexAsync();
        File.WriteAllBytes(Path.Combine(Root, "a-corrupt"), new byte[bytes.Length]);
        File.WriteAllBytes(Path.Combine(Root, "c-valid"), bytes);
        await index.Chunks.UpsertAsync([new(Hash(bytes), "a-corrupt", 0, bytes.Length),
            new(Hash(bytes), "b-missing", 0, bytes.Length), new(Hash(bytes), "c-valid", 0, bytes.Length)], Token);
        using var tree = new SafeFileTree(Root); using var temporary = new MemoryStream();
        var result = await new ChunkAssembler(tree, index).AssembleAsync(Version([bytes]), temporary,
            (_, _, _) => throw new InvalidOperationException("Network must not be needed."), Token);
        result.LocalBytes.ShouldBe(bytes.Length); result.RemoteBytes.ShouldBe(0);
        temporary.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task RemoteChunksArePrefetchedConcurrentlyWithinTheAssemblyWindow()
    {
        byte[][] chunks = [Enumerable.Repeat((byte)1, 64 * 1024).ToArray(),
            Enumerable.Repeat((byte)2, 64 * 1024).ToArray(), Enumerable.Repeat((byte)3, 64 * 1024).ToArray()];
        await using var index = await OpenBoundIndexAsync();
        using var tree = new SafeFileTree(Root); using var temporary = new MemoryStream();
        int active = 0, maximumActive = 0;
        var result = await new ChunkAssembler(tree, index).AssembleAsync(Version(chunks), temporary, async (hash, length, token) =>
        {
            int count = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, count);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40), token);
                return chunks.Single(chunk => chunk.Length == length && Hash(chunk).SequenceEqual(hash.ToArray()));
            }
            finally { Interlocked.Decrement(ref active); }
        }, Token);

        maximumActive.ShouldBeGreaterThan(1);
        result.RemoteBytes.ShouldBe(chunks.Sum(chunk => (long)chunk.Length));
        temporary.ToArray().ShouldBe(chunks.SelectMany(chunk => chunk).ToArray());
    }

    [Fact]
    public async Task RemoteChunksUseBatchesBoundedToSixtyFourRequests()
    {
        byte[][] chunks = Enumerable.Range(0, 65).Select(index => Enumerable.Repeat((byte)(index + 1), 64).ToArray()).ToArray();
        await using var index = await OpenBoundIndexAsync();
        using var tree = new SafeFileTree(Root); using var temporary = new MemoryStream();
        var batchSizes = new List<int>();
        var endgameFlags = new List<bool>();
        var result = await new ChunkAssembler(tree, index).AssembleAsync(Version(chunks), temporary,
            (_, _, _) => throw new InvalidOperationException("Batch callback must serve remote blocks."),
            (requests, endgame, _) =>
            {
                batchSizes.Add(requests.Count);
                endgameFlags.Add(endgame);
                return ValueTask.FromResult<IReadOnlyList<PeerBlockResult>>(requests.Select(request =>
                    new PeerBlockResult(chunks.Single(chunk => chunk.Length == request.Length
                        && Hash(chunk).SequenceEqual(request.ChunkHash.ToArray())), null)).ToArray());
            }, Token);

        batchSizes.ShouldBe([64, 1]);
        endgameFlags.ShouldBe([false, true]);
        result.RemoteBytes.ShouldBe(chunks.Sum(chunk => (long)chunk.Length));
        temporary.ToArray().ShouldBe(chunks.SelectMany(chunk => chunk).ToArray());
    }

    [Fact]
    public async Task SuccessfulBlocksInAPartialBatchAreRetainedWhenAnotherBlockIsUnavailable()
    {
        byte[][] chunks = ["available"u8.ToArray(), "missing"u8.ToArray()];
        await using var index = await OpenBoundIndexAsync();
        using var tree = new SafeFileTree(Root); using var temporary = new MemoryStream();
        Exception? error = await Record.ExceptionAsync(() => new ChunkAssembler(tree, index).AssembleAsync(Version(chunks), temporary,
            (_, _, _) => throw new InvalidOperationException("Batch callback must serve remote blocks."),
            (requests, _, _) => ValueTask.FromResult<IReadOnlyList<PeerBlockResult>>(
            [new(chunks[0], null), new(ReadOnlyMemory<byte>.Empty, BlockError.NotFound)]), Token));

        error.ShouldBeOfType<PeerBlockException>().ErrorCode.ShouldBe(BlockError.NotFound);
        temporary.ToArray().ShouldBe(chunks[0]);
    }

    [Fact]
    public async Task ReparseCandidateIsRejectedAndCorruptTemporaryDataIsReplacedFromNetwork()
    {
        byte[] bytes = "private fixture content"u8.ToArray();
        string outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "source"), bytes);
        Directory.CreateSymbolicLink(Path.Combine(Root, "link"), outside);
        await using var index = await OpenBoundIndexAsync();
        await index.Chunks.UpsertAsync([new(Hash(bytes), "link/source", 0, bytes.Length)], Token);
        using var tree = new SafeFileTree(Root); using var temporary = new MemoryStream();
        temporary.Write(new byte[bytes.Length]);
        int calls = 0;
        var result = await new ChunkAssembler(tree, index).AssembleAsync(Version([bytes]), temporary, (_, _, _) =>
        {
            calls++; return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);
        }, Token);
        calls.ShouldBe(1); result.LocalBytes.ShouldBe(0); result.TemporaryBytes.ShouldBe(0);
        temporary.ToArray().ShouldBe(bytes); File.ReadAllBytes(Path.Combine(outside, "source")).ShouldBe(bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteLengthOrHashFailureCannotProduceASuccessfulAssembly(bool wrongLength)
    {
        byte[] bytes = "verified bytes"u8.ToArray();
        await using var index = await OpenBoundIndexAsync();
        using var tree = new SafeFileTree(Root); using var temporary = new MemoryStream();
        byte[] corrupt = new byte[bytes.Length + (wrongLength ? 1 : 0)];
        var error = await Record.ExceptionAsync(() => new ChunkAssembler(tree, index).AssembleAsync(Version([bytes]), temporary,
            (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(corrupt), Token));
        if (wrongLength) error.ShouldBeOfType<InvalidDataException>(); else error.ShouldBeOfType<CryptographicException>();
        temporary.Length.ShouldBe(0);
    }

    [Fact]
    public async Task WholeFileHashIsIndependentAndDiagnosticsContainOnlyCountsAndCodes()
    {
        byte[] bytes = "secret filename and payload"u8.ToArray();
        await using var index = await OpenBoundIndexAsync();
        using var tree = new SafeFileTree(Root); using var temporary = new MemoryStream();
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "assembly", debugEnabled: true);
        var assembler = new ChunkAssembler(tree, index, journal);
        await Should.ThrowAsync<CryptographicException>(() => assembler.AssembleAsync(Version([bytes], wrongWholeHash: true), temporary,
            (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes), Token));
        // The verified chunk can be reused, but the independent whole-file check still gates success.
        var result = await assembler.AssembleAsync(Version([bytes]), temporary, (_, _, _) => throw new InvalidOperationException(), Token);
        result.TemporaryBytes.ShouldBe(bytes.Length);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("assembly.remote_verified"); log.ShouldContain("assembly.temporary_reused");
        log.ShouldContain("assembly.failed"); log.ShouldContain("assembly.complete");
        log.ShouldNotContain("private-target"); log.ShouldNotContain("secret filename"); log.ShouldNotContain(Root);
        log.ShouldNotContain(keys.ExportReadWriteText()); log.ShouldNotContain(Convert.ToHexString(Hash(bytes)));
    }

    [Fact]
    public async Task CancellationAfterAnUncooperativeFetchStopsBeforeWritingTheChunk()
    {
        byte[] bytes = "cancelled chunk"u8.ToArray();
        await using var index = await OpenBoundIndexAsync();
        using var tree = new SafeFileTree(Root); using var temporary = new MemoryStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Should.ThrowAsync<OperationCanceledException>(() => new ChunkAssembler(tree, index).AssembleAsync(Version([bytes]), temporary,
            (_, _, _) => { cancellation.Cancel(); return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes); }, cancellation.Token));
        temporary.Length.ShouldBe(0);
    }

    [Fact]
    public async Task EmptyFileVerifiesItsHashAndDoesNotFetchOrRetainOldTemporaryData()
    {
        await using var index = await OpenBoundIndexAsync();
        using var tree = new SafeFileTree(Root); using var temporary = new MemoryStream(); temporary.WriteByte(42);
        var result = await new ChunkAssembler(tree, index).AssembleAsync(Version([]), temporary,
            (_, _, _) => throw new InvalidOperationException(), Token);
        result.ShouldBe(new(0, 0, 0, 0)); temporary.Length.ShouldBe(0);
    }

    private async Task<FolderIndex> OpenBoundIndexAsync()
    {
        var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        try
        {
            using var tree = new SafeFileTree(Root); await index.Meta.UpsertAsync([new("_root-identity", tree.Identity.ToArray())], Token); return index;
        }
        catch { await index.DisposeAsync(); throw; }
    }

    private VersionView Version(byte[][] chunks, bool wrongWholeHash = false)
    {
        byte[] device = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(device, 1);
        byte[] all = chunks.SelectMany(c => c).ToArray();
        var body = new FileRecordBody { Path = "private-target", Size = (ulong)all.Length, MtimeTicks = DateTime.UtcNow.Ticks,
            ChangedTicks = DateTime.UtcNow.Ticks, ModifiedBy = ByteString.CopyFrom(device),
            ChunkClass = ChunkParameters.ClassFor(all.Length), FileHash = ByteString.CopyFrom(wrongWholeHash ? new byte[32] : Hash(all)) };
        body.Vv.Add(new VvEntry { Device = 1, Counter = 1 });
        foreach (byte[] chunk in chunks) body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash(chunk)), Length = (uint)chunk.Length });
        return VersionView.Open(SignedRecordFactory.Create(body, keys, 1), keys, device);
    }
    private static byte[] Hash(byte[] bytes) { var hash = Blake3.Hasher.Hash(bytes); return hash.AsSpan().ToArray(); }
    private static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        while (value > (observed = Volatile.Read(ref maximum))
            && Interlocked.CompareExchange(ref maximum, value, observed) != observed) { }
    }
    public void Dispose() { keys.Dispose(); Directory.Delete(fixture, recursive: true); }
}
