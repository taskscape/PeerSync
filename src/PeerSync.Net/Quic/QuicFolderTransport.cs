using System.Collections.Concurrent;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Net.Security;
using PeerSync.Net;
using PeerSync.Protocol;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Signing;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;

namespace PeerSync.Net.Quic;

/// <summary>One authenticated folder's correlated index stream and bounded block-stream pool.</summary>
internal sealed class QuicFolderTransport : IPeerTransport, IPeerSessionIdentity, IPeerIndexPushTransport
{
    private const int MaximumOutstandingRequests = 64;
    private readonly QuicPeerSession owner;
    private readonly QuicConnection connection;
    private readonly PeerFrameStream index;
    private readonly PeerAuthenticationContext authentication;
    private readonly AuthenticatedFolder folder;
    private readonly SemaphoreSlim requestSlots = new(MaximumOutstandingRequests);
    private readonly SemaphoreSlim indexPushGate = new(1, 1);
    private readonly Channel<long> indexTargets = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
    { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
    private readonly SemaphoreSlim blockSlots;
    private readonly TimeSpan requestTimeout;
    private readonly ConcurrentQueue<PeerFrameStream> availableBlockStreams = new();
    private readonly ConcurrentDictionary<PeerFrameStream, byte> allBlockStreams = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<IMessage>> pending = new();
    private readonly ConcurrentDictionary<ulong, byte> abandoned = new();
    private readonly ConcurrentDictionary<int, Task> inbound = new();
    private readonly Task reader;
    private long requestId;
    private long lastPublishedIndexTarget;
    private int inboundId;
    private int disposed;

    internal QuicFolderTransport(QuicPeerSession owner, QuicConnection connection, QuicStream indexStream,
        PeerAuthenticationContext authentication, AuthenticatedFolder folder, SemaphoreSlim blockSlots, TimeSpan requestTimeout)
    {
        this.owner = owner; this.connection = connection; index = new(indexStream);
        this.authentication = authentication; this.folder = folder;
        this.blockSlots = blockSlots;
        this.requestTimeout = requestTimeout;
        reader = ReadLoopAsync();
    }

    public byte[] RemoteDeviceId => authentication.RemoteDeviceId.ToArray();
    public Guid SessionId => authentication.SessionId;
    public ChannelReader<long> IndexTargets => indexTargets.Reader;

    public async ValueTask PublishIndexTargetAsync(long highestSequence, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(highestSequence);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await indexPushGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (highestSequence <= lastPublishedIndexTarget) return;
            var notification = new IndexSummary
            {
                FolderId = ByteString.CopyFrom(folder.FolderId), HighestSeq = checked((ulong)highestSequence),
                Request = false, RequestId = 0, Notification = true,
            };
            MessageValidator.Validate(notification, folder.Keys);
            await index.WriteAsync(notification, linked.Token).ConfigureAwait(false);
            lastPublishedIndexTarget = highestSequence;
            owner.LogTransportEvent("transport.index_push_sent", "quic_index_push", items: highestSequence);
        }
        finally { indexPushGate.Release(); }
    }

