using System.ComponentModel;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public sealed partial class FileApplier
{
    internal int DirectoryCaseVerificationWorkLimit { get; init; } = 32;
    private async Task<(bool Completed, int Processed)> CompleteDirectoryCaseAsync(SafeFileTree tree, PendingCasePreservation pending, VersionView source, int budget, CancellationToken token)
    {
        if (budget == 0 || pending.DestinationPath is null || pending.InstalledFileId is null) return (false, 0);
        int processed = 0; Guid session = Guid.NewGuid();
        try
        {
            if (!index.ReadDirectoryCaseProgress(pending.Source.SourceId, pending.InstalledFileId).Exhausted) return (false, 0);
            using var root = tree.Directory(pending.DestinationPath);
            if (!root.Identity.AsSpan().SequenceEqual(pending.InstalledFileId)) throw new DirectoryCaseChanged("Copied root identity changed during completion.");
            var localRoot = index.LocalFiles.Find(pending.DestinationPath);
            if (localRoot is not { Type: 1, Deleted: false, Flags: LocalFileState.None, FileId: not null } || !localRoot.FileId.AsSpan().SequenceEqual(pending.InstalledFileId))
                return await Pending("case-tree-scan-pending").ConfigureAwait(false);
            if (keys.CanWrite)
            {
                if (!CaseDirectoryPublished(localRoot)) return await Pending("case-tree-root-publication-pending").ConfigureAwait(false);
                var cache = index.ReadDirectoryPublicationCache(pending.DestinationPath);
                if (cache is null || cache.InputEpoch != index.ReadDirectoryCaptureEpoch() || !cache.RootId.AsSpan().SequenceEqual(tree.Identity)
                    || !cache.NativeId.AsSpan().SequenceEqual(pending.InstalledFileId)
                    || !cache.RecordSourceId.AsSpan().SequenceEqual(CaseConflictPolicy.SourceId(VersionView.Open(localRoot.Record!, keys, ownDevice)))
                    || index.HasPendingDirectoryPublication(pending.DestinationPath, includeDescendants: true))
                    return await Pending("case-tree-snapshot-pending").ConfigureAwait(false);
            }
            var proof = await index.BeginDirectoryCaseVerificationAsync(pending, localRoot.LocalSequence, keys.CanWrite, token).ConfigureAwait(false);
            if (!proof.Complete)
            {
                int collision = 1;
                while (collision <= 1000 && ConflictNames.CaseSourcePath(source.Path, pending.Source.SourceId, collision) != pending.DestinationPath) collision++;
                if (collision > 1000) throw new InvalidDataException("Case completion destination changed.");
                var page = await new DirectoryCaseProjection(rootPath, index, keys, pending.Source.SourceDevice, journal)
                    .ReadUnderGateAsync(source.Record, MissingCompletionMetadata, proof.LastKey, collision, budget, token: token).ConfigureAwait(false);
                var observations = new List<DirectoryCaseObservation>();
                foreach (var projected in page.Members)
                {
                    token.ThrowIfCancellationRequested(); processed++;
                    var member = index.ReadDirectoryCaseMember(pending.Source.SourceId, pending.InstalledFileId, projected.Original.Path);
                    if (member?.InstalledFileId is null || member.DestinationPath != projected.Destination || member.Type != projected.Original.Type
                        || !member.MemberSourceId.AsSpan().SequenceEqual(CaseConflictPolicy.SourceId(projected.Original))) throw new InvalidDataException("Installed case member proof changed.");
                    using var parents = PinDirectoryMemberParent(tree, pending, source, projected.Original);
                    if (member.Type == 1)
                    {
                        using var directory = parents.Parent.OpenDirectory(ApplyState.Name(projected.Destination));
                        if (!directory.Identity.AsSpan().SequenceEqual(member.InstalledFileId)) throw new DirectoryCaseChanged("Copied member directory changed.");
                    }
                    else
                    {
                        using var file = parents.Parent.OpenFile(ApplyState.Name(projected.Destination), mutation: true);
                        if (!CaseMatches(file, member.InstalledFileId, projected.Original, token)) throw new DirectoryCaseChanged("Copied member bytes changed.");
                    }
                    var local = index.LocalFiles.Find(projected.Destination);
                    if (local is null || local.Type != member.Type || local.Deleted || local.Flags != LocalFileState.None || local.FileId is null
                        || !local.FileId.AsSpan().SequenceEqual(member.InstalledFileId)
                        || local.Type == 0 && (local.Size != projected.Original.Size || local.FileHash is null || !local.FileHash.AsSpan().SequenceEqual(projected.Original.FileHash)))
                        return await Pending("case-tree-member-scan-pending").ConfigureAwait(false);
                    if (keys.CanWrite)
                    {
                        if (local.Record is null || local.Type == 0 && !local.ContentReady || local.Type == 1 && !CaseDirectoryPublished(local))
                            return await Pending("case-tree-member-publication-pending").ConfigureAwait(false);
                        var publication = VersionView.Open(local.Record, keys, ownDevice);
                        if (publication.Path != local.Path || publication.Type != local.Type || publication.Deleted
                            || publication.Type == 0 && (publication.Size != local.Size || !publication.FileHash.SequenceEqual(local.FileHash))) throw new InvalidDataException("Copied member publication changed.");
                    }
                    observations.Add(new(PathRules.Key(projected.Original.Path), local.LocalSequence, member.InstalledFileId));
                }
                proof = await index.AdvanceDirectoryCaseVerificationAsync(pending, proof, observations, !page.More, token).ConfigureAwait(false);
                Signal("case-tree-verified-prefix", session);
            }
            if (!proof.Complete) return (false, processed);
            using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
            long now = CaseNow; long? until = keys.CanWrite ? null : Math.Min(DateTime.MaxValue.Ticks, now + (long)settings.ArchiveDays * TimeSpan.TicksPerDay);
            Signal("case-tree-before-completion", session);
            await index.CompleteDirectoryCasePreservationAsync(pending, proof, now, until, token).ConfigureAwait(false);
            Signal("case-tree-completed", session); return (true, Math.Max(1, processed));
        }
        catch (Exception error) when (error is DirectoryCaseChanged || error is Win32Exception { NativeErrorCode: 2 or 3 or 267 } || error is FileNotFoundException or DirectoryNotFoundException)
        {
            using var references = await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false);
            await index.ResetDirectoryCaseRootAsync(pending, token).ConfigureAwait(false); Signal("case-tree-visible-changed", session); return (false, budget);
        }
        catch (SqliteException error)
        {
            journal?.TryWrite(new("apply.case-tree-transaction-failed", "case-tree") { SessionId = session, Level = DiagnosticLevel.Warning,
                ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult, NativeError = error.SqliteExtendedErrorCode }); throw;
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        { StagingProbeFailed("case-tree-verification-deferred", session, error); return await Pending(error.GetType().Name).ConfigureAwait(false); }

        async Task<(bool, int)> Pending(string code) { await RetryCaseAsync(pending, code, token).ConfigureAwait(false); return (false, budget); }
        static ValueTask<ReadOnlyMemory<byte>> MissingCompletionMetadata(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellation) => throw new IOException("Retain authenticated case metadata before completion.");
    }
    private bool CaseDirectoryPublished(LocalFileRow row)
    {
        if (row.Record is null) return false; var version = VersionView.Open(row.Record, keys, ownDevice); var body = version.ExportBody();
        return version.Path == row.Path && version.Type == 1 && !version.Deleted && body.DirectorySnapshotHash.Length == 32 && body.DirectorySnapshotLength != 0;
    }
}
