using System.Collections.Concurrent;

namespace PeerSync.Service.Hosting;

/// <summary>Tracker-specific relay presence; a peer on one server cannot be requested from another.</summary>
internal sealed class RelayPresenceCatalog
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<(string Server, string Peer, string Folder), DateTimeOffset> entries = new();

    internal void Update(Uri server, ReadOnlySpan<byte> folderId, IReadOnlySet<string> peers,
        bool enabled, DateTimeOffset now)
    {
        string serverKey = server.AbsoluteUri;
        string folderKey = Convert.ToHexString(folderId);
        foreach (string peer in peers)
            if (enabled) entries[(serverKey, peer, folderKey)] = now;
        foreach (var entry in entries.Keys.Where(item => item.Server == serverKey
            && item.Folder == folderKey && (!enabled || !peers.Contains(item.Peer))))
            entries.TryRemove(entry, out _);
    }

    internal bool TryGetFolder(Uri server, string peer, Func<string, bool> locallyAllowed,
        DateTimeOffset now, out byte[]? folderId)
    {
        string? folder = entries.Where(item => item.Key.Server == server.AbsoluteUri
                && item.Key.Peer == peer && now - item.Value < Lifetime
                && locallyAllowed(item.Key.Folder))
            .Select(item => item.Key.Folder)
            .OrderBy(item => item, StringComparer.Ordinal)
            .FirstOrDefault();
        folderId = folder is null ? null : Convert.FromHexString(folder);
        return folderId is not null;
    }
}