    public async ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hello);
        Hello request = hello.Clone(); request.Response = false; request.RequestId = NextRequestId();
        MessageValidator.Validate(request);
        Hello response = await SendRequestAsync(request, request.RequestId, cancellationToken).ConfigureAwait(false) as Hello
            ?? throw new ProtocolException(ProtocolError.UnknownMessageType);
        MessageValidator.Validate(response);
        if (!response.Response || response.RequestId != request.RequestId
            || !CryptographicOperations.FixedTimeEquals(response.DeviceId.Span, authentication.RemoteDeviceId))
            throw new InvalidDataException("Peer Hello response does not match the authenticated device.");
        return response;
    }

    public async ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken = default)
    {
        ulong id = NextRequestId();
        var request = new IndexSummary { FolderId = ByteString.CopyFrom(folder.FolderId), Request = true, RequestId = id };
        MessageValidator.Validate(request, folder.Keys);
        var response = await SendRequestAsync(request, id, cancellationToken).ConfigureAwait(false) as IndexSummary
            ?? throw new ProtocolException(ProtocolError.UnknownMessageType);
        MessageValidator.Validate(response, folder.Keys);
        if (response.Request || response.RequestId != id) throw new InvalidDataException("Peer index summary does not match its request.");
        return checked((long)response.HighestSeq);
    }

    public async ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (limit is < 1 or > MessageValidator.MaximumRecords) throw new ArgumentOutOfRangeException(nameof(limit));
        ulong id = NextRequestId();
        var request = new IndexRequest { FolderId = ByteString.CopyFrom(folder.FolderId), AfterSeq = (ulong)afterSequence, Limit = (uint)limit, RequestId = id };
        MessageValidator.Validate(request, folder.Keys);
        var response = await SendRequestAsync(request, id, cancellationToken).ConfigureAwait(false) as IndexUpdate
            ?? throw new ProtocolException(ProtocolError.UnknownMessageType);
        MessageValidator.Validate(response, folder.Keys);
        if (response.RequestId != id || response.AfterSeq != (ulong)afterSequence)
            throw new InvalidDataException("Peer index page does not match its request.");
        var records = response.Records.Select(SignedRecordFactory.FromWire).ToArray();
        return new PeerIndexPage(afterSequence, checked((long)response.LastSeq), records);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length,
        CancellationToken cancellationToken = default)
    {
        var results = await ReadBlockBatchAsync([new PeerBlockRequest(hash.ToArray(), length)], cancellationToken).ConfigureAwait(false);
        PeerBlockResult result = results[0];
        if (result.ErrorCode is { } errorCode) throw new PeerBlockException(errorCode);
        return result.Data;
    }

    public async ValueTask<IReadOnlyList<PeerBlockResult>> ReadBlockBatchAsync(
        IReadOnlyList<PeerBlockRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > MessageValidator.MaximumBlockRequests) throw new ArgumentOutOfRangeException(nameof(requests));
        foreach (PeerBlockRequest item in requests)
            if (item.ChunkHash.Length != 32 || item.Length is <= 0 or > MessageValidator.MaximumBlockBytes)
                throw new ArgumentException("A block batch contains an invalid request.", nameof(requests));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.CancelAfter(requestTimeout);
        try { await blockSlots.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            var timeout = new TimeoutException("Timed out waiting for a QUIC block stream.");
            owner.ReportTransportFailure(timeout); throw timeout;
        }
        PeerFrameStream? framed = null;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int requestedBytes = requests.Sum(item => item.Length);
        owner.LogTransportEvent("transport.request_start", "quic_block", bytes: requestedBytes, items: requests.Count);
        try
        {
            if (!availableBlockStreams.TryDequeue(out framed))
            {
                QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, linked.Token).ConfigureAwait(false);
                framed = new PeerFrameStream(stream);
                allBlockStreams.TryAdd(framed, 0);
                owner.LogTransportEvent("transport.block_stream_opened", "quic_block", items: allBlockStreams.Count);
            }
            var request = new BlockRequestBatch
            {
                FolderId = ByteString.CopyFrom(folder.FolderId),
            };
            foreach (PeerBlockRequest item in requests)
                request.Requests.Add(new BlockRequest { ChunkHash = ByteString.CopyFrom(item.ChunkHash.Span), Length = (uint)item.Length });
            MessageValidator.Validate(request, folder.Keys);
            await framed.WriteAsync(request, linked.Token).ConfigureAwait(false);
            var results = new PeerBlockResult[requests.Count];
            for (int i = 0; i < requests.Count; i++)
            {
                ProtocolFrame? frame = await framed.ReadAsync(linked.Token).ConfigureAwait(false);
                if (frame is null) throw new EndOfStreamException("Peer closed a block stream before completing its batch.");
                if (WireMessageCodec.Decode(frame.Value) is not BlockResponse response) throw new ProtocolException(ProtocolError.UnknownMessageType);
                MessageValidator.Validate(response);
                if (!CryptographicOperations.FixedTimeEquals(response.ChunkHash.Span, requests[i].ChunkHash.Span))
                    throw new InvalidDataException("Peer returned a block response out of request order.");
                if (response.PayloadCase == BlockResponse.PayloadOneofCase.ErrorCode)
                    results[i] = new(ReadOnlyMemory<byte>.Empty, response.ErrorCode);
                else
                {
                    if (response.Data.Length != requests[i].Length) throw new InvalidDataException("Peer returned a block with the wrong length.");
                    results[i] = new(response.Data.ToByteArray(), null);
                }
            }
            owner.LogTransportEvent("transport.request_complete", "quic_block", bytes: results.Where(item => item.ErrorCode is null).Sum(item => item.Data.Length),
                items: requests.Count, elapsed: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            availableBlockStreams.Enqueue(framed); framed = null;
            return results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            if (framed is not null)
            {
                allBlockStreams.TryRemove(framed, out _);
                try { await framed.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            var timeout = new TimeoutException("QUIC block request timed out.");
            owner.ReportTransportFailure(timeout); throw timeout;
        }
        catch
        {
            if (framed is not null)
            {
                allBlockStreams.TryRemove(framed, out _);
                try { await framed.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            throw;
        }
        finally { blockSlots.Release(); }
    }

    internal async ValueTask ServeBlockStreamAsync(PeerFrameStream framed, BlockRequestBatch request, CancellationToken token)
    {
        while (true)
        {
            MessageValidator.Validate(request, folder.Keys);
            // A stream stays assigned to this authenticated folder and is reused for later batches.
            foreach (BlockRequest item in request.Requests)
            {
                var response = new BlockResponse { ChunkHash = item.ChunkHash };
                try
                {
                    ReadOnlyMemory<byte> bytes = await folder.Handler.ReadBlockAsync(item.ChunkHash.Memory, checked((int)item.Length), token).ConfigureAwait(false);
                    var frozen = bytes.ToArray();
                    if (frozen.Length != item.Length || !CryptographicOperations.FixedTimeEquals(
                        Blake3.Hasher.Hash(frozen).AsSpan(), item.ChunkHash.Span))
                        throw new InvalidDataException("Authenticated block handler returned invalid bytes.");
                    response.Data = ByteString.CopyFrom(frozen);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    response.ErrorCode = MapBlockError(error);
                    owner.LogTransportEvent("transport.block_request_unavailable", "quic_block", DiagnosticLevel.Warning,
                        errorCode: response.ErrorCode.ToString());
                }
                MessageValidator.Validate(response);
                await framed.WriteAsync(response, token).ConfigureAwait(false);
            }
            ProtocolFrame? frame = await framed.ReadAsync(token).ConfigureAwait(false);
            if (frame is null) return;
            if (WireMessageCodec.Decode(frame.Value) is not BlockRequestBatch nextRequest) throw new ProtocolException(ProtocolError.UnknownMessageType);
            request = nextRequest;
        }
    }

    internal static BlockError MapBlockError(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => BlockError.NotFound,
        UnauthorizedAccessException => BlockError.Forbidden,
        TimeoutException => BlockError.Busy,
        _ => BlockError.Unavailable,
    };

    private async ValueTask DisposeBlockStreamsAsync()
    {
        while (availableBlockStreams.TryDequeue(out _)) { }
        foreach (PeerFrameStream stream in allBlockStreams.Keys)
        {
            if (!allBlockStreams.TryRemove(stream, out _)) continue;
            try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private async Task<IMessage> SendRequestAsync(IMessage message, ulong id, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        linked.CancelAfter(requestTimeout);
        try { await requestSlots.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            var timeout = new TimeoutException("Timed out waiting for a QUIC folder request slot.");
            owner.ReportTransportFailure(timeout); throw timeout;
        }
        var completion = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) { requestSlots.Release(); throw new InvalidOperationException("Duplicate request identifier."); }
        string operation = message switch { Hello => "quic_hello", IndexSummary => "quic_index_summary", _ => "quic_index_page" };
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            owner.LogTransportEvent("transport.request_start", operation);
            await index.WriteAsync(message, linked.Token).ConfigureAwait(false);
            IMessage response = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            owner.LogTransportEvent("transport.request_complete", operation,
                items: response is IndexUpdate update ? update.Records.Count : null,
                elapsed: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            var timeout = new TimeoutException("QUIC folder request timed out.");
            owner.ReportTransportFailure(timeout); throw timeout;
        }
        finally
        {
            pending.TryRemove(id, out _);
            if (!completion.Task.IsCompleted && !lifetime.IsCancellationRequested)
            {
                if (abandoned.Count >= MaximumOutstandingRequests)
                    owner.ReportTransportFailure(new IOException("Too many abandoned QUIC requests."));
                else abandoned.TryAdd(id, 0);
            }
            requestSlots.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                ProtocolFrame? frame = await index.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (frame is null) throw new EndOfStreamException("Peer closed the folder index stream.");
                IMessage message = WireMessageCodec.Decode(frame.Value);
                if (IsRequest(message))
                {
                    if (inbound.Count >= MaximumOutstandingRequests)
                        throw new InvalidDataException("Peer exceeded the concurrent folder request limit.");
                    int id = Interlocked.Increment(ref inboundId);
                    Task task = HandleRequestAsync(message, lifetime.Token);
                    inbound.TryAdd(id, task);
                    _ = task.ContinueWith(completed =>
                    {
                        inbound.TryRemove(id, out _);
                        if (completed.Exception is { } failure) owner.ReportTransportFailure(failure.GetBaseException());
                    }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    continue;
                }
                if (message is IndexSummary { Request: false, RequestId: 0, Notification: true } notification)
                {
                    MessageValidator.Validate(notification, folder.Keys);
                    indexTargets.Writer.TryWrite(checked((long)notification.HighestSeq));
                    owner.LogTransportEvent("transport.index_push_received", "quic_index_push", items: checked((long)notification.HighestSeq));
                    continue;
                }
                ulong requestId = ResponseRequestId(message);
                ValidateResponse(message);
                if (!pending.TryGetValue(requestId, out var completion))
                {
                    if (abandoned.TryRemove(requestId, out _)) continue;
                    throw new InvalidDataException("Peer sent an unmatched folder-stream response.");
                }
                completion.TrySetResult(message);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            foreach (var request in pending.Values) request.TrySetException(error);
            owner.ReportTransportFailure(error);
            try { await lifetime.CancelAsync().ConfigureAwait(false); } catch { }
        }
        finally { indexTargets.Writer.TryComplete(); }
    }

    private async Task HandleRequestAsync(IMessage message, CancellationToken token)
    {
        string operation = message switch { Hello => "quic_hello", IndexSummary => "quic_index_summary", _ => "quic_index_page" };
        owner.LogTransportEvent("transport.request_received", operation);
        IMessage response;
        switch (message)
        {
            case Hello hello:
                MessageValidator.Validate(hello);
                if (hello.Response || !CryptographicOperations.FixedTimeEquals(hello.DeviceId.Span, authentication.RemoteDeviceId))
                    throw new InvalidDataException("Peer Hello identity is invalid.");
                Hello helloResponse = (await folder.Handler.ExchangeHelloAsync(hello, token).ConfigureAwait(false)).Clone();
                helloResponse.RequestId = hello.RequestId; helloResponse.Response = true;
                if (!helloResponse.DeviceId.Equals(ByteString.CopyFrom(authentication.LocalDeviceId)))
                    throw new InvalidDataException("Hello handler returned an untrusted local identity.");
                MessageValidator.Validate(helloResponse); response = helloResponse; break;
            case IndexSummary summary:
                MessageValidator.Validate(summary, folder.Keys);
                if (!summary.Request) throw new InvalidDataException("Unexpected unsolicited index summary.");
                response = new IndexSummary
                {
                    FolderId = ByteString.CopyFrom(folder.FolderId), HighestSeq = checked((ulong)await folder.Handler.ReadIndexTargetAsync(token).ConfigureAwait(false)),
                    Request = false, RequestId = summary.RequestId,
                };
                MessageValidator.Validate(response, folder.Keys);
                break;
            case IndexRequest request:
                MessageValidator.Validate(request, folder.Keys);
                PeerIndexPage page = await folder.Handler.ReadIndexAsync(checked((long)request.AfterSeq), checked((int)request.Limit), token).ConfigureAwait(false);
                if (page.AfterSequence != (long)request.AfterSeq) throw new InvalidDataException("Index handler returned a page for a different cursor.");
                var update = new IndexUpdate
                {
                    FolderId = ByteString.CopyFrom(folder.FolderId), AfterSeq = (ulong)page.AfterSequence,
                    LastSeq = (ulong)page.LastSequence, RequestId = request.RequestId,
                };
                update.Records.Add(page.Records.Select(SignedRecordFactory.ToWire));
                MessageValidator.Validate(update, folder.Keys); response = update;
                break;
            default:
                throw new InvalidDataException("Peer sent an unsupported folder-stream message.");
        }
        await index.WriteAsync(response, token).ConfigureAwait(false);
        owner.LogTransportEvent("transport.request_served", operation,
            items: response is IndexUpdate servedUpdate ? servedUpdate.Records.Count : null);
    }

    private void ValidateResponse(IMessage message)
    {
        if (message is Hello hello)
        {
            MessageValidator.Validate(hello);
            if (!hello.Response || !CryptographicOperations.FixedTimeEquals(hello.DeviceId.Span, authentication.RemoteDeviceId))
                throw new InvalidDataException("Peer Hello response identity is invalid.");
        }
        else MessageValidator.Validate(message, folder.Keys);
    }

    private static bool IsRequest(IMessage message) => message switch
    {
        Hello hello => !hello.Response,
        IndexSummary summary => summary.Request,
        IndexRequest => true,
        _ => false,
    };

    private static ulong ResponseRequestId(IMessage message) => message switch
    {
        Hello hello => hello.RequestId,
        IndexSummary summary => summary.RequestId,
        IndexUpdate update => update.RequestId,
        _ => throw new InvalidDataException("Peer sent an unsupported folder-stream response."),
    };

    private ulong NextRequestId()
    {
        ulong id = unchecked((ulong)Interlocked.Increment(ref requestId));
        return id == 0 ? unchecked((ulong)Interlocked.Increment(ref requestId)) : id;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await lifetime.CancelAsync().ConfigureAwait(false); } catch { }
        indexTargets.Writer.TryComplete();
        foreach (var request in pending.Values) request.TrySetCanceled(lifetime.Token);
        abandoned.Clear();
        await DisposeBlockStreamsAsync().ConfigureAwait(false);
        try { await index.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await reader.ConfigureAwait(false); } catch { }
        try { await Task.WhenAll(inbound.Values).ConfigureAwait(false); } catch { }
        await indexPushGate.WaitAsync().ConfigureAwait(false);
        indexPushGate.Release();
        requestSlots.Dispose(); indexPushGate.Dispose(); lifetime.Dispose();
    }
}
