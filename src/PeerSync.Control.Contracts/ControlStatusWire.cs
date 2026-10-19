using Google.Protobuf;
using PeerSync.Core.Sync;
using Wire = PeerSync.Control.Contracts.V1;

namespace PeerSync.Control.Contracts;

/// <summary>Explicit bounded control mapping. Display data never authorizes
/// index admission; all authenticated runtime decisions remain in the engine.</summary>
public static class ControlStatusWire
{
    public static Wire.ServiceStatus Encode(ControlStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var message = new Wire.ServiceStatus { InstanceId = snapshot.InstanceId.ToString("N"), Sequence = snapshot.Sequence,
            ProductionSyncAvailable = snapshot.ProductionSyncAvailable, ClockStatusTruncated = snapshot.ClockStatusTruncated };
        foreach (var row in snapshot.Clocks)
        {
            ValidateClock(row.Session.Clock);
            var entry = new Wire.PeerSessionClockStatus { FolderId = ByteString.CopyFrom(row.FolderId), DeviceId = ByteString.CopyFrom(row.Session.RemoteDeviceId),
                SessionId = row.Session.SessionId.ToString("N"), State = EncodeState(row.Session.Clock.State), IsConnected = row.Session.IsConnected };
            if (row.Session.Clock.OffsetTicks is { } offset) entry.OffsetTicks = offset;
            message.Clocks.Add(entry);
        }
        if (message.CalculateSize() > ControlStatusSnapshot.MaximumMessageBytes) throw new InvalidDataException("Control status exceeds the frame bound.");
        return message;
    }

    public static ControlStatusSnapshot Decode(Wire.ServiceStatus message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Clocks.Count > ControlStatusSnapshot.MaximumClockRows || message.CalculateSize() > ControlStatusSnapshot.MaximumMessageBytes)
            throw new InvalidDataException("Control status exceeds its bound.");
        // Freeze generated mutable messages before validating or handing to UI.
        message = message.Clone();
        if (message.Clocks.Count > ControlStatusSnapshot.MaximumClockRows || message.CalculateSize() > ControlStatusSnapshot.MaximumMessageBytes
            || !Guid.TryParseExact(message.InstanceId, "N", out var instance) || instance == Guid.Empty || message.Sequence == 0)
            throw new InvalidDataException("Invalid control status envelope.");
        var rows = new List<FolderPeerClockStatus>(message.Clocks.Count);
        foreach (var row in message.Clocks)
        {
            if (row.FolderId.Length != 32 || row.DeviceId.Length != 32 || !Guid.TryParseExact(row.SessionId, "N", out var session) || session == Guid.Empty)
                throw new InvalidDataException("Invalid control status row identity.");
            var clock = new PeerClockStatus(DecodeState(row.State), row.HasOffsetTicks ? row.OffsetTicks : null); ValidateClock(clock);
            rows.Add(new(row.FolderId.ToByteArray(), new(session, row.DeviceId.ToByteArray(), clock, row.IsConnected)));
        }
        if (rows.Select(row => row.Session.SessionId).Distinct().Count() != rows.Count) throw new InvalidDataException("Duplicate control session identity.");
        return new(instance, message.Sequence, message.ProductionSyncAvailable, rows.AsReadOnly(), message.ClockStatusTruncated);
    }

    private static void ValidateClock(PeerClockStatus status)
    {
        bool measured = status.State is PeerClockState.Accepted or PeerClockState.Skewed;
        if (measured != status.OffsetTicks.HasValue) throw new InvalidDataException("Clock sample presence disagrees with its state.");
        if (status.OffsetTicks is { } offset)
        {
            if (offset < -DateTime.MaxValue.Ticks || offset > DateTime.MaxValue.Ticks) throw new InvalidDataException("Clock offset is outside the UTC range.");
            bool skewed = offset < -600 * TimeSpan.TicksPerSecond || offset > 600 * TimeSpan.TicksPerSecond;
            if (skewed != (status.State == PeerClockState.Skewed)) throw new InvalidDataException("Clock offset disagrees with admission state.");
        }
    }
    private static Wire.ClockState EncodeState(PeerClockState state) => state switch
    {
        PeerClockState.AwaitingHello => Wire.ClockState.AwaitingHello,
        PeerClockState.Accepted => Wire.ClockState.Accepted,
        PeerClockState.Skewed => Wire.ClockState.Skewed,
        PeerClockState.InvalidTimestamp => Wire.ClockState.InvalidTimestamp,
        PeerClockState.InvalidElapsed => Wire.ClockState.InvalidElapsed,
        _ => throw new InvalidDataException("Unsupported clock state."),
    };
    private static PeerClockState DecodeState(Wire.ClockState state) => state switch
    {
        Wire.ClockState.AwaitingHello => PeerClockState.AwaitingHello,
        Wire.ClockState.Accepted => PeerClockState.Accepted,
        Wire.ClockState.Skewed => PeerClockState.Skewed,
        Wire.ClockState.InvalidTimestamp => PeerClockState.InvalidTimestamp,
        Wire.ClockState.InvalidElapsed => PeerClockState.InvalidElapsed,
        _ => throw new InvalidDataException("Unsupported control clock state."),
    };
}
