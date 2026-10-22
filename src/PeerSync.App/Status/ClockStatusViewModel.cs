using System.ComponentModel;
using System.IO;
using System.Globalization;
using PeerSync.Control.Contracts;
using PeerSync.Core.Sync;

namespace PeerSync.App.Status;

// Mutated only by the UI dispatcher; generation/epoch guards also cover queued work.
internal sealed class ClockStatusViewModel : INotifyPropertyChanged
{
    private long generation;
    private Guid? epoch;
    private ulong sequence;
    private bool accepting;
    public string Connection { get; private set; } = "Connecting to PeerSync service…";
    public string Availability { get; private set; } = "Production sync status is unavailable.";
    public string Detail { get; private set; } = "Waiting for verified service status.";
    public IReadOnlyList<ClockStatusRow> Rows { get; private set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    internal long BeginAttempt()
    {
        generation = checked(generation + 1); epoch = null; sequence = 0; accepting = true;
        Clear("Connecting to PeerSync service…", "Waiting for verified service status."); return generation;
    }
    internal bool Apply(long attempt, ControlStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (attempt != generation || !accepting) return false;
        if (epoch is { } current && current != snapshot.InstanceId) throw new InvalidDataException("Control service instance changed within a connection.");
        if (snapshot.Sequence <= sequence) return false;
        epoch = snapshot.InstanceId; sequence = snapshot.Sequence;
        Rows = snapshot.Clocks.Select(ClockStatusRow.From).ToArray();
        Connection = "Connected to verified PeerSync service";
        Availability = snapshot.ProductionSyncAvailable ? "Production sync is available." : "Production sync is not available right now.";
        Detail = snapshot.ClockStatusTruncated ? "Showing a limited snapshot (at most 1,000 peer sessions). Additional sessions are omitted."
            : Rows.Count == 0 ? "No peer clock sessions reported." : "Clock offset is peer UTC minus this computer's UTC.";
        Notify(); return true;
    }
    internal void Fail(long attempt, string reason)
    { if (attempt == generation && accepting) { accepting = false; epoch = null; sequence = 0; Clear("Service status unavailable — reconnecting…", reason); } }
    private void Clear(string connection, string detail)
    { Connection = connection; Detail = detail; Availability = "Production sync status is unavailable."; Rows = []; Notify(); }
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

internal sealed record ClockStatusRow(string Folder, string Peer, string Connection, string Reason, string OffsetMinutes)
{
    internal static ClockStatusRow From(FolderPeerClockStatus row)
    {
        var clock = row.Session.Clock;
        string reason = clock.State switch
        {
            PeerClockState.Accepted => "Clock accepted",
            PeerClockState.AwaitingHello => "Waiting for peer clock sample",
            PeerClockState.Skewed => "Clock difference exceeds 10 minutes; exchange blocked",
            PeerClockState.InvalidTimestamp => "Invalid peer timestamp; exchange blocked",
            PeerClockState.InvalidElapsed => "Invalid clock elapsed time; exchange blocked",
            _ => throw new InvalidDataException("Unknown peer clock state.")
        };
        string offset = clock.OffsetTicks is { } ticks ? ((double)ticks / TimeSpan.TicksPerMinute).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) : "Unavailable";
        return new(Convert.ToHexString(row.FolderId)[..12], Convert.ToHexString(row.Session.RemoteDeviceId)[..12],
            row.Session.IsConnected ? "Connected" : "Disconnected", reason, offset);
    }
}
