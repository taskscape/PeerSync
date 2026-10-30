using System.Collections.Concurrent;
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

namespace PeerSync.Net.Relay;

/// <summary>PeerSync handshake, index and blocks multiplexed over one end-to-end TLS relay stream.</summary>
internal sealed class RelayPeerProtocolSession : IAsyncDisposable
{
    private readonly RelayTransport connection;
    private readonly PeerFrameStream frames;
    private readonly PeerHandshakeOptions handshakeOptions;
    private readonly List<AuthenticatedFolder> folders;
    private readonly Hello remoteHello;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<IMessage>> pendingIndex = new();
    private readonly Channel<BlockResponse> blockResponses = Channel.CreateBounded<BlockResponse>(
        new BoundedChannelOptions(MessageValidator.MaximumBlockRequests)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly SemaphoreSlim blockRequestGate = new(1, 1);
    private readonly SemaphoreSlim inboundBlockGate = new(1, 1);
    private readonly ConcurrentDictionary<int, Task> inbound = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DiagnosticJournal? diagnostics;
    private readonly Action<byte[], MessageFailure>? strike;
    private readonly TimeSpan requestTimeout;
    private Task readLoop = Task.CompletedTask;
    private long requestId;
    private int inboundId;
    private int awaitingBlockResponses;
    private int disposed;

    private RelayPeerProtocolSession(RelayTransport connection, PeerFrameStream frames,
        PeerHandshakeOptions handshakeOptions, PeerAuthenticationContext authentication,
        Hello remoteHello, List<AuthenticatedFolder> folders, DiagnosticJournal? diagnostics,
        Action<byte[], MessageFailure>? strike, TimeSpan requestTimeout)
    {
        this.connection = connection; this.frames = frames; this.handshakeOptions = handshakeOptions;
        Authentication = authentication; this.remoteHello = remoteHello.Clone(); this.folders = folders;
        this.diagnostics = diagnostics; this.strike = strike; this.requestTimeout = requestTimeout;
    }

    internal PeerAuthenticationContext Authentication { get; }
    internal byte[] LocalDeviceId => Authentication.LocalDeviceId;
    internal byte[] RemoteDeviceId => Authentication.RemoteDeviceId;
    internal Guid SessionId => Authentication.SessionId;
    internal bool IsInitiator => connection.IsInitiator;
    internal Task Closed => closed.Task;
    internal IReadOnlyList<AuthenticatedFolder> AuthenticatedFolders => folders.AsReadOnly();
    internal Hello RemoteHello => remoteHello.Clone();

    internal static async ValueTask<RelayPeerProtocolSession> StartAsync(RelayTransport connection,
        Func<Hello> createHello, IReadOnlyList<PeerFolderRegistration> registrations,
        DiagnosticJournal? diagnostics, Action<byte[], MessageFailure>? strike,
        TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var options = new PeerHandshakeOptions
        {
            CreateHello = createHello, Folders = registrations, DeviceName = Environment.MachineName,
            ClientVersion = "PeerSync/1", Diagnostics = diagnostics, AddStrike = strike,
            RequestTimeout = requestTimeout, Transport = DiagnosticTransport.Relay,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        var frames = new PeerFrameStream(connection.Stream);
        List<AuthenticatedFolder>? folders = null;
        try
        {
            var result = await PeerHandshake.AuthenticateAsync(frames, options,
                connection.LocalDeviceId, connection.RemoteDeviceId, timeout.Token).ConfigureAwait(false);
            folders = result.Folders;
            foreach (AuthenticatedFolder folder in folders)
                _ = await folder.Handler.ExchangeHelloAsync(result.RemoteHello, timeout.Token).ConfigureAwait(false);
            var session = new RelayPeerProtocolSession(connection, frames, options, result.Authentication,
                result.RemoteHello, folders, diagnostics, strike, requestTimeout);
            foreach (AuthenticatedFolder folder in folders)
                folder.AttachTransport(new RelayFolderTransport(session, folder));
            session.readLoop = session.ReadLoopAsync();
            diagnostics?.TryWrite(new("transport.authenticated", "relay_session")
            { SessionId = session.SessionId, Transport = DiagnosticTransport.Relay });
            return session;
        }
        catch
        {
            if (folders is not null)
                foreach (AuthenticatedFolder folder in folders)
                    try { await folder.Handler.DisposeAsync().ConfigureAwait(false); } catch { }
            await frames.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal ulong NextRequestId() => unchecked((ulong)Interlocked.Increment(ref requestId));

    internal async ValueTask<IMessage> SendIndexRequestAsync(IMessage request, ulong id,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var completion = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingIndex.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate relay request identifier.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(requestTimeout);
        try
        {
            await frames.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // A late response to an abandoned request cannot be distinguished from a
            // malicious unmatched response. Close this relay rather than desynchronize it.
            _ = DisposeAsync().AsTask();
            throw;
        }
        finally { pendingIndex.TryRemove(id, out _); }
    }

    internal ValueTask WriteAsync(IMessage message, CancellationToken cancellationToken)
        => frames.WriteAsync(message, cancellationToken);

    internal async ValueTask<IReadOnlyList<PeerBlockResult>> ReadBlockBatchAsync(AuthenticatedFolder folder,
        IReadOnlyList<PeerBlockRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count is < 1 or > MessageValidator.MaximumBlockRequests)
            throw new ArgumentOutOfRangeException(nameof(requests));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(requestTimeout);
        await blockRequestGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var batch = new BlockRequestBatch { FolderId = ByteString.CopyFrom(folder.FolderId) };
            foreach (PeerBlockRequest item in requests)
            {
                if (item.ChunkHash.Length != 32 || item.Length is <= 0 or > MessageValidator.MaximumBlockBytes)
                    throw new ArgumentException("Invalid relay block request.", nameof(requests));
                batch.Requests.Add(new BlockRequest
                { ChunkHash = ByteString.CopyFrom(item.ChunkHash.Span), Length = (uint)item.Length });
            }
            MessageValidator.Validate(batch, folder.Keys);
            Volatile.Write(ref awaitingBlockResponses, requests.Count);
            await frames.WriteAsync(batch, timeout.Token).ConfigureAwait(false);
            var results = new PeerBlockResult[requests.Count];
            for (int index = 0; index < requests.Count; index++)
            {
                BlockResponse response = await blockResponses.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                MessageValidator.Validate(response);
                if (!CryptographicOperations.FixedTimeEquals(response.ChunkHash.Span, requests[index].ChunkHash.Span))
                    throw new InvalidDataException("Relay block response is out of order.");
                if (response.PayloadCase == BlockResponse.PayloadOneofCase.ErrorCode)
                    results[index] = new(ReadOnlyMemory<byte>.Empty, response.ErrorCode);
                else
                {
                    if (response.Data.Length != requests[index].Length
                        || !CryptographicOperations.FixedTimeEquals(Blake3.Hasher.Hash(response.Data.Span).AsSpan(),
                            requests[index].ChunkHash.Span))
                        throw new InvalidDataException("Relay block response contains invalid bytes.");
                    results[index] = new(response.Data.Memory, null);
                }
            }
            diagnostics?.TryWrite(new("transport.request_complete", "relay_block")
            { SessionId = SessionId, Transport = DiagnosticTransport.Relay,
                Bytes = results.Where(result => result.ErrorCode is null).Sum(result => result.Data.Length) });
            return results;
        }
        catch
        {
            _ = DisposeAsync().AsTask();
            throw;
        }
        finally { blockRequestGate.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                ProtocolFrame? frame = await frames.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (frame is null) throw new EndOfStreamException("Peer closed the relay stream.");
                IMessage message = WireMessageCodec.Decode(frame.Value);
                switch (message)
                {
                    case Close close:
                        MessageValidator.Validate(close);
                        _ = DisposeAsync().AsTask();
                        return;
                    case IndexSummary { Request: false, Notification: true } notification:
                        AuthenticatedFolder? announced = FindFolder(notification.FolderId.Span);
                        if (announced?.Transport is not RelayFolderTransport transport)
                            throw new AuthenticationException("Relay index notification targets an unauthorized folder.");
                        MessageValidator.Validate(notification, announced.Keys);
                        transport.PublishTarget(checked((long)notification.HighestSeq));
                        break;
                    case IndexSummary { Request: true } or IndexRequest or BlockRequestBatch:
                        if (inbound.Count >= 64) throw new InvalidDataException("Too many relay requests.");
                        int taskId = Interlocked.Increment(ref inboundId);
                        Task task = HandleRequestAsync(message, lifetime.Token);
                        inbound.TryAdd(taskId, task);
                        _ = task.ContinueWith(completed =>
                        {
                            inbound.TryRemove(taskId, out _);
                            if (completed.IsFaulted) ReportFailure(completed.Exception!.GetBaseException());
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        break;
                    case BlockResponse block:
                        if (Interlocked.Decrement(ref awaitingBlockResponses) < 0
                            || !blockResponses.Writer.TryWrite(block))
                            throw new InvalidDataException("Unexpected relay block response.");
                        break;
                    case IndexSummary or IndexUpdate:
                        ulong id = message is IndexSummary summary ? summary.RequestId : ((IndexUpdate)message).RequestId;
                        if (!pendingIndex.TryGetValue(id, out TaskCompletionSource<IMessage>? completion))
                            throw new InvalidDataException("Unmatched relay index response.");
                        ReadOnlyMemory<byte> folderId = message is IndexSummary summaryResponse
                            ? summaryResponse.FolderId.Memory : ((IndexUpdate)message).FolderId.Memory;
                        AuthenticatedFolder? responseFolder = FindFolder(folderId.Span);
                        if (responseFolder is null) throw new AuthenticationException("Relay response targets an unauthorized folder.");
                        MessageValidator.Validate(message, responseFolder.Keys);
                        completion.TrySetResult(message);
                        break;
                    default: throw new ProtocolException(ProtocolError.UnknownMessageType);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportFailure(error); }
    }

    private async Task HandleRequestAsync(IMessage message, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> folderId = message switch
        {
            IndexSummary summary => summary.FolderId.Memory,
            IndexRequest request => request.FolderId.Memory,
            BlockRequestBatch batch => batch.FolderId.Memory,
            _ => throw new ProtocolException(ProtocolError.UnknownMessageType),
        };
        AuthenticatedFolder? folder = FindFolder(folderId.Span);
        if (folder is null)
        {
            strike?.Invoke(RemoteDeviceId, MessageFailure.FolderNotAuthorized);
            throw new AuthenticationException("Relay request targets an unauthorized folder.");
        }
        MessageValidator.Validate(message, folder.Keys);
        switch (message)
        {
            case IndexSummary summary:
                var answer = new IndexSummary
                {
                    FolderId = ByteString.CopyFrom(folder.FolderId), RequestId = summary.RequestId,
                    HighestSeq = checked((ulong)await folder.Handler.ReadIndexTargetAsync(cancellationToken)
                        .ConfigureAwait(false)),
                };
                MessageValidator.Validate(answer, folder.Keys);
                await frames.WriteAsync(answer, cancellationToken).ConfigureAwait(false);
                break;
            case IndexRequest request:
                PeerIndexPage page = await folder.Handler.ReadIndexAsync(checked((long)request.AfterSeq),
                    checked((int)request.Limit), cancellationToken).ConfigureAwait(false);
                var update = new IndexUpdate
                {
                    FolderId = ByteString.CopyFrom(folder.FolderId), AfterSeq = request.AfterSeq,
                    LastSeq = checked((ulong)page.LastSequence), RequestId = request.RequestId,
                };
                update.Records.Add(page.Records.Select(SignedRecordFactory.ToWire));
                MessageValidator.Validate(update, folder.Keys);
                await frames.WriteAsync(update, cancellationToken).ConfigureAwait(false);
                break;
            case BlockRequestBatch batch:
                await inboundBlockGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    foreach (BlockRequest item in batch.Requests)
                    {
                        var response = new BlockResponse { ChunkHash = item.ChunkHash };
                        try
                        {
                            ReadOnlyMemory<byte> data = await folder.Handler.ReadBlockAsync(item.ChunkHash.Memory,
                                checked((int)item.Length), cancellationToken).ConfigureAwait(false);
                            byte[] copy = data.ToArray();
                            if (copy.Length != item.Length || !CryptographicOperations.FixedTimeEquals(
                                Blake3.Hasher.Hash(copy).AsSpan(), item.ChunkHash.Span))
                                throw new InvalidDataException("Relay block handler returned invalid bytes.");
                            response.Data = ByteString.CopyFrom(copy);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception error) { response.ErrorCode = QuicFolderTransport.MapBlockError(error); }
                        MessageValidator.Validate(response);
                        await frames.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally { inboundBlockGate.Release(); }
                break;
        }
    }

    private AuthenticatedFolder? FindFolder(ReadOnlySpan<byte> id)
    {
        byte[] value = id.ToArray();
        return folders.FirstOrDefault(folder => CryptographicOperations.FixedTimeEquals(folder.FolderId, value));
    }

    private void ReportFailure(Exception error)
    {
        diagnostics?.TryWrite(new("transport.stream_failed", "relay_session")
        { SessionId = SessionId, Transport = DiagnosticTransport.Relay,
            Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
        foreach (TaskCompletionSource<IMessage> completion in pendingIndex.Values)
            completion.TrySetException(error);
        blockResponses.Writer.TryComplete(error);
        _ = DisposeAsync().AsTask();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return new(closed.Task);
        return DisposeCoreAsync();
    }

    private async ValueTask DisposeCoreAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try { await frames.WriteAsync(new Close { Reason = CloseReason.PeerShutdown }, timeout.Token)
                .ConfigureAwait(false); } catch { }
            await lifetime.CancelAsync().ConfigureAwait(false);
            blockResponses.Writer.TryComplete();
            await connection.DisposeAsync().ConfigureAwait(false);
            try { await readLoop.ConfigureAwait(false); } catch { }
            try { await Task.WhenAll(inbound.Values.ToArray()).ConfigureAwait(false); } catch { }
            foreach (AuthenticatedFolder folder in folders)
            {
                if (folder.Transport is not null) try { await folder.Transport.DisposeAsync().ConfigureAwait(false); } catch { }
                try { await folder.Handler.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            await frames.DisposeAsync().ConfigureAwait(false);
            diagnostics?.TryWrite(new("transport.disconnected", "relay_session")
            { SessionId = SessionId, Transport = DiagnosticTransport.Relay });
            closed.TrySetResult();
        }
        catch (Exception error) { closed.TrySetException(error); throw; }
        finally
        { blockRequestGate.Dispose(); inboundBlockGate.Dispose(); lifetime.Dispose(); }
    }
}
