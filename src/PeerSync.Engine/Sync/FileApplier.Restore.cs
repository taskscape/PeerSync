using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

public sealed partial class FileApplier
{
    internal Task<FileApplyOutcome> RestoreArchiveAsync(long archiveId, string? destination, bool copyOnly, CancellationToken token) =>
        ObserveAsync(copyOnly ? "restore-copy" : "restore", session => RestoreCoreAsync(archiveId, destination, copyOnly, session, token));

    private async Task<FileApplyOutcome> RestoreCoreAsync(long archiveId, string? destination, bool copyOnly, Guid session, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(archiveId);
        if (copyOnly) ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        using var operation = await FolderOperationGate.AcquireAsync(index, token).ConfigureAwait(false);
        if (index.ReadApplyIntents().Any()) throw new IOException("Recover pending application before restoring.");
        using var tree = new SafeFileTree(rootPath); ValidateRoot(tree);
        var sourceRow = index.Archive.Find(archiveId) ?? throw new FileNotFoundException("Archived version is no longer available.");
        string path = copyOnly ? destination! : sourceRow.OriginalPath;
        string key = PathRules.Key(path);
        if (sourceRow.FileHash is not { Length: 32 }) throw new InvalidDataException("Archived version has no verified hash.");
        using var sourceParent = tree.Directory(ApplyState.Parent(sourceRow.ArchivePath), privatePath: true);
        using var source = sourceParent.OpenFile(ApplyState.Name(sourceRow.ArchivePath), mutation: true);
        var sourceSnapshot = FileSnapshot.Read(source);
        if (sourceSnapshot.Length != sourceRow.Size || !Hash(source, token).AsSpan().SequenceEqual(sourceRow.FileHash))
            throw new CryptographicException("Archived content does not match its recorded version.");
        var restore = new LocalRestoreData(path, sourceRow.Size, clock.GetUtcNow().UtcTicks,
            (uint)(sourceSnapshot.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden)), sourceRow.FileHash);
        using var parent = tree.Directory(ApplyState.Parent(path), create: true);
        if (copyOnly)
        {
            using var occupied = TryOpen(parent, ApplyState.Name(path));
            if (occupied is not null) { Signal("restore-copy-blocked", session); return FileApplyOutcome.Blocked; }
        }
        var known = index.LocalFiles.Find(key);
        using var temporary = OpenTemporary(tree, parent, path, out string temporaryPath, out byte[] temporaryId);
        var state = new ApplyState { Root = tree.Identity.ToArray(), Restore = restore, Vector = new Dictionary<ulong, ulong>(),
            Temporary = temporaryPath, TemporaryId = temporaryId };
        var intent = new ApplyIntent(session, path, known?.LocalSequence, state.Encode());
        await index.ReserveApplyAsync(intent, token).ConfigureAwait(false);
        Signal("staging", intent.Id);
        await CopyRestoreAsync(source, temporary, sourceRow.Size, intent.Id, token).ConfigureAwait(false);
        if (!Hash(temporary, token).AsSpan().SequenceEqual(state.FileHash)) throw new CryptographicException("Restoration staging verification failed.");
        using var original = TryOpen(parent, ApplyState.Name(path));
        if (copyOnly && original is not null)
        {
            await ParkAsync(state, token).ConfigureAwait(false);
            await index.DeferApplyAsync(intent, [], token).ConfigureAwait(false);
            Signal("restore-copy-blocked", session); return FileApplyOutcome.Blocked;
        }
        if (original is not null)
        {
            CaptureOriginal(state, original, known?.Path ?? path, known, token);
            state.ArchivedTicks = clock.GetUtcNow().UtcTicks;
            var fate = settings.KeepBothCopies || state.OriginalSize > settings.ArchiveMaxFileBytes ? LoserFate.ConflictCopy : LoserFate.Archive;
            state.Retained = RetainedPath(state, fate, 1);
            FileMetadata.CopyReplacementMetadata(original, temporary);
        }
        else FileMetadata.PrepareNewFile(parent, temporary);
        Signal("metadata-prepared", intent.Id);
        FileMetadata.SetMtime(temporary, restore.MtimeTicks); RandomAccess.FlushToDisk(temporary);
        state.TemporaryAttributes = (uint)FileSnapshot.Read(temporary).Attributes;
        state.Prepared = true;
        intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false);
        Signal("prepared", intent.Id);
        return await FinishAsync(tree, state, intent, original, temporary, token).ConfigureAwait(false);
    }

    private void CaptureOriginal(ApplyState state, SafeFileHandle original, string path, LocalFileRow? known, CancellationToken token)
    {
        var snapshot = FileSnapshot.Read(original);
        bool directory = (snapshot.Attributes & FileAttributes.Directory) != 0;
        state.Original = path; state.OriginalId = Identity(snapshot); state.OriginalHash = directory ? [] : Hash(original, token);
        bool tracked = directory ? known is { Type: 1, FileId: not null } && known.FileId.AsSpan().SequenceEqual(state.OriginalId)
            : known?.FileHash is { } hash && hash.AsSpan().SequenceEqual(state.OriginalHash);
        state.OriginalDevice = tracked && known!.Record is { } record
            ? VersionView.Open(record, keys, ownDevice).ModifiedBy.ToArray() : ownDevice.ToArray();
        state.OriginalSize = snapshot.Length; state.OriginalMtime = DateTime.FromFileTimeUtc(snapshot.LastWrite).Ticks;
        state.OriginalAttributes = (uint)snapshot.Attributes;
    }

    private async Task CopyRestoreAsync(SafeFileHandle source, SafeFileHandle target, long size, Guid session, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var watch = Stopwatch.StartNew(); TimeSpan nextProgress = TimeSpan.FromSeconds(5); long offset = 0;
            while (offset < size)
            {
                token.ThrowIfCancellationRequested();
                int read = await RandomAccess.ReadAsync(source, buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - offset)), offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                await RandomAccess.WriteAsync(target, buffer.AsMemory(0, read), offset, token).ConfigureAwait(false); offset += read;
                if (watch.Elapsed < nextProgress) continue;
                journal?.TryWrite(new("restore.progress", "restore") { SessionId = session, Bytes = offset,
                    ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds, Level = DiagnosticLevel.Debug });
                nextProgress = watch.Elapsed + TimeSpan.FromSeconds(5);
            }
            token.ThrowIfCancellationRequested(); RandomAccess.SetLength(target, size); RandomAccess.FlushToDisk(target);
            Signal("restore-copied", session);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
