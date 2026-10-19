using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Archive;

public sealed record HistoryPolicyResult(int MarkersRetired, int SourcesReleased, bool WaitingForRecovery = false);

/// <summary>Retire policy protection metadata under both maintenance gates.
/// The host owns keys/index. Filesystem deletion requires a separate fresh,
/// serialized ownership/reference sweep and is never performed here.</summary>
public sealed class HistoryPolicy
{
    private readonly string rootPath;
    private readonly FolderIndex index;
    private readonly FolderKeys keys;
    private readonly byte[] device;
    private readonly FolderSettings settings;
    private readonly DiagnosticJournal? journal;
    private readonly TimeProvider clock;
    internal Action<string>? Checkpoint { get; init; }

    public HistoryPolicy(string rootPath, FolderIndex index, FolderKeys keys, byte[] device,
        FolderSettings? settings = null, TimeProvider? clock = null, DiagnosticJournal? journal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath); ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(device);
        if (device.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(device));
        this.rootPath = Path.GetFullPath(rootPath); this.index = index; this.keys = keys; this.device = device.ToArray();
        this.settings = FolderSettings.FromJson((settings ?? new()).ToJson()); this.clock = clock ?? TimeProvider.System; this.journal = journal;
    }

    public async Task<HistoryPolicyResult> RetireAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await FolderOperationGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        using var references = await FolderReferenceGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        using var tree = new SafeFileTree(rootPath); byte[] root = tree.Identity.ToArray();
        if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } bound || !bound.AsSpan().SequenceEqual(root))
            throw new IOException("History policy root changed or is not bound.");
        Guid session = Guid.NewGuid(); var watch = Stopwatch.StartNew(); int retired = 0, released = 0;
        try
        {
            var bootstrap = await new HistoryRecovery(rootPath, index, keys, device, settings, journal)
                .BootstrapUnderGateAsync(tree, cancellationToken).ConfigureAwait(false);
            if (bootstrap.WaitingForRecovery) { Log("history.policy_wait", "pending-apply"); return new(0, 0, true); }
            long now = clock.GetUtcNow().UtcTicks, cutoff = Math.Max(0, now - settings.ArchiveDays * TimeSpan.TicksPerDay);
            foreach (var marker in index.Meta.ReadAll())
            {
                cancellationToken.ThrowIfCancellationRequested(); if (!marker.Key.StartsWith("_history-", StringComparison.Ordinal)) continue;
                var state = HistoryState.DecodeMarker(marker, keys);
                if (!state.Root.AsSpan().SequenceEqual(root)) throw new IOException("Historical policy source belongs to another root.");
                bool observed = state.PublicCopy && PublicCopyObserved(state);
                if (await index.RetireHistoryMarkerAsync(marker, root, cutoff, now, observed, cancellationToken).ConfigureAwait(false))
                { retired++; Checkpoint?.Invoke("marker-retired"); }
            }
            foreach (var source in index.ReadRetainedArchiveProvenance(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await index.ReleaseArchiveSourceAsync(source.SourceId, root, cutoff, now, cancellationToken).ConfigureAwait(false))
                { released++; Checkpoint?.Invoke("source-released"); }
            }
            Log("history.policy_complete"); return new(retired, released);
        }
        catch (Exception error) { Log("history.policy_failed", error.GetType().Name, error); throw; }

        void Log(string name, string? code = null, Exception? error = null) => journal?.TryWrite(new(name, "history-policy")
        { SessionId = session, Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning, ErrorCode = code, HResult = error?.HResult,
            NativeError = error switch { Win32Exception native => native.NativeErrorCode, SqliteException sqlite => sqlite.SqliteExtendedErrorCode, _ => null },
            ItemsChanged = retired + released, ElapsedMilliseconds = watch.ElapsedMilliseconds,
            RetryInMilliseconds = error is not null || name == "history.policy_wait" ? 10000 : null });
    }

    private bool PublicCopyObserved(HistoryState state)
    {
        var row = index.LocalFiles.Find(state.Destination);
        if (row?.Record is null) return false;
        var version = VersionView.Open(row.Record, keys, device);
        if (PathRules.Key(version.Path) != PathRules.Key(state.Destination) || version.Type != row.Type || version.Deleted != row.Deleted
            || !version.Deleted && version.Type == 0 && (version.Size != row.Size || !version.FileHash.SequenceEqual(row.FileHash)))
            throw new InvalidDataException("Published public history observation changed.");
        return row.Deleted || row.ContentReady;
    }
}
