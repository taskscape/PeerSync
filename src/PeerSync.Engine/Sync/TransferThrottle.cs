using System.Diagnostics;

namespace PeerSync.Engine.Sync;

/// <summary>Shared token bucket for one device direction. The owner must keep it
/// alive until every folder engine using it has drained.</summary>
internal sealed class TransferThrottle : IDisposable
{
    private const int SliceBytes = 64 * 1024;
    private readonly int bytesPerSecond;
    private readonly int maximumSliceBytes;
    private readonly double tokenLimit;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private double tokens;
    private long lastTimestamp;
    private int disposed;

    internal TransferThrottle(int bytesPerSecond, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        this.bytesPerSecond = bytesPerSecond;
        maximumSliceBytes = Math.Max(1, Math.Min(SliceBytes, bytesPerSecond / 4));
        tokenLimit = Math.Max(bytesPerSecond / 4d, maximumSliceBytes);
        tokens = tokenLimit;
        clock = timeProvider ?? TimeProvider.System;
        lastTimestamp = clock.GetTimestamp();
    }

    /// <summary>Consumes permits in bounded slices and returns the time spent
    /// waiting. A zero rate is represented by not constructing a throttle.</summary>
    internal async ValueTask<TimeSpan> AcquireAsync(int byteCount, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        var elapsed = Stopwatch.StartNew();
        for (int remaining = byteCount; remaining > 0;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int slice = Math.Min(remaining, maximumSliceBytes);
            await AcquireSliceAsync(slice, cancellationToken).ConfigureAwait(false);
            remaining -= slice;
        }
        return elapsed.Elapsed;
    }

    private async Task AcquireSliceAsync(int permits, CancellationToken cancellationToken)
    {
        while (true)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan delay;
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                Refill();
                if (tokens >= permits)
                {
                    tokens -= permits;
                    return;
                }
                delay = TimeSpan.FromSeconds((permits - tokens) / bytesPerSecond);
            }
            finally { gate.Release(); }

            await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        long now = clock.GetTimestamp();
        long elapsed = Math.Max(0, now - lastTimestamp);
        tokens = Math.Min(tokenLimit, tokens + elapsed * (double)bytesPerSecond / clock.TimestampFrequency);
        lastTimestamp = now;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Dispose();
    }
}
