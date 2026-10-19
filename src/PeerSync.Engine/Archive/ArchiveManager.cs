using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Archive;

public sealed record ArchivePruneResult(int Removed, int Missing, int Deferred, long BytesRemoved,
    long? IndexedBytesRemaining, TimeSpan NextRunIn, bool WaitingForRecovery = false, bool NotDue = false);

/// <summary>Only indexed, verified archive versions are eligible for retention
/// cleanup. Unknown filesystem data is preserved. The host owns the index/journal.</summary>
public sealed partial class ArchiveManager
{
    private const string LastSuccessKey = "_archive-last-success";
    private static readonly TimeSpan Daily = TimeSpan.FromDays(1);
    private readonly string rootPath;
    private readonly FolderIndex index;
    private readonly FolderSettings settings;
    private readonly TimeProvider clock;
    private readonly DiagnosticJournal? journal;
    private int retryAttempt, running;
    internal Func<ulong>? CapacityOverride { get; init; }
    internal Action<string>? Checkpoint { get; init; }

    public ArchiveManager(string rootPath, FolderIndex index, FolderSettings? settings = null,
        TimeProvider? clock = null, DiagnosticJournal? journal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath); ArgumentNullException.ThrowIfNull(index);
        this.rootPath = Path.GetFullPath(rootPath); this.index = index;
        this.settings = FolderSettings.FromJson((settings ?? new()).ToJson());
        this.clock = clock ?? TimeProvider.System; this.journal = journal;
    }

    public Task RunDailyAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Archive maintenance is already running.");
        return Task.Run(() => RunDailyCoreAsync(cancellationToken), CancellationToken.None);
    }

    private async Task RunDailyCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested(); TimeSpan next;
                try { next = (await PruneAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).NextRunIn; }
                catch (Exception error) when (IsFileError(error) || error is SqliteException { SqliteErrorCode: 5 or 6 or 10 or 13 or 14 })
                { next = Retry(); }
                await Task.Delay(next, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref running, 0); }
    }

    public async Task<ArchivePruneResult> PruneAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        using var operation = await FolderOperationGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        Guid session = Guid.NewGuid(); Log("archive.start", session);
        try
        {
            using var tree = new SafeFileTree(rootPath);
            if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } identity || !identity.AsSpan().SequenceEqual(tree.Identity))
                throw new IOException("Archive root identity changed or is not bound.");
            if (index.ReadApplyIntents().Any())
            {
                TimeSpan retry = Retry(); Log("archive.wait_recovery", session, code: "pending-apply", retry: retry);
                return new(0, 0, 0, 0, null, retry, WaitingForRecovery: true);
            }
            if (index.HasUnaccountedHistory(identity))
            {
                TimeSpan retry = Retry(); Log("archive.wait_recovery", session, code: "history-accounting-required", deferred: 1, retry: retry);
                return new(0, 0, 1, 0, null, retry, WaitingForRecovery: true);
            }
            var recovered = await RecoverRetirementsAsync(tree, cancellationToken).ConfigureAwait(false);
            var pendingRetirements = index.ReadArchiveRetirements().Where(r => r.CompletedTicks is null).Select(r => r.Archive.Id).ToHashSet();
            long now = clock.GetUtcNow().UtcTicks, last = ReadLastSuccess();
            long elapsed = now - last;
            if (!force && last != 0 && elapsed >= 0 && elapsed < Daily.Ticks)
            {
                TimeSpan remaining = recovered.Deferred == 0 ? TimeSpan.FromTicks(Daily.Ticks - elapsed) : Retry(); Log("archive.not_due", session, retry: remaining);
                return new(recovered.Removed, recovered.Missing, recovered.Deferred, recovered.Bytes, null, remaining, NotDue: recovered.Deferred == 0);
            }
            ulong capacity = CapacityOverride?.Invoke() ?? tree.CapacityBytes;
            if (capacity == 0) throw new IOException("Archive capacity must be positive.");
            long cap = (long)UInt128.Min(long.MaxValue, (UInt128)capacity * (uint)settings.ArchiveMaxPercentOfVolume / 100);
            long cutoff = Math.Max(0, now - (long)settings.ArchiveDays * TimeSpan.TicksPerDay);
            long bytes = 0, removedBytes = recovered.Bytes; int removed = recovered.Removed, missing = recovered.Missing, deferred = recovered.Deferred;
            bool capBlocked = deferred != 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Reconcile all missing rows before cap selection: a missing newer
            // entry must not make an intact older version look over budget.
            foreach (var row in index.Archive.ReadAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!paths.Add(row.ArchivePath)) throw new InvalidDataException("Archive paths are not unique.");
                bool absent = false;
                try
                {
                    using var parent = Parent(tree, row);
                    using var file = parent.OpenFile(ApplyState.Name(row.ArchivePath));
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { absent = true; }
                catch (Exception error) when (IsFileError(error)) { /* Inaccessible still counts toward the cap. */ }
                if (absent && !pendingRetirements.Contains(row.Id)) { await index.Archive.DeleteAsync([row.Id], cancellationToken).ConfigureAwait(false); missing++; }
                else bytes = checked(bytes + row.Size);
            }
            foreach (var candidate in index.ArchivesByRetentionTime(identity))
            {
                var row = candidate.Archive;
                cancellationToken.ThrowIfCancellationRequested();
                if (pendingRetirements.Contains(row.Id)) continue;
                if (candidate.RetainedTicks > cutoff && (bytes <= cap || capBlocked)) break;
                try
                {
                    using var parent = Parent(tree, row);
                    ArchiveRetirement retirement;
                    using (var file = parent.OpenFile(ApplyState.Name(row.ArchivePath), mutation: true))
                    {
                        PublishedBlocks.RequireIndependent(file);
                        if (row.FileHash is null || FileSnapshot.Read(file).Length != row.Size
                            || !GuardedFile.Hash(file, cancellationToken).AsSpan().SequenceEqual(row.FileHash))
                        { deferred++; capBlocked = true; Log("archive.defer", session, code: "changed-or-unverified"); continue; }
                        cancellationToken.ThrowIfCancellationRequested();
                        retirement = new(Guid.NewGuid(), row, tree.Identity.ToArray(), BlockCaptureProof.Identity(file),
                            candidate.RetainedTicks <= cutoff ? ArchiveRetirementReason.Age : ArchiveRetirementReason.Capacity, now);
                        await index.ReserveArchiveRetirementAsync(retirement, cancellationToken).ConfigureAwait(false);
                        Log("archive.retirement_prepared", retirement.Id, bytes: row.Size); Checkpoint?.Invoke("retirement-prepared");
                        Log("archive.delete_prepared", session, bytes: row.Size);
                        Checkpoint?.Invoke("before-delete"); cancellationToken.ThrowIfCancellationRequested(); GuardedFile.DeleteOnClose(file);
                    }
                    Checkpoint?.Invoke("data-deleted");
                    Log("archive.delete_requested", session, bytes: row.Size);
                    // Traditional deletion can remain pending while another
                    // delete-sharing reader is open. Keep its row until absent.
                    bool gone = false;
                    try { using var current = parent.OpenFile(ApplyState.Name(row.ArchivePath)); }
                    catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { gone = true; }
                    if (!gone) { deferred++; capBlocked = true; Log("archive.defer", session, code: "delete-pending-or-concurrent-file"); continue; }
                    Checkpoint?.Invoke("before-retirement-commit");
                    await index.CompleteArchiveRetirementAsync(retirement, Math.Max(now, clock.GetUtcNow().UtcTicks), cancellationToken).ConfigureAwait(false);
                    Log("archive.retirement_complete", retirement.Id, bytes: row.Size); Checkpoint?.Invoke("retirement-committed");
                    removed++; bytes -= row.Size; removedBytes = checked(removedBytes + row.Size);
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
                {
                    await index.Archive.DeleteAsync([row.Id], cancellationToken).ConfigureAwait(false);
                    missing++; bytes -= row.Size;
                }
                catch (Exception error) when (IsFileError(error))
                { deferred++; capBlocked = true; Log("archive.defer", session, error, "unavailable"); }
            }
            TimeSpan next = deferred == 0 ? Daily : Retry();
            if (deferred == 0)
            {
                byte[] stamp = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(stamp, now);
                await index.Meta.UpsertAsync([new(LastSuccessKey, stamp)], cancellationToken).ConfigureAwait(false);
                retryAttempt = 0;
            }
            Log("archive.complete", session, visited: removed + missing + deferred, changed: removed + missing, bytes: removedBytes, deferred: deferred, retry: next);
            return new(removed, missing, deferred, removedBytes, bytes, next);
        }
        catch (Exception error) { Log("archive.failed", session, error, error.GetType().Name); throw; }
    }

    private static DirectoryLease Parent(SafeFileTree tree, ArchiveRow row) =>
        tree.Directory(ApplyState.Parent(row.ArchivePath), privatePath: true);

    private long ReadLastSuccess()
    {
        byte[]? value = index.Meta.Find(LastSuccessKey)?.Value;
        if (value is null) return 0;
        if (value.Length != 8) throw new InvalidDataException("Invalid archive maintenance timestamp.");
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(value);
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks) throw new InvalidDataException("Invalid archive maintenance timestamp.");
        return ticks;
    }
    private TimeSpan Retry()
    {
        int exponent = retryAttempt; retryAttempt = Math.Min(retryAttempt + 1, 6);
        return TimeSpan.FromSeconds(Math.Min(600, 10 * (1 << exponent)));
    }
    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception;
    private void Log(string name, Guid session, Exception? error = null, string? code = null,
        long? visited = null, long? changed = null, long? bytes = null, long? deferred = null, TimeSpan? retry = null) =>
        journal?.TryWrite(new(name, "archive") { SessionId = session, Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
            ErrorCode = error is SqliteException sqlite ? sqlite.SqliteErrorCode switch
            { 5 => "sqlite-busy", 6 => "sqlite-locked", 10 => "sqlite-io", 13 => "sqlite-full", 14 => "sqlite-open", _ => "sqlite-error" } : code,
            HResult = error?.HResult, NativeError = error switch
            { Win32Exception native => native.NativeErrorCode, SqliteException databaseError => databaseError.SqliteExtendedErrorCode, _ => null },
            ItemsVisited = visited, ItemsChanged = changed, ItemsDeferred = deferred, Bytes = bytes,
            RetryInMilliseconds = retry is { } next ? checked((int)next.TotalMilliseconds) : null });
}
