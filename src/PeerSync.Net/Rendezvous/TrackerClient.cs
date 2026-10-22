using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
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
    private readonly Channel<ClientMessage> outgoing = Channel.CreateBounded<ClientMessage>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;
    private int connected;

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
    // A reconnect starts with a fresh snapshot. Do not replay stale folder sets
    // that were queued while the tracker was offline.
    internal void Refresh()
    {
        if (IsConnected)
            outgoing.Writer.TryWrite(new ClientMessage { Announce = announcement() });
    }

    internal bool RequestRelay(ReadOnlySpan<byte> peerDeviceId, ReadOnlySpan<byte> folderId)
    {
        if (!IsConnected || peerDeviceId.Length != 32 || folderId.Length != 32) return false;
        return outgoing.Writer.TryWrite(new ClientMessage { RelayRequest = new RelayRequest
        { DeviceId = ByteString.CopyFrom(peerDeviceId), FolderId = ByteString.CopyFrom(folderId) } });
    }

    internal bool RequestPunch(ReadOnlySpan<byte> peerDeviceId, ReadOnlySpan<byte> folderId)
    {
        if (!IsConnected || peerDeviceId.Length != 32 || folderId.Length != 32) return false;
        return outgoing.Writer.TryWrite(new ClientMessage { PunchRequest = new PunchRequest
        { DeviceId = ByteString.CopyFrom(peerDeviceId), FolderId = ByteString.CopyFrom(folderId) } });
    }

    private async Task RunAsync()
    {
        int failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stopping.Token).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                diagnostics?.TryWrite(new("tracker.session_failed", "rendezvous")
                { Level = DiagnosticLevel.Warning, Transport = DiagnosticTransport.Http2,
                    ErrorCode = error.GetType().Name, HResult = error.HResult });
            }
            Volatile.Write(ref connected, 0);
            if (stopping.IsCancellationRequested) break;
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(300, 1 << Math.Min(failures++, 8)));
            await Task.Delay(delay, stopping.Token).ConfigureAwait(false);
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls13;
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
        LoopbackCertificatePin.Apply(handler, server, loopbackServerCertificate);
        using var channel = GrpcChannel.ForAddress(server, new GrpcChannelOptions
        { HttpHandler = handler, HttpVersion = HttpVersion.Version20,
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact });
        using var call = new TrackerRpc.RendezvousClient(channel).Session(cancellationToken: cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await call.RequestStream.WriteAsync(new ClientMessage { Announce = announcement() }, linked.Token)
            .ConfigureAwait(false);
        Volatile.Write(ref connected, 1);
        diagnostics?.TryWrite(new("tracker.session_connected", "rendezvous")
        { Transport = DiagnosticTransport.Http2 });
        Task writer = WriteLoopAsync(call.RequestStream, linked.Token);
        Task ticker = AnnounceTickerAsync(linked.Token);
        try
        {
            while (await call.ResponseStream.MoveNext(linked.Token).ConfigureAwait(false))
                await receive(call.ResponseStream.Current, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref connected, 0);
            await linked.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(writer, ticker).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            diagnostics?.TryWrite(new("tracker.session_disconnected", "rendezvous")
            { Transport = DiagnosticTransport.Http2 });
        }
    }

    private async Task WriteLoopAsync(IClientStreamWriter<ClientMessage> requests, CancellationToken cancellationToken)
    {
        await foreach (ClientMessage message in outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await requests.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task AnnounceTickerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(AnnounceInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            Refresh();
    }

    public async ValueTask DisposeAsync()
    {
        if (stopping.IsCancellationRequested) return;
        await stopping.CancelAsync().ConfigureAwait(false);
        outgoing.Writer.TryComplete();
        if (worker is not null)
            try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        stopping.Dispose();
    }
}
