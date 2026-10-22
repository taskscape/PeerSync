using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Sync;
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
    private readonly Guid instance = Guid.NewGuid();
    private InstalledFolderHost? folderHost;
    private ulong sequence;
    public ControlStatusRuntime(DiagnosticJournal? diagnostics = null) => this.diagnostics = diagnostics;
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

    public IDisposable RegisterFolder(TransportFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder); string key = Convert.ToHexString(folder.FolderId);
        lock (gate)
        {
            if (!folders.TryAdd(key, folder)) throw new InvalidOperationException("Folder status is already registered.");
        }
        return new Registration(this, key, folder);
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
    private sealed class Registration(ControlStatusRuntime runtime, string key, TransportFolder folder) : IDisposable
    {
        private ControlStatusRuntime? owner = runtime;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null); if (current is null) return;
            lock (current.gate) if (current.folders.TryGetValue(key, out var actual) && ReferenceEquals(actual, folder)) current.folders.Remove(key);
        }
    }
}

internal sealed class ControlRuntimeUnavailableException() : InvalidOperationException("Folder management is unavailable.");
