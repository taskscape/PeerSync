using PeerSync.Core.Sync;

namespace PeerSync.Engine.Sync;

// Tracks sessions the host already owns; completed history is bounded. Never
// use display records for admission or infer authenticated identities from Hello.
internal sealed class PeerClockStatusLedger
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, (PeerIndexSession Session, long Order)> active = [];
    private readonly Dictionary<string, (PeerSessionClockStatus Status, long Order)> recent = new(StringComparer.Ordinal);
    private long order;

    internal void Register(PeerIndexSession session)
    { lock (gate) active.Add(session.CorrelationId, (session, checked(++order))); }

    internal void Retire(PeerIndexSession session)
    {
        var status = session.ReadDisplayClockStatus(); string peer = Convert.ToHexString(status.RemoteDeviceId);
        lock (gate)
        {
            if (!active.Remove(session.CorrelationId, out var entry)) return;
            // A late close from an older attempt cannot replace newer history.
            if (recent.TryGetValue(peer, out var previous) && previous.Order > entry.Order) return;
            recent[peer] = (status, entry.Order);
            if (recent.Count > 64) recent.Remove(recent.MinBy(pair => pair.Value.Order).Key);
        }
    }

    internal PeerClockStatusPage Read(int limit)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        PeerIndexSession[] live; PeerSessionClockStatus[] history; bool truncated;
        lock (gate)
        {
            truncated = active.Count > limit; live = active.Values.Take(limit).Select(entry => entry.Session).ToArray();
            history = recent.Values.OrderByDescending(entry => entry.Order).Select(entry => entry.Status).ToArray();
        }
        var rows = live.Select(session => session.ReadDisplayClockStatus()).ToList();
        var observedPeers = rows.Where(row => row.Clock.State != PeerClockState.AwaitingHello)
            .Select(row => Convert.ToHexString(row.RemoteDeviceId)).ToHashSet(StringComparer.Ordinal);
        foreach (var row in history)
        {
            if (row.Clock.State is PeerClockState.Accepted or PeerClockState.AwaitingHello || observedPeers.Contains(Convert.ToHexString(row.RemoteDeviceId))) continue;
            if (rows.Count == limit) { truncated = true; break; }
            // Close may have raced the live snapshot: keep only one row/session.
            if (!rows.Any(existing => existing.SessionId == row.SessionId)) rows.Add(row);
        }
        return new(rows.AsReadOnly(), truncated);
    }
}
