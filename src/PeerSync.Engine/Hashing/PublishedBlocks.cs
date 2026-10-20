using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using PeerSync.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace PeerSync.Engine.Hashing;

/// <summary>Independent verified bytes for published versions, never hardlinks
/// to user files. The owner must finish all operations before disposal.
/// Retention/garbage collection is separate; this component never evicts blocks.</summary>
public sealed partial class PublishedBlocks : IDisposable
{
    public const int MaximumBlockBytes = 8 * 1024 * 1024;
    private readonly SafeFileTree tree;
    private readonly bool ownsTree;
    private readonly SemaphoreSlim[] stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private bool disposed;
    private readonly ConcurrentDictionary<string, BlockObjectRow> captured = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, BlockCaptureProof> proofs = new();
    internal Action<string>? Checkpoint { get; set; }

    public PublishedBlocks(string rootPath, FolderIndex index)
    {
        ArgumentNullException.ThrowIfNull(index); tree = new(rootPath); ownsTree = true;
        if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } identity || !identity.AsSpan().SequenceEqual(tree.Identity))
        { tree.Dispose(); throw new IOException("Bind the sync root before reading published content."); }
    }

    internal PublishedBlocks(NativeRoot borrowedRoot) => tree = new(borrowedRoot);
    internal PublishedBlocks(SafeFileTree borrowedTree) => tree = borrowedTree;

    /// <summary>Returns independent verified bytes, or null when missing. Corrupt,
    /// inaccessible or reparse entries fail visibly. Call from a bounded I/O worker.</summary>
    public byte[]? TryRead(ReadOnlySpan<byte> hash, int length, CancellationToken cancellationToken = default)
    {
        Validate(hash, length); byte[] bytes = new byte[length];
        return TryRead(hash, bytes.AsSpan(), cancellationToken) ? bytes : null;
    }

    internal bool TryRead(ReadOnlySpan<byte> hash, Span<byte> bytes, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this); Validate(hash, bytes.Length); token.ThrowIfCancellationRequested();
        string name = Convert.ToHexString(hash);
        try
        {
            using var parent = tree.Directory(".psp/blocks/" + name[..2], privatePath: true);
            using var file = parent.OpenReadGuard(name);
            RequireIndependent(file);
            if (RandomAccess.GetLength(file) != bytes.Length) throw new CryptographicException("Published block length changed.");
            Read(file, bytes, 0, token);
            if (!Matches(bytes, hash)) throw new CryptographicException("Published block verification failed.");
            return true;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return false; }
    }

    // The hasher/assembler owns these buffers exclusively until this returns.
    internal void Put(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> hash, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this); Validate(hash, bytes.Length); token.ThrowIfCancellationRequested();
        if (!Matches(bytes, hash)) throw new CryptographicException("Captured block verification failed.");
        var stripe = stripes[hash[0] % stripes.Length]; stripe.Wait(token);
        try
        {
            string name = Convert.ToHexString(hash);
            using var parent = tree.Directory(".psp/blocks/" + name[..2], create: true, privatePath: true);
            if (VerifyExisting(parent, name, hash, bytes.Length, token, out byte[] existingId))
            {
                Track(hash, bytes.Length, existingId, DateTime.UtcNow.Ticks); return;
            }
            Guid id = Guid.NewGuid(); string staging = id.ToString("N") + ".!psp";
            using var file = parent.CreateFile(staging, temporary: true); bool installed = false;
            BlockCaptureProof? proof = null;
            try
            {
                using (var proofDirectory = tree.Directory(BlockCaptureProof.Directory, create: true, privatePath: true))
                using (var marker = proofDirectory.CreateFile(id.ToString("N") + ".capture"))
                {
                    proof = new(id, tree.Identity.ToArray(), BlockCaptureProof.Identity(marker), BlockCaptureProof.Identity(file), hash.ToArray(), bytes.Length, DateTime.UtcNow.Ticks);
                    RandomAccess.Write(marker, proof.Encode(), 0); RandomAccess.FlushToDisk(marker);
                }
                Checkpoint?.Invoke("proof");
                Checkpoint?.Invoke("staged");
                for (int offset = 0; offset < bytes.Length; offset += 65536)
                {
                    token.ThrowIfCancellationRequested();
                    RandomAccess.Write(file, bytes.Slice(offset, Math.Min(65536, bytes.Length - offset)), offset);
                }
                RandomAccess.FlushToDisk(file); Checkpoint?.Invoke("flushed"); token.ThrowIfCancellationRequested();
                try
                {
                    parent.MoveHere(file, name); installed = true; RandomAccess.FlushToDisk(file);
                    proofs[proof.Id] = proof; Track(proof.Hash, proof.Length, proof.DataId, proof.CapturedTicks); Checkpoint?.Invoke("installed");
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 80 or 183)
                {
                    if (!VerifyExisting(parent, name, hash, bytes.Length, token, out existingId)) throw new IOException("Concurrent block installation disappeared.");
                    Track(hash, bytes.Length, existingId, DateTime.UtcNow.Ticks);
                    // Flush must catalog the winner before retiring this loser's
                    // proof, even though finally removes its owned temporary.
                    proofs[proof.Id] = proof;
                }
            }
            finally
            {
                // Only the exclusively created object is eligible for cleanup.
                // A crash may leave an orphan for the separate retention worker.
                if (!installed) GuardedFile.DeleteOnClose(file);
            }
        }
        finally { stripe.Release(); }
    }

    private void Track(ReadOnlySpan<byte> hash, int length, byte[] id, long ticks)
    {
        string key = Convert.ToHexString(hash); var row = new BlockObjectRow(hash.ToArray(), length, tree.Identity.ToArray(), id, ticks, Math.Max(ticks, DateTime.UtcNow.Ticks));
        captured.AddOrUpdate(key, row, (_, old) => old.FileId.AsSpan().SequenceEqual(id)
            ? row with { CapturedTicks = Math.Min(old.CapturedTicks, ticks), LastUsedTicks = Math.Max(old.LastUsedTicks, row.LastUsedTicks) } : row);
    }

    /// <summary>Finish capture workers first. Publication must wait for this FULL
    /// registration; cleanup failures retain recoverable proof files.</summary>
    internal async Task<int> FlushAsync(FolderIndex index, CancellationToken token, DiagnosticJournal? journal = null)
    {
        int registered = 0;
        try
        {
            foreach (var batch in captured.Values.Chunk(1000))
            {
                registered += await index.RegisterBlockObjectsAsync(batch, token).ConfigureAwait(false);
                foreach (var row in batch) captured.TryRemove(Convert.ToHexString(row.Hash), out _);
            }
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("block.catalog_failed", "block-capture") { Level = DiagnosticLevel.Error,
                ErrorCode = error is OperationCanceledException ? "cancelled" : error.GetType().Name, HResult = error.HResult,
                NativeError = error is SqliteException sqlite ? sqlite.SqliteExtendedErrorCode : null,
                ItemsChanged = registered, ItemsDeferred = captured.Count, RetryInMilliseconds = 10000 });
            throw;
        }
        if (registered != 0) journal?.TryWrite(new("block.catalog_committed", "block-capture") { Level = DiagnosticLevel.Debug, ItemsChanged = registered });
        foreach (var entry in proofs.ToArray())
        {
            try
            {
                using var parent = tree.Directory(BlockCaptureProof.Directory, privatePath: true);
                using var marker = parent.OpenFile(entry.Value.Name, mutation: true);
                var actual = ReadProof(marker, entry.Value.Name, tree.Identity);
                if (!actual.Encode().AsSpan().SequenceEqual(entry.Value.Encode())) throw new IOException("Capture proof changed.");
                GuardedFile.DeleteOnClose(marker); proofs.TryRemove(entry.Key, out _);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
            {
                journal?.TryWrite(new("block.proof_cleanup_deferred", "block-capture") { SessionId = entry.Key, Level = DiagnosticLevel.Warning,
                    ErrorCode = error.GetType().Name, HResult = error.HResult, NativeError = error is Win32Exception native ? native.NativeErrorCode : null,
                    RetryInMilliseconds = 10000 });
            }
        }
        return registered;
    }

    private static BlockCaptureProof ReadProof(SafeFileHandle marker, string name, ReadOnlySpan<byte> rootId)
    {
        RequireIndependent(marker);
        if (RandomAccess.GetLength(marker) != BlockCaptureProof.Size) throw new InvalidDataException("Invalid capture proof size.");
        byte[] bytes = new byte[BlockCaptureProof.Size]; Read(marker, bytes, 0, CancellationToken.None);
        return BlockCaptureProof.Decode(bytes, name, rootId, BlockCaptureProof.Identity(marker));
    }

    internal void CaptureFile(SafeFileHandle file, VersionView version, CancellationToken token)
    {
        if (version.Type != 0 || version.Deleted) return;
        var chunks = version.ExportBody().Chunks;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunks.Count == 0 ? 1 : checked((int)chunks.Max(c => c.Length)));
        try
        {
            long offset = 0;
            foreach (var chunk in chunks)
            {
                var bytes = buffer.AsSpan(0, checked((int)chunk.Length)); Read(file, bytes, offset, token);
                Put(bytes, chunk.Hash.Span, token); offset += bytes.Length;
            }
            if (offset != version.Size) throw new InvalidDataException("Published content length mismatch.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static bool VerifyExisting(DirectoryLease parent, string name, ReadOnlySpan<byte> hash, int length, CancellationToken token, out byte[] identity)
    {
        identity = [];
        try
        {
            using var file = parent.OpenReadGuard(name);
            RequireIndependent(file);
            if (RandomAccess.GetLength(file) != length || !GuardedFile.Hash(file, token).AsSpan().SequenceEqual(hash))
                throw new CryptographicException("Existing published block is not verified.");
            identity = BlockCaptureProof.Identity(file); return true;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return false; }
    }

    internal static void RequireIndependent(SafeFileHandle file)
    {
        if (!IsIndependent(file)) throw new IOException("Published blocks must be independent regular files.");
    }
    internal static bool IsIndependent(SafeFileHandle file)
    {
        if (!NativeFileInfo.GetStandard(file, 1, out var info, 24)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return info.NumberOfLinks == 1 && info.DeletePending == 0 && info.Directory == 0;
    }

    private static void Read(SafeFileHandle file, Span<byte> bytes, long offset, CancellationToken token)
    {
        int read = 0;
        while (read < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            int count = RandomAccess.Read(file, bytes.Slice(read, Math.Min(65536, bytes.Length - read)), offset + read);
            if (count == 0) throw new EndOfStreamException("Published block is incomplete."); read += count;
        }
    }
    private static bool Matches(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> hash) =>
        CryptographicOperations.FixedTimeEquals(Blake3.Hasher.Hash(bytes).AsSpan(), hash);
    private static void Validate(ReadOnlySpan<byte> hash, int length)
    {
        if (hash.Length != 32) throw new ArgumentException("Invalid block hash length.", nameof(hash));
        if (length is <= 0 or > MaximumBlockBytes) throw new ArgumentOutOfRangeException(nameof(length));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var stripe in stripes) stripe.Dispose();
        if (ownsTree) tree.Dispose();
    }
}
