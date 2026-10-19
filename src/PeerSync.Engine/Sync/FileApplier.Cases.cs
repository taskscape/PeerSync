using System.ComponentModel;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Paths;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public sealed partial class FileApplier
{
    internal async Task<bool> PrepareCaseCopiesAsync(
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, CancellationToken token)
    {
        using var operation = await FolderOperationGate.AcquireAsync(index, token).ConfigureAwait(false);
        using var tree = new SafeFileTree(rootPath); ValidateRoot(tree); bool readyForScan = false;
        if (index.ReadApplyIntents().Any()) return false;
        foreach (var pending in index.ReadPendingCasePreservations())
        {
            token.ThrowIfCancellationRequested(); var source = OpenCaseSource(tree, pending);
            if (source.Type != 0) continue; // Subtree mapping is a separate mandatory integration.
            try
            {
                try { FileMetadata.ValidateMtime(source.MtimeTicks); }
                catch (ArgumentOutOfRangeException error) { StagingProbeFailed("case-timestamp-blocked", Guid.NewGuid(), error); continue; }
                if (pending.InstalledFileId is not null && CaseLocationMatches(tree, pending, source, token)) { readyForScan = true; continue; }
                using var parent = tree.Directory(ApplyState.Parent(source.Path));
                int collision = 1; string destination;
                for (;; collision++)
                {
                    if (collision > 1000) throw new IOException("Case copy collision budget exceeded.");
                    destination = ConflictNames.CaseSourcePath(source.Path, pending.Source.SourceId, collision);
                    bool occupied;
                    try { using var node = parent.OpenMetadataNode(ApplyState.Name(destination)); occupied = true; }
                    catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { occupied = false; }
                    if (!occupied && index.LocalFiles.Find(destination) is null) break;
                }
                Guid session = Guid.NewGuid();
                using var file = OpenTemporary(tree, parent, destination, out string temporary, out byte[] temporaryId, session: session, cancellationToken: token);
                var state = new CaseCopyState { Root = tree.Identity.ToArray(), Source = source, SourceId = pending.Source.SourceId,
                    Temporary = temporary, TemporaryId = temporaryId, Collision = collision };
                var intent = new ApplyIntent(session, destination, null, state.Encode());
                using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
                    await index.ReserveCaseCopyAsync(pending, intent, destination, token).ConfigureAwait(false);
                Signal("case-reserved", session);
                try
                {
                    using (var borrowed = new SafeFileHandle(file.DangerousGetHandle(), ownsHandle: false))
                    using (var stream = new FileStream(borrowed, FileAccess.ReadWrite, 0, isAsync: false))
                    {
                        await new ChunkAssembler(tree, index, journal).AssembleAsync(source, stream,
                            (hash, length, cancellation) => fetch(pending.Source.SourceDevice, hash, length, cancellation), token).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }
                    FileMetadata.PrepareNewFile(parent, file); FileMetadata.SetMtime(file, source.MtimeTicks); RandomAccess.FlushToDisk(file);
                    state.TemporaryAttributes = (uint)FileSnapshot.Read(file).Attributes; state.Prepared = true;
                    intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false); Signal("case-prepared", session);
                    readyForScan |= await InstallCaseCopyAsync(tree, pending, state, intent, parent, file, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException or CryptographicException)
                {
                    if (state.InstalledInThisCall) throw; // Preserve recovery proof after the native move.
                    await ParkAsync(tree, state.StagingState(), intent, token, file).ConfigureAwait(false);
                    await index.AbandonApplyAsync(intent, token).ConfigureAwait(false); StagingProbeFailed("case-deferred", session, error);
                }
            }
            catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
            {
                if (index.ReadApplyIntents().Any()) throw;
                StagingProbeFailed("case-source-deferred", Guid.NewGuid(), error);
            }
        }
        return readyForScan;
    }

    internal async Task<IReadOnlyList<string>> CompleteCaseCopiesAsync(CancellationToken token)
    {
        using var operation = await FolderOperationGate.AcquireAsync(index, token).ConfigureAwait(false);
        using var tree = new SafeFileTree(rootPath); ValidateRoot(tree); var completed = new List<string>();
        foreach (var pending in index.ReadPendingCasePreservations())
        {
            token.ThrowIfCancellationRequested(); var source = OpenCaseSource(tree, pending);
            if (source.Type != 0 || pending.DestinationPath is not { } destination || pending.InstalledFileId is null) continue;
            using var parent = tree.Directory(ApplyState.Parent(destination)); using var file = TryOpen(parent, ApplyState.Name(destination));
            if (file is null || !CaseMatches(file, pending.InstalledFileId, source, token)) continue;
            using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
            var local = index.LocalFiles.Find(destination);
            if (local is not { Type: 0, Deleted: false, Flags: LocalFileState.None, FileId: not null, FileHash: not null }
                || !local.FileId.AsSpan().SequenceEqual(pending.InstalledFileId) || local.Size != source.Size || !source.FileHash.SequenceEqual(local.FileHash)) continue;
            if (keys.CanWrite)
            {
                if (!local.ContentReady || local.Record is null) continue;
                var published = VersionView.Open(local.Record, keys, ownDevice);
                if (published.Deleted || published.Type != 0 || published.Path != local.Path || published.Size != source.Size || !published.FileHash.SequenceEqual(source.FileHash)) continue;
            }
            Guid session = Guid.NewGuid(); Signal("case-before-completion", session);
            await index.CompleteCasePreservationAsync(pending, local.LocalSequence, keys.CanWrite, token).ConfigureAwait(false);
            completed.Add(pending.Source.PathKey); Signal("case-completed", session);
        }
        return completed.AsReadOnly();
    }

    private async Task RecoverCaseIntentAsync(SafeFileTree tree, ApplyIntent intent, CancellationToken token)
    {
        var state = CaseCopyState.Decode(intent, keys);
        var pending = index.ReadPendingCasePreservations().SingleOrDefault(row => row.Source.SourceId.AsSpan().SequenceEqual(state.SourceId))
            ?? throw new InvalidDataException("Case recovery has no pending authenticated source.");
        var source = OpenCaseSource(tree, pending);
        if (!state.Root.AsSpan().SequenceEqual(tree.Identity) || !source.Record.Body.SequenceEqual(state.Source.Record.Body)
            || pending.DestinationPath != state.Destination) throw new InvalidDataException("Case recovery binding changed.");
        using var parent = tree.Directory(ApplyState.Parent(state.Destination));
        using var installed = TryOpen(parent, ApplyState.Name(state.Destination));
        if (state.Prepared && installed is not null && CaseMatches(installed, state.TemporaryId, source, token))
        {
            using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
            FileMetadata.SetMtime(installed, source.MtimeTicks); FileMetadata.SetSyncedAttributes(installed, source.Attributes); RandomAccess.FlushToDisk(installed);
            await index.AccountInstalledCaseCopyAsync(pending, intent, state.Destination, state.TemporaryId, token).ConfigureAwait(false);
            Signal("case-recovered-installed", intent.Id); return;
        }
        using var temporary = TryOpen(parent, ApplyState.Name(state.Temporary), temporary: true);
        if (state.Prepared && temporary is not null && CaseMatches(temporary, state.TemporaryId, source, token))
        { await InstallCaseCopyAsync(tree, pending, state, intent, parent, temporary, token).ConfigureAwait(false); return; }
        await ParkAsync(tree, state.StagingState(), intent, token, temporary).ConfigureAwait(false);
        await index.AbandonApplyAsync(intent, token).ConfigureAwait(false); Signal("case-recovered-staging", intent.Id);
    }

    private async Task<bool> InstallCaseCopyAsync(SafeFileTree tree, PendingCasePreservation pending, CaseCopyState state,
        ApplyIntent intent, DirectoryLease parent, SafeFileHandle file, CancellationToken token)
    {
        if (!CaseMatches(file, state.TemporaryId, state.Source, token)) throw new IOException("Prepared case copy changed.");
        using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); Signal("case-install-ready", intent.Id);
        try { parent.MoveHere(file, ApplyState.Name(state.Destination)); }
        catch (Win32Exception error) when (error.NativeErrorCode is 80 or 183)
        {
            await ParkAsync(tree, state.StagingState(), intent, token, file).ConfigureAwait(false);
            await index.AbandonApplyAsync(intent, token).ConfigureAwait(false); Signal("case-collision", intent.Id); return false;
        }
        state.InstalledInThisCall = true; Signal("case-installed", intent.Id);
        FileMetadata.SetSyncedAttributes(file, state.Source.Attributes); RandomAccess.FlushToDisk(file);
        await index.AccountInstalledCaseCopyAsync(pending, intent, state.Destination, state.TemporaryId, token).ConfigureAwait(false);
        Signal("case-accounted", intent.Id); return true;
    }

    private VersionView OpenCaseSource(SafeFileTree tree, PendingCasePreservation pending)
    {
        if (!pending.RootId.AsSpan().SequenceEqual(tree.Identity)) throw new IOException("Case source root changed.");
        var source = VersionView.Open(pending.Source.Record, keys, pending.Source.SourceDevice);
        if (PathRules.Key(source.Path) != pending.Source.PathKey || source.Deleted || source.Type > 1
            || !CaseConflictPolicy.SourceId(source).AsSpan().SequenceEqual(pending.Source.SourceId)) throw new InvalidDataException("Case source proof changed.");
        return source;
    }
    private static bool CaseMatches(SafeFileHandle file, byte[] identity, VersionView source, CancellationToken token)
    {
        var snapshot = FileSnapshot.Read(file);
        return (snapshot.Attributes & FileAttributes.Directory) == 0 && HasIdentity(file, identity) && PublishedBlocks.IsIndependent(file)
            && snapshot.Length == source.Size && Hash(file, token).AsSpan().SequenceEqual(source.FileHash);
    }
    private static bool CaseLocationMatches(SafeFileTree tree, PendingCasePreservation pending, VersionView source, CancellationToken token)
    {
        if (pending.DestinationPath is null || pending.InstalledFileId is null) return false;
        using var parent = tree.Directory(ApplyState.Parent(pending.DestinationPath)); using var file = TryOpen(parent, ApplyState.Name(pending.DestinationPath));
        return file is not null && CaseMatches(file, pending.InstalledFileId, source, token);
    }
}
