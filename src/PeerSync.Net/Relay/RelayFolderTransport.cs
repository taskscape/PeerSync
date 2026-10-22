using System.Threading.Channels;
using Google.Protobuf;
using PeerSync.Core.Sync;
using PeerSync.Net.Quic;
using PeerSync.Protocol;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Signing;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;

namespace PeerSync.Net.Relay;

/// <summary>One admitted folder over the end-to-end encrypted relay session.</summary>
internal sealed class RelayFolderTransport(RelayPeerProtocolSession owner, AuthenticatedFolder folder)
    : IPeerTransport, IPeerSessionIdentity, IPeerIndexPushTransport
{
    private readonly SemaphoreSlim pushGate = new(1, 1);
    private readonly Channel<long> indexTargets = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest });
    private long lastPublished;
    private int disposed;

    public byte[] RemoteDeviceId => owner.RemoteDeviceId;
    public Guid SessionId => owner.SessionId;
    public ChannelReader<long> IndexTargets => indexTargets.Reader;

    public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return ValueTask.FromResult(owner.RemoteHello);
    }

    public async ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken = default)
    {
        ulong id = owner.NextRequestId();
        var request = new IndexSummary
        { FolderId = ByteString.CopyFrom(folder.FolderId), Request = true, RequestId = id };
        MessageValidator.Validate(request, folder.Keys);
        IMessage response = await owner.SendIndexRequestAsync(request, id, cancellationToken).ConfigureAwait(false);
        if (response is not IndexSummary summary || summary.Request || summary.RequestId != id)
            throw new InvalidDataException("Relay index summary did not match the request.");
        MessageValidator.Validate(summary, folder.Keys);
        return checked((long)summary.HighestSeq);
    }

    public async ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (limit is < 1 or > MessageValidator.MaximumRecords) throw new ArgumentOutOfRangeException(nameof(limit));
        ulong id = owner.NextRequestId();
        var request = new IndexRequest
        { FolderId = ByteString.CopyFrom(folder.FolderId), AfterSeq = (ulong)afterSequence,
            Limit = (uint)limit, RequestId = id };
        MessageValidator.Validate(request, folder.Keys);
        IMessage response = await owner.SendIndexRequestAsync(request, id, cancellationToken).ConfigureAwait(false);
        if (response is not IndexUpdate update || update.RequestId != id || update.AfterSeq != (ulong)afterSequence)
            throw new InvalidDataException("Relay index page did not match the request.");
        MessageValidator.Validate(update, folder.Keys);
        return new PeerIndexPage(afterSequence, checked((long)update.LastSeq),
            update.Records.Select(SignedRecordFactory.FromWire).ToArray());
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PeerBlockResult> result = await ReadBlockBatchAsync(
            [new PeerBlockRequest(hash.ToArray(), length)], cancellationToken).ConfigureAwait(false);
        if (result[0].ErrorCode is { } error) throw new PeerBlockException(error);
        return result[0].Data;
    }

    public ValueTask<IReadOnlyList<PeerBlockResult>> ReadBlockBatchAsync(
        IReadOnlyList<PeerBlockRequest> requests, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return owner.ReadBlockBatchAsync(folder, requests, cancellationToken);
    }

    public async ValueTask PublishIndexTargetAsync(long highestSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(highestSequence);
        await pushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (highestSequence <= lastPublished) return;
            var notification = new IndexSummary
            { FolderId = ByteString.CopyFrom(folder.FolderId), HighestSeq = (ulong)highestSequence,
                Notification = true };
            MessageValidator.Validate(notification, folder.Keys);
            await owner.WriteAsync(notification, cancellationToken).ConfigureAwait(false);
            lastPublished = highestSequence;
        }
        finally { pushGate.Release(); }
    }

    internal void PublishTarget(long highestSequence) => indexTargets.Writer.TryWrite(highestSequence);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        { indexTargets.Writer.TryComplete(); pushGate.Dispose(); }
        return ValueTask.CompletedTask;
    }
}
