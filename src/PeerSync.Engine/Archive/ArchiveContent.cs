using System.ComponentModel;
using System.Security.Cryptography;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Archive;

// Caller holds both maintenance gates. Content protection never authorizes
// deleting the original archive or an owned block.
internal static class ArchiveContent
{
    internal static async Task RecoverAsync(SafeFileTree tree, FolderIndex index, DiagnosticJournal? journal, CancellationToken token)
    {
        using var blocks = new PublishedBlocks(tree);
        foreach (var state in index.ReadPendingArchiveContents())
        {
            if (index.IsArchiveSourceReleased(state.SourceId, tree.Identity.ToArray())) continue;
            token.ThrowIfCancellationRequested(); if (state.NextOffset != state.Size) continue;
            try
            {
                if (!state.RootId.AsSpan().SequenceEqual(tree.Identity) || state.ChunkCount > ArchiveContentSnapshot.MaximumChunks)
                    throw new IOException("Pending archive content belongs to another root.");
                using var whole = Blake3.Hasher.New(); long offset = 0; int count = 0;
                foreach (var chunk in index.ReadPendingArchiveContentChunks(state.Id))
                {
                    if (chunk.Offset != offset || chunk.Length > state.Size - offset) throw new InvalidDataException("Pending archive content is incomplete.");
                    byte[] bytes = blocks.TryRead(chunk.Hash, chunk.Length, token) ?? throw new IOException("Pending archive block is missing.");
                    try { whole.Update(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
                    offset += chunk.Length; count++;
                }
                if (offset != state.Size || count != state.ChunkCount || !whole.Finalize().AsSpan().SequenceEqual(state.FileHash))
                    throw new InvalidDataException("Pending archive content failed verification.");
                await index.ActivateArchiveContentAsync(state.Id, token).ConfigureAwait(false);
                journal?.TryWrite(new("archive.content_recovered", "archive-content") { SessionId = state.Id, Level = DiagnosticLevel.Debug, Bytes = state.Size, ItemsChanged = count });
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or CryptographicException)
            {
                journal?.TryWrite(new("archive.content_recovery_deferred", "archive-content") { SessionId = state.Id, Level = DiagnosticLevel.Warning,
                    ErrorCode = error.GetType().Name, HResult = error.HResult, NativeError = error is Win32Exception native ? native.NativeErrorCode : null,
                    ItemsDeferred = 1, RetryInMilliseconds = 10000 });
            }
        }
    }

    internal static async Task<ArchiveContentSnapshot?> CaptureAsync(SafeFileTree tree, FolderIndex index,
        ArchiveProvenance source, DiagnosticJournal? journal, Action<string>? checkpoint, CancellationToken token)
    {
        Guid session = Guid.NewGuid();
        try
        {
            if (source.RootId is not null && !source.RootId.AsSpan().SequenceEqual(tree.Identity)
                || source.Archive.FileHash is not { Length: 32 } || source.Archive.Size < 0
                || !source.Archive.ArchivePath.StartsWith(".psp/archive/", StringComparison.Ordinal))
                throw new InvalidDataException("Archive source has no verified root/hash framing.");
            token.ThrowIfCancellationRequested();
            using var parent = tree.Directory(ApplyState.Parent(source.Archive.ArchivePath), privatePath: true);
            using var file = parent.OpenReadGuard(ApplyState.Name(source.Archive.ArchivePath));
            PublishedBlocks.RequireIndependent(file);
            if (FileSnapshot.Read(file).Length != source.Archive.Size) throw new IOException("Archive content length changed.");
            using var blocks = new PublishedBlocks(tree); int captured = 0; FileHash? hashed;
            try
            {
                hashed = await FileHasher.HashGuardedAsync(file, (bytes, hash) =>
                {
                    if (++captured > ArchiveContentSnapshot.MaximumChunks) throw new InvalidDataException("Archive content exceeds the manifest limit.");
                    blocks.Put(bytes, hash, token);
                }, token).ConfigureAwait(false);
            }
            finally { await blocks.FlushAsync(index, CancellationToken.None, journal).ConfigureAwait(false); }
            if (hashed is null || hashed.Length != source.Archive.Size || !hashed.WholeHash.AsSpan().SequenceEqual(source.Archive.FileHash))
                throw new IOException("Archive content failed whole-file verification.");
            checkpoint?.Invoke("archive-blocks");
            var state = await index.BeginArchiveContentAsync(source, tree.Identity.ToArray(), BlockCaptureProof.Identity(file), DateTime.UtcNow.Ticks, token).ConfigureAwait(false);
            checkpoint?.Invoke("archive-content-begun");
            foreach (var batch in hashed.Chunks.Chunk(1000))
            {
                await index.AppendArchiveContentChunksAsync(state.Id, batch.Select(c => new LocalChunk(c.Offset, c.Length, c.Hash)).ToArray(), token).ConfigureAwait(false);
                checkpoint?.Invoke("archive-content-batch");
            }
            checkpoint?.Invoke("archive-content-before-activate");
            await index.ActivateArchiveContentAsync(state.Id, token).ConfigureAwait(false); checkpoint?.Invoke("archive-content-activated");
            journal?.TryWrite(new("archive.content_captured", "archive-content") { SessionId = session, Level = DiagnosticLevel.Debug, Bytes = hashed.Length, ItemsChanged = hashed.Chunks.Count });
            return index.FindActiveArchiveContent(source.SourceId) ?? throw new InvalidOperationException("Archive content activation disappeared.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or CryptographicException)
        {
            journal?.TryWrite(new("archive.content_deferred", "archive-content") { SessionId = session, Level = DiagnosticLevel.Warning,
                ErrorCode = error.GetType().Name, HResult = error.HResult, NativeError = error is Win32Exception native ? native.NativeErrorCode : null,
                ItemsDeferred = 1, RetryInMilliseconds = 10000 });
            return null;
        }
    }
}
