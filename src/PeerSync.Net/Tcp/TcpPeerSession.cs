using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Security;

namespace PeerSync.Net.Tcp;

/// <summary>
/// Mutual-TLS control connection with up to three independently authenticated data lanes.
/// The 32-byte attachment ticket is held only in memory and is never exposed or logged.
/// </summary>
public sealed class TcpPeerSession : IAsyncDisposable
{
    public const int MaximumDataConnections = 3;
    internal const byte ControlConnectionKind = 1;
    internal const byte DataConnectionKind = 2;
    internal const byte Accepted = 0xA5;

    private readonly TcpPeerListener listener;
    private readonly TcpClient controlClient;
    private readonly byte[] ticket;
    private readonly byte[] remoteDeviceId;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<TcpDataConnection> acceptedData = Channel.CreateBounded<TcpDataConnection>(
        new BoundedChannelOptions(MaximumDataConnections) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<TcpDataConnection, byte> dataConnections = new();
    private int dataCount;
    private int disposed;

    internal TcpPeerSession(TcpPeerListener listener, SslStream control, TcpClient controlClient, byte[] remoteDeviceId,
        byte[] ticket, IPEndPoint remoteEndPoint)
    {
        this.listener = listener; this.controlClient = controlClient; ControlStream = control;
        this.remoteDeviceId = remoteDeviceId.ToArray();
        this.ticket = ticket.ToArray(); RemoteEndPoint = remoteEndPoint;
    }

    public Stream ControlStream { get; }
    public byte[] LocalDeviceId => PeerTls.DeviceId(listener.Options.Certificate);
    public byte[] RemoteDeviceId => remoteDeviceId.ToArray();
    public IPEndPoint RemoteEndPoint { get; }
    public int ActiveDataConnections => Volatile.Read(ref dataCount);

    public async ValueTask<TcpDataConnection> OpenDataConnectionAsync(IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ReserveDataSlot();
        TcpClient? client = null;
        SslStream? ssl = null;
        string stage = "connect";
        try
        {
            client = new TcpClient(remoteEndPoint.AddressFamily) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, listener.LifetimeToken, lifetime.Token);
            timeout.CancelAfter(listener.Options.HandshakeTimeout);
            await client.ConnectAsync(remoteEndPoint, timeout.Token).ConfigureAwait(false);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            stage = "tls";
            await ssl.AuthenticateAsClientAsync(PeerTls.Client(listener.Options.Certificate), timeout.Token).ConfigureAwait(false);
            stage = "pin";
            byte[] remoteId = RequirePinnedPeer(ssl.RemoteCertificate, listener.Options);
            if (!CryptographicOperations.FixedTimeEquals(remoteId, RemoteDeviceId))
                throw new AuthenticationException("The TCP data connection changed the peer device identity.");
            stage = "ticket_attach";
            await TcpPeerListener.WriteAttachHeaderAsync(ssl, DataConnectionKind, ticket, timeout.Token).ConfigureAwait(false);
            stage = "ticket_ack";
            byte[] accepted = new byte[1];
            await ssl.ReadExactlyAsync(accepted, timeout.Token).ConfigureAwait(false);
            if (accepted[0] != Accepted) throw new AuthenticationException("The TCP peer rejected the data connection ticket.");
            var result = new TcpDataConnection(this, ssl, client, remoteId);
            ObjectDisposedException.ThrowIf(!Track(result), this);
            ssl = null;
            client = null;
            listener.Log("transport.tcp_data_attached", "tcp_data", remoteEndPoint.Address);
            return result;
        }
        catch (Exception error)
        {
            ReleaseReservation();
            listener.LogFailure("transport.tcp_data_attach_failed", $"tcp_{stage}", remoteEndPoint.Address, error);
            throw;
        }
        finally
        {
            if (ssl is not null) await ssl.DisposeAsync().ConfigureAwait(false);
            client?.Dispose();
        }
    }

    public ValueTask<TcpDataConnection> AcceptDataConnectionAsync(CancellationToken cancellationToken = default)
        => acceptedData.Reader.ReadAsync(cancellationToken);

    internal byte[] TicketCopy() => ticket.ToArray();
    internal bool TryAcceptData(SslStream stream, TcpClient client, byte[] remoteDeviceId, out TcpDataConnection? channel)
    {
        channel = null;
        if (Volatile.Read(ref disposed) != 0 || !TryReserveDataSlot()) return false;
        var created = new TcpDataConnection(this, stream, client, remoteDeviceId);
        if (!Track(created)) { ReleaseReservation(); return false; }
        if (!acceptedData.Writer.TryWrite(created))
        {
            if (dataConnections.TryRemove(created, out _)) ReleaseReservation();
            channel = null;
            return false;
        }
        channel = created;
        return true;
    }

    private bool TryReserveDataSlot()
    {
        while (true)
        {
            int current = Volatile.Read(ref dataCount);
            if (current >= MaximumDataConnections || Volatile.Read(ref disposed) != 0) return false;
            if (Interlocked.CompareExchange(ref dataCount, current + 1, current) == current) return true;
        }
    }

    private void ReserveDataSlot()
    {
        if (!TryReserveDataSlot()) throw new InvalidOperationException("The TCP session already has its maximum of three data connections.");
    }

    private bool Track(TcpDataConnection connection)
    {
        if (Volatile.Read(ref disposed) != 0 || !dataConnections.TryAdd(connection, 0)) return false;
        if (Volatile.Read(ref disposed) == 0) return true;
        dataConnections.TryRemove(connection, out _);
        return false;
    }

    internal void Release(TcpDataConnection connection)
    {
        if (dataConnections.TryRemove(connection, out _)) ReleaseReservation();
    }

    private void ReleaseReservation() => Interlocked.Decrement(ref dataCount);

    internal static byte[] RequirePinnedPeer(X509Certificate? certificate, TcpPeerOptions options)
    {
        if (!PeerTls.IsDeviceCertificate(certificate)) throw new AuthenticationException("The TCP peer did not present a valid device certificate.");
        byte[] deviceId = PeerTls.DeviceId(certificate!);
        if (!options.IsDevicePinned(deviceId.ToArray())) throw new AuthenticationException("The TCP peer device identity is not pinned.");
        return deviceId;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await lifetime.CancelAsync().ConfigureAwait(false); } catch { }
        listener.Unregister(this);
        acceptedData.Writer.TryComplete();
        foreach (TcpDataConnection connection in dataConnections.Keys)
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await ControlStream.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            controlClient.Dispose();
            CryptographicOperations.ZeroMemory(ticket);
            CryptographicOperations.ZeroMemory(remoteDeviceId);
            lifetime.Dispose();
        }
        listener.Log("transport.tcp_disconnected", "tcp_control", RemoteEndPoint.Address);
    }
}
