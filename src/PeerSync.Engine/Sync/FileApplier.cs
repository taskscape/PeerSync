using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Net;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public enum FileApplyOutcome { Applied, Unchanged, Deferred, Blocked }
public sealed record ScheduledFileApplyResult(FileApplyOutcome Outcome, bool RequiresScan);

/// <summary>Serializes file application and file/directory transitions with scanning. The host
/// must recover before its first scan; errors retain durable recovery state.
/// No path, content, hash or envelope is written to diagnostic output.</summary>
public sealed partial class FileApplier
{
    private readonly string rootPath;
    private readonly FolderIndex index;
    private readonly FolderKeys keys;
    private readonly byte[] ownDevice;
    private readonly NeededSet needed;
    private readonly FolderSettings settings;
    private readonly DiagnosticJournal? journal;
    private readonly TimeProvider clock;
    internal Action<string>? Checkpoint { get; set; }

    public FileApplier(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        FolderSettings? settings = null, DiagnosticJournal? journal = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath); ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(keys);
        this.rootPath = Path.GetFullPath(rootPath); this.index = index; this.keys = keys;
        this.settings = FolderSettings.FromJson((settings ?? new()).ToJson());
        needed = new(index, keys, ownDevice, this.settings, journal: journal);
        this.ownDevice = ownDevice.ToArray();
        this.journal = journal; this.clock = clock ?? TimeProvider.System;
    }

    public Task<FileApplyOutcome> ApplyAsync(string path,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        return ObserveAsync("pull", session => ApplyCoreAsync(path, (_, hash, length, token) => fetch(hash, length, token), session, false, null, cancellationToken));
    }

    /// <summary>Fetch receives the actually selected source device, then chunk hash.
    /// A scan requirement must be handled after this call releases mutation gates.</summary>
    public async Task<ScheduledFileApplyResult> ApplyScheduledAsync(string path,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetch); bool requiresScan = false;
        var outcome = await ObserveAsync("scheduled-pull", session => ApplyCoreAsync(path, fetch, session, true,
            () => requiresScan = true, cancellationToken)).ConfigureAwait(false);
        return new(outcome, requiresScan);
    }

    internal async Task<ScheduledFileApplyResult> ApplyScheduledAsync(string path,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        Func<ReadOnlyMemory<byte>, IReadOnlyList<PeerBlockRequest>, bool, CancellationToken, ValueTask<IReadOnlyList<PeerBlockResult>>>? fetchBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetch); bool requiresScan = false;
        var outcome = await ObserveAsync("scheduled-pull", session => ApplyCoreAsync(path, fetch, session, true,
            () => requiresScan = true, cancellationToken, fetchBatch)).ConfigureAwait(false);
        return new(outcome, requiresScan);
    }

    private async Task<FileApplyOutcome> ApplyCoreAsync(string path,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        Guid session, bool scheduled, Action? requireScan, CancellationToken cancellationToken,
        Func<ReadOnlyMemory<byte>, IReadOnlyList<PeerBlockRequest>, bool, CancellationToken, ValueTask<IReadOnlyList<PeerBlockResult>>>? fetchBatch = null)
    {
        ArgumentNullException.ThrowIfNull(fetch); string key = PathRules.Key(path);
        using var operation = await FolderOperationGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        if (index.ReadApplyIntents().Any()) throw new IOException("Recover pending application before pulling.");
        using var tree = new SafeFileTree(rootPath); ValidateRoot(tree);
        using var selection = await ScheduledApplySelection.AcquireAsync(index, needed, ownDevice, key, scheduled, cancellationToken).ConfigureAwait(false);
        var known = selection.Known; var decision = selection.Decision;
        if (decision.BlockedReason is not null)
        {
            if (scheduled && decision.BlockedReason == "directory-publication-pending"
                && await TryApplyAbsentTombstoneAsync(tree, selection, key, session, cancellationToken).ConfigureAwait(false))
                return FileApplyOutcome.Applied;
            if (decision.BlockedReason == "directory-publication-pending") Signal("publication-pending", session);
            return FileApplyOutcome.Blocked;
        }
        if (scheduled && decision.Winner is { Deleted: true } && index.HasBlockingDirectoryPublication(key, includeDescendants: true))
            Signal("ancestor-publication-bypassed", session);
        if (!decision.RequiresApply) return FileApplyOutcome.Unchanged;
        if (!selection.RangeComplete) { Signal("index-range-pending", session); return FileApplyOutcome.Deferred; }
        var winner = decision.Winner!;
        // Directory-only operations use DirectoryApplier. A type transition
        // preserves the exact old node without traversing any of its children.
        if (winner.Type > 1 || winner.Type == 1 && known is not { Deleted: false, Type: 0 }) return FileApplyOutcome.Deferred;
        var desired = VersionView.Open(decision.DesiredRecord!, keys, winner.SourceDevice.ToArray());
        if (!desired.Deleted)
        {
            try { FileMetadata.ValidateMtime(desired.MtimeTicks); }
            catch (ArgumentOutOfRangeException) { Signal("timestamp-unrepresentable", session); return FileApplyOutcome.Blocked; }
        }
        using var parentProbe = desired.Deleted ? tree.ProbeDirectory(ApplyState.Parent(desired.Path)) : null;
        if (parentProbe?.Absent == true)
        {
            // Keep a known live version available for the scanner's immutable
            // supersession archive before replacing its metadata.
            if (known is { Deleted: false }) { requireScan?.Invoke(); return FileApplyOutcome.Deferred; }
            var absentState = new ApplyState { Root = tree.Identity.ToArray(), Version = desired, Vector = decision.Vector,
                Prepared = true, ScheduledDeletion = scheduled };
            var absentIntent = new ApplyIntent(session, path, known?.LocalSequence, absentState.Encode());
            await index.ReserveApplyAsync(absentIntent, cancellationToken).ConfigureAwait(false);
            Signal("absent-parent", session); Signal("prepared", session);
            await CompleteAsync(tree, absentState, absentIntent, null, null, cancellationToken).ConfigureAwait(false);
            requireScan?.Invoke(); return FileApplyOutcome.Applied;
        }
        using var parent = parentProbe?.Directory ?? tree.Directory(ApplyState.Parent(desired.Path));
        string temporary = ""; byte[] temporaryId = [];
        bool replaceKind = known is { Deleted: false } && known.Type != desired.Type;
        using var tempHandle = !desired.Deleted && (decision.RequiresContent || replaceKind)
            ? OpenTemporary(tree, parent, desired.Path, out temporary, out temporaryId, desired.Type == 1, session, cancellationToken) : null;
        var state = new ApplyState { Root = tree.Identity.ToArray(), Version = desired, Vector = decision.Vector,
            Temporary = temporary, TemporaryId = temporaryId, ScheduledDeletion = scheduled && desired.Deleted };
        var intent = new ApplyIntent(session, path, known?.LocalSequence, state.Encode());
        await index.ReserveApplyAsync(intent, cancellationToken).ConfigureAwait(false);
        Signal("staging", intent.Id);
        if (tempHandle is not null && desired.Type == 0)
        {
            byte[] source = winner.SourceDevice.ToArray();
            using var borrowed = new SafeFileHandle(tempHandle.DangerousGetHandle(), ownsHandle: false);
            using var stream = new FileStream(borrowed, FileAccess.ReadWrite, 0, isAsync: false);
            await new ChunkAssembler(tree, index, journal).AssembleAsync(desired, stream,
                (hash, length, token) => fetch(source, hash, length, token),
                fetchBatch is null ? null : (requests, endgame, token) => fetchBatch(source, requests, endgame, token), cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        using var mutationReferences = selection.HoldsReferences ? null : await FolderReferenceGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        if (index.HasPendingCasePreservation(key) || ScheduledApplySelection.BlocksDirectoryPublication(index, ownDevice, key, known, decision, scheduled))
        {
            await ParkAsync(tree, state, intent, cancellationToken, tempHandle).ConfigureAwait(false);
            await index.AbandonApplyAsync(intent, cancellationToken).ConfigureAwait(false);
            Signal("case-barrier", session); return FileApplyOutcome.Deferred;
        }
        using var oldParent = tree.Directory(ApplyState.Parent(known?.Path ?? desired.Path));
        using var original = TryOpen(oldParent, ApplyState.Name(known?.Path ?? desired.Path));
        if (!MatchesKnown(original, known, cancellationToken))
        {
            await ParkAsync(tree, state, intent, cancellationToken, tempHandle).ConfigureAwait(false);
            await index.DeferApplyAsync(intent, [], cancellationToken).ConfigureAwait(false);
            requireScan?.Invoke(); Signal("target-changed", intent.Id); return FileApplyOutcome.Deferred;
        }
        if (original is not null)
        {
            CaptureOriginal(state, original, known!.Path, known, cancellationToken);
            if (tempHandle is not null || desired.Deleted)
            {
                state.ArchivedTicks = clock.GetUtcNow().UtcTicks;
                state.Retained = RetainedPath(state, decision.LocalLoserFate, 1);
                if (tempHandle is not null) FileMetadata.CopyReplacementMetadata(original, tempHandle);
            }
        }
        if (tempHandle is not null)
        {
            if (original is null) FileMetadata.PrepareNewFile(parent, tempHandle);
            FileMetadata.SetMtime(tempHandle, desired.MtimeTicks);
            if (desired.Type == 0) RandomAccess.FlushToDisk(tempHandle);
            state.TemporaryAttributes = (uint)FileSnapshot.Read(tempHandle).Attributes;
        }
        state.Prepared = true;
        intent = await index.RewriteApplyAsync(intent, state.Encode(), cancellationToken).ConfigureAwait(false);
        Signal("prepared", intent.Id);
        var outcome = await FinishAsync(tree, state, intent, original, tempHandle, cancellationToken).ConfigureAwait(false);
        if (outcome == FileApplyOutcome.Deferred || state.Retained.Length != 0 && !state.Retained.StartsWith(".psp/", StringComparison.Ordinal)) requireScan?.Invoke();
        return outcome;
    }

    /// <summary>Recover local intents without network access before allowing scans.
    /// Staging bytes are parked for later verified reuse. Unrecognized state fails closed.</summary>
    public Task<int> RecoverAsync(CancellationToken cancellationToken = default) =>
        ObserveAsync("recovery", _ => RecoverCoreAsync(cancellationToken));

    private async Task<int> RecoverCoreAsync(CancellationToken cancellationToken)
    {
        using var operation = await FolderOperationGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        using var tree = new SafeFileTree(rootPath); ValidateRoot(tree); int recovered = 0;
        await PublishedBlocks.RecoverCapturesAsync(tree, index, journal, cancellationToken).ConfigureAwait(false);
        foreach (var intent in index.ReadApplyIntents().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DirectoryCaseMemberState.IsMember(intent))
            { await RecoverDirectoryMemberAsync(tree, intent, cancellationToken).ConfigureAwait(false); recovered++; continue; }
            if (DirectoryCaseRootState.IsRoot(intent))
            { await RecoverDirectoryCaseRootAsync(tree, intent, cancellationToken).ConfigureAwait(false); recovered++; continue; }
            if (CaseCopyState.IsCase(intent))
            { await RecoverCaseIntentAsync(tree, intent, cancellationToken).ConfigureAwait(false); recovered++; continue; }
            if (HistoryState.IsHistory(intent))
            {
                var history = new SnapshotHistory(index, keys, ownDevice, settings, journal, clock) { Checkpoint = Checkpoint };
                await history.RecoverIntentAsync(tree, intent, cancellationToken).ConfigureAwait(false); recovered++; continue;
            }
            if (intent.Payload.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(intent.Payload) is 3 or 6)
            {
                var directories = new DirectoryApplier(rootPath, index, keys, ownDevice, settings, journal) { Checkpoint = Checkpoint };
                await directories.RecoverIntentAsync(tree, intent, cancellationToken).ConfigureAwait(false); recovered++; continue;
            }
            var state = ApplyState.Decode(intent, keys);
            if (!state.Root.AsSpan().SequenceEqual(tree.Identity)) throw new IOException("Recovery root identity changed.");
            Signal("recover-start", intent.Id);
            if (!state.Prepared)
            {
                await ParkAsync(tree, state, intent, cancellationToken).ConfigureAwait(false);
                await index.AbandonApplyAsync(intent, cancellationToken).ConfigureAwait(false);
                recovered++; Signal("recover-staging", intent.Id); continue;
            }
            using var deletionBarrier = state.ScheduledDeletion
                ? await ScheduledApplySelection.AcquireRecoveryAsync(index, state.Version!, ownDevice, true, cancellationToken).ConfigureAwait(false) : null;
            using var recoveryReferences = state.ScheduledDeletion ? null : await FolderReferenceGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
            using var parentProbe = state.Deleted ? tree.ProbeDirectory(ApplyState.Parent(state.Path)) : null;
            if (parentProbe?.Absent == true)
            {
                if (state.Retained.Length == 0 && index.LocalFiles.Find(state.Path) is { Deleted: false })
                    await DeferAsync(tree, state, intent, null, cancellationToken).ConfigureAwait(false);
                else if (state.OriginalId.Length == 0 || state.Retained.Length != 0)
                    await CompleteAsync(tree, state, intent, null, null, cancellationToken).ConfigureAwait(false);
                else await DeferAsync(tree, state, intent, null, cancellationToken).ConfigureAwait(false);
                recovered++; Signal("recover-complete", intent.Id); continue;
            }
            using var targetParent = parentProbe?.Directory ?? tree.Directory(ApplyState.Parent(state.Path));
            using var target = TryOpen(targetParent, ApplyState.Name(state.Path));
            using var tempParent = state.Temporary.Length == 0 ? null : tree.Directory(ApplyState.Parent(state.Temporary));
            using var temporary = tempParent is null ? null : TryOpen(tempParent, ApplyState.Name(state.Temporary), temporary: true);
            if (!state.IsRestore && (index.HasPendingCasePreservation(state.Path)
                || ScheduledApplySelection.BlocksDirectoryPublicationDuringRecovery(index, needed, ownDevice, state.Version!, state.ScheduledDeletion)))
            {
                if (state.OriginalId.Length == 0 && target is null || target is not null && HasIdentity(target, state.OriginalId))
                {
                    await ParkAsync(tree, state, intent, cancellationToken, temporary).ConfigureAwait(false);
                    await index.AbandonApplyAsync(intent, cancellationToken).ConfigureAwait(false);
                }
                else await DeferAsync(tree, state, intent, null, cancellationToken, temporary).ConfigureAwait(false);
                recovered++; Signal("case-recovery-barrier", intent.Id); continue;
            }
            if (temporary is not null && !HasIdentity(temporary, state.TemporaryId))
                throw new IOException("Recovery temporary identity changed.");
            if (target is not null && HasIdentity(target, state.TemporaryId))
            {
                if (MatchesNew(target, state, cancellationToken))
                    await CompleteAsync(tree, state, intent, target, null, cancellationToken).ConfigureAwait(false);
                else await DeferAsync(tree, state, intent, null, cancellationToken).ConfigureAwait(false);
            }
            else if (target is not null && HasIdentity(target, state.OriginalId))
            {
                VerifyOld(target, state, cancellationToken);
                if (state.Temporary.Length == 0 || temporary is not null && MatchesNew(temporary, state, cancellationToken))
                    await FinishAsync(tree, state, intent, target, temporary, cancellationToken).ConfigureAwait(false);
                else
                {
                    FileMetadata.RestoreAttributes(target, (FileAttributes)state.OriginalAttributes);
                    await ParkAsync(tree, state, intent, cancellationToken, temporary).ConfigureAwait(false);
                    await index.AbandonApplyAsync(intent, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (target is null)
            {
                if (temporary is not null && MatchesNew(temporary, state, cancellationToken) || state.Deleted)
                    await FinishAsync(tree, state, intent, null, temporary, cancellationToken).ConfigureAwait(false);
                else
                {
                    if (state.OriginalId.Length != 0)
                    {
                        using var retained = Retain(tree, state, null)!;
                        VerifyOld(retained.File, state, cancellationToken);
                        FileMetadata.ClearReadOnly(retained.File);
                        targetParent.MoveHere(retained.File, ApplyState.Name(state.Original));
                        FileMetadata.RestoreAttributes(retained.File, (FileAttributes)state.OriginalAttributes);
                    }
                    await ParkAsync(tree, state, intent, cancellationToken, temporary).ConfigureAwait(false);
                    await index.AbandonApplyAsync(intent, cancellationToken).ConfigureAwait(false);
                    Signal("recover-restored", intent.Id);
                }
            }
            else await DeferAsync(tree, state, intent, null, cancellationToken, temporary).ConfigureAwait(false);
            recovered++; Signal("recover-complete", intent.Id);
        }
        return recovered;
    }

    private async Task<FileApplyOutcome> FinishAsync(SafeFileTree tree, ApplyState state, ApplyIntent intent,
        SafeFileHandle? original, SafeFileHandle? temporary, CancellationToken token)
    {
        using var retained = original is null ? Retain(tree, state, null) : null;
        DirectoryLease? destination = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (temporary is not null) VerifyNew(temporary, state, token);
            if (original is not null && state.Retained.Length != 0)
            {
                VerifyOld(original, state, token);
                for (int collision = 1; ; collision++)
                {
                    destination?.Dispose();
                    destination = tree.Directory(ApplyState.Parent(state.Retained), create: true,
                        privatePath: state.Retained.StartsWith(".psp/", StringComparison.Ordinal));
                    FileMetadata.ClearReadOnly(original);
                    Signal("original-cleared", intent.Id);
                    try { destination.MoveHere(original, ApplyState.Name(state.Retained)); break; }
                    catch (Win32Exception error) when (error.NativeErrorCode is 80 or 183)
                    {
                        FileMetadata.RestoreAttributes(original, (FileAttributes)state.OriginalAttributes);
                        if (collision >= 1000) throw new IOException("Retained name collision budget exceeded.");
                        var fate = state.Retained.StartsWith(".psp/", StringComparison.Ordinal) ? LoserFate.Archive : LoserFate.ConflictCopy;
                        state.Retained = RetainedPath(state, fate, collision + 1);
                        intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false);
                    }
                }
                Signal("archive-moved", intent.Id);
                FileMetadata.RestoreAttributes(original, (FileAttributes)state.OriginalAttributes);
                Signal("archived", intent.Id);
            }
            else if (original is null && state.Retained.Length != 0)
            {
                VerifyOld(retained!.File, state, token);
            }
            if (temporary is not null)
            {
                token.ThrowIfCancellationRequested();
                using var parent = tree.Directory(ApplyState.Parent(state.Path));
                Signal("install-ready", intent.Id);
                try { parent.MoveHere(temporary, ApplyState.Name(state.Path)); }
                catch (Win32Exception error) when (error.NativeErrorCode is 80 or 183)
                { await DeferAsync(tree, state, intent, original ?? retained?.File, token, temporary).ConfigureAwait(false); return FileApplyOutcome.Deferred; }
                Signal("installed", intent.Id);
            }
            if (temporary is null && original is not null && !state.Deleted && state.Original != state.Path)
            {
                using var parent = tree.Directory(ApplyState.Parent(state.Path));
                parent.MoveHere(original, ApplyState.Name(state.Path));
                Signal("case-renamed", intent.Id);
            }
            if (state.Deleted)
            {
                using var parent = tree.Directory(ApplyState.Parent(state.Path));
                using var concurrent = TryOpen(parent, ApplyState.Name(state.Path));
                if (concurrent is not null)
                {
                    await DeferAsync(tree, state, intent, original ?? retained?.File, token).ConfigureAwait(false);
                    return FileApplyOutcome.Deferred;
                }
            }
            await CompleteAsync(tree, state, intent, temporary ?? (state.Deleted ? null : original), original ?? retained?.File, token).ConfigureAwait(false);
            return FileApplyOutcome.Applied;
        }
        finally { destination?.Dispose(); }
    }

    private async Task CompleteAsync(SafeFileTree tree, ApplyState state, ApplyIntent intent,
        SafeFileHandle? installed, SafeFileHandle? retainedOriginal, CancellationToken token)
    {
        if (!state.Deleted)
        {
            if (installed is null) throw new IOException("Verified installed file is missing.");
            // Metadata-only application is allowed only to this verified old object.
            if (state.Temporary.Length == 0) VerifyOld(installed, state, token);
            else VerifyNew(installed, state, token);
            FileMetadata.SetMtime(installed, state.MtimeTicks); FileMetadata.SetSyncedAttributes(installed, state.Attributes);
        }
        using var retained = Retain(tree, state, retainedOriginal);
        var archives = ReadArchives(state, retained?.File, token);
        if (state.IsRestore)
        {
            // Restoration is a local filesystem edit. Withhold stale metadata;
            // the normal scanner signs once (RW) or leaves it unpublished (RO).
            Signal("before-commit", intent.Id);
            await CommitDeferredAsync(state, intent, archives, token).ConfigureAwait(false);
            Signal("committed", intent.Id); return;
        }
        var version = state.Version!; var body = version.ExportBody();
        if (!version.Deleted && version.Type == 0)
        {
            using var blocks = new PublishedBlocks(tree);
            try { blocks.CaptureFile(installed!, version, token); }
            finally { await blocks.FlushAsync(index, CancellationToken.None, journal).ConfigureAwait(false); }
        }
        FileSnapshot? actual = installed is null ? null : FileSnapshot.Read(installed);
        var chunks = new List<LocalChunk>(); long offset = 0;
        foreach (var chunk in body.Chunks) { chunks.Add(new(offset, checked((int)chunk.Length), chunk.Hash.ToByteArray())); offset += chunk.Length; }
        var row = new LocalFileRow(version.Path, version.Type, version.Size, version.MtimeTicks,
            actual is null ? body.Attributes : (uint)actual.Value.Attributes, version.Deleted, version.ChangedTicks,
            version.Deleted || version.Type == 1 ? null : version.FileHash.ToArray(), state.Vector, version.Record,
            actual is null ? null : Identity(actual.Value)) { IndexedChunks = chunks, ContentReady = !version.Deleted && version.Type == 0,
                ChangeFileTime = actual?.Changed };
        Signal("before-commit", intent.Id);
        if (state.OriginalIsDirectory && state.Retained.Length != 0)
            await index.CommitMovedDirectoryApplyAsync(intent, row, archives, needed.ToNeededRow, token).ConfigureAwait(false);
        else await index.CommitApplyAsync(intent, row, archives, needed.ToNeededRow, token).ConfigureAwait(false);
        Signal("committed", intent.Id);
    }

    private async Task DeferAsync(SafeFileTree tree, ApplyState state, ApplyIntent intent, SafeFileHandle? heldOriginal, CancellationToken token, SafeFileHandle? heldTemporary = null)
    {
        using var retained = Retain(tree, state, heldOriginal);
        var archives = ReadArchives(state, retained?.File, token);
        await ParkAsync(tree, state, intent, token, heldTemporary).ConfigureAwait(false);
        await CommitDeferredAsync(state, intent, archives, token).ConfigureAwait(false);
        Signal("concurrent-target", intent.Id);
    }

    private Task<bool> CommitDeferredAsync(ApplyState state, ApplyIntent intent, ArchiveRow[] archives, CancellationToken token) =>
        state.OriginalIsDirectory && state.Retained.Length != 0
            ? index.DeferMovedDirectoryApplyAsync(intent, archives, token) : index.DeferApplyAsync(intent, archives, token);

    private static ArchiveRow[] ReadArchives(ApplyState state, SafeFileHandle? file, CancellationToken token)
    {
        if (state.Retained.Length == 0) return [];
        if (file is null) throw new IOException("Retained original is unavailable.");
        VerifyOld(file, state, token);
        FileMetadata.RestoreAttributes(file, (FileAttributes)state.OriginalAttributes);
        return state.Retained.StartsWith(".psp/", StringComparison.Ordinal)
            ? [new(0, state.Original, state.Retained, state.ArchivedTicks, state.OriginalSize, state.OriginalHash)] : [];
    }

    private static RetainedLease? Retain(SafeFileTree tree, ApplyState state, SafeFileHandle? held) =>
        state.Retained.Length == 0 ? null : new(tree, state.Retained, held);

    private sealed class RetainedLease : IDisposable
    {
        private readonly DirectoryLease parent;
        private readonly SafeFileHandle? owned;
        internal SafeFileHandle File { get; }
        internal RetainedLease(SafeFileTree tree, string path, SafeFileHandle? held)
        {
            parent = tree.Directory(ApplyState.Parent(path), privatePath: path.StartsWith(".psp/", StringComparison.Ordinal));
            try { owned = held is null ? TryOpen(parent, ApplyState.Name(path)) ?? throw new IOException("Retained node is missing.") : null; File = held ?? owned!; }
            catch { parent.Dispose(); throw; }
        }
        public void Dispose() { owned?.Dispose(); parent.Dispose(); }
    }

    private void ValidateRoot(SafeFileTree tree)
    {
        if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } identity || !identity.AsSpan().SequenceEqual(tree.Identity))
            throw new IOException("Scan and bind the sync root before application.");
    }

    private static SafeFileHandle? TryOpen(DirectoryLease parent, string name, bool temporary = false)
    {
        try
        {
            var node = parent.OpenNode(name, temporary);
            try { if ((FileSnapshot.Read(node).Attributes & FileAttributes.Directory) != 0) return node; }
            catch { node.Dispose(); throw; }
            node.Dispose();
            // Classification is not a file content guard. Reopen with write
            // sharing excluded, rechecking all expectations under that handle.
            return parent.OpenFile(name, mutation: true, temporary);
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
    }

    private static bool MatchesKnown(SafeFileHandle? file, LocalFileRow? known, CancellationToken token)
    {
        if (file is null) return known is null or { Deleted: true };
        if (known is not { Deleted: false, FileId: not null }) return false;
        var actual = FileSnapshot.Read(file);
        bool directory = (actual.Attributes & FileAttributes.Directory) != 0;
        return HasIdentity(file, known.FileId) && directory == (known.Type == 1) && actual.Length == known.Size && (uint)actual.Attributes == known.Attributes
            && (directory || DateTime.FromFileTimeUtc(actual.LastWrite).Ticks == known.MtimeTicks
                && known.FileHash is not null && Hash(file, token).AsSpan().SequenceEqual(known.FileHash));
    }
    private static void VerifyOld(SafeFileHandle file, ApplyState state, CancellationToken token)
    {
        var actual = FileSnapshot.Read(file);
        // Windows can set Archive during rename. Normal represents no flags;
        // neither is synchronized metadata or evidence of changed content.
        const uint incidental = (uint)(FileAttributes.Normal | FileAttributes.Archive);
        uint attributes = (uint)actual.Attributes & ~incidental, original = state.OriginalAttributes & ~incidental;
        uint cleared = original & ~(uint)FileAttributes.ReadOnly;
        if (!HasIdentity(file, state.OriginalId) || ((actual.Attributes & FileAttributes.Directory) != 0) != state.OriginalIsDirectory
            || actual.Length != state.OriginalSize
            || !state.OriginalIsDirectory && DateTime.FromFileTimeUtc(actual.LastWrite).Ticks != state.OriginalMtime
                && !(state.Temporary.Length == 0 && !state.Deleted && DateTime.FromFileTimeUtc(actual.LastWrite).Ticks == state.MtimeTicks)
            || attributes != original && attributes != cleared
                && !(state.Temporary.Length == 0 && !state.Deleted && attributes ==
                    state.Attributes)
            || !state.OriginalIsDirectory && !Hash(file, token).AsSpan().SequenceEqual(state.OriginalHash))
            throw new IOException("Retained original changed; recovery requires attention.");
    }
    private static void VerifyNew(SafeFileHandle file, ApplyState state, CancellationToken token)
    {
        if (!MatchesNew(file, state, token))
            throw new IOException("Verified new file changed; recovery requires attention.");
    }
    private static bool MatchesNew(SafeFileHandle file, ApplyState state, CancellationToken token)
    {
        var actual = FileSnapshot.Read(file);
        return HasIdentity(file, state.TemporaryId) && ((actual.Attributes & FileAttributes.Directory) != 0) == (state.Type == 1) && actual.Length == state.Size
            && (state.Type == 1 || DateTime.FromFileTimeUtc(actual.LastWrite).Ticks == state.MtimeTicks)
            && ((uint)actual.Attributes == state.TemporaryAttributes || (uint)actual.Attributes ==
                (state.Type == 1 ? state.Attributes | (uint)FileAttributes.Directory : state.Attributes is 0 ? (uint)FileAttributes.Normal : state.Attributes))
            && (state.Type == 1 || Hash(file, token).AsSpan().SequenceEqual(state.FileHash));
    }
    private static bool HasIdentity(SafeFileHandle file, ReadOnlySpan<byte> identity) => identity.Length == 16
        && Identity(FileSnapshot.Read(file)).AsSpan().SequenceEqual(identity);
    private static byte[] Identity(FileSnapshot snapshot)
    {
        byte[] result = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(result, snapshot.IdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), snapshot.IdHigh); return result;
    }
    private static byte[] Hash(SafeFileHandle file, CancellationToken token) => GuardedFile.Hash(file, token);
    private static string RetainedPath(ApplyState state, LoserFate fate, int collision)
    {
        var at = new DateTimeOffset(state.ArchivedTicks, TimeSpan.Zero);
        if (state.OriginalIsDirectory || fate == LoserFate.ConflictCopy) return ConflictNames.CopyPath(state.Original, at, state.OriginalDevice, collision);
        return ArchiveNames.Candidate(state.Original, at, collision);
    }
    private void Signal(string stage, Guid session)
    {
        journal?.TryWrite(new("apply." + stage, "apply") { SessionId = session, Level = DiagnosticLevel.Debug });
        Checkpoint?.Invoke(stage);
    }

    private async Task<T> ObserveAsync<T>(string operation, Func<Guid, Task<T>> action)
    {
        Guid session = Guid.NewGuid(); var watch = Stopwatch.StartNew();
        journal?.TryWrite(new("apply.start", operation) { SessionId = session });
        try
        {
            var result = await action(session).ConfigureAwait(false);
            journal?.TryWrite(new("apply.complete", operation) { SessionId = session, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
            return result;
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("apply.failed", operation) { SessionId = session, Level = DiagnosticLevel.Warning,
                ErrorCode = error.GetType().Name, HResult = error.HResult,
                NativeError = error is Win32Exception native ? native.NativeErrorCode : null, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
            throw;
        }
    }
}
