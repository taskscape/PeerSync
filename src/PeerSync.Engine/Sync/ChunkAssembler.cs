using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Native;
using PeerSync.Engine.Hashing;
using PeerSync.Net;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

internal readonly record struct AssemblyResult(long TemporaryBytes, long LocalBytes, long RemoteBytes, int Chunks);

/// <summary>Assembles only a caller-owned temporary stream. Never installs a target or commits its index.
/// The host owns the tree/index/journal and serializes use of the destination.</summary>
internal sealed class ChunkAssembler(SafeFileTree tree, FolderIndex index, DiagnosticJournal? journal = null)
{
    private const int MaximumAssemblyWindowBytes = TransferScheduler.MaximumWindowBytes;

    public Task<AssemblyResult> AssembleAsync(VersionView version, Stream destination,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        CancellationToken cancellationToken = default)
        => AssembleAsync(version, destination, fetch, fetchBatch: null, cancellationToken);

    internal async Task<AssemblyResult> AssembleAsync(VersionView version, Stream destination,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        Func<IReadOnlyList<PeerBlockRequest>, bool, CancellationToken, ValueTask<IReadOnlyList<PeerBlockResult>>>? fetchBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version); ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(fetch);
        if (version.Deleted || version.Type != 0) throw new ArgumentException("Only live regular files can be assembled.", nameof(version));
        if (!destination.CanRead || !destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("The temporary stream must support reading, writing and seeking.", nameof(destination));
        var body = version.ExportBody();
        Guid session = Guid.NewGuid(); var stopwatch = Stopwatch.StartNew();
        long offset = 0, temporaryBytes = 0, localBytes = 0, remoteBytes = 0;
        int verified = 0;
        using var blocks = new PublishedBlocks(tree);
        try
        {
            using var whole = Blake3.Hasher.New();
            journal?.TryWrite(new("assembly.started", "assembly") { SessionId = session, Bytes = version.Size, ItemsVisited = body.Chunks.Count });
            int chunkIndex = 0;
            while (chunkIndex < body.Chunks.Count)
            {
                var window = new List<ChunkWork>();
                int windowBytes = 0;
                long nextOffset = offset;
                while (chunkIndex < body.Chunks.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = body.Chunks[chunkIndex++];
                    int length = checked((int)chunk.Length);
                    if (window.Count != 0 && windowBytes + length > MaximumAssemblyWindowBytes) { chunkIndex--; break; }
                    windowBytes += length;
                    long chunkOffset = nextOffset;
                    nextOffset = checked(nextOffset + length);
                    byte[] bytes = new byte[length];
                    string origin = "assembly.temporary_reused";
                    bool reused = destination.Length >= chunkOffset + length;
                    Task<ReadOnlyMemory<byte>>? remote = null;
                    bool remoteRequested = false;
                    if (reused)
                    {
                        destination.Position = chunkOffset;
                        await destination.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                        reused = Matches(bytes, chunk.Hash.Span);
                    }
                    if (reused) temporaryBytes += length;
                    else
                    {
                        bool published = blocks.TryRead(chunk.Hash.Span, bytes, cancellationToken);
                        if (published || TryLocal(chunk.Hash.ToByteArray(), bytes, cancellationToken))
                        {
                            localBytes += length; origin = published ? "assembly.published_reused" : "assembly.local_reused";
                        }
                        else
                        {
                            origin = "assembly.remote_verified";
                            remoteRequested = true;
                            if (fetchBatch is null) remote = FetchSafelyAsync(fetch, chunk.Hash.ToByteArray(), length, cancellationToken);
                        }
                    }
                    window.Add(new(chunk.Hash.ToByteArray(), length, bytes, chunkOffset, origin, reused, remoteRequested, remote));
                }

                await Task.WhenAll(window.Where(item => item.Remote is not null).Select(item => ObserveFetchAsync(item))).ConfigureAwait(false);
                if (fetchBatch is not null)
                {
                    ChunkWork[][] batches = window.Where(item => item.RemoteRequested).Chunk(64).ToArray();
                    for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
                    {
                        ChunkWork[] batch = batches[batchIndex];
                        try
                        {
                            PeerBlockRequest[] requests = batch.Select(item => new PeerBlockRequest(item.Hash, item.Length)).ToArray();
                            bool endgame = chunkIndex == body.Chunks.Count && batchIndex == batches.Length - 1;
                            IReadOnlyList<PeerBlockResult> received = await fetchBatch(requests, endgame, cancellationToken).ConfigureAwait(false);
                            if (received.Count != batch.Length) throw new InvalidDataException("Remote peer returned the wrong block batch size.");
                            for (int i = 0; i < batch.Length; i++)
                            {
                                if (received[i].ErrorCode is { } errorCode) batch[i].Failure = new PeerBlockException(errorCode);
                                else batch[i].Received = received[i].Data;
                            }
                        }
                        catch (Exception error)
                        {
                            foreach (ChunkWork item in batch) item.Failure = error;
                        }
                    }
                }
                Exception? failure = null;
                foreach (ChunkWork item in window)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.RemoteRequested)
                    {
                        if (item.Failure is not null) { failure ??= item.Failure; continue; }
                        ReadOnlyMemory<byte> received = item.Received;
                        if (received.Length != item.Length)
                        { failure ??= new InvalidDataException("Remote chunk length does not match its signed record."); continue; }
                        received.CopyTo(item.Bytes);
                        if (!Matches(item.Bytes, item.Hash))
                        { failure ??= new CryptographicException("Remote chunk hash verification failed."); continue; }
                        remoteBytes += item.Length;
                    }
                    if (!item.Reused)
                    {
                        destination.Position = item.Offset;
                        await destination.WriteAsync(item.Bytes, cancellationToken).ConfigureAwait(false);
                    }
                    whole.Update(item.Bytes);
                    blocks.Put(item.Bytes, item.Hash, cancellationToken);
                    offset = checked(offset + item.Length); verified++;
                    journal?.TryWrite(new(item.Origin, "assembly") { SessionId = session, Level = DiagnosticLevel.Debug,
                        Bytes = offset, ItemsVisited = verified, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds });
                }
                if (failure is not null) throw failure;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var digest = whole.Finalize();
            if (offset != version.Size || !CryptographicOperations.FixedTimeEquals(digest.AsSpan(), version.FileHash))
                throw new CryptographicException("Whole-file hash verification failed.");
            destination.SetLength(offset);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            journal?.TryWrite(new("assembly.complete", "assembly") { SessionId = session, Bytes = offset, ItemsHashed = verified,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds });
            return new(temporaryBytes, localBytes, remoteBytes, verified);
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("assembly.failed", "assembly") { SessionId = session, Bytes = offset, ItemsHashed = verified,
                Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds });
            throw;
        }
        finally
        {
            await blocks.FlushAsync(index, CancellationToken.None, journal).ConfigureAwait(false);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> FetchSafelyAsync(
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        ReadOnlyMemory<byte> hash, int length, CancellationToken token)
        => await fetch(hash, length, token).ConfigureAwait(false);

    private static async Task ObserveFetchAsync(ChunkWork item)
    {
        try { item.Received = await item.Remote!.ConfigureAwait(false); }
        catch (Exception error) { item.Failure = error; }
    }

    private sealed class ChunkWork(byte[] hash, int length, byte[] bytes, long offset, string origin, bool reused, bool remoteRequested,
        Task<ReadOnlyMemory<byte>>? remote)
    {
        internal byte[] Hash { get; } = hash;
        internal int Length { get; } = length;
        internal byte[] Bytes { get; } = bytes;
        internal long Offset { get; } = offset;
        internal string Origin { get; } = origin;
        internal bool Reused { get; } = reused;
        internal bool RemoteRequested { get; } = remoteRequested;
        internal Task<ReadOnlyMemory<byte>>? Remote { get; } = remote;
        internal ReadOnlyMemory<byte> Received { get; set; }
        internal Exception? Failure { get; set; }
    }

    private bool TryLocal(byte[] hash, Memory<byte> bytes, CancellationToken cancellationToken)
    {
        // A stale chunk index is only a hint. Bound candidate work, prohibit link traversal,
        // and verify actual bytes; an edit or rename cannot make unverified data authoritative.
        foreach (var candidate in index.FindChunks(hash).Take(64))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Length != bytes.Length) continue;
            int slash = candidate.PathKey.LastIndexOf('/');
            try
            {
                using var parent = tree.Directory(slash < 0 ? "" : candidate.PathKey[..slash]);
                using var file = parent.OpenFile(candidate.PathKey[(slash + 1)..]);
                if (candidate.Offset > RandomAccess.GetLength(file) - bytes.Length) continue;
                int read = 0;
                while (read < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = RandomAccess.Read(file, bytes.Span.Slice(read, Math.Min(65536, bytes.Length - read)), candidate.Offset + read);
                    if (count == 0) break;
                    read += count;
                }
                if (read == bytes.Length && Matches(bytes.Span, hash)) return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
        }
        return false;
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> expected)
    {
        var digest = Blake3.Hasher.Hash(bytes);
        return CryptographicOperations.FixedTimeEquals(digest.AsSpan(), expected);
    }
}
