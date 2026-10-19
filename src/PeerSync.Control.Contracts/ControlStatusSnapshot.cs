using PeerSync.Core.Sync;

namespace PeerSync.Control.Contracts;

public sealed class FolderPeerClockStatus
{
    private readonly byte[] folder;
    public byte[] FolderId => folder.ToArray();
    public PeerSessionClockStatus Session { get; }
    public FolderPeerClockStatus(byte[] folderId, PeerSessionClockStatus session)
    {
        ArgumentNullException.ThrowIfNull(folderId); ArgumentNullException.ThrowIfNull(session);
        if (folderId.Length != 32) throw new ArgumentException("Folder identifier must contain 32 bytes.", nameof(folderId));
        folder = folderId.ToArray(); Session = session;
    }
    public override string ToString() => "Folder peer clock status (redacted)";
}

public sealed class ControlStatusSnapshot
{
    public const int MaximumClockRows = 1000;
    public const int MaximumMessageBytes = 256 * 1024;
    public Guid InstanceId { get; }
    public ulong Sequence { get; }
    public bool ProductionSyncAvailable { get; }
    public IReadOnlyList<FolderPeerClockStatus> Clocks { get; }
    public bool ClockStatusTruncated { get; }
    public ControlStatusSnapshot(Guid instanceId, ulong sequence, bool productionSyncAvailable,
        IReadOnlyList<FolderPeerClockStatus> clocks, bool clockStatusTruncated)
    {
        ArgumentNullException.ThrowIfNull(clocks);
        if (instanceId == Guid.Empty || sequence == 0 || clocks.Count > MaximumClockRows)
            throw new ArgumentException("Invalid control status identity, sequence or row bound.");
        var frozen = clocks.ToArray();
        if (frozen.Any(row => row is null) || frozen.Select(row => row.Session.SessionId).Distinct().Count() != frozen.Length)
            throw new ArgumentException("Control status session identities must be unique.", nameof(clocks));
        InstanceId = instanceId; Sequence = sequence; ProductionSyncAvailable = productionSyncAvailable;
        Clocks = Array.AsReadOnly(frozen); ClockStatusTruncated = clockStatusTruncated;
    }
    public override string ToString() => "Control status snapshot (redacted)";
}
