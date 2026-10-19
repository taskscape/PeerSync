using PeerSync.Control.Contracts;
using PeerSync.Engine.Sync;

namespace PeerSync.Service.Control;

/// <summary>Status projection of host-owned engines. Registration transfers no
/// lifetime or admission authority; remove it before disposing the engine.</summary>
public sealed class ControlStatusRuntime
{
    private readonly object gate = new();
    private readonly Dictionary<string, TransportFolder> folders = new(StringComparer.Ordinal);
    private readonly Guid instance = Guid.NewGuid();
    private ulong sequence;
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
            // Production engine/network supervision is not implemented by this status slice.
            return new(instance, checked(++sequence), false, rows.AsReadOnly(), truncated);
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
