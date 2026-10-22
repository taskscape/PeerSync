using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Google.Protobuf.Collections;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Security;
using PeerSync.Protocol;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;

namespace PeerSync.Net.Quic;

/// <summary>A mutually authenticated QUIC connection with direction-bound folder admission.
/// The session owns its accepted folder handlers and must be drained before its keys or
/// certificate are disposed.</summary>
public sealed class QuicPeerSession : IAsyncDisposable
{
    private readonly QuicConnection connection;
    private readonly PeerFrameStream control;
    private readonly List<AuthenticatedFolder> folders;
    private readonly QuicPeerOptions options;
    private readonly Hello remoteHello;
    private readonly bool initiator;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim inboundBlockSlots;
    private readonly SemaphoreSlim outboundBlockSlots;
    private readonly ConcurrentDictionary<int, Task> blockTasks = new();
    private Task blockAcceptLoop = Task.CompletedTask;
    private Task controlMonitor = Task.CompletedTask;
    private readonly TaskCompletionSource disposedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int requestedCloseReason;
    private int protocolStrikeRecorded;
    private int disposed;

    private QuicPeerSession(QuicConnection connection, PeerFrameStream control,
        PeerAuthenticationContext authentication, Hello remoteHello,
        List<AuthenticatedFolder> folders, QuicPeerOptions options, bool initiator)
    {
        this.connection = connection; this.control = control; Authentication = authentication;
        this.remoteHello = remoteHello.Clone(); this.folders = folders; this.options = options;
        this.initiator = initiator;
        inboundBlockSlots = new SemaphoreSlim(options.MaximumConcurrentBlockStreams, options.MaximumConcurrentBlockStreams);
        outboundBlockSlots = new SemaphoreSlim(options.MaximumConcurrentBlockStreams, options.MaximumConcurrentBlockStreams);
    }

    public PeerAuthenticationContext Authentication { get; }
    public byte[] LocalDeviceId => Authentication.LocalDeviceId;
    public byte[] RemoteDeviceId => Authentication.RemoteDeviceId;
    public Guid SessionId => Authentication.SessionId;
    public bool IsInitiator => initiator;
    public Task Closed => disposedCompletion.Task;
    public EndPoint RemoteEndPoint => connection.RemoteEndPoint;
    public Hello RemoteHello => remoteHello.Clone();
    public IReadOnlyList<AuthenticatedFolder> AuthenticatedFolders => folders.AsReadOnly();

