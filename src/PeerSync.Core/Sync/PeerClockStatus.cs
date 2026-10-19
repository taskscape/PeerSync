using System.Globalization;

namespace PeerSync.Core.Sync;

public enum PeerClockState { AwaitingHello, Accepted, Skewed, InvalidTimestamp, InvalidElapsed }

/// <summary>Shared control status. Signed offset is estimated peer UTC minus
/// current local UTC. No engine, storage or native dependency is required.</summary>
public sealed record PeerClockStatus(PeerClockState State, long? OffsetTicks = null)
{
    public bool IndexExchangeAllowed => State == PeerClockState.Accepted;
    public string DisplayMessage => State switch
    {
        PeerClockState.Accepted => "",
        PeerClockState.AwaitingHello => "waiting for peer clock",
        PeerClockState.Skewed => "clock differs by " + (Math.Abs((double)OffsetTicks.GetValueOrDefault()) / TimeSpan.TicksPerMinute).ToString("0.##", CultureInfo.InvariantCulture) + " minutes",
        PeerClockState.InvalidTimestamp => "invalid peer clock timestamp",
        _ => "peer clock elapsed time is invalid",
    };
}
