using System.ComponentModel;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public sealed partial class FileApplier
{
    private async Task<(bool Created, int GraphWork, int NativeWork)> PrepareDirectoryCaseRootAsync(SafeFileTree tree, PendingCasePreservation pending, VersionView source,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, int graphBudget, int memberBudget, CancellationToken token)
    {
        int processed = 0, nativeWork = 0;
        using var routes = new CaseBlockFetch(this, pending, source, fetch, token);
        try
        {
            FileMetadata.ValidateMtime(source.MtimeTicks);
            if (source.ExportBody().DirectorySnapshotHash.Length == 0)
            {
                await RetryCaseAsync(pending, "case-root-snapshot-unavailable", token).ConfigureAwait(false);
                return (false, 0, 0);
            }
            // An exception can follow already committed graph progress. Charge
            // the full offered budget until the call returns its exact count,
            // so another failing source cannot exceed the shared pass bound.
            processed = graphBudget;
            var graph = await new DirectorySnapshotGraph(rootPath, index, keys, pending.Source.SourceDevice, journal) { Checkpoint = Checkpoint }.AdvanceUnderGateAsync(source.Record,
                routes.FetchAsync, graphBudget, token).ConfigureAwait(false); processed = graph.Processed;
            if (!graph.Complete)
            {
                Signal("case-root-graph-pending", Guid.NewGuid());
                if (processed == 0 && graphBudget > 0) await RetryCaseAsync(pending, "case-root-graph-waiting", token).ConfigureAwait(false);
                return (false, processed, 0);
            }
            if (pending.DestinationPath is not null && pending.InstalledFileId is not null)
            {
                bool valid;
                using (var knownParent = tree.Directory(ApplyState.Parent(pending.DestinationPath)))
                using (var known = TryCaseRoot(knownParent, ApplyState.Name(pending.DestinationPath), out _)) valid = known is not null && CaseRootMatches(known, pending.InstalledFileId);
                if (valid)
                {
                    // Failure may follow successful member accounting. Charge
                    // the offered work until exact progress is returned.
                    nativeWork = memberBudget;
                    var members = await PrepareDirectoryMembersAsync(tree, pending, source, routes.FetchAsync, memberBudget, token).ConfigureAwait(false);
                    return (members.Changed, processed, members.Processed);
                }
            }
            if (memberBudget == 0) return (false, processed, 0);
            nativeWork = 1;
            using var parent = tree.Directory(ApplyState.Parent(source.Path)); string destination = ""; int collision;
            for (collision = 1; collision <= 1000; collision++)
            {
                token.ThrowIfCancellationRequested(); destination = ConflictNames.CaseSourcePath(source.Path, pending.Source.SourceId, collision);
                using var occupant = TryCaseRoot(parent, ApplyState.Name(destination), out bool occupied);
                if (!occupied && occupant is null && index.LocalFiles.Find(destination) is null) break;
            }
            if (collision > 1000) throw new IOException("Directory case root collision budget exceeded.");
            Guid session = Guid.NewGuid();
            using var temporary = OpenTemporary(tree, parent, destination, out string path, out byte[] identity, directory: true, session: session, cancellationToken: token);
            var state = new DirectoryCaseRootState { Root = tree.Identity.ToArray(), Source = source, SourceId = pending.Source.SourceId,
                Temporary = path, TemporaryId = identity, Collision = collision };
            var intent = new ApplyIntent(session, destination, null, state.Encode());
            using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
                await index.ReserveCaseCopyAsync(pending, intent, destination, token).ConfigureAwait(false);
            Signal("case-root-reserved", session);
            try
            {
                if (!IsEmptyStagingDirectory(temporary, token)) throw new IOException("Directory case staging is not empty.");
                FileMetadata.PrepareNewFile(parent, temporary); FileMetadata.SetMtime(temporary, source.MtimeTicks);
                state.TemporaryAttributes = (uint)FileSnapshot.Read(temporary).Attributes; state.Prepared = true;
                intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false); Signal("case-root-prepared", session);
                return (await InstallDirectoryCaseRootAsync(tree, pending, state, intent, parent, temporary, token).ConfigureAwait(false), processed, 1);
            }
            catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or CryptographicException)
            {
                if (state.InstalledInThisCall) throw;
                await ParkDirectoryCaseRootAsync(state, intent, temporary, token).ConfigureAwait(false);
                await index.AbandonApplyAsync(intent, token).ConfigureAwait(false); throw;
            }


        }
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException or CryptographicException or ArgumentOutOfRangeException)
        {
            if (index.ReadApplyIntents().Any()) throw;
            StagingProbeFailed("case-root-deferred", Guid.NewGuid(), error); await RetryCaseAsync(pending, error.GetType().Name, token).ConfigureAwait(false);
            return (false, processed, nativeWork);
        }
    }
    private async Task RecoverDirectoryCaseRootAsync(SafeFileTree tree, ApplyIntent intent, CancellationToken token)
    {
        var state = DirectoryCaseRootState.Decode(intent, keys);
        var pending = index.ReadPendingCasePreservations().SingleOrDefault(row => row.Source.SourceId.AsSpan().SequenceEqual(state.SourceId))
            ?? throw new InvalidDataException("Directory case recovery has no pending source.");
        var source = OpenCaseSource(tree, pending);
        if (!state.Root.AsSpan().SequenceEqual(tree.Identity) || pending.DestinationPath != state.Destination
            || !source.Record.Body.SequenceEqual(state.Source.Record.Body) || !source.Record.Signature.SequenceEqual(state.Source.Record.Signature))
            throw new InvalidDataException("Directory case root recovery binding changed.");
        RequireCaseRootGraph(tree, state);
        using var parent = tree.Directory(ApplyState.Parent(state.Destination));
        using var installed = TryCaseRoot(parent, ApplyState.Name(state.Destination), out bool occupied);
        if (state.Prepared && installed is not null && CaseRootMatches(installed, state.TemporaryId))
        {
            using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
            if (PreparedCaseRootMatches(installed, state, token)) FileMetadata.SetSyncedAttributes(installed, source.Attributes);
            await index.AccountInstalledCaseCopyAsync(pending, intent, state.Destination, state.TemporaryId, token).ConfigureAwait(false);
            Signal("case-root-recovered-installed", intent.Id); return;
        }
        using var temporary = TryOwnedCaseRoot(parent, state);
        if (!occupied && state.Prepared && temporary is not null && PreparedCaseRootMatches(temporary, state, token))
        { await InstallDirectoryCaseRootAsync(tree, pending, state, intent, parent, temporary, token).ConfigureAwait(false); return; }
        if (temporary is not null) await ParkDirectoryCaseRootAsync(state, intent, temporary, token).ConfigureAwait(false);
        await index.AbandonApplyAsync(intent, token).ConfigureAwait(false); Signal("case-root-recovered-preserved", intent.Id);
    }
    private async Task<bool> InstallDirectoryCaseRootAsync(SafeFileTree tree, PendingCasePreservation pending, DirectoryCaseRootState state,
        ApplyIntent intent, DirectoryLease parent, SafeFileHandle temporary, CancellationToken token)
    {
        RequireCaseRootGraph(tree, state);
        if (!PreparedCaseRootMatches(temporary, state, token)) throw new IOException("Prepared directory case root changed.");
        using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        Signal("case-root-install-ready", intent.Id);
        try { parent.MoveHere(temporary, ApplyState.Name(state.Destination)); }
        catch (Win32Exception error) when (error.NativeErrorCode is 80 or 183)
        {
            await ParkDirectoryCaseRootAsync(state, intent, temporary, token).ConfigureAwait(false); await index.AbandonApplyAsync(intent, token).ConfigureAwait(false);
            Signal("case-root-collision", intent.Id); return false;
        }
        state.InstalledInThisCall = true; Signal("case-root-installed", intent.Id);
        FileMetadata.SetSyncedAttributes(temporary, state.Source.Attributes);
        try { await index.AccountInstalledCaseCopyAsync(pending, intent, state.Destination, state.TemporaryId, token).ConfigureAwait(false); }
        catch (Exception error)
        {
            journal?.TryWrite(new("apply.case-root-account-failed", "case-root") { SessionId = intent.Id, Level = DiagnosticLevel.Warning,
                ErrorCode = error.GetType().Name, HResult = error.HResult,
                NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null } });
            throw;
        }
        Signal("case-root-accounted", intent.Id); return true;
    }
    private void RequireCaseRootGraph(SafeFileTree tree, DirectoryCaseRootState state)
    {
        var graph = index.ReadDirectoryGraph(state.SourceId); var snapshot = DirectorySnapshotBlocks.SnapshotOf(state.Source);
        if (graph is null || !graph.Complete || graph.Retiring || !graph.RootId.AsSpan().SequenceEqual(tree.Identity) || graph.DirectoryPath != state.Source.Path
            || !graph.Hash.AsSpan().SequenceEqual(snapshot.Hash) || graph.Length != snapshot.EncodedLength) throw new InvalidDataException("Directory case root graph is not complete or bound.");
    }
    private async Task ParkDirectoryCaseRootAsync(DirectoryCaseRootState state, ApplyIntent intent, SafeFileHandle handle, CancellationToken token)
    {
        if (!CaseRootMatches(handle, state.TemporaryId)) return;
        var snapshot = FileSnapshot.Read(handle);
        bool protect = !IsEmptyStagingDirectory(handle, token) || state.Prepared && !PreparedCaseRootMatches(handle, state, token);
        var row = new StagedObject(Guid.NewGuid(), state.Root, state.TemporaryId, state.Destination, state.Temporary, true, CaseNow,
            snapshot.Length, null, snapshot.Changed, protect ? StagingObjectState.Protected : StagingObjectState.Parked);
        await index.ParkStagingAsync(intent, row, token).ConfigureAwait(false); Signal(protect ? "case-root-staging-protected" : "case-root-staging-parked", intent.Id);
    }
    private static bool CaseRootMatches(SafeFileHandle handle, byte[] identity) => HasIdentity(handle, identity) && (FileSnapshot.Read(handle).Attributes & FileAttributes.Directory) != 0;
    private static bool PreparedCaseRootMatches(SafeFileHandle handle, DirectoryCaseRootState state, CancellationToken token)
    {
        var snapshot = FileSnapshot.Read(handle);
        return CaseRootMatches(handle, state.TemporaryId) && (uint)snapshot.Attributes == state.TemporaryAttributes
            && snapshot.LastWrite == state.Source.MtimeTicks - DateTime.FromFileTimeUtc(0).Ticks && IsEmptyStagingDirectory(handle, token);
    }
    private static SafeFileHandle? TryCaseRoot(DirectoryLease parent, string name, out bool occupied)
    {
        occupied = true;
        try { return parent.OpenNode(name); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { occupied = false; return null; }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException) { return null; }
    }
    private static SafeFileHandle? TryOwnedCaseRoot(DirectoryLease parent, DirectoryCaseRootState state)
    {
        try { return parent.OwnedTemporaryDirectory(ApplyState.Name(state.Temporary), create: false); }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException) { return null; }
    }
}
