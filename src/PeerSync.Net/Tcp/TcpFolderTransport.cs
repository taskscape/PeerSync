using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Net.Quic;
using PeerSync.Protocol;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Signing;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;

namespace PeerSync.Net.Tcp;

/// <summary>One authenticated folder view over the session's correlated TCP control channel.</summary>
internal sealed class TcpFolderTransport : IPeerTransport, IPeerSessionIdentity, IPeerIndexPushTransport
{
    private readonly TcpPeerProtocolSession owner;
    private readonly AuthenticatedFolder folder;
    private readonly TimeSpan requestTimeout;
    private readonly SemaphoreSlim pushGate = new(1, 1);
    private readonly Channel<long> indexTargets = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
    { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    private long lastPublishedTarget;
    private int disposed;

    internal TcpFolderTransport(TcpPeerProtocolSession owner, AuthenticatedFolder folder, TimeSpan requestTimeout)
    { this.owner = owner; this.folder = folder; this.requestTimeout = requestTimeout; }

    public byte[] RemoteDeviceId => owner.RemoteDeviceId;
    public Guid SessionId => owner.SessionId;
    public ChannelReader<long> IndexTargets => indexTargets.Reader;

    public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hello);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return ValueTask.FromResult(owner.RemoteHello);
    }

    public async ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken = default)
    {
        ulong id = owner.NextRequestId();
        var request = new IndexSummary { FolderId = ByteString.CopyFrom(folder.FolderId), Request = true, RequestId = id };
        MessageValidator.Validate(request, folder.Keys);
        IMessage response = await owner.SendRequestAsync(folder, request, id, cancellationToken).ConfigureAwait(false);
        if (response is not IndexSummary summary) throw new ProtocolException(ProtocolError.UnknownMessageType);
        MessageValidator.Validate(summary, folder.Keys);
        if (summary.Request || summary.RequestId != id) throw new InvalidDataException("TCP index summary does not match its request.");
        return checked((long)summary.HighestSeq);
    }

    public async ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (limit is < 1 or > MessageValidator.MaximumRecords) throw new ArgumentOutOfRangeException(nameof(limit));
        ulong id = owner.NextRequestId();
        var request = new IndexRequest
        {
            FolderId = ByteString.CopyFrom(folder.FolderId), AfterSeq = (ulong)afterSequence,
            Limit = (uint)limit, RequestId = id,
        };
        MessageValidator.Validate(request, folder.Keys);
        IMessage response = await owner.SendRequestAsync(folder, request, id, cancellationToken).ConfigureAwait(false);
        if (response is not IndexUpdate update) throw new ProtocolException(ProtocolError.UnknownMessageType);
        MessageValidator.Validate(update, folder.Keys);
        if (update.RequestId != id || update.AfterSeq != (ulong)afterSequence)
            throw new InvalidDataException("TCP index page does not match its request.");
        return new PeerIndexPage(afterSequence, checked((long)update.LastSeq), update.Records.Select(SignedRecordFactory.FromWire).ToArray());
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PeerBlockResult> results = await ReadBlockBatchAsync([new PeerBlockRequest(hash.ToArray(), length)], cancellationToken)
            .ConfigureAwait(false);
        if (results[0].ErrorCode is { } errorCode) throw new PeerBlockException(errorCode);
        return results[0].Data;
    }

    public async ValueTask<IReadOnlyList<PeerBlockResult>> ReadBlockBatchAsync(IReadOnlyList<PeerBlockRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        return await owner.ReadBlockBatchAsync(folder, requests, timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask PublishIndexTargetAsync(long highestSequence, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(highestSequence);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await pushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (highestSequence <= lastPublishedTarget) return;
            var notification = new IndexSummary
            {
                FolderId = ByteString.CopyFrom(folder.FolderId), HighestSeq = checked((ulong)highestSequence),
                Request = false, RequestId = 0, Notification = true,
            };
            MessageValidator.Validate(notification, folder.Keys);
            await owner.WriteAsync(notification, cancellationToken).ConfigureAwait(false);
            lastPublishedTarget = highestSequence;
            owner.Log("transport.index_push_sent", "tcp_index_push", items: highestSequence);
        }
        finally { pushGate.Release(); }
    }

    internal void PublishTarget(long highestSequence) => indexTargets.Writer.TryWrite(highestSequence);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            indexTargets.Writer.TryComplete();
            pushGate.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
