using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Archive;

// Caller holds the shared folder gate. This never opens/mutates the current
// user target; it reconstructs the previously published, authenticated bytes.
internal sealed class SnapshotHistory(FolderIndex index, FolderKeys keys, byte[] device,
    FolderSettings settings, DiagnosticJournal? journal = null, TimeProvider? clock = null)
{
    internal Action<string>? Checkpoint { get; init; }

    // The caller authenticates the marker and owns the folder gate. Reuse has
    // native identity proof; reconstruction only reads immutable source blocks.
    internal async Task BootstrapMarkerAsync(SafeFileTree tree, MetaRow marker, HistoryState retained, CancellationToken token)
    {
        if (!retained.Root.AsSpan().SequenceEqual(tree.Identity)) throw new IOException("Historical root changed.");
        if (VerifyLocation(tree, retained, token) && (retained.PublicCopy || index.CanRetainHistoryArchive(Protection(retained))))
        {
            using var parent = tree.Directory(ApplyState.Parent(retained.Destination), privatePath: !retained.PublicCopy);
            using var file = parent.OpenFile(ApplyState.Name(retained.Destination), mutation: true); Verify(file, retained, token);
            if (!await index.AccountExistingHistoryAsync(marker, Protection(retained), token).ConfigureAwait(false))
                throw new InvalidOperationException("History changed while bootstrapping retention.");
            Signal("accounted", Guid.NewGuid()); return;
        }
        var state = new HistoryState { Root = tree.Identity.ToArray(), Source = retained.Source, PublicCopy = retained.PublicCopy,
            ArchivedTicks = retained.ArchivedTicks, Temporary = "" };
        state.Temporary = TemporaryFor(state); var current = index.LocalFiles.Find(retained.Source.Path);
        var intent = new ApplyIntent(Guid.NewGuid(), retained.Source.Path, current?.LocalSequence, state.Encode());
        await index.ReserveApplyAsync(intent, token).ConfigureAwait(false); Signal("reserved", intent.Id);
        await RecoverIntentAsync(tree, intent, token).ConfigureAwait(false);
    }

    internal async Task<bool> EnsureAsync(SafeFileTree tree, LocalFileRow previous, LocalFileRow next, string? retainedCopy, CancellationToken token)
    {
        if (previous is not { Type: 0, Deleted: false, Record: not null }
            || !next.Deleted && next.Type == 0 && previous.FileHash is not null && previous.FileHash.AsSpan().SequenceEqual(next.FileHash)) return false;
        var source = VersionView.Open(previous.Record, keys, device);
        if (source.Type != 0 || source.Deleted || PathRules.Key(source.Path) != PathRules.Key(previous.Path)
            || source.Size != previous.Size || !source.FileHash.SequenceEqual(previous.FileHash))
            throw new InvalidDataException("Historical source does not match its indexed version.");
        FileMetadata.ValidateMtime(source.MtimeTicks);
        bool publicCopy = settings.KeepBothCopies || source.Size > settings.ArchiveMaxFileBytes;
        var state = new HistoryState { Root = tree.Identity.ToArray(), Source = source, PublicCopy = publicCopy,
            ArchivedTicks = (clock ?? TimeProvider.System).GetUtcNow().UtcTicks, Temporary = "" };
        if (index.Meta.Find(state.MarkerKey)?.Value is { } marker)
        {
            var retained = HistoryState.Decode(new(Guid.NewGuid(), source.Path, null, marker), keys);
            if (!retained.Root.AsSpan().SequenceEqual(tree.Identity) || retained.MarkerKey != state.MarkerKey || !retained.Prepared)
                throw new InvalidDataException("History marker does not match its source/root.");
            if ((!publicCopy || retained.PublicCopy) && VerifyLocation(tree, retained, token)
                && (retained.PublicCopy || index.CanRetainHistoryArchive(Protection(retained))))
            {
                using var parent = tree.Directory(ApplyState.Parent(retained.Destination), privatePath: !retained.PublicCopy);
                using var file = parent.OpenFile(ApplyState.Name(retained.Destination), mutation: true); Verify(file, retained, token);
                if (!await index.AccountExistingHistoryAsync(new(retained.MarkerKey, marker), Protection(retained), token).ConfigureAwait(false))
                    throw new InvalidOperationException("History changed while accounting for retention.");
                return retained.PublicCopy;
            }
        }
        if (retainedCopy is not null && await ReuseAsync(tree, source, previous.LocalSequence, retainedCopy, publicCopy: true, token).ConfigureAwait(false)) return true;
        if (!publicCopy)
            foreach (var archive in index.ArchivesForVersion(source.Path, source.FileHash.ToArray(), source.Size))
                if (await ReuseAsync(tree, source, previous.LocalSequence, archive.ArchivePath, publicCopy: false, token).ConfigureAwait(false)) return false;
        state.Temporary = TemporaryFor(state);
        var intent = new ApplyIntent(Guid.NewGuid(), source.Path, previous.LocalSequence, state.Encode());
        await index.ReserveApplyAsync(intent, token).ConfigureAwait(false); Signal("reserved", intent.Id);
        await RecoverIntentAsync(tree, intent, token).ConfigureAwait(false); return publicCopy;
    }

    internal async Task RecoverIntentAsync(SafeFileTree tree, ApplyIntent intent, CancellationToken token)
    {
        try
        {
            var state = HistoryState.Decode(intent, keys);
            if (!state.Root.AsSpan().SequenceEqual(tree.Identity)) throw new IOException("History root identity changed.");
            using var parent = tree.Directory(ApplyState.Parent(state.Destination), create: true, privatePath: !state.PublicCopy);
            if (state.Prepared && VerifyLocation(tree, state, token) && CanRetain(state))
            {
                using var installed = parent.OpenFile(ApplyState.Name(state.Destination), mutation: true);
                Verify(installed, state, token); await CompleteAsync(state, intent, installed, flushData: false, token).ConfigureAwait(false); return;
            }
            if (state.ExistingDestination.Length != 0)
            {
                state.ExistingDestination = ""; state.Prepared = false; state.TemporaryId = []; state.Temporary = TemporaryFor(state);
                intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false);
                await RecoverIntentAsync(tree, intent, token).ConfigureAwait(false); return;
            }
            SafeFileHandle? file = null;
            try
            {
                if (state.TemporaryId.Length != 0)
                {
                    try { file = parent.OpenOwnedTemporary(ApplyState.Name(state.Temporary)); }
                    catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
                    if (file is not null && (!Identity(file).AsSpan().SequenceEqual(state.TemporaryId)
                        || state.Prepared && !Matches(file, state, token))) { file.Dispose(); file = null; }
                }
                if (file is null)
                {
                    // Unknown/replaced prepared objects stay untouched. Reserve
                    // another exclusive name before writing a fresh reconstruction.
                    state.Prepared = false; state.TemporaryId = []; state.Temporary = TemporaryFor(state);
                    intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false);
                    file = parent.CreateFile(ApplyState.Name(state.Temporary), temporary: true);
                    Signal("created", intent.Id);
                    state.TemporaryId = Identity(file);
                    intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false);
                    Signal("owned", intent.Id);
                }
                if (!state.Prepared)
                {
                    using var blocks = new PublishedBlocks(tree); using var whole = Blake3.Hasher.New();
                    var chunks = state.Source.ExportBody().Chunks; long offset = 0;
                    var watch = Stopwatch.StartNew(); TimeSpan nextProgress = TimeSpan.FromSeconds(5); int copied = 0;
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(chunks.Count == 0 ? 1 : checked((int)chunks.Max(c => c.Length)));
                    try
                    {
                        foreach (var chunk in chunks)
                        {
                            token.ThrowIfCancellationRequested(); var bytes = buffer.AsSpan(0, checked((int)chunk.Length));
                            if (!blocks.TryRead(chunk.Hash.Span, bytes, token)) throw new IOException("Historical content is unavailable.");
                            whole.Update(bytes);
                            for (int written = 0; written < bytes.Length; written += 65536)
                            {
                                token.ThrowIfCancellationRequested();
                                RandomAccess.Write(file, bytes.Slice(written, Math.Min(65536, bytes.Length - written)), offset + written);
                            }
                            offset += bytes.Length; copied++;
                            if (watch.Elapsed >= nextProgress)
                            {
                                journal?.TryWrite(new("history.copy_progress", "history") { SessionId = intent.Id, Level = DiagnosticLevel.Debug,
                                    Bytes = offset, ItemsVisited = copied, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
                                nextProgress = watch.Elapsed + TimeSpan.FromSeconds(5);
                            }
                        }
                        if (offset != state.Source.Size || !whole.Finalize().AsSpan().SequenceEqual(state.Source.FileHash))
                            throw new CryptographicException("Historical whole-file verification failed.");
                        RandomAccess.SetLength(file, offset); FileMetadata.SetMtime(file, state.Source.MtimeTicks); RandomAccess.FlushToDisk(file);
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
                    Signal("copied", intent.Id); state.Prepared = true;
                    intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false); Signal("prepared", intent.Id);
                }
                Verify(file, state, token);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!state.PublicCopy && index.ArchivePathExists(state.Destination))
                    {
                        if (++state.Collision > 1000) throw new IOException("History collision budget exceeded.");
                        intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false);
                        continue;
                    }
                    try { parent.MoveHere(file, ApplyState.Name(state.Destination)); break; }
                    catch (Win32Exception error) when (error.NativeErrorCode is 80 or 183)
                    {
                        if (++state.Collision > 1000) throw new IOException("History collision budget exceeded.");
                        intent = await index.RewriteApplyAsync(intent, state.Encode(), token).ConfigureAwait(false);
                    }
                }
                Signal("installed", intent.Id);
                await CompleteAsync(state, intent, file, flushData: true, token).ConfigureAwait(false);
            }
            finally { file?.Dispose(); }
        }
        catch (Exception error)
        {
            try { await index.WithholdApplySourceAsync(intent, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception withholdingError) when (withholdingError is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or SqliteException)
            { journal?.TryWrite(new("history.withhold_failed", "history") { SessionId = intent.Id, Level = DiagnosticLevel.Warning,
                ErrorCode = Code(withholdingError), HResult = withholdingError.HResult, NativeError = NativeCode(withholdingError) }); }
            journal?.TryWrite(new("history.failed", "history") { SessionId = intent.Id, Level = DiagnosticLevel.Warning,
                ErrorCode = Code(error), HResult = error.HResult, NativeError = NativeCode(error) });
            throw;
        }
    }

    private async Task CompleteAsync(HistoryState state, ApplyIntent intent, SafeFileHandle file, bool flushData, CancellationToken token)
    {
        Verify(file, state, token);
        if (state.ExistingDestination.Length == 0)
        {
            FileMetadata.SetMtime(file, state.Source.MtimeTicks); FileMetadata.SetSyncedAttributes(file, state.Source.Attributes);
            if (flushData) RandomAccess.FlushToDisk(file);
        }
        ArchiveRow? archive = state.PublicCopy || state.ExistingDestination.Length != 0 ? null
            : new(0, state.Source.Path, state.Destination, state.ArchivedTicks, state.Source.Size, state.Source.FileHash.ToArray());
        Signal("before-commit", intent.Id);
        await index.CommitProtectedHistoryAsync(intent, new(state.MarkerKey, state.Encode()), archive, Protection(state), token).ConfigureAwait(false);
        Signal("committed", intent.Id);
    }

    private async Task<bool> ReuseAsync(SafeFileTree tree, VersionView source, long sequence, string destination, bool publicCopy, CancellationToken token)
    {
        try
        {
            using var parent = tree.Directory(ApplyState.Parent(destination), privatePath: !publicCopy);
            using var file = parent.OpenFile(ApplyState.Name(destination), mutation: true);
            var state = new HistoryState { Root = tree.Identity.ToArray(), Source = source, PublicCopy = publicCopy,
                ArchivedTicks = (clock ?? TimeProvider.System).GetUtcNow().UtcTicks, ExistingDestination = destination,
                Prepared = true, TemporaryId = Identity(file), Temporary = ApplyState.Join(ApplyState.Parent(destination), Guid.NewGuid().ToString("N") + ".!psp") };
            if (!Matches(file, state, token)) return false;
            if (!CanRetain(state)) return false;
            var intent = new ApplyIntent(Guid.NewGuid(), source.Path, sequence, state.Encode());
            await index.ReserveApplyAsync(intent, token).ConfigureAwait(false); Signal("reserved", intent.Id);
            await CompleteAsync(state, intent, file, flushData: false, token).ConfigureAwait(false); return true;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return false; }
    }

    private static bool VerifyLocation(SafeFileTree tree, HistoryState state, CancellationToken token)
    {
        try
        {
            using var parent = tree.Directory(ApplyState.Parent(state.Destination), privatePath: !state.PublicCopy);
            using (var node = parent.OpenMetadataNode(ApplyState.Name(state.Destination)))
                if ((FileSnapshot.Read(node).Attributes & FileAttributes.Directory) != 0 || !Identity(node).AsSpan().SequenceEqual(state.TemporaryId)) return false;
            using var file = parent.OpenFile(ApplyState.Name(state.Destination), mutation: true);
            return Identity(file).AsSpan().SequenceEqual(state.TemporaryId) && Matches(file, state, token);
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return false; }
    }
    private bool CanRetain(HistoryState state) => state.PublicCopy || state.ExistingDestination.Length == 0 || index.CanRetainHistoryArchive(Protection(state));
    private static HistoryArchiveProtection Protection(HistoryState state) => new(state.Root, state.TemporaryId, state.Source.Path,
        state.Destination, state.Source.Size, state.Source.FileHash.ToArray(), state.ArchivedTicks, state.PublicCopy);
    private static void Verify(SafeFileHandle file, HistoryState state, CancellationToken token)
    {
        if (!Identity(file).AsSpan().SequenceEqual(state.TemporaryId) || !Matches(file, state, token))
            throw new IOException("Owned historical reconstruction changed.");
    }
    private static bool Matches(SafeFileHandle file, HistoryState state, CancellationToken token)
    {
        return PublishedBlocks.IsIndependent(file) && RandomAccess.GetLength(file) == state.Source.Size
            && GuardedFile.Hash(file, token).AsSpan().SequenceEqual(state.Source.FileHash);
    }
    private static byte[] Identity(SafeFileHandle file)
    {
        var snapshot = FileSnapshot.Read(file); byte[] id = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(id, snapshot.IdLow); BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(8), snapshot.IdHigh); return id;
    }
    private static string TemporaryFor(HistoryState state) => ApplyState.Join(ApplyState.Parent(state.Destination), Guid.NewGuid().ToString("N") + ".!psp");
    private static string Code(Exception error) => error is SqliteException sqlite ? sqlite.SqliteErrorCode switch
    { 5 => "sqlite-busy", 6 => "sqlite-locked", 10 => "sqlite-io", 13 => "sqlite-full", 14 => "sqlite-open", _ => "sqlite-error" } : error.GetType().Name;
    private static int? NativeCode(Exception error) => error switch
    { Win32Exception native => native.NativeErrorCode, SqliteException sqlite => sqlite.SqliteExtendedErrorCode, _ => null };
    private void Signal(string stage, Guid session)
    {
        journal?.TryWrite(new("history." + stage, "history") { SessionId = session, Level = DiagnosticLevel.Debug });
        Checkpoint?.Invoke("history-" + stage);
    }
}
