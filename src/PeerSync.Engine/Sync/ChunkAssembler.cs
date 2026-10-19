using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

internal readonly record struct AssemblyResult(long TemporaryBytes, long LocalBytes, long RemoteBytes, int Chunks);

/// <summary>Assembles only a caller-owned temporary stream. Never installs a target or commits its index.
/// The host owns the tree/index/journal and serializes use of the destination.</summary>
internal sealed class ChunkAssembler(SafeFileTree tree, FolderIndex index, DiagnosticJournal? journal = null)
{
    public async Task<AssemblyResult> AssembleAsync(VersionView version, Stream destination,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version); ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(fetch);
        if (version.Deleted || version.Type != 0) throw new ArgumentException("Only live regular files can be assembled.", nameof(version));
        if (!destination.CanRead || !destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("The temporary stream must support reading, writing and seeking.", nameof(destination));
        var body = version.ExportBody();
        int capacity = body.Chunks.Count == 0 ? 1 : checked((int)body.Chunks.Max(c => c.Length));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        Guid session = Guid.NewGuid(); var stopwatch = Stopwatch.StartNew();
        long offset = 0, temporaryBytes = 0, localBytes = 0, remoteBytes = 0;
        int verified = 0;
        try
        {
            using var whole = Blake3.Hasher.New();
            journal?.TryWrite(new("assembly.started", "assembly") { SessionId = session, Bytes = version.Size, ItemsVisited = body.Chunks.Count });
            foreach (var chunk in body.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = checked((int)chunk.Length);
                Memory<byte> bytes = buffer.AsMemory(0, length);
                string origin = "assembly.temporary_reused";
                bool resumed = destination.Length >= offset + length;
                if (resumed)
                {
                    destination.Position = offset;
                    await destination.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    resumed = Matches(bytes.Span, chunk.Hash.Span);
                }
                if (resumed) temporaryBytes += length;
                else
                {
                    if (TryLocal(chunk.Hash.ToByteArray(), bytes, cancellationToken))
                    {
                        localBytes += length; origin = "assembly.local_reused";
                    }
                    else
                    {
                        ReadOnlyMemory<byte> received = await fetch(chunk.Hash.ToByteArray(), length, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (received.Length != length) throw new InvalidDataException("Remote chunk length does not match its signed record.");
                        // Own the bytes before verification; a transport buffer may be reused after the callback.
                        received.CopyTo(bytes);
                        if (!Matches(bytes.Span, chunk.Hash.Span)) throw new CryptographicException("Remote chunk hash verification failed.");
                        remoteBytes += length; origin = "assembly.remote_verified";
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Position = offset;
                    await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                whole.Update(bytes.Span);
                offset = checked(offset + length); verified++;
                journal?.TryWrite(new(origin, "assembly") { SessionId = session, Level = DiagnosticLevel.Debug,
                    Bytes = offset, ItemsVisited = verified, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds });
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
                Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds });
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
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
