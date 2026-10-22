using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PeerSync.Net.Tcp;

/// <summary>A TLS-protected data lane attached to one authenticated control session.</summary>
public sealed class TcpDataConnection : IAsyncDisposable
{
    private readonly TcpPeerSession owner;
    private readonly TcpClient client;
    private readonly byte[] remoteDeviceId;
    private int disposed;

    internal TcpDataConnection(TcpPeerSession owner, SslStream stream, TcpClient client, byte[] remoteDeviceId)
    { this.owner = owner; Stream = stream; this.client = client; this.remoteDeviceId = remoteDeviceId.ToArray(); }

    public Stream Stream { get; }
    public byte[] RemoteDeviceId => remoteDeviceId.ToArray();
    public bool IsAuthenticated => Volatile.Read(ref disposed) == 0;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        owner.Release(this);
        try { await Stream.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            client.Dispose();
            CryptographicOperations.ZeroMemory(remoteDeviceId);
        }
    }
}
