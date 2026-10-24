using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Security;
using PeerSync.Rendezvous.Contracts.V1;
using TrackerRpc = PeerSync.Rendezvous.Contracts.V1.Rendezvous;

namespace PeerSync.Net.Rendezvous;

/// <summary>A reconnecting certificate-bound tracker stream for one configured server.</summary>
internal sealed class TrackerClient : IAsyncDisposable
{
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromMinutes(5);
    private readonly Uri server;
    private readonly X509Certificate2 certificate;
    private readonly Func<Announce> announcement;
    private readonly Func<ServerMessage, CancellationToken, ValueTask> receive;
    private readonly DiagnosticJournal? diagnostics;
    private readonly X509Certificate2? loopbackServerCertificate;
    private readonly TrackerOutgoingQueue outgoing = new();
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;
    private int connected;
    // The response reader sets this; failure and disconnect read it after all session tasks end.
    private Guid? observedSessionId;

    internal TrackerClient(Uri server, X509Certificate2 certificate, Func<Announce> announcement,
        Func<ServerMessage, CancellationToken, ValueTask> receive, DiagnosticJournal? diagnostics = null,
        X509Certificate2? loopbackServerCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Scheme != Uri.UriSchemeHttps || server.UserInfo.Length != 0 ||
            server.Query.Length != 0 || server.Fragment.Length != 0 || server.AbsolutePath != "/")
            throw new ArgumentException("Tracker address must be an HTTPS origin.", nameof(server));
        if (!certificate.HasPrivateKey || !PeerTls.IsDeviceCertificate(certificate))
            throw new ArgumentException("A current private P-256 device certificate is required.", nameof(certificate));
        this.server = server;
        this.certificate = certificate;
        this.announcement = announcement ?? throw new ArgumentNullException(nameof(announcement));
        this.receive = receive ?? throw new ArgumentNullException(nameof(receive));
        this.diagnostics = diagnostics;
        this.loopbackServerCertificate = loopbackServerCertificate;
    }

    internal bool IsConnected => Volatile.Read(ref connected) != 0;
    internal Uri Server => server;
    internal void Start() => worker ??= Task.Run(RunAsync);
    // Refresh remains pending until the writer sends the newest snapshot, even
    // when its bounded critical-request queue is full or the link is offline.
    internal void Refresh() => outgoing.Refresh();

    internal bool RequestRelay(ReadOnlySpan<byte> peerDeviceId, ReadOnlySpan<byte> folderId)
    {
        if (!IsConnected || peerDeviceId.Length != 32 || folderId.Length != 32) return false;
        bool queued = outgoing.TryEnqueue(new ClientMessage { RelayRequest = new RelayRequest
        { DeviceId = ByteString.CopyFrom(peerDeviceId), FolderId = ByteString.CopyFrom(folderId) } });
        if (!queued) QueueFull("relay_request");
        return queued;
    }

    internal bool RequestPunch(ReadOnlySpan<byte> peerDeviceId, ReadOnlySpan<byte> folderId)
    {
        if (!IsConnected || peerDeviceId.Length != 32 || folderId.Length != 32) return false;
        bool queued = outgoing.TryEnqueue(new ClientMessage { PunchRequest = new PunchRequest
        { DeviceId = ByteString.CopyFrom(peerDeviceId), FolderId = ByteString.CopyFrom(folderId) } });
        if (!queued) QueueFull("punch_request");
        return queued;
    }

    private void QueueFull(string kind) => diagnostics?.TryWrite(new("tracker.outgoing_full", "rendezvous")
    { Level = DiagnosticLevel.Warning, ErrorCode = kind, ItemsDeferred = 1 });

    private async Task RunAsync()
    {
        int failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                await RunSessionAsync(stopping.Token).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception) when (stopping.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                diagnostics?.TryWrite(new("tracker.session_failed", "rendezvous")
                { Level = DiagnosticLevel.Warning, Transport = DiagnosticTransport.Http2,
                    SessionId = observedSessionId,
                    ErrorCode = error is RpcException rpc ? $"rpc_{rpc.StatusCode}" : error.GetType().Name,
                    HResult = error.HResult, NativeError = NativeSocketError(error),
                    ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds });
            }
            Volatile.Write(ref connected, 0);
            if (stopping.IsCancellationRequested) break;
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(300, 1 << Math.Min(failures++, 8)));
            await Task.Delay(delay, stopping.Token).ConfigureAwait(false);
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        observedSessionId = null;
        using var handler = new SocketsHttpHandler
        {
            // The tracker stream is otherwise quiet for five minutes between
            // announcements. Keep the HTTP/2 connection active across shorter
            // NAT and firewall idle windows, and detect a dead path promptly.
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls13;
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
        LoopbackCertificatePin.Apply(handler, server, loopbackServerCertificate);
        using var channel = GrpcChannel.ForAddress(server, new GrpcChannelOptions
        { HttpHandler = handler, HttpVersion = HttpVersion.Version20,
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact });
        using var call = new TrackerRpc.RendezvousClient(channel).Session(cancellationToken: cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The preceding stream's writer is finished before reconnect starts.
        // Its queued requests refer to an old presence snapshot or relay offer.
        outgoing.Reset();
        Announce firstAnnouncement = announcement();
        await call.RequestStream.WriteAsync(new ClientMessage { Announce = firstAnnouncement }, linked.Token)
            .ConfigureAwait(false);
        LogAnnounce("tracker.announced_initial", firstAnnouncement);
        Volatile.Write(ref connected, 1);
        diagnostics?.TryWrite(new("tracker.session_connected", "rendezvous")
        { Transport = DiagnosticTransport.Http2 });
        Task writer = outgoing.WriteLoopAsync(call.RequestStream, announcement, linked.Token,
            sent => LogAnnounce("tracker.announced_refresh", sent));
        Task ticker = AnnounceTickerAsync(linked.Token);
        Task reader = ReadLoopAsync(call.ResponseStream, linked.Token);
        try
        {
            // A failed outbound writer must end this session even if the server
            // keeps its inbound stream open. The next session starts with a
            // fresh announcement and discards its stale relay offers.
            Task finished = await Task.WhenAny(reader, writer, ticker).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref connected, 0);
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                try { await Task.WhenAll(reader, writer, ticker).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            }
            finally
            {
                diagnostics?.TryWrite(new("tracker.session_disconnected", "rendezvous")
                { Transport = DiagnosticTransport.Http2, SessionId = observedSessionId });
            }
        }
    }

    private async Task ReadLoopAsync(IAsyncStreamReader<ServerMessage> responses, CancellationToken cancellationToken)
    {
        while (await responses.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            ServerMessage message = responses.Current;
            if (observedSessionId is null && message.ObservedAddress is { } observed
                && observed.SessionId.Length == 16)
            {
                observedSessionId = new Guid(observed.SessionId.Span);
                diagnostics?.TryWrite(new("tracker.session_identified", "rendezvous")
                { Transport = DiagnosticTransport.Http2, SessionId = observedSessionId });
            }
            await receive(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AnnounceTickerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(AnnounceInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            Refresh();
    }

    private void LogAnnounce(string eventName, Announce sent) =>
        diagnostics?.TryWrite(new(eventName, "rendezvous")
        { Transport = DiagnosticTransport.Http2, ItemsVisited = sent.FolderIds.Count });

    private static int? NativeSocketError(Exception error)
    {
        for (Exception? current = error is RpcException rpc ? rpc.Status.DebugException ?? error : error;
             current is not null; current = current.InnerException)
            if (current is SocketException socket) return socket.NativeErrorCode;
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (stopping.IsCancellationRequested) return;
        await stopping.CancelAsync().ConfigureAwait(false);
        outgoing.Complete();
        if (worker is not null)
            try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        stopping.Dispose();
    }
}