    public static async ValueTask<QuicPeerSession> ConnectAsync(IPEndPoint remoteEndPoint,
        QuicPeerOptions options, IPEndPoint? localEndPoint = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint); ArgumentNullException.ThrowIfNull(options); options.Validate();
        var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = remoteEndPoint,
            LocalEndPoint = localEndPoint,
            DefaultCloseErrorCode = 1,
            DefaultStreamErrorCode = 1,
            MaxInboundBidirectionalStreams = 512,
            MaxInboundUnidirectionalStreams = 0,
            IdleTimeout = TimeSpan.FromMinutes(2),
            ClientAuthenticationOptions = PeerTls.Client(options.Certificate),
        }, cancellationToken).ConfigureAwait(false);

        try { return await StartAsync(connection, options, initiator: true, cancellationToken).ConfigureAwait(false); }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static async ValueTask<QuicPeerSession> StartAsync(QuicConnection connection,
        QuicPeerOptions options, bool initiator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        var token = timeout.Token;
        PeerFrameStream? control = null;
        Guid sessionId = Guid.Empty;
        try
        {
            X509Certificate remoteCertificate = connection.RemoteCertificate
                ?? throw new AuthenticationException("QUIC peer did not present a device certificate.");
            byte[] localDevice = PeerTls.DeviceId(options.Certificate);
            byte[] remoteDevice = PeerTls.DeviceId(remoteCertificate);
            if (!options.IsDevicePinned(remoteDevice.ToArray()))
                throw new AuthenticationException("The QUIC peer device identity is not pinned.");
            QuicStream stream = initiator
                ? await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false)
                : await connection.AcceptInboundStreamAsync(token).ConfigureAwait(false);
            control = new PeerFrameStream(stream);

            var handshake = await PeerHandshake.AuthenticateAsync(control, options.HandshakeOptions,
                localDevice, remoteDevice, token).ConfigureAwait(false);
            Hello remoteHello = handshake.RemoteHello;
            PeerAuthenticationContext authentication = handshake.Authentication;
            sessionId = authentication.SessionId;
            List<AuthenticatedFolder> folders = handshake.Folders;

            var result = new QuicPeerSession(connection, control, authentication, remoteHello, folders, options, initiator);
            control = null;
            try { await result.EstablishFolderStreamsAsync(token).ConfigureAwait(false); }
            catch { await result.DisposeAsync().ConfigureAwait(false); throw; }
            options.Diagnostics?.TryWrite(new("transport.authenticated", "quic_session")
                { SessionId = sessionId, Transport = DiagnosticTransport.Quic, RemoteAddress = (connection.RemoteEndPoint as IPEndPoint)?.Address });
            return result;
        }
        catch (Exception error)
        {
            options.Diagnostics?.TryWrite(new("transport.handshake_failed", "quic_session")
                { SessionId = sessionId == Guid.Empty ? null : sessionId, Transport = DiagnosticTransport.Quic,
                    Level = DiagnosticLevel.Warning, ErrorCode = SafeError(error), HResult = error.HResult,
                    RemoteAddress = (connection.RemoteEndPoint as IPEndPoint)?.Address });
            if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask EstablishFolderStreamsAsync(CancellationToken token)
    {
        try
        {
            foreach (AuthenticatedFolder folder in folders)
            {
                // The authenticated control Hello is the clock sample for this
                // folder handler. Observe it before any index or block request can
                // reach the clock-gated engine.
                _ = await folder.Handler.ExchangeHelloAsync(remoteHello, token).ConfigureAwait(false);
                bool localOpens = initiator;
                QuicStream stream;
                if (localOpens) stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
                else
                {
                    try { stream = await connection.AcceptInboundStreamAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException error) when (token.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Timed out waiting for the peer folder stream (initiator={initiator}, localFolders={folders.Count}).", error);
                    }
                }
                var transport = new QuicFolderTransport(this, connection, stream, Authentication, folder,
                    outboundBlockSlots, options.RequestTimeout);
                folder.AttachTransport(transport);
                LogTransportEvent("transport.folder_stream_opened", "quic_index", items: 1);
                // QUIC exposes the stream to the acceptor only after its opener sends
                // a frame. Send the authenticated clock Hello first; an index summary
                // here would run before the peer's clock guard had accepted the sample.
                if (localOpens)
                    _ = await transport.ExchangeHelloAsync(PeerHandshake.CreateLocalHello(options.HandshakeOptions, LocalDeviceId), token).ConfigureAwait(false);
            }
            blockAcceptLoop = AcceptBlockStreamsAsync(); controlMonitor = MonitorControlAsync();
        }
        catch
        {
            foreach (AuthenticatedFolder folder in folders)
            {
                if (folder.Transport is not null) try { await folder.Transport.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            throw;
        }
    }

    private async Task MonitorControlAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                ProtocolFrame? frame = await control.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (frame is null) throw new EndOfStreamException("Peer closed the control stream.");
                if (WireMessageCodec.Decode(frame.Value) is not Close close)
                    throw new ProtocolException(ProtocolError.UnknownMessageType);
                MessageValidator.Validate(close);
                options.Diagnostics?.TryWrite(new("transport.remote_close", "quic_session")
                    { SessionId = SessionId, Transport = DiagnosticTransport.Quic, ItemsVisited = (long)close.Reason });
                await lifetime.CancelAsync().ConfigureAwait(false);
                Interlocked.CompareExchange(ref requestedCloseReason, (int)close.Reason, 0);
                _ = DisposeAsync().AsTask();
                return;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportTransportFailure(error); }
    }

    private async Task AcceptBlockStreamsAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await inboundBlockSlots.WaitAsync(lifetime.Token).ConfigureAwait(false);
                QuicStream stream;
                try { stream = await connection.AcceptInboundStreamAsync(lifetime.Token).ConfigureAwait(false); }
                catch { inboundBlockSlots.Release(); throw; }
                int id = Interlocked.Increment(ref nextBlockTaskId);
                Task task = RouteBlockStreamAsync(stream);
                blockTasks.TryAdd(id, task);
                _ = task.ContinueWith(completed =>
                {
                    blockTasks.TryRemove(id, out _);
                    inboundBlockSlots.Release();
                    if (completed.Exception is { } failure)
                    {
                        Exception error = failure.GetBaseException();
                        if (error is QuicException) LogTransportEvent("transport.block_stream_aborted", "quic_block", errorCode: SafeError(error));
                        else ReportTransportFailure(error);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportTransportFailure(error); }
    }

    private int nextBlockTaskId;

    private async Task RouteBlockStreamAsync(QuicStream stream)
    {
        await using var framed = new PeerFrameStream(stream);
        ProtocolFrame? frame = await framed.ReadAsync(lifetime.Token).ConfigureAwait(false);
        if (frame is null) return;
        if (WireMessageCodec.Decode(frame.Value) is not BlockRequestBatch request)
            throw new ProtocolException(ProtocolError.UnknownMessageType);
        string folderId = Convert.ToHexString(request.FolderId.Span);
        AuthenticatedFolder? folder = folders.FirstOrDefault(item =>
            CryptographicOperations.FixedTimeEquals(item.FolderId, request.FolderId.Span));
        if (folder?.Transport is null)
        {
            options.AddStrike?.Invoke(RemoteDeviceId, MessageFailure.FolderNotAuthorized);
            throw new AuthenticationException("Block stream targets an unauthorized folder.");
        }
        LogTransportEvent("transport.block_stream_accepted", "quic_block");
        await folder.QuicTransport!.ServeBlockStreamAsync(framed, request, lifetime.Token).ConfigureAwait(false);
    }

    internal void ReportTransportFailure(Exception error)
    {
        if (ProtocolViolationStrike.TryGetFailure(error, out MessageFailure failure)
            && Interlocked.Exchange(ref protocolStrikeRecorded, 1) == 0)
            ProtocolViolationStrike.Record(options.AddStrike, RemoteDeviceId, failure, options.Diagnostics,
                DiagnosticTransport.Quic, SessionId, "quic_session");
        options.Diagnostics?.TryWrite(new("transport.stream_failed", "quic_session")
            { SessionId = SessionId, Transport = DiagnosticTransport.Quic, Level = DiagnosticLevel.Warning,
                ErrorCode = SafeError(error), HResult = error.HResult,
                RemoteAddress = (connection.RemoteEndPoint as IPEndPoint)?.Address });
        Interlocked.CompareExchange(ref requestedCloseReason,
            error is TimeoutException ? (int)CloseReason.Timeout
                : error is AuthenticationException or MessageValidationException or ProtocolException
                    ? (int)CloseReason.ProtocolViolation : (int)CloseReason.InternalError, 0);
        _ = DisposeAsync().AsTask();
    }

    internal void LogTransportEvent(string eventName, string operation, DiagnosticLevel level = DiagnosticLevel.Debug,
        long? bytes = null, long? items = null, double? elapsed = null, string? errorCode = null)
    {
        options.Diagnostics?.TryWrite(new(eventName, operation)
        {
            SessionId = SessionId, Transport = DiagnosticTransport.Quic, Level = level, Bytes = bytes,
            ItemsVisited = items, ElapsedMilliseconds = elapsed, ErrorCode = errorCode,
            RemoteAddress = (connection.RemoteEndPoint as IPEndPoint)?.Address,
        });
    }

    private static string SafeError(Exception error) => error switch
    {
        AuthenticationException => "authentication_failed",
        MessageValidationException invalid => $"message_{invalid.Failure}",
        ProtocolException protocol => $"protocol_{protocol.Code}",
        OperationCanceledException => "cancelled_or_timed_out",
        _ => error.GetType().Name,
    };

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return new(disposedCompletion.Task);
        return DisposeAndCompleteAsync();
    }

    private async ValueTask DisposeAndCompleteAsync()
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            disposedCompletion.TrySetResult();
        }
        catch (Exception error)
        {
            disposedCompletion.TrySetException(error);
            throw;
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        try
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            CloseReason reason = (CloseReason)Volatile.Read(ref requestedCloseReason);
            await control.WriteAsync(new Close { Reason = reason == CloseReason.Unspecified ? CloseReason.PeerShutdown : reason }, closeTimeout.Token).ConfigureAwait(false);
            if (control.Stream is QuicStream quicControl) quicControl.CompleteWrites();
        }
        catch (Exception error)
        {
            options.Diagnostics?.TryWrite(new("transport.close_frame_failed", "quic_session")
                { SessionId = SessionId, Transport = DiagnosticTransport.Quic, Level = DiagnosticLevel.Debug,
                    ErrorCode = SafeError(error), HResult = error.HResult });
        }
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        foreach (var folder in folders)
        {
            try { if (folder.Transport is not null) await folder.Transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        try { await controlMonitor.ConfigureAwait(false); } catch (Exception error) { (failures ??= []).Add(error); }
        try { await blockAcceptLoop.ConfigureAwait(false); } catch (Exception error) { (failures ??= []).Add(error); }
        try { await Task.WhenAll(blockTasks.Values).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { (failures ??= []).Add(error); }
        foreach (var folder in folders)
        {
            try { await folder.Handler.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        try { await control.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        options.Diagnostics?.TryWrite(new("transport.disconnected", "quic_session")
            { SessionId = SessionId, Transport = DiagnosticTransport.Quic });
        lifetime.Dispose(); inboundBlockSlots.Dispose(); outboundBlockSlots.Dispose();
        if (failures is { Count: > 0 }) throw new AggregateException(failures);
    }
}

public sealed class AuthenticatedFolder
{
    private IPeerTransport? transport;
    internal AuthenticatedFolder(PeerSync.Crypto.FolderKeys keys, FolderAccess remoteAccess, IPeerRequestHandler handler)
    { Keys = keys; RemoteAccess = remoteAccess; Handler = handler; }
    public PeerSync.Crypto.FolderKeys Keys { get; }
    public byte[] FolderId => Keys.FolderId();
    public FolderAccess RemoteAccess { get; }
    public IPeerTransport? Transport => transport;
    internal QuicFolderTransport? QuicTransport => transport as QuicFolderTransport;
    internal IPeerRequestHandler Handler { get; }
    internal void AttachTransport(IPeerTransport value) => transport = value ?? throw new ArgumentNullException(nameof(value));
}
