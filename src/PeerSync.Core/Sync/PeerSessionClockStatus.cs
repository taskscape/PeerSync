namespace PeerSync.Core.Sync;

/// <summary>Display data from a host-owned authenticated session. This is not
/// an admission token. The containing folder supplies the folder identity.</summary>
public sealed class PeerSessionClockStatus
{
    private readonly byte[] peer;
    public Guid SessionId { get; }
    public byte[] RemoteDeviceId => peer.ToArray();
    public PeerClockStatus Clock { get; }
    public bool IsConnected { get; }

    public PeerSessionClockStatus(Guid sessionId, byte[] remoteDeviceId, PeerClockStatus clock, bool isConnected)
    {
        ArgumentNullException.ThrowIfNull(remoteDeviceId); ArgumentNullException.ThrowIfNull(clock);
        if (sessionId == Guid.Empty || remoteDeviceId.Length != 32 || !Enum.IsDefined(clock.State))
            throw new ArgumentException("Invalid peer clock display identity or state.");
        SessionId = sessionId; peer = remoteDeviceId.ToArray(); Clock = clock; IsConnected = isConnected;
    }

    public override string ToString() => "Peer session clock status (redacted)";
}

public sealed class PeerClockStatusPage
{
    public IReadOnlyList<PeerSessionClockStatus> Sessions { get; }
    public bool Truncated { get; }
    public PeerClockStatusPage(IReadOnlyList<PeerSessionClockStatus> sessions, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count > 1000) throw new ArgumentException("Clock status page exceeds its bound.", nameof(sessions));
        var frozen = sessions.ToArray();
        if (frozen.Any(row => row is null) || frozen.Select(row => row.SessionId).Distinct().Count() != frozen.Length)
            throw new ArgumentException("Clock status rows must have unique session identities.", nameof(sessions));
        Sessions = Array.AsReadOnly(frozen); Truncated = truncated;
    }
    public override string ToString() => "Peer clock status page (redacted)";
}
