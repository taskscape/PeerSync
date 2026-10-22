namespace PeerSync.Engine.Sync;

/// <summary>Bounds authenticated block bytes in flight independently for each peer.
/// Successful service grows the peer window additively; transport failures reduce it multiplicatively.
/// Availability responses release capacity without changing the window.</summary>
internal sealed class TransferScheduler
{
    internal const int InitialWindowBytes = 4 * 1024 * 1024;
    internal const int MaximumWindowBytes = 32 * 1024 * 1024;
    private const int MinimumWindowBytes = 256 * 1024;
    private const int MaximumRequestBytes = MaximumWindowBytes;
    private readonly object gate = new();
    private readonly Dictionary<string, PeerWindow> peers = new(StringComparer.Ordinal);
    private int waiting;
    internal int WaitingCount => Volatile.Read(ref waiting);

    internal async ValueTask<Lease> AcquireAsync(string peerId, int bytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        if (bytes is <= 0 or > MaximumRequestBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        PeerWindow peer;
        lock (gate)
        {
            if (!peers.TryGetValue(peerId, out peer!)) peers.Add(peerId, peer = new());
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (peer.Gate)
            {
                if (peer.InFlightBytes == 0 || bytes <= peer.WindowBytes - peer.InFlightBytes)
                {
                    peer.InFlightBytes = checked(peer.InFlightBytes + bytes);
                    return new(peer, bytes);
                }
                changed = peer.Changed.Task;
            }
            Interlocked.Increment(ref waiting);
            try { await changed.WaitAsync(cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref waiting); }
        }
    }

    internal int ReadWindow(string peerId)
    {
        lock (gate)
        {
            if (!peers.TryGetValue(peerId, out PeerWindow? peer)) return InitialWindowBytes;
            lock (peer.Gate) return peer.WindowBytes;
        }
    }

    private static void Release(PeerWindow peer, int bytes, bool? succeeded)
    {
        TaskCompletionSource signal;
        lock (peer.Gate)
        {
            peer.InFlightBytes -= bytes;
            if (peer.InFlightBytes < 0) throw new InvalidOperationException("Transfer window lease underflow.");
            if (succeeded == true)
                peer.WindowBytes = Math.Min(MaximumWindowBytes, peer.WindowBytes + Math.Max(16 * 1024, bytes / 16));
            else if (succeeded == false)
                peer.WindowBytes = Math.Max(MinimumWindowBytes, peer.WindowBytes / 2);
            signal = peer.Changed;
            peer.Changed = NewSignal();
        }
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class PeerWindow
    {
        internal object Gate { get; } = new();
        internal int WindowBytes = InitialWindowBytes;
        internal int InFlightBytes;
        internal TaskCompletionSource Changed = NewSignal();
    }

    internal sealed class Lease : IDisposable
    {
        private readonly PeerWindow peer;
        private readonly int bytes;
        private int completed;
        internal Lease(PeerWindow peer, int bytes) { this.peer = peer; this.bytes = bytes; }

        internal void Complete(bool succeeded)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            Release(peer, bytes, succeeded);
        }

        internal void CompleteWithoutCongestion()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            Release(peer, bytes, succeeded: null);
        }

        public void Dispose() => Complete(succeeded: false);
    }
}
