using System.Collections.Concurrent;
using System.Net;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Connections;

namespace PeerSync.Service.Hosting;

/// <summary>Keeps a bounded, expiring set of recently observed dial targets per authenticated peer.</summary>
internal sealed class PeerEndpointCatalog
{
    internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);
    internal const int DefaultMaximumEndpointsPerPeer = 16;

    private readonly ConcurrentDictionary<string, EndpointSet> peers = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan lifetime;
    private readonly int maximumEndpointsPerPeer;
    private readonly DiagnosticJournal? diagnostics;

    internal PeerEndpointCatalog(TimeProvider timeProvider, TimeSpan? lifetime = null,
        int maximumEndpointsPerPeer = DefaultMaximumEndpointsPerPeer, DiagnosticJournal? diagnostics = null)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.lifetime = lifetime ?? DefaultLifetime;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.lifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEndpointsPerPeer, 1);
        this.maximumEndpointsPerPeer = maximumEndpointsPerPeer;
        this.diagnostics = diagnostics;
    }

    internal void Remember(string peerKey, IPEndPoint endpoint, ConnectionSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerKey);
        ArgumentNullException.ThrowIfNull(endpoint);
        peers.GetOrAdd(peerKey, static _ => new EndpointSet())
            .Remember(endpoint, source, timeProvider.GetUtcNow(), lifetime, maximumEndpointsPerPeer, diagnostics);
    }

    internal IReadOnlyList<PeerEndpoint> Snapshot(string peerKey)
    {
        if (!peers.TryGetValue(peerKey, out EndpointSet? set)) return [];
        return set.Snapshot(timeProvider.GetUtcNow(), lifetime, diagnostics);
    }

    internal string[] KnownPeerKeys()
        => peers.Keys.Where(key => Snapshot(key).Count != 0).Order(StringComparer.Ordinal).ToArray();

    internal bool Contains(string peerKey, IPEndPoint endpoint)
        => Snapshot(peerKey).Any(candidate => candidate.Address.Equals(endpoint));

    internal sealed record PeerEndpoint(IPEndPoint Address, ConnectionSource Source, DateTimeOffset LastSeenUtc);

    private sealed class EndpointSet
    {
        private readonly object gate = new();
        private readonly Dictionary<string, PeerEndpoint> entries = new(StringComparer.Ordinal);

        public void Remember(IPEndPoint endpoint, ConnectionSource source, DateTimeOffset now,
            TimeSpan lifetime, int maximum, DiagnosticJournal? diagnostics)
        {
            lock (gate)
            {
                int expired = Prune(now, lifetime);
                if (expired != 0)
                    diagnostics?.TryWrite(new("service.peer_endpoint_expired", "peer_endpoint_catalog")
                        { ItemsVisited = expired });
                IPEndPoint copy = Clone(endpoint);
                string key = copy.ToString();
                if (entries.TryGetValue(key, out PeerEndpoint? current))
                    entries[key] = new(copy, source < current.Source ? source : current.Source, now);
                else
                    entries.Add(key, new(copy, source, now));

                int evicted = 0;
                while (entries.Count > maximum)
                {
                    string oldest = entries.OrderBy(pair => pair.Value.LastSeenUtc)
                        .ThenByDescending(pair => pair.Value.Source)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                        .First().Key;
                    entries.Remove(oldest);
                    evicted++;
                }
                if (evicted != 0)
                    diagnostics?.TryWrite(new("service.peer_endpoint_limit_reached", "peer_endpoint_catalog")
                        { ItemsVisited = evicted, ItemsSkipped = entries.Count });
            }
        }

        public PeerEndpoint[] Snapshot(DateTimeOffset now, TimeSpan lifetime, DiagnosticJournal? diagnostics)
        {
            lock (gate)
            {
                int expired = Prune(now, lifetime);
                if (expired != 0)
                    diagnostics?.TryWrite(new("service.peer_endpoint_expired", "peer_endpoint_catalog")
                        { ItemsVisited = expired });
                return entries.Values
                    .OrderBy(endpoint => endpoint.Source)
                    .ThenByDescending(endpoint => endpoint.LastSeenUtc)
                    .ThenBy(endpoint => endpoint.Address.AddressFamily)
                    .ThenBy(endpoint => endpoint.Address.Address.ToString(), StringComparer.Ordinal)
                    .ThenBy(endpoint => endpoint.Address.Port)
                    .Select(endpoint => endpoint with { Address = Clone(endpoint.Address) })
                    .ToArray();
            }
        }

        private int Prune(DateTimeOffset now, TimeSpan lifetime)
        {
            string[] expired = entries.Where(pair => now - pair.Value.LastSeenUtc >= lifetime)
                .Select(pair => pair.Key).ToArray();
            foreach (string key in expired)
                entries.Remove(key);
            return expired.Length;
        }

        private static IPEndPoint Clone(IPEndPoint endpoint) => new(endpoint.Address, endpoint.Port);
    }
}
