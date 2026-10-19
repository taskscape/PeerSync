namespace PeerSync.Core.Sync;

/// <summary>Frozen index handoff; authentication and signature validation belong
/// to the receiving peer session. Cursors can span unpublished local gaps.</summary>
public sealed class PeerIndexPage
{
    public long AfterSequence { get; }
    public long LastSequence { get; }
    public IReadOnlyList<SignedRecord> Records { get; }
    public PeerIndexPage(long afterSequence, long lastSequence, IReadOnlyList<SignedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records); ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (lastSequence < afterSequence || records.Count > 1000) throw new ArgumentOutOfRangeException(nameof(lastSequence));
        var frozen = records.ToArray(); ulong cursor = (ulong)afterSequence;
        foreach (var record in frozen)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (record.LocalSequence <= cursor || record.LocalSequence > (ulong)lastSequence) throw new ArgumentException("Invalid index page sequence.", nameof(records));
            cursor = record.LocalSequence;
        }
        AfterSequence = afterSequence; LastSequence = lastSequence; Records = Array.AsReadOnly(frozen);
    }
    public override string ToString() => "Peer index page (redacted)";
}
