using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Net.Quic;
using PeerSync.Net.Security;
using PeerSync.Protocol;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Signing;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;

namespace PeerSync.Net.Tcp;

/// <summary>PeerSync Hello, folder proof and index/block protocol over a mutual-TLS TCP session.</summary>
public sealed class TcpPeerProtocolSession : IAsyncDisposable
{
    private const int MaximumOutstandingRequests = 64;
    private readonly TcpPeerSession connection;
    private readonly TcpPeerProtocolOptions options;
    private readonly PeerFrameStream control;
    private readonly List<AuthenticatedFolder> folders;
    private readonly PeerAuthenticationContext authentication;
    private readonly Hello remoteHello;
    private readonly bool initiator;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requestSlots = new(MaximumOutstandingRequests, MaximumOutstandingRequests);
    private readonly SemaphoreSlim blockSlots;
    private readonly ConcurrentDictionary<ulong, PendingRequest> pending = new();
    private readonly ConcurrentDictionary<ulong, byte> abandoned = new();
    private readonly ConcurrentDictionary<int, Task> inbound = new();
    private readonly ConcurrentDictionary<int, Task> blockTasks = new();
    private readonly TaskCompletionSource disposedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task controlLoop = Task.CompletedTask;
    private Task dataAcceptLoop = Task.CompletedTask;
    private long requestId;
    private int inboundId;
    private int blockTaskId;
    private int requestedCloseReason;
    private int protocolStrikeRecorded;
    private int disposed;

    private TcpPeerProtocolSession(TcpPeerSession connection, TcpPeerProtocolOptions options,
        PeerFrameStream control, PeerAuthenticationContext authentication, Hello remoteHello,
        List<AuthenticatedFolder> folders, bool initiator)
    {
        this.connection = connection;
        this.options = options;
        this.control = control;
        this.authentication = authentication;
        this.remoteHello = remoteHello.Clone();
        this.folders = folders;
        this.initiator = initiator;
        blockSlots = new SemaphoreSlim(options.MaximumConcurrentBlockRequests, options.MaximumConcurrentBlockRequests);
    }

    public PeerAuthenticationContext Authentication => authentication;
    public byte[] LocalDeviceId => authentication.LocalDeviceId;
    public byte[] RemoteDeviceId => authentication.RemoteDeviceId;
    public Guid SessionId => authentication.SessionId;
    public bool IsInitiator => initiator;
    public Task Closed => disposedCompletion.Task;
    public IPEndPoint RemoteEndPoint => connection.RemoteEndPoint;
    public Hello RemoteHello => remoteHello.Clone();
    public IReadOnlyList<AuthenticatedFolder> AuthenticatedFolders => folders.AsReadOnly();

    public static async ValueTask<TcpPeerProtocolSession> StartAsync(TcpPeerSession connection,
        TcpPeerProtocolOptions options, bool initiator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        var control = new PeerFrameStream(connection.ControlStream);
        List<AuthenticatedFolder>? folders = null;
        try
        {
            var handshake = await PeerHandshake.AuthenticateAsync(control, options.HandshakeOptions,
                connection.LocalDeviceId, connection.RemoteDeviceId, timeout.Token).ConfigureAwait(false);
            folders = handshake.Folders;
            foreach (AuthenticatedFolder folder in folders)
                _ = await folder.Handler.ExchangeHelloAsync(handshake.RemoteHello, timeout.Token).ConfigureAwait(false);

            var result = new TcpPeerProtocolSession(connection, options, control, handshake.Authentication,
                handshake.RemoteHello, folders, initiator);
            foreach (AuthenticatedFolder folder in folders)
                folder.AttachTransport(new TcpFolderTransport(result, folder, options.RequestTimeout));
            result.controlLoop = result.ControlLoopAsync();
            result.dataAcceptLoop = result.AcceptDataLoopAsync();
            options.Diagnostics?.TryWrite(new("transport.authenticated", "tcp_session")
                { SessionId = result.SessionId, Transport = DiagnosticTransport.Tcp,
                    RemoteAddress = result.RemoteEndPoint.Address });
            return result;
        }
        catch (Exception error)
        {
            if (folders is not null)
                foreach (AuthenticatedFolder folder in folders)
                    try { await folder.Handler.DisposeAsync().ConfigureAwait(false); } catch { }
            await control.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            options.Diagnostics?.TryWrite(new("transport.handshake_failed", "tcp_session")
                { Transport = DiagnosticTransport.Tcp, Level = DiagnosticLevel.Warning,
                    ErrorCode = SafeError(error), HResult = error.HResult,
                    RemoteAddress = connection.RemoteEndPoint.Address });
            throw;
        }
    }

