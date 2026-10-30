using System.ComponentModel;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Archive;

public sealed record HistoryBootstrapResult(int Verified, int Accounted, bool WaitingForRecovery = false);

/// <summary>The host owns the index and keys. Recover legacy history protection
/// before policy pruning; current user files are never installed or rolled back.</summary>
public sealed class HistoryRecovery
{
    private readonly string rootPath;
    private readonly FolderIndex index;
    private readonly FolderKeys keys;
    private readonly byte[] device;
    private readonly FolderSettings settings;
    private readonly DiagnosticJournal? journal;
    internal Action<string>? Checkpoint { get; init; }

    public HistoryRecovery(string rootPath, FolderIndex index, FolderKeys keys, byte[] device,
        FolderSettings? settings = null, DiagnosticJournal? journal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath); ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(device);
        if (device.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(device));
        this.rootPath = Path.GetFullPath(rootPath); this.index = index; this.keys = keys; this.device = device.ToArray();
        this.settings = FolderSettings.FromJson((settings ?? new()).ToJson()); this.journal = journal;
    }

    public async Task<HistoryBootstrapResult> BootstrapAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await FolderOperationGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        using var tree = new SafeFileTree(rootPath);
        if (index.Meta.Find("_root-identity")?.Value is not { Length: 24 } root || !tree.Identity.SequenceEqual(root))
            throw new IOException("History bootstrap root changed or is not bound.");
        return await BootstrapUnderGateAsync(tree, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<HistoryBootstrapResult> BootstrapUnderGateAsync(SafeFileTree tree, CancellationToken token)
    {
        Guid session = Guid.NewGuid(); int verified = 0, accounted = 0;
        try
        {
            if (index.ReadApplyIntents().Any())
            { Log("history.bootstrap_wait", "pending-apply"); return new(0, 0, true); }
            await PublishedBlocks.RecoverCapturesAsync(tree, index, journal, token).ConfigureAwait(false);
            var history = new SnapshotHistory(index, keys, device, settings, journal) { Checkpoint = Checkpoint };
            foreach (var marker in index.Meta.ReadAll())
            {
                token.ThrowIfCancellationRequested(); if (!marker.Key.StartsWith("_history-", StringComparison.Ordinal)) continue;
                var state = HistoryState.DecodeMarker(marker, keys);
                if (!state.Root.AsSpan().SequenceEqual(tree.Identity)) throw new IOException("Historical source belongs to another root.");
                verified++; if (index.IsHistoryAccounted(marker.Key, tree.Identity.ToArray())) continue;
                await history.BootstrapMarkerAsync(tree, marker, state, token).ConfigureAwait(false); accounted++;
            }
            Log("history.bootstrap_complete"); return new(verified, accounted);
        }
        catch (Exception error) { Log("history.bootstrap_failed", error.GetType().Name, error); throw; }

        void Log(string name, string? code = null, Exception? error = null) => journal?.TryWrite(new(name, "history-bootstrap")
        { SessionId = session, Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning, ErrorCode = code, Exception = error, HResult = error?.HResult,
            NativeError = error switch { Win32Exception native => native.NativeErrorCode, SqliteException sqlite => sqlite.SqliteExtendedErrorCode, _ => null },
            ItemsVisited = verified, ItemsChanged = accounted, RetryInMilliseconds = error is not null || name == "history.bootstrap_wait" ? 10000 : null });
    }
}
