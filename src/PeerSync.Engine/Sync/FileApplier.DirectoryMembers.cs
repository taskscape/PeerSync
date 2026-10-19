using System.ComponentModel;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Protocol.Directories;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public sealed partial class FileApplier
{
    internal int DirectoryCaseMemberWorkLimit { get; init; } = 32;
    private async Task<(bool Changed, int Processed)> PrepareDirectoryMembersAsync(SafeFileTree tree, PendingCasePreservation pending, VersionView source,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, int budget, CancellationToken token)
    {
        if (budget == 0) return (false, 0);
        var progress = index.ReadDirectoryCaseProgress(pending.Source.SourceId, pending.InstalledFileId!);
        if (progress.Exhausted) { await RetryCaseAsync(pending, "case-root-publication-pending", token).ConfigureAwait(false); return (false, 0); }
        int collision = 1;
        while (collision <= 1000 && ConflictNames.CaseSourcePath(source.Path, pending.Source.SourceId, collision) != pending.DestinationPath) collision++;
        if (collision > 1000) throw new InvalidDataException("Directory case destination policy changed.");
        var page = await new DirectoryCaseProjection(rootPath, index, keys, pending.Source.SourceDevice, journal)
            .ReadUnderGateAsync(source.Record, fetch, progress.LastKey, collision, budget, token: token).ConfigureAwait(false);
        bool changed = false; int processed = 0;
        try
        {
            foreach (var projected in page.Members)
            {
                token.ThrowIfCancellationRequested(); processed++;
                using var parents = PinDirectoryMemberParent(tree, pending, source, projected.Original);
                using (var occupant = TryCaseRoot(parents.Parent, ApplyState.Name(projected.Destination), out bool occupied))
                    if (occupied || occupant is not null) throw new DirectoryCaseChanged("Directory case member destination is occupied.");
                var original = projected.Original; FileMetadata.ValidateMtime(original.MtimeTicks); Guid session = Guid.NewGuid();
                using var temporary = OpenTemporary(tree, parents.Parent, projected.Destination, out string path, out byte[] identity,
                    directory: original.Type == 1, session: session, cancellationToken: token);
                var state = new DirectoryCaseMemberState { Root = tree.Identity.ToArray(), SourceId = pending.Source.SourceId, RootFileId = pending.InstalledFileId!,
                    MemberKey = PathRules.Key(original.Path), Link = projected.RecordLink, MemberId = CaseConflictPolicy.SourceId(original), Type = original.Type,
                    Destination = projected.Destination, Temporary = path, TemporaryId = identity };
                var row = MemberRow(state); var intent = new ApplyIntent(session, state.Destination, index.LocalFiles.Find(state.Destination)?.LocalSequence, state.Encode());
                using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
                    await index.ReserveDirectoryCaseMemberAsync(pending, row, intent, token).ConfigureAwait(false);
                Signal("case-member-reserved", session);
                try
                {
                    if (original.Type == 1)
                    { if (!IsEmptyStagingDirectory(temporary, token)) throw new DirectoryCaseChanged("Directory member staging has unknown children."); }
                    else
                    {
                        using var borrowed = new SafeFileHandle(temporary.DangerousGetHandle(), ownsHandle: false);
                        using var stream = new FileStream(borrowed, FileAccess.ReadWrite, 0, isAsync: false);
                        await new ChunkAssembler(tree, index, journal).AssembleAsync(original, stream, fetch, token).ConfigureAwait(false); stream.Flush(flushToDisk: true);
                    }
                    FileMetadata.PrepareNewFile(parents.Parent, temporary); FileMetadata.SetMtime(temporary, original.MtimeTicks);
                    // Keep the visible Hidden value through rename/replay;
                    // ReadOnly is applied after moving the writable temporary.
                    FileMetadata.SetSyncedAttributes(temporary, original.Attributes & (uint)FileAttributes.Hidden);
                    if (original.Type == 0) RandomAccess.FlushToDisk(temporary);
                    state.TemporaryAttributes = (uint)FileSnapshot.Read(temporary).Attributes; state.Prepared = true;
                    intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false); Signal("case-member-prepared", session);
                    await InstallDirectoryMemberAsync(pending, state, row, intent, original, parents.Parent, temporary, token).ConfigureAwait(false); changed = true;
                }
                catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or CryptographicException)
                {
                    if (state.InstalledInThisCall) throw;
                    await ParkDirectoryMemberAsync(state, intent, original, temporary, token).ConfigureAwait(false);
                    await index.AbandonApplyAsync(intent, token).ConfigureAwait(false); throw;
                }
            }
            if (!page.More)
            {
                await index.MarkDirectoryCaseMembersExhaustedAsync(pending, token).ConfigureAwait(false);
                if (!changed) await RetryCaseAsync(pending, "case-root-publication-pending", token).ConfigureAwait(false);
            }
            return (changed, processed);
        }
        catch (DirectoryCaseChanged)
        {
            if (index.ReadApplyIntents().Any()) throw;
            using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
            await index.ResetDirectoryCaseRootAsync(pending, token).ConfigureAwait(false); Signal("case-member-root-reselected", Guid.NewGuid()); return (changed, Math.Max(1, processed));
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or CryptographicException or ArgumentOutOfRangeException)
        {
            if (index.ReadApplyIntents().Any()) throw;
            StagingProbeFailed("case-member-deferred", Guid.NewGuid(), error); await RetryCaseAsync(pending, error.GetType().Name, token).ConfigureAwait(false);
            return (changed, budget); // Earlier accounted members still need scanning.
        }
    }
    private async Task RecoverDirectoryMemberAsync(SafeFileTree tree, ApplyIntent intent, CancellationToken token)
    {
        var state = DirectoryCaseMemberState.Decode(intent); var pending = index.ReadPendingCasePreservations().SingleOrDefault(row => row.Source.SourceId.SequenceEqual(state.SourceId))
            ?? throw new InvalidDataException("Directory member recovery has no source.");
        var source = OpenCaseSource(tree, pending);
        if (!state.Root.AsSpan().SequenceEqual(tree.Identity) || pending.InstalledFileId is null || !pending.InstalledFileId.AsSpan().SequenceEqual(state.RootFileId)) throw new InvalidDataException("Directory member native root binding changed.");
        var graph = index.ReadDirectoryGraph(state.SourceId);
        var snapshot = DirectorySnapshotBlocks.SnapshotOf(source);
        if (graph is null || !graph.Complete || graph.Retiring || !graph.RootId.AsSpan().SequenceEqual(tree.Identity) || graph.DirectoryPath != source.Path
            || graph.Length != snapshot.EncodedLength || !graph.Hash.AsSpan().SequenceEqual(snapshot.Hash)) throw new InvalidDataException("Directory member graph is unavailable.");
        var cursor = index.ReadDirectoryCaseProgress(state.SourceId, state.RootFileId);
        var next = index.ReadDirectoryGraphMemberPage(state.SourceId, cursor.LastKey, 1).SingleOrDefault();
        if (cursor.Exhausted || next is null || next.PathKey != state.MemberKey || next.RecordLength != state.Link.EncodedLength
            || !next.RecordHash.AsSpan().SequenceEqual(state.Link.Hash)) throw new InvalidDataException("Directory member recovery cursor changed.");
        var row = index.ReadDirectoryCaseMember(state.SourceId, state.RootFileId, state.MemberKey) ?? throw new InvalidDataException("Directory member journal has no ledger.");
        if (row.RecordLength != state.Link.EncodedLength || !row.RecordHash.AsSpan().SequenceEqual(state.Link.Hash) || row.Type != state.Type || row.DestinationPath != state.Destination
            || !row.MemberSourceId.AsSpan().SequenceEqual(state.MemberId) || row.InstalledFileId is not null) throw new InvalidDataException("Directory member ledger changed.");
        using var blocks = new DirectorySnapshotBlocks(rootPath, index, keys, pending.Source.SourceDevice, journal);
        var original = await blocks.ReadMemberAsync(ApplyState.Parent(state.MemberKey), state.Link, MissingMemberMetadata, token).ConfigureAwait(false);
        string relative = string.Join('/', original.Path.Split('/').Skip(source.Path.Count(value => value == '/') + 1));
        if (PathRules.Key(original.Path) != state.MemberKey || original.Type != state.Type || !CaseConflictPolicy.SourceId(original).AsSpan().SequenceEqual(state.MemberId)
            || pending.DestinationPath + '/' + relative != state.Destination) throw new InvalidDataException("Directory member authenticated mapping changed.");
        try
        {
            using var parents = PinDirectoryMemberParent(tree, pending, source, original);
            using var installed = TryMemberDestination(parents.Parent, state, out bool occupied);
            if (state.Prepared && installed is not null && MemberMatches(installed, state, original, token))
            {
                using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
                await index.AccountDirectoryCaseMemberAsync(pending, row with { TouchedTicks = CaseNow }, intent, state.TemporaryId, token).ConfigureAwait(false);
                Signal("case-member-recovered-installed", intent.Id); return;
            }
            using var temporary = TryMemberTemporary(parents.Parent, state);
            if (!occupied && state.Prepared && temporary is not null && PreparedMemberMatches(temporary, state, original, token))
            { await InstallDirectoryMemberAsync(pending, state, row, intent, original, parents.Parent, temporary, token).ConfigureAwait(false); return; }
            if (temporary is not null) await ParkDirectoryMemberAsync(state, intent, original, temporary, token).ConfigureAwait(false);
            await index.AbandonApplyAsync(intent, token).ConfigureAwait(false);
            if (occupied) throw new DirectoryCaseChanged("Directory member recovery destination changed.");
            Signal("case-member-recovered-staging", intent.Id);
        }
        catch (DirectoryCaseChanged)
        {
            await index.AbandonApplyAsync(intent, token).ConfigureAwait(false);
            using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
            await index.ResetDirectoryCaseRootAsync(pending, token).ConfigureAwait(false); Signal("case-member-recovered-root-changed", intent.Id);
        }
        static ValueTask<ReadOnlyMemory<byte>> MissingMemberMetadata(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellation) => throw new IOException("Recover retained member metadata before native mutation.");
    }
    private async Task InstallDirectoryMemberAsync(PendingCasePreservation pending, DirectoryCaseMemberState state, DirectoryCaseMemberRow row, ApplyIntent intent,
        VersionView original, DirectoryLease parent, SafeFileHandle temporary, CancellationToken token)
    {
        if (!PreparedMemberMatches(temporary, state, original, token)) throw new DirectoryCaseChanged("Prepared directory member changed.");
        using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); Signal("case-member-install-ready", intent.Id);
        try { parent.MoveHere(temporary, ApplyState.Name(state.Destination)); }
        catch (Win32Exception error) when (error.NativeErrorCode is 80 or 183) { throw new DirectoryCaseChanged("Directory member installation collided.", error); }
        state.InstalledInThisCall = true; Signal("case-member-installed", intent.Id); FileMetadata.SetSyncedAttributes(temporary, original.Attributes);
        if (original.Type == 0) RandomAccess.FlushToDisk(temporary);
        try { await index.AccountDirectoryCaseMemberAsync(pending, row with { TouchedTicks = CaseNow }, intent, state.TemporaryId, token).ConfigureAwait(false); }
        catch (Exception error)
        {
            journal?.TryWrite(new("apply.case-member-account-failed", "case-member") { SessionId = intent.Id, Level = DiagnosticLevel.Warning,
                ErrorCode = error.GetType().Name, HResult = error.HResult, NativeError = error is SqliteException sqlite ? sqlite.SqliteExtendedErrorCode : null }); throw;
        }
        Signal("case-member-accounted", intent.Id);
    }
    private DirectoryCaseMemberRow MemberRow(DirectoryCaseMemberState state) => new(state.SourceId, state.RootFileId, state.MemberKey, state.Link.Hash, state.Link.EncodedLength,
        state.MemberId, state.Type, state.Destination, null, CaseNow);
    private CaseMemberParent PinDirectoryMemberParent(SafeFileTree tree, PendingCasePreservation pending, VersionView source, VersionView member)
    {
        var leases = new List<DirectoryLease>();
        try
        {
            var root = tree.Directory(pending.DestinationPath!); leases.Add(root);
            if (!root.Identity.AsSpan().SequenceEqual(pending.InstalledFileId)) throw new DirectoryCaseChanged("Directory case root identity changed.");
            string[] parts = member.Path.Split('/'); int depth = source.Path.Count(value => value == '/') + 1; string originalKey = PathRules.Key(source.Path);
            for (int i = depth; i < parts.Length - 1; i++)
            {
                originalKey += '/' + parts[i].ToUpperInvariant(); var parent = index.ReadDirectoryCaseMember(pending.Source.SourceId, pending.InstalledFileId!, originalKey);
                if (parent is not { Type: 1, InstalledFileId: not null }) throw new InvalidDataException("Directory case parent has no installed identity.");
                var next = leases[^1].OpenDirectory(parts[i]); leases.Add(next);
                if (!next.Identity.AsSpan().SequenceEqual(parent.InstalledFileId)) throw new DirectoryCaseChanged("Directory case ancestor identity changed.");
            }
            return new(leases);
        }
        catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
    }
    private async Task ParkDirectoryMemberAsync(DirectoryCaseMemberState state, ApplyIntent intent, VersionView original, SafeFileHandle handle, CancellationToken token)
    {
        if (!HasIdentity(handle, state.TemporaryId)) return; var snapshot = FileSnapshot.Read(handle); bool directory = (snapshot.Attributes & FileAttributes.Directory) != 0;
        if (directory != (state.Type == 1) || !directory && !PublishedBlocks.IsIndependent(handle)) return;
        byte[]? hash = directory ? null : GuardedFile.Hash(handle, token);
        bool protect = directory ? !IsEmptyStagingDirectory(handle, token) : !state.Prepared && snapshot.Length != 0;
        protect |= state.Prepared && !PreparedMemberMatches(handle, state, original, token);
        await index.ParkStagingAsync(intent, new(Guid.NewGuid(), state.Root, state.TemporaryId, state.Destination, state.Temporary, directory, CaseNow,
            snapshot.Length, hash, snapshot.Changed, protect ? StagingObjectState.Protected : StagingObjectState.Parked), token).ConfigureAwait(false);
        Signal(protect ? "case-member-staging-protected" : "case-member-staging-parked", intent.Id);
    }
    private static bool MemberMatches(SafeFileHandle handle, DirectoryCaseMemberState state, VersionView original, CancellationToken token) =>
        original.Type == 1 ? CaseRootMatches(handle, state.TemporaryId) : CaseMatches(handle, state.TemporaryId, original, token);
    private static bool PreparedMemberMatches(SafeFileHandle handle, DirectoryCaseMemberState state, VersionView original, CancellationToken token)
    {
        var snapshot = FileSnapshot.Read(handle);
        return MemberMatches(handle, state, original, token) && (uint)snapshot.Attributes == state.TemporaryAttributes
            && snapshot.LastWrite == original.MtimeTicks - DateTime.FromFileTimeUtc(0).Ticks && (original.Type == 0 || IsEmptyStagingDirectory(handle, token));
    }
    private static SafeFileHandle? TryMemberTemporary(DirectoryLease parent, DirectoryCaseMemberState state)
    {
        try { return state.Type == 1 ? parent.OwnedTemporaryDirectory(ApplyState.Name(state.Temporary), create: false) : parent.OpenOwnedTemporary(ApplyState.Name(state.Temporary)); }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException) { return null; }
    }
    private static SafeFileHandle? TryMemberDestination(DirectoryLease parent, DirectoryCaseMemberState state, out bool occupied)
    {
        if (state.Type == 1) return TryCaseRoot(parent, ApplyState.Name(state.Destination), out occupied);
        occupied = true;
        try { return parent.OpenFile(ApplyState.Name(state.Destination), mutation: true); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { occupied = false; return null; }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException) { return null; }
    }
    private sealed class CaseMemberParent(List<DirectoryLease> leases) : IDisposable
    {
        internal DirectoryLease Parent => leases[^1];
        public void Dispose() { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }
    private sealed class DirectoryCaseChanged(string message, Exception? inner = null) : IOException(message, inner);
}
