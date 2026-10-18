using System.Net;
using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;

namespace PeerSync.Spikes;

public static class QuicProbe
{
    public static async ValueTask<QuicListener> ListenAsync(int port, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        if (!QuicListener.IsSupported) throw new PlatformNotSupportedException("QUIC is unavailable.");
        return await QuicListener.ListenAsync(new()
        {
            ListenEndPoint = new(IPAddress.IPv6Any, port),
            ApplicationProtocols = [PeerTls.Alpn],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0, DefaultStreamErrorCode = 1,
                MaxInboundBidirectionalStreams = 4, MaxInboundUnidirectionalStreams = 0,
                IdleTimeout = TimeSpan.FromSeconds(30), ServerAuthenticationOptions = PeerTls.Server(certificate),
            }),
        }, cancellationToken).ConfigureAwait(false);
    }

    public static ValueTask<QuicConnection> DialAsync(IPEndPoint remote, X509Certificate2 certificate,
        CancellationToken cancellationToken, IPEndPoint? local = null) => QuicConnection.ConnectAsync(new()
        {
            RemoteEndPoint = remote, LocalEndPoint = local,
            DefaultCloseErrorCode = 0, DefaultStreamErrorCode = 1,
            MaxInboundBidirectionalStreams = 4, MaxInboundUnidirectionalStreams = 0,
            IdleTimeout = TimeSpan.FromSeconds(30), ClientAuthenticationOptions = PeerTls.Client(certificate),
        }, cancellationToken);
}
