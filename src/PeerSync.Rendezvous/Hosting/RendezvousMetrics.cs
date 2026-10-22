using System.Diagnostics.Metrics;

namespace PeerSync.Rendezvous.Hosting;

internal static class RendezvousMetrics
{
    internal const string MeterName = "PeerSync.Rendezvous";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    internal static readonly Counter<long> TrackerSessions = Meter.CreateCounter<long>("peersync.tracker.sessions.opened");
    internal static readonly UpDownCounter<long> ActiveTrackerSessions = Meter.CreateUpDownCounter<long>("peersync.tracker.sessions.active");
    internal static readonly Counter<long> RelaySessions = Meter.CreateCounter<long>("peersync.relay.sessions.joined");
    internal static readonly Counter<long> RelayBytes = Meter.CreateCounter<long>("peersync.relay.bytes.forwarded", "By");
    internal static readonly Counter<long> AuthenticationRejected = Meter.CreateCounter<long>("peersync.authentication.rejected");
    internal static readonly Counter<long> RequestsRateLimited = Meter.CreateCounter<long>("peersync.requests.rate_limited");
    internal static readonly Counter<long> Probes = Meter.CreateCounter<long>("peersync.probe.requests");
}
