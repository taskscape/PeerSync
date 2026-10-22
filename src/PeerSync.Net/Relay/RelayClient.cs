using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using PeerSync.Net.Security;
using PeerSync.Net.Rendezvous;
using PeerSync.Rendezvous.Contracts.V1;
using RelayRpc = PeerSync.Rendezvous.Contracts.V1.Relay;

namespace PeerSync.Net.Relay;

/// <summary>Establishes end-to-end device TLS inside a certificate-bound relay pipe.</summary>
internal static class RelayClient
{
    internal static async ValueTask<RelayTransport> ConnectAsync(Uri server, RelayOffer offer,
        X509Certificate2 certificate, Func<byte[], bool> isDevicePinned,
        CancellationToken cancellationToken, X509Certificate2? loopbackServerCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(isDevicePinned);
        if (server.Scheme != Uri.UriSchemeHttps || offer.Token.Length != 32 || offer.PeerDeviceId.Length != 32
            || offer.SessionId.Length != 16 || !isDevicePinned(offer.PeerDeviceId.ToByteArray()))
            throw new AuthenticationException("Invalid relay offer or unapproved device identity.");
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls13;
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
        LoopbackCertificatePin.Apply(handler, server, loopbackServerCertificate);
        var channel = GrpcChannel.ForAddress(server, new GrpcChannelOptions
        { HttpHandler = handler, HttpVersion = HttpVersion.Version20,
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact });
        var metadata = new Metadata { { "psp-relay-token", Convert.ToHexString(offer.Token.Span) } };
        AsyncDuplexStreamingCall<RelayChunk, RelayChunk>? call = null;
        GrpcDuplexStream? pipe = null;
        SslStream? tls = null;
        try
        {
            // The candidate token is canceled after this connection wins the
            // race. Only establishment uses it; disposing the transport owns
            // the lifetime of the established duplex RPC.
            call = new RelayRpc.RelayClient(channel).Pipe(headers: metadata);
            pipe = new GrpcDuplexStream(call);
            tls = new SslStream(pipe, leaveInnerStreamOpen: false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (offer.Initiator)
                await tls.AuthenticateAsClientAsync(PeerTls.Client(certificate), timeout.Token).ConfigureAwait(false);
            else
                await tls.AuthenticateAsServerAsync(PeerTls.Server(certificate), timeout.Token).ConfigureAwait(false);
            if (!PeerTls.IsDeviceCertificate(tls.RemoteCertificate)
                || !CryptographicOperations.FixedTimeEquals(PeerTls.DeviceId(tls.RemoteCertificate!),
                    offer.PeerDeviceId.Span))
                throw new AuthenticationException("Relay peer device identity did not match the offer.");
            return new RelayTransport(tls, channel, handler, PeerTls.DeviceId(certificate),
                offer.PeerDeviceId.ToByteArray(), offer.Initiator);
        }
        catch
        {
            if (tls is not null) await tls.DisposeAsync().ConfigureAwait(false);
            else if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
            else call?.Dispose();
            channel.Dispose();
            handler.Dispose();
            throw;
        }
    }
}

internal sealed class RelayTransport(SslStream tls, GrpcChannel channel, SocketsHttpHandler handler,
    byte[] localDeviceId, byte[] remoteDeviceId, bool initiator) : IAsyncDisposable
{
    private int disposed;
    internal Stream Stream => tls;
    internal byte[] LocalDeviceId => localDeviceId.ToArray();
    internal byte[] RemoteDeviceId => remoteDeviceId.ToArray();
    internal bool IsInitiator => initiator;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await tls.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            channel.Dispose();
            handler.Dispose();
            CryptographicOperations.ZeroMemory(localDeviceId);
            CryptographicOperations.ZeroMemory(remoteDeviceId);
        }
    }
}