    internal ulong NextRequestId()
    {
        ulong id = unchecked((ulong)Interlocked.Increment(ref requestId));
        return id == 0 ? unchecked((ulong)Interlocked.Increment(ref requestId)) : id;
    }

    internal async ValueTask<IMessage> SendRequestAsync(AuthenticatedFolder folder, IMessage request,
        ulong id, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.CancelAfter(options.RequestTimeout);
        try { await requestSlots.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            var timeout = new TimeoutException("Timed out waiting for a TCP folder request slot.");
            ReportFailure(timeout);
            throw timeout;
        }

        var completion = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        string folderId = Convert.ToHexString(folder.FolderId);
        if (!pending.TryAdd(id, new(folderId, completion)))
        {
            requestSlots.Release();
            throw new InvalidOperationException("Duplicate TCP request identifier.");
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            options.Diagnostics?.TryWrite(new("transport.request_start", "tcp_index")
                { SessionId = SessionId, Transport = DiagnosticTransport.Tcp });
            await control.WriteAsync(request, linked.Token).ConfigureAwait(false);
            IMessage response = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            options.Diagnostics?.TryWrite(new("transport.request_complete", "tcp_index")
                { SessionId = SessionId, Transport = DiagnosticTransport.Tcp,
                    ItemsVisited = response is IndexUpdate update ? update.Records.Count : null,
                    ElapsedMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            var timeout = new TimeoutException("TCP folder request timed out.");
            ReportFailure(timeout);
            throw timeout;
        }
        finally
        {
            pending.TryRemove(id, out _);
            if (!completion.Task.IsCompleted && !lifetime.IsCancellationRequested)
            {
                if (abandoned.Count >= MaximumOutstandingRequests) ReportFailure(new IOException("Too many abandoned TCP requests."));
                else abandoned.TryAdd(id, 0);
            }
            requestSlots.Release();
        }
    }

    internal ValueTask WriteAsync(IMessage message, CancellationToken cancellationToken)
        => control.WriteAsync(message, cancellationToken);

    private async Task ControlLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                ProtocolFrame? frame = await control.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (frame is null) throw new EndOfStreamException("Peer closed the TCP control connection.");
                IMessage message = WireMessageCodec.Decode(frame.Value);
                if (message is Close close)
                {
                    MessageValidator.Validate(close);
                    options.Diagnostics?.TryWrite(new("transport.remote_close", "tcp_session")
                    {
                        SessionId = SessionId,
                        Transport = DiagnosticTransport.Tcp,
                        ItemsVisited = (long)close.Reason,
                        RemoteAddress = RemoteEndPoint.Address,
                    });
                    Interlocked.CompareExchange(ref requestedCloseReason, (int)close.Reason, 0);
                    _ = DisposeAsync().AsTask();
                    return;
                }
                if (message is IndexSummary { Request: false, Notification: true } notification)
                {
                    AuthenticatedFolder? folder = FindFolder(notification.FolderId.Span);
                    if (folder?.Transport is not TcpFolderTransport tcpTransport)
                        throw new AuthenticationException("The peer published an index target for an unauthorized folder.");
                    MessageValidator.Validate(notification, folder.Keys);
                    tcpTransport.PublishTarget(checked((long)notification.HighestSeq));
                    Log("transport.index_push_received", "tcp_index_push", items: checked((long)notification.HighestSeq));
                    continue;
                }
                if (message is IndexSummary { Request: true } or IndexRequest)
                {
                    if (inbound.Count >= MaximumOutstandingRequests)
                        throw new InvalidDataException("Peer exceeded the concurrent TCP folder request limit.");
                    int id = Interlocked.Increment(ref inboundId);
                    Task task = HandleFolderRequestAsync(message, lifetime.Token);
                    inbound.TryAdd(id, task);
                    _ = task.ContinueWith(completed =>
                    {
                        inbound.TryRemove(id, out _);
                        if (completed.Exception is { } failure) ReportFailure(failure.GetBaseException());
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    continue;
                }

                ulong responseId = GetResponseId(message);
                if (!pending.TryGetValue(responseId, out PendingRequest? pendingRequest))
                {
                    if (abandoned.TryRemove(responseId, out _)) continue;
                    throw new InvalidDataException("Peer sent an unmatched TCP folder response.");
                }
                ValidateResponse(message, pendingRequest.FolderId);
                pendingRequest.Completion.TrySetResult(message);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            foreach (PendingRequest item in pending.Values) item.Completion.TrySetException(error);
            ReportFailure(error);
        }
    }

    private async Task HandleFolderRequestAsync(IMessage message, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> folderId = message switch
        {
            IndexSummary summary => summary.FolderId.Memory,
            IndexRequest request => request.FolderId.Memory,
            _ => throw new ProtocolException(ProtocolError.UnknownMessageType),
        };
        AuthenticatedFolder? folder = FindFolder(folderId.Span);
        if (folder is null)
        {
            options.AddStrike?.Invoke(RemoteDeviceId, MessageFailure.FolderNotAuthorized);
            throw new AuthenticationException("Peer requested an unauthorized TCP folder.");
        }

        IMessage response;
        switch (message)
        {
            case IndexSummary summary:
                MessageValidator.Validate(summary, folder.Keys);
                if (!summary.Request) throw new InvalidDataException("Unexpected TCP index summary notification.");
                response = new IndexSummary
                {
                    FolderId = ByteString.CopyFrom(folder.FolderId),
                    HighestSeq = checked((ulong)await folder.Handler.ReadIndexTargetAsync(cancellationToken).ConfigureAwait(false)),
                    Request = false, RequestId = summary.RequestId,
                };
                MessageValidator.Validate(response, folder.Keys);
                break;
            case IndexRequest request:
                MessageValidator.Validate(request, folder.Keys);
                PeerIndexPage page = await folder.Handler.ReadIndexAsync(checked((long)request.AfterSeq),
                    checked((int)request.Limit), cancellationToken).ConfigureAwait(false);
                if (page.AfterSequence != (long)request.AfterSeq)
                    throw new InvalidDataException("TCP index handler returned a page for a different cursor.");
                var update = new IndexUpdate
                {
                    FolderId = ByteString.CopyFrom(folder.FolderId), AfterSeq = (ulong)page.AfterSequence,
                    LastSeq = (ulong)page.LastSequence, RequestId = request.RequestId,
                };
                update.Records.Add(page.Records.Select(SignedRecordFactory.ToWire));
                MessageValidator.Validate(update, folder.Keys);
                response = update;
                break;
            default: throw new ProtocolException(ProtocolError.UnknownMessageType);
        }
        await control.WriteAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateResponse(IMessage message, string expectedFolderId)
    {
        ReadOnlyMemory<byte> folderId;
        switch (message)
        {
            case IndexSummary summary:
                if (summary.Request || summary.Notification) throw new InvalidDataException("TCP index summary response flags are invalid.");
                folderId = summary.FolderId.Memory;
                break;
            case IndexUpdate update:
                folderId = update.FolderId.Memory;
                break;
            default: throw new ProtocolException(ProtocolError.UnknownMessageType);
        }
        if (!StringComparer.Ordinal.Equals(Convert.ToHexString(folderId.Span), expectedFolderId))
            throw new InvalidDataException("TCP folder response belongs to a different authenticated folder.");
        AuthenticatedFolder? folder = FindFolder(folderId.Span);
        if (folder is null) throw new AuthenticationException("TCP folder response is not authorized.");
        if (!CryptographicOperations.FixedTimeEquals(folderId.Span, folder.Keys.FolderId()))
            throw new AuthenticationException("TCP folder response does not match the authenticated folder key.");
        MessageValidator.Validate(message, folder.Keys);
    }

    private static ulong GetResponseId(IMessage message) => message switch
    {
        IndexSummary summary when !summary.Request => summary.RequestId,
        IndexUpdate update => update.RequestId,
        _ => throw new ProtocolException(ProtocolError.UnknownMessageType),
    };

    private AuthenticatedFolder? FindFolder(ReadOnlySpan<byte> folderId)
    {
        byte[] value = folderId.ToArray();
        return folders.FirstOrDefault(folder => CryptographicOperations.FixedTimeEquals(folder.FolderId, value));
    }

    private async Task AcceptDataLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                TcpDataConnection data = await connection.AcceptDataConnectionAsync(lifetime.Token).ConfigureAwait(false);
                int id = Interlocked.Increment(ref blockTaskId);
                Task task = ServeBlockLaneAsync(data, lifetime.Token);
                blockTasks.TryAdd(id, task);
                _ = task.ContinueWith(completed =>
                {
                    blockTasks.TryRemove(id, out _);
                    if (completed.Exception is { } failure) ReportFailure(failure.GetBaseException());
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ChannelClosedException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportFailure(error); }
    }

    private async Task ServeBlockLaneAsync(TcpDataConnection data, CancellationToken cancellationToken)
    {
        await using (data.ConfigureAwait(false))
        await using (var frames = new PeerFrameStream(data.Stream))
        {
            ProtocolFrame? frame = await frames.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null) return;
            if (WireMessageCodec.Decode(frame.Value) is not BlockRequestBatch request)
                throw new ProtocolException(ProtocolError.UnknownMessageType);
            AuthenticatedFolder? folder = FindFolder(request.FolderId.Span);
            if (folder is null)
            {
                options.AddStrike?.Invoke(RemoteDeviceId, MessageFailure.FolderNotAuthorized);
                throw new AuthenticationException("TCP block lane targets an unauthorized folder.");
            }
            MessageValidator.Validate(request, folder.Keys);
            Log("transport.block_stream_accepted", "tcp_block", items: request.Requests.Count);
            foreach (BlockRequest item in request.Requests)
            {
                var response = new BlockResponse { ChunkHash = item.ChunkHash };
                try
                {
                    ReadOnlyMemory<byte> block = await folder.Handler.ReadBlockAsync(item.ChunkHash.Memory,
                        checked((int)item.Length), cancellationToken).ConfigureAwait(false);
                    byte[] frozen = block.ToArray();
                    if (frozen.Length != item.Length || !CryptographicOperations.FixedTimeEquals(Blake3.Hasher.Hash(frozen).AsSpan(), item.ChunkHash.Span))
                        throw new InvalidDataException("TCP block handler returned invalid bytes.");
                    response.Data = ByteString.CopyFrom(frozen);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    response.ErrorCode = QuicFolderTransport.MapBlockError(error);
                    Log("transport.block_request_unavailable", "tcp_block", DiagnosticLevel.Warning,
                        errorCode: response.ErrorCode.ToString());
                }
                MessageValidator.Validate(response);
                await frames.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async ValueTask<IReadOnlyList<PeerBlockResult>> ReadBlockBatchAsync(AuthenticatedFolder folder,
        IReadOnlyList<PeerBlockRequest> requests, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (requests.Count is < 1 or > MessageValidator.MaximumBlockRequests)
            throw new ArgumentOutOfRangeException(nameof(requests));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.CancelAfter(options.RequestTimeout);
        await blockSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await using TcpDataConnection data = await connection.OpenDataConnectionAsync(RemoteEndPoint, linked.Token).ConfigureAwait(false);
            await using var frames = new PeerFrameStream(data.Stream);
            var request = new BlockRequestBatch { FolderId = ByteString.CopyFrom(folder.FolderId) };
            foreach (PeerBlockRequest item in requests)
            {
                if (item.ChunkHash.Length != 32 || item.Length is <= 0 or > MessageValidator.MaximumBlockBytes)
                    throw new ArgumentException("A TCP block batch contains an invalid request.", nameof(requests));
                request.Requests.Add(new BlockRequest { ChunkHash = ByteString.CopyFrom(item.ChunkHash.Span), Length = (uint)item.Length });
            }
            MessageValidator.Validate(request, folder.Keys);
            await frames.WriteAsync(request, linked.Token).ConfigureAwait(false);
            var results = new PeerBlockResult[requests.Count];
            for (int index = 0; index < requests.Count; index++)
            {
                ProtocolFrame? frame = await frames.ReadAsync(linked.Token).ConfigureAwait(false);
                if (frame is null || WireMessageCodec.Decode(frame.Value) is not BlockResponse response)
                    throw new EndOfStreamException("TCP peer closed a block batch before completing it.");
                MessageValidator.Validate(response);
                if (!CryptographicOperations.FixedTimeEquals(response.ChunkHash.Span, requests[index].ChunkHash.Span))
                    throw new InvalidDataException("TCP peer returned a block response out of request order.");
                if (response.PayloadCase == BlockResponse.PayloadOneofCase.ErrorCode)
                    results[index] = new(ReadOnlyMemory<byte>.Empty, response.ErrorCode);
                else
                {
                    if (response.Data.Length != requests[index].Length
                        || !CryptographicOperations.FixedTimeEquals(Blake3.Hasher.Hash(response.Data.Span).AsSpan(), requests[index].ChunkHash.Span))
                        throw new InvalidDataException("TCP peer returned invalid block bytes.");
                    results[index] = new(response.Data.Memory, null);
                }
            }
            Log("transport.request_complete", "tcp_block", bytes: results.Where(item => item.ErrorCode is null).Sum(item => item.Data.Length),
                items: results.Length);
            return results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            var timeout = new TimeoutException("TCP block request timed out.");
            ReportFailure(timeout);
            throw timeout;
        }
        finally { blockSlots.Release(); }
    }

    internal void Log(string eventName, string operation, DiagnosticLevel level = DiagnosticLevel.Debug,
        long? bytes = null, long? items = null, string? errorCode = null)
        => options.Diagnostics?.TryWrite(new(eventName, operation)
        {
            SessionId = SessionId, Transport = DiagnosticTransport.Tcp, Level = level,
            Bytes = bytes, ItemsVisited = items, ErrorCode = errorCode, RemoteAddress = RemoteEndPoint.Address,
        });

    private void ReportFailure(Exception error)
    {
        if (ProtocolViolationStrike.TryGetFailure(error, out MessageFailure failure)
            && Interlocked.Exchange(ref protocolStrikeRecorded, 1) == 0)
            ProtocolViolationStrike.Record(options.AddStrike, RemoteDeviceId, failure, options.Diagnostics,
                DiagnosticTransport.Tcp, SessionId, "tcp_session");
        options.Diagnostics?.TryWrite(new("transport.stream_failed", "tcp_session")
        {
            SessionId = SessionId, Transport = DiagnosticTransport.Tcp, Level = DiagnosticLevel.Warning,
            ErrorCode = SafeError(error), HResult = error.HResult, RemoteAddress = RemoteEndPoint.Address,
        });
        Interlocked.CompareExchange(ref requestedCloseReason,
            error is TimeoutException ? (int)CloseReason.Timeout
                : error is AuthenticationException or MessageValidationException or ProtocolException
                    ? (int)CloseReason.ProtocolViolation : (int)CloseReason.InternalError, 0);
        _ = DisposeAsync().AsTask();
    }

    private static string SafeError(Exception error) => error switch
    {
        AuthenticationException => "authentication_failed",
        MessageValidationException invalid => $"message_{invalid.Failure}",
        ProtocolException protocol => $"protocol_{protocol.Code}",
        OperationCanceledException => "cancelled_or_timed_out",
        IOException => "io_failed",
        _ => "operation_failed",
    };

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return new(disposedCompletion.Task);
        return DisposeCoreAsync();
    }

    private async ValueTask DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        try
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            CloseReason reason = (CloseReason)Volatile.Read(ref requestedCloseReason);
            await control.WriteAsync(new Close { Reason = reason == CloseReason.Unspecified ? CloseReason.PeerShutdown : reason }, closeTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            options.Diagnostics?.TryWrite(new("transport.close_frame_failed", "tcp_session")
                { SessionId = SessionId, Transport = DiagnosticTransport.Tcp, Level = DiagnosticLevel.Debug,
                    ErrorCode = SafeError(error), HResult = error.HResult });
        }
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { await controlLoop.ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { await dataAcceptLoop.ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { await Task.WhenAll(inbound.Values.Concat(blockTasks.Values)).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { (failures ??= []).Add(error); }
        foreach (AuthenticatedFolder folder in folders)
        {
            try { if (folder.Transport is not null) await folder.Transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
            try { await folder.Handler.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        try { await control.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        requestSlots.Dispose(); blockSlots.Dispose(); lifetime.Dispose();
        options.Diagnostics?.TryWrite(new("transport.disconnected", "tcp_session")
            { SessionId = SessionId, Transport = DiagnosticTransport.Tcp, RemoteAddress = RemoteEndPoint.Address });
        if (failures is { Count: > 0 }) disposedCompletion.TrySetException(new AggregateException(failures));
        else disposedCompletion.TrySetResult();
        if (failures is { Count: > 0 }) throw new AggregateException(failures);
    }

    private sealed record PendingRequest(string FolderId, TaskCompletionSource<IMessage> Completion);
}
