using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Protocol.Handshake;

namespace PeerSync.Engine.Sync;

public sealed class PeerClockException(PeerClockStatus status) : IOException(status.DisplayMessage)
{
    public PeerClockStatus Status { get; } = status;
}

/// <summary>One sample per authenticated peer session. This is clock sanity,
/// not authentication. The transport must enforce this guard for both index
/// directions and bind fresh Hello to its authenticated session.</summary>
public sealed class ClockGuard
{
    public const long MaximumSkewTicks = 600 * TimeSpan.TicksPerSecond;
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly DiagnosticJournal? journal;
    private readonly Guid session;
    private Sample? sample;
    private PeerClockState? logged;

    public ClockGuard(TimeProvider? timeProvider = null, DiagnosticJournal? journal = null, Guid? sessionId = null)
    { clock = timeProvider ?? TimeProvider.System; this.journal = journal; session = sessionId ?? Guid.NewGuid(); }

    public PeerClockStatus ObserveHello(Hello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        lock (gate)
        {
            sample = new(hello.UtcTicks, clock.GetTimestamp());
        }
        return StatusAndLog();
    }

    public PeerClockStatus GetStatus() => StatusAndLog();
    public void Reset() { lock (gate) sample = null; _ = StatusAndLog(); }
    public void RequireIndexExchangeAllowed()
    {
        var status = GetStatus(); if (!status.IndexExchangeAllowed) throw new PeerClockException(status);
    }

    internal void RequireIndexExchangeAllowedWithoutDiagnostics()
    {
        PeerClockStatus status; lock (gate) status = Evaluate();
        if (!status.IndexExchangeAllowed) throw new PeerClockException(status);
    }

    private PeerClockStatus StatusAndLog()
    {
        PeerClockStatus status; bool changed;
        lock (gate) { status = Evaluate(); changed = logged != status.State; logged = status.State; }
        if (changed)
        {
            journal?.TryWrite(new(status.IndexExchangeAllowed ? "clock.accepted" : "clock.blocked", "peer-clock")
            {
                SessionId = session, Level = status.IndexExchangeAllowed ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
                ErrorCode = status.State.ToString(), ClockSkewSeconds = status.OffsetTicks / (double?)TimeSpan.TicksPerSecond,
            });
        }
        return status;
    }

    private PeerClockStatus Evaluate()
    {
        if (sample is not { } current) return new(PeerClockState.AwaitingHello);
        if (current.UtcTicks <= 0 || current.UtcTicks > DateTime.MaxValue.Ticks) return new(PeerClockState.InvalidTimestamp);
        TimeSpan elapsed;
        try { elapsed = clock.GetElapsedTime(current.Timestamp, clock.GetTimestamp()); }
        catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException) { return new(PeerClockState.InvalidElapsed); }
        if (elapsed < TimeSpan.Zero || elapsed.Ticks > DateTime.MaxValue.Ticks - current.UtcTicks) return new(PeerClockState.InvalidElapsed);
        long offset = current.UtcTicks + elapsed.Ticks - clock.GetUtcNow().UtcTicks;
        return new(Math.Abs(offset) > MaximumSkewTicks ? PeerClockState.Skewed : PeerClockState.Accepted, offset);
    }

    private sealed record Sample(long UtcTicks, long Timestamp);
}
