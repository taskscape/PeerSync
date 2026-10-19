using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public sealed record DirectoryApplyResult(FileApplyOutcome Outcome, bool RequiresScan = false);

/// <summary>Directory-only application after the advertised sender index range
/// is received. Parents precede children; signed child
/// deletions precede parents. Scan after RequiresScan, outside the folder gate.
/// File/directory transitions are coordinated separately by the puller.</summary>
public sealed class DirectoryApplier
{
    private readonly string rootPath;
    private readonly FolderIndex index;
    private readonly FolderKeys keys;
    private readonly NeededSet needed;
    private readonly byte[] ownDevice;
    private readonly DiagnosticJournal? journal;
    internal Action<string>? Checkpoint { get; set; }

    public DirectoryApplier(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        FolderSettings? settings = null, DiagnosticJournal? journal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath); ArgumentNullException.ThrowIfNull(index); ArgumentNullException.ThrowIfNull(keys);
        this.rootPath = Path.GetFullPath(rootPath); this.index = index; this.keys = keys;
        needed = new(index, keys, ownDevice, settings, journal: journal); this.journal = journal; this.ownDevice = ownDevice.ToArray();
    }

    public Task<DirectoryApplyResult> ApplyAsync(string path, CancellationToken cancellationToken = default) => ApplyCoreAsync(path, false, cancellationToken);

    public Task<DirectoryApplyResult> ApplyScheduledAsync(string path, CancellationToken cancellationToken = default) => ApplyCoreAsync(path, true, cancellationToken);

    private async Task<DirectoryApplyResult> ApplyCoreAsync(string path, bool scheduled, CancellationToken cancellationToken)
    {
        string key = PathRules.Key(path); Guid session = Guid.NewGuid();
        using var operation = await FolderOperationGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        try
        {
            if (index.ReadApplyIntents().Any()) throw new IOException("Recover pending application before changing directories.");
            using var tree = new SafeFileTree(rootPath); ValidateRoot(tree); Signal("start", session);
            using var selection = await ScheduledApplySelection.AcquireAsync(index, needed, ownDevice, key, scheduled, cancellationToken).ConfigureAwait(false);
            var known = selection.Known; var decision = selection.Decision;
            if (decision.BlockedReason is not null) { Signal("blocked", session); return new(FileApplyOutcome.Blocked); }
            if (!decision.RequiresApply) { Signal("unchanged", session); return new(FileApplyOutcome.Unchanged); }
            if (!selection.RangeComplete) { Signal("index-range-pending", session); return new(FileApplyOutcome.Deferred); }
            if (decision.Winner!.Type != 1) { Signal("unsupported-type", session); return new(FileApplyOutcome.Deferred); }
            var version = VersionView.Open(decision.DesiredRecord!, keys, decision.Winner.SourceDevice.ToArray());
            if (!version.Deleted) FileMetadata.ValidateMtime(version.MtimeTicks);
            using var parent = tree.Directory(ApplyState.Parent(version.Path));
            using var existing = TryNode(parent, ApplyState.Name(version.Path));
            if (existing is not null)
            {
                var current = FileSnapshot.Read(existing);
                if ((current.Attributes & FileAttributes.Directory) == 0)
                { Signal("type-conflict", session); return new(FileApplyOutcome.Deferred, known is not { Deleted: false, Type: 0 }); }
                if (known is { Deleted: false, Type: 1, FileId: not null } && !Identity(current).AsSpan().SequenceEqual(known.FileId))
                { Signal("identity-changed", session); return new(FileApplyOutcome.Deferred, RequiresScan: true); }
            }
            var state = new DirectoryState { Root = tree.Identity.ToArray(), Version = version, Vector = decision.Vector,
                DirectoryId = existing is null ? [] : Identity(FileSnapshot.Read(existing)) };
            var intent = new ApplyIntent(session, version.Path, known?.LocalSequence, state.Encode());
            await index.ReserveApplyAsync(intent, cancellationToken).ConfigureAwait(false); Signal("staging", session);
            using var created = existing is null && !version.Deleted ? parent.CreateDirectory(ApplyState.Name(version.Path)) : null;
            var node = existing ?? created;
            if (created is not null) { state.DirectoryId = Identity(FileSnapshot.Read(created)); Signal("created", session); }
            state.Prepared = true;
            intent = await index.RewriteApplyAsync(intent, state.Encode(), cancellationToken).ConfigureAwait(false); Signal("prepared", session);
            if (version.Deleted)
            {
                // Close the exact deletion handle before checking disappearance.
                return await DeleteAsync(parent, node, state, intent, cancellationToken).ConfigureAwait(false);
            }
            return await CompleteLiveAsync(parent, node!, state, intent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { Failure(session, error); throw; }
    }

    internal async Task<DirectoryApplyResult> RecoverIntentAsync(SafeFileTree tree, ApplyIntent intent, CancellationToken token)
    {
        var state = DirectoryState.Decode(intent, keys);
        if (!state.Root.AsSpan().SequenceEqual(tree.Identity)) throw new IOException("Directory recovery root changed.");
        Signal("recover-start", intent.Id);
        if (!state.Prepared)
        {
            // Before preparation there is no destructive mutation. An empty
            // directory created just before interruption is left for normal scan.
            await index.AbandonApplyAsync(intent, token).ConfigureAwait(false); Signal("recover-staging", intent.Id);
            return new(FileApplyOutcome.Deferred, RequiresScan: true);
        }
        using var parent = tree.Directory(ApplyState.Parent(state.Version.Path));
        using var node = TryNode(parent, ApplyState.Name(state.Version.Path));
        if (node is not null && ((FileSnapshot.Read(node).Attributes & FileAttributes.Directory) == 0
            || !Identity(FileSnapshot.Read(node)).AsSpan().SequenceEqual(state.DirectoryId)))
            return await ChangedAsync(intent, token).ConfigureAwait(false);
        if (state.Version.Deleted) return await DeleteAsync(parent, node, state, intent, token).ConfigureAwait(false);
        if (node is null) return await ChangedAsync(intent, token).ConfigureAwait(false);
        return await CompleteLiveAsync(parent, node, state, intent, token).ConfigureAwait(false);
    }

    private async Task<DirectoryApplyResult> CompleteLiveAsync(DirectoryLease parent, SafeFileHandle node,
        DirectoryState state, ApplyIntent intent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if ((FileSnapshot.Read(node).Attributes & FileAttributes.Directory) == 0
            || !Identity(FileSnapshot.Read(node)).AsSpan().SequenceEqual(state.DirectoryId))
            throw new IOException("Directory identity changed before completion.");
        parent.MoveHere(node, ApplyState.Name(state.Version.Path)); // Includes case-only spelling changes, without overwrite.
        FileMetadata.SetMtime(node, state.Version.MtimeTicks); FileMetadata.SetSyncedAttributes(node, state.Version.Attributes);
        Signal("metadata", intent.Id);
        var snapshot = FileSnapshot.Read(node);
        await CommitAsync(state, intent, (uint)snapshot.Attributes, Identity(snapshot), token).ConfigureAwait(false);
        return new(FileApplyOutcome.Applied);
    }

    private async Task<DirectoryApplyResult> DeleteAsync(DirectoryLease parent, SafeFileHandle? node,
        DirectoryState state, ApplyIntent intent, CancellationToken token)
    {
        if (node is not null)
        {
            token.ThrowIfCancellationRequested();
            if ((FileSnapshot.Read(node).Attributes & FileAttributes.Directory) == 0
                || !Identity(FileSnapshot.Read(node)).AsSpan().SequenceEqual(state.DirectoryId))
                return await ChangedAsync(intent, token).ConfigureAwait(false);
            Signal("before-delete", intent.Id);
            try { GuardedFile.DeleteOnClose(node); }
            catch (Win32Exception error) when (error.NativeErrorCode == 145)
            {
                Signal("keep-nonempty", intent.Id);
                await CommitAsync(state, intent, state.Version.Attributes, null, token).ConfigureAwait(false);
                return new(FileApplyOutcome.Applied, RequiresScan: true);
            }
            node.Dispose(); Signal("deleted", intent.Id);
            using var current = TryNode(parent, ApplyState.Name(state.Version.Path));
            if (current is not null) return await ChangedAsync(intent, token).ConfigureAwait(false);
        }
        await CommitAsync(state, intent, state.Version.Attributes, null, token).ConfigureAwait(false);
        return new(FileApplyOutcome.Applied);
    }

    private async Task CommitAsync(DirectoryState state, ApplyIntent intent, uint attributes, byte[]? identity, CancellationToken token)
    {
        var version = state.Version;
        var row = new LocalFileRow(version.Path, 1, 0, version.MtimeTicks, attributes, version.Deleted,
            version.ChangedTicks, null, state.Vector, version.Record, identity) { IndexedChunks = [] };
        Signal("before-commit", intent.Id);
        await index.CommitApplyAsync(intent, row, [], needed.ToNeededRow, token).ConfigureAwait(false);
        Signal("committed", intent.Id);
    }
    private async Task<DirectoryApplyResult> ChangedAsync(ApplyIntent intent, CancellationToken token)
    {
        await index.DeferApplyAsync(intent, [], token).ConfigureAwait(false); Signal("changed", intent.Id);
        return new(FileApplyOutcome.Deferred, RequiresScan: true);
    }
    private void ValidateRoot(SafeFileTree tree)
    {
        if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } identity || !identity.AsSpan().SequenceEqual(tree.Identity))
            throw new IOException("Directory root identity changed or is not bound.");
    }
    private static SafeFileHandle? TryNode(DirectoryLease parent, string name)
    {
        try { return parent.OpenNode(name); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
    }
    private static byte[] Identity(FileSnapshot snapshot)
    {
        byte[] id = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(id, snapshot.IdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(8), snapshot.IdHigh); return id;
    }
    private void Signal(string stage, Guid session)
    {
        journal?.TryWrite(new("directory." + stage, "directory") { SessionId = session, Level = DiagnosticLevel.Debug });
        Checkpoint?.Invoke(stage);
    }
    private void Failure(Guid session, Exception error) => journal?.TryWrite(new("directory.failed", "directory")
    { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult,
        NativeError = error switch { Win32Exception native => native.NativeErrorCode, SqliteException database => database.SqliteExtendedErrorCode, _ => null } });
}
