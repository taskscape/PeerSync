using System.Net;
using System.Net.Quic;
using PeerSync.Net.Security;

namespace PeerSync.Net.Quic;

/// <summary>The caller owns accepted sessions and must drain them before disposing certificate/keys.</summary>
public sealed class QuicPeerListener : IAsyncDisposable
{
    private readonly QuicListener listener;
    private readonly QuicPeerOptions options;
    private QuicPeerListener(QuicListener listener, QuicPeerOptions options) { this.listener = listener; this.options = options; }
    public IPEndPoint LocalEndPoint => listener.LocalEndPoint;

    public static async ValueTask<QuicPeerListener> ListenAsync(IPEndPoint endpoint, QuicPeerOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint); ArgumentNullException.ThrowIfNull(options); options.Validate();
        var listener = await QuicListener.ListenAsync(new() { ListenEndPoint = endpoint, ApplicationProtocols = [PeerTls.Alpn],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions {
                ServerAuthenticationOptions = PeerTls.Server(options.Certificate), DefaultCloseErrorCode = 1,
                DefaultStreamErrorCode = 1, MaxInboundBidirectionalStreams = 512, MaxInboundUnidirectionalStreams = 0,
                IdleTimeout = TimeSpan.FromMinutes(2) }) }, token).ConfigureAwait(false);
        return new(listener, options);
    }

    public async ValueTask<QuicPeerSession> AcceptAsync(CancellationToken token = default)
    {
        var connection = await listener.AcceptConnectionAsync(token).ConfigureAwait(false);
        try { return await QuicPeerSession.StartAsync(connection, options, initiator: false, token).ConfigureAwait(false); }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public ValueTask DisposeAsync() => listener.DisposeAsync();
}
