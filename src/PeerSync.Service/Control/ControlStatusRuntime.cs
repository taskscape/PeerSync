using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Sync;
using PeerSync.Engine.Scanning;
using PeerSync.Service.Hosting;

namespace PeerSync.Service.Control;

/// <summary>Status projection of host-owned engines. Registration transfers no
/// lifetime or admission authority; remove it before disposing the engine.</summary>
public sealed class ControlStatusRuntime
{
    private readonly DiagnosticJournal? diagnostics;
    private readonly object gate = new();
    private readonly Dictionary<string, TransportFolder> folders = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> activePeerSessions = [];
    private readonly Dictionary<Guid, (string DeviceId, DiagnosticTransport Transport, DateTimeOffset Since)> peerTransports = [];
    private readonly Dictionary<string, TransferTotals> transferTotals = new(StringComparer.Ordinal);
    private readonly Queue<ControlDiagnosticError> recentErrors = new();
    private readonly Guid instance = Guid.NewGuid();
    private InstalledFolderHost? folderHost;
    private ulong sequence;
    private long hashedBytes;
    public ControlStatusRuntime(DiagnosticJournal? diagnostics = null)
    {
        this.diagnostics = diagnostics;
        if (diagnostics is not null) diagnostics.EntryWritten += OnDiagnosticWritten;
    }
    public event Action? Suspending;
    public event Action? Resumed;
    public event Action? NetworkChanged;

    internal void NotifySuspending() => Raise(Suspending, "service.power_suspending");
    internal void NotifyResumed() => Raise(Resumed, "service.power_resumed");
    internal void NotifyNetworkChanged() => Raise(NetworkChanged, "service.network_changed");
    internal void AttachFolderHost(InstalledFolderHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (gate)
        {
            if (folderHost is not null) throw new InvalidOperationException("Folder host is already attached.");
            folderHost = host;
        }
    }
    internal InstalledFolderHost RequireFolderHost()
        => folderHost ?? throw new ControlRuntimeUnavailableException();

    internal int ActivePeerSessionCount
    { get { lock (gate) return activePeerSessions.Count; } }

    internal int PendingTransferRequests
    { get { lock (gate) return folders.Values.Sum(folder => folder.PendingTransferRequests); } }

    internal IReadOnlyList<(string FolderId, long Uploaded, long Downloaded)> ReadTransferProgress()
    {
        lock (gate) return transferTotals.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value.Uploaded, pair.Value.Downloaded)).ToArray();
    }

    internal (long Uploaded, long Downloaded) ReadTransferTotals()
    {
        lock (gate) return (transferTotals.Values.Sum(item => item.Uploaded),
            transferTotals.Values.Sum(item => item.Downloaded));
    }
    internal long ReadHashedBytes() => Interlocked.Read(ref hashedBytes);

    internal IReadOnlyList<ControlDiagnosticError> ReadRecentErrors()
    { lock (gate) return recentErrors.ToArray(); }

    private void OnDiagnosticWritten(DiagnosticEvent entry, DateTime occurredUtc)
    {
        if (entry.Level is not (DiagnosticLevel.Warning or DiagnosticLevel.Error)) return;
        lock (gate)
        {
            recentErrors.Enqueue(new(entry.ErrorCode ?? entry.Event, entry.Operation, occurredUtc.Ticks));
            while (recentErrors.Count > 64) recentErrors.Dequeue();
        }
    }

    private void Raise(Action? callbacks, string eventName)
    {
        if (callbacks is null) return;
        foreach (Action callback in callbacks.GetInvocationList())
        {
            try { callback(); }
            catch (Exception error)
            {
                diagnostics?.TryWrite(new(eventName, "service_power")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
            }
        }
    }

    public IDisposable RegisterFolder(TransportFolder folder, Scanner? scanner = null)
    {
        ArgumentNullException.ThrowIfNull(folder); string key = Convert.ToHexString(folder.FolderId);
        Action<long, bool> onTransfer = (bytes, download) =>
        {
            lock (gate)
            {
                if (!transferTotals.TryGetValue(key, out var totals)) return;
                transferTotals[key] = download
                    ? totals with { Downloaded = checked(totals.Downloaded + bytes) }
                    : totals with { Uploaded = checked(totals.Uploaded + bytes) };
            }
        };
        Action<long> onHash = bytes => Interlocked.Add(ref hashedBytes, bytes);
        lock (gate)
        {
            if (!folders.TryAdd(key, folder)) throw new InvalidOperationException("Folder status is already registered.");
            transferTotals[key] = new(0, 0);
            folder.BytesTransferred += onTransfer;
            if (scanner is not null) scanner.BytesHashed += onHash;
        }
        return new Registration(this, key, folder, scanner, onTransfer, onHash);
    }
    public ControlStatusSnapshot ReadStatus()
    {
        lock (gate)
        {
            var rows = new List<FolderPeerClockStatus>(); bool truncated = false;
            foreach (var folder in folders.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
            {
                int remaining = ControlStatusSnapshot.MaximumClockRows - rows.Count;
                var page = folder.ReadClockStatuses(Math.Max(1, remaining));
                if (remaining == 0) { truncated |= page.Sessions.Count != 0 || page.Truncated; continue; }
                rows.AddRange(page.Sessions.Select(session => new FolderPeerClockStatus(folder.FolderId, session)));
                truncated |= page.Truncated;
            }
            return new(instance, checked(++sequence), activePeerSessions.Count != 0, rows.AsReadOnly(), truncated);
        }
    }

    internal void SetPeerSessionActive(Guid sessionId, bool active)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session id is required.", nameof(sessionId));
        lock (gate)
        {
            if (active) activePeerSessions.Add(sessionId);
            else activePeerSessions.Remove(sessionId);
        }
    }

    internal void SetPeerTransport(Guid sessionId, ReadOnlySpan<byte> deviceId, DiagnosticTransport transport)
    {
        if (sessionId == Guid.Empty || deviceId.Length != 32) throw new ArgumentException("A session and device id are required.");
        lock (gate) peerTransports[sessionId] = (Convert.ToHexString(deviceId), transport, DateTimeOffset.UtcNow);
    }

    internal void ClearPeerTransport(Guid sessionId)
    { lock (gate) peerTransports.Remove(sessionId); }

    internal string ReadPeerTransport(ReadOnlySpan<byte> deviceId)
    {
        if (deviceId.Length != 32) return "";
        string key = Convert.ToHexString(deviceId);
        lock (gate) return peerTransports.Values.Where(item => item.DeviceId == key)
            .OrderByDescending(item => item.Since).Select(item => item.Transport.ToString()).FirstOrDefault() ?? "";
    }
    private sealed record TransferTotals(long Uploaded, long Downloaded);
    internal sealed record ControlDiagnosticError(string Code, string Source, long OccurredUtcTicks);
    private sealed class Registration(ControlStatusRuntime runtime, string key, TransportFolder folder, Scanner? scanner,
        Action<long, bool> onTransfer, Action<long> onHash) : IDisposable
    {
        private ControlStatusRuntime? owner = runtime;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null); if (current is null) return;
            lock (current.gate) if (current.folders.TryGetValue(key, out var actual) && ReferenceEquals(actual, folder))
            {
                folder.BytesTransferred -= onTransfer;
                if (scanner is not null) scanner.BytesHashed -= onHash;
                current.folders.Remove(key);
                current.transferTotals.Remove(key);
            }
        }
    }
}

internal sealed class ControlRuntimeUnavailableException() : InvalidOperationException("Folder management is unavailable.");
