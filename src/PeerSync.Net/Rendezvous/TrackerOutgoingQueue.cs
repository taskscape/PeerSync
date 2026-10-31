using System.Threading.Channels;
using Grpc.Core;
using PeerSync.Rendezvous.Contracts.V1;

namespace PeerSync.Net.Rendezvous;

/// <summary>Bounded critical requests plus one coalesced latest-announcement
/// signal. A full request queue reports failure instead of silently dropping
/// a relay or punch request.</summary>
internal sealed class TrackerOutgoingQueue
{
    private readonly Channel<ClientMessage?> messages;
    private int refreshPending;

    internal TrackerOutgoingQueue(int capacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        messages = Channel.CreateBounded<ClientMessage?>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    }

    internal bool TryEnqueue(ClientMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return messages.Writer.TryWrite(message);
    }

    internal void Refresh()
    {
        if (Interlocked.Exchange(ref refreshPending, 1) != 0) return;
        // If the queue is full, the writer will observe the flag before the
        // next queued request. If it is empty, the marker wakes the reader.
        messages.Writer.TryWrite(null);
    }

    internal void Reset()
    {
        while (messages.Reader.TryRead(out _)) { }
        Interlocked.Exchange(ref refreshPending, 0);
    }

    internal async Task WriteLoopAsync(IClientStreamWriter<ClientMessage> writer,
        Func<Announce> snapshot, CancellationToken cancellationToken, Action<Announce>? onAnnounced = null,
        Action<ClientMessage>? onSent = null)
    {
        await foreach (ClientMessage? message in messages.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Interlocked.Exchange(ref refreshPending, 0) != 0)
            {
                Announce sent = snapshot();
                await writer.WriteAsync(new ClientMessage { Announce = sent }, cancellationToken).ConfigureAwait(false);
                onAnnounced?.Invoke(sent);
            }
            if (message is not null)
            {
                await writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                onSent?.Invoke(message);
            }
        }
    }

    internal void Complete() => messages.Writer.TryComplete();
}
