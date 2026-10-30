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

/// <summary>TCP listener that authenticates control and ticket-bound data TLS connections.</summary>
public sealed class TcpPeerListener : IAsyncDisposable
{
    private static readonly byte[] Magic = "PSP1"u8.ToArray();
    private const int TicketLength = 32;
    private const int MaximumPendingHandshakes = 64;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<TcpPeerSession> incoming = Channel.CreateUnbounded<TcpPeerSession>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<string, TcpPeerSession> sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim handshakes = new(MaximumPendingHandshakes, MaximumPendingHandshakes);
    private readonly ConcurrentDictionary<int, Task> handlers = new();
    private readonly Task acceptLoop;
    private int handlerId;
    private int disposed;

    private TcpPeerListener(TcpListener listener, TcpPeerOptions options)
    { this.listener = listener; Options = options; acceptLoop = AcceptLoopAsync(); }

    internal TcpPeerOptions Options { get; }
    internal CancellationToken LifetimeToken => lifetime.Token;
    public IPEndPoint LocalEndPoint => (IPEndPoint)listener.LocalEndpoint;

    public static TcpPeerListener Listen(IPEndPoint endpoint, TcpPeerOptions options, int backlog = 128)
    {
        ArgumentNullException.ThrowIfNull(endpoint); ArgumentNullException.ThrowIfNull(options); options.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(backlog, 1);
        var listener = new TcpListener(endpoint);
        listener.Start(backlog);
        return new(listener, options);
    }

    public async ValueTask<TcpPeerSession> AcceptAsync(CancellationToken cancellationToken = default)
        => await incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<TcpPeerSession> ConnectAsync(IPEndPoint remoteEndPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(Options.HandshakeTimeout);
        TcpClient? client = new TcpClient(remoteEndPoint.AddressFamily) { NoDelay = true };
        SslStream? ssl = null;
        TcpPeerSession? created = null;
        string stage = "connect";
        try
        {
            await client.ConnectAsync(remoteEndPoint, timeout.Token).ConfigureAwait(false);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            stage = "tls";
            await ssl.AuthenticateAsClientAsync(PeerTls.Client(Options.Certificate), timeout.Token).ConfigureAwait(false);
            stage = "pin";
            byte[] remoteId = TcpPeerSession.RequirePinnedPeer(ssl.RemoteCertificate, Options);
            stage = "attach";
            await WriteAttachHeaderAsync(ssl, TcpPeerSession.ControlConnectionKind, ReadOnlyMemory<byte>.Empty,
                timeout.Token, LocalEndPoint.Port).ConfigureAwait(false);
            stage = "ticket";
            byte[] ticket = new byte[TicketLength];
            await ssl.ReadExactlyAsync(ticket, timeout.Token).ConfigureAwait(false);
            byte[] remotePortBytes = new byte[sizeof(ushort)];
            await ssl.ReadExactlyAsync(remotePortBytes, timeout.Token).ConfigureAwait(false);
            int peerPort = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(remotePortBytes);
            if (peerPort == 0) throw new AuthenticationException("The peer did not advertise a valid TCP listener port.");
            var peerEndPoint = new IPEndPoint(remoteEndPoint.Address, peerPort);
            var session = new TcpPeerSession(this, ssl, client, remoteId, ticket, peerEndPoint);
            CryptographicOperations.ZeroMemory(ticket);
            created = session;
            Register(session);
            stage = "admission_ack";
            await ssl.WriteAsync(new[] { TcpPeerSession.Accepted }, timeout.Token).ConfigureAwait(false);
            await ssl.FlushAsync(timeout.Token).ConfigureAwait(false);
            ssl = null;
            client = null;
            created = null;
            Log("transport.tcp_connected", "tcp_control", remoteEndPoint.Address);
            return session;
        }
        catch (Exception error)
        {
            LogFailure("transport.tcp_connect_failed", $"tcp_{stage}", remoteEndPoint.Address, error);
            if (created is not null) await created.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (ssl is not null) await ssl.DisposeAsync().ConfigureAwait(false);
            client?.Dispose();
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                client.NoDelay = true;
                if (!handshakes.Wait(0)) { client.Dispose(); Log("transport.tcp_handshake_capacity", "tcp_accept", null, DiagnosticLevel.Warning, "capacity_exceeded"); continue; }
                int id = Interlocked.Increment(ref handlerId);
                Task handler = HandleAcceptedAsync(client);
                handlers.TryAdd(id, handler);
                _ = handler.ContinueWith(completed =>
                {
                    handlers.TryRemove(id, out _); handshakes.Release();
                    if (completed.Exception is { } failure)
                        Log("transport.tcp_accept_failed", "tcp_accept", null, DiagnosticLevel.Warning,
                            failure.GetBaseException().GetType().Name, failure.GetBaseException().HResult, failure);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        finally { incoming.Writer.TryComplete(); }
    }

    private async Task HandleAcceptedAsync(TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SslStream? ssl = null;
        bool transferred = false;
        string stage = "tls";
        try
        {
            timeout.CancelAfter(Options.HandshakeTimeout);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(PeerTls.Server(Options.Certificate), timeout.Token).ConfigureAwait(false);
            stage = "pin";
            byte[] remoteId = TcpPeerSession.RequirePinnedPeer(ssl.RemoteCertificate, Options);
            stage = "attach_header";
            byte[] header = new byte[5];
            await ssl.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new AuthenticationException("TCP attach header is invalid.");
            IPEndPoint remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            if (header[4] == TcpPeerSession.ControlConnectionKind)
            {
                byte[] advertisedPortBytes = new byte[sizeof(ushort)];
                await ssl.ReadExactlyAsync(advertisedPortBytes, timeout.Token).ConfigureAwait(false);
                int advertisedPort = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(advertisedPortBytes);
                if (advertisedPort == 0) throw new AuthenticationException("The peer advertised an invalid TCP listener port.");
                remote = new IPEndPoint(remote.Address, advertisedPort);
                stage = "control_ticket";
                byte[] ticket = RandomNumberGenerator.GetBytes(TicketLength);
                var session = new TcpPeerSession(this, ssl, client, remoteId, ticket, remote);
                CryptographicOperations.ZeroMemory(ticket);
                try
                {
                    Register(session);
                    byte[] ticketToSend = session.TicketCopy();
                    try
                    {
                        await ssl.WriteAsync(ticketToSend, timeout.Token).ConfigureAwait(false);
                        byte[] localPort = new byte[sizeof(ushort)];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(localPort, checked((ushort)LocalEndPoint.Port));
                        await ssl.WriteAsync(localPort, timeout.Token).ConfigureAwait(false);
                    }
                    finally { CryptographicOperations.ZeroMemory(ticketToSend); }
                    await ssl.FlushAsync(timeout.Token).ConfigureAwait(false);
                    stage = "control_ack";
                    byte[] controlAck = new byte[1];
                    await ssl.ReadExactlyAsync(controlAck, timeout.Token).ConfigureAwait(false);
                    if (controlAck[0] != TcpPeerSession.Accepted) throw new AuthenticationException("The TCP control ticket was not acknowledged.");
                    if (!incoming.Writer.TryWrite(session)) throw new IOException("The TCP session accept queue is closed.");
                    transferred = true;
                    Log("transport.tcp_accepted", "tcp_control", remote.Address);
                    return;
                }
                catch { Unregister(session); await session.DisposeAsync().ConfigureAwait(false); throw; }
            }
            if (header[4] != TcpPeerSession.DataConnectionKind) throw new AuthenticationException("TCP attach type is invalid.");
            stage = "data_ticket";
            byte[] ticketBytes = new byte[TicketLength];
            await ssl.ReadExactlyAsync(ticketBytes, timeout.Token).ConfigureAwait(false);
            TcpPeerSession? owner = Resolve(ticketBytes);
            CryptographicOperations.ZeroMemory(ticketBytes);
            if (owner is null || !CryptographicOperations.FixedTimeEquals(owner.RemoteDeviceId, remoteId))
            {
                await ssl.WriteAsync(new byte[] { 0 }, timeout.Token).ConfigureAwait(false);
                throw new AuthenticationException("The TCP data ticket or device identity is invalid.");
            }
            if (!owner.TryAcceptData(ssl, client, remoteId, out TcpDataConnection? data))
            {
                await ssl.WriteAsync(new byte[] { 0 }, timeout.Token).ConfigureAwait(false);
                throw new AuthenticationException("The TCP session data connection limit was reached.");
            }
            try
            {
                stage = "data_ack";
                await ssl.WriteAsync(new[] { TcpPeerSession.Accepted }, timeout.Token).ConfigureAwait(false);
                await ssl.FlushAsync(timeout.Token).ConfigureAwait(false);
                transferred = true;
            }
            catch { await data!.DisposeAsync().ConfigureAwait(false); throw; }
            Log("transport.tcp_data_accepted", "tcp_data", remote.Address);
        }
        catch (Exception error)
        {
            LogFailure("transport.tcp_accept_failed", $"tcp_{stage}",
                (client.Client.RemoteEndPoint as IPEndPoint)?.Address, error);
            throw;
        }
        finally
        {
            if (!transferred)
            {
                if (ssl is not null) await ssl.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
            }
        }
    }

    private void Register(TcpPeerSession session)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        byte[] ticket = session.TicketCopy();
        string key;
        try { key = Convert.ToHexString(ticket); }
        finally { CryptographicOperations.ZeroMemory(ticket); }
        if (!sessions.TryAdd(key, session)) throw new AuthenticationException("A duplicate TCP session ticket was generated.");
        if (Volatile.Read(ref disposed) == 0) return;
        sessions.TryRemove(key, out _);
        throw new ObjectDisposedException(nameof(TcpPeerListener));
    }

    private TcpPeerSession? Resolve(ReadOnlySpan<byte> ticket)
        => sessions.TryGetValue(Convert.ToHexString(ticket), out TcpPeerSession? session) ? session : null;

    internal void Unregister(TcpPeerSession session)
    {
        foreach (var item in sessions)
            if (ReferenceEquals(item.Value, session)) sessions.TryRemove(item.Key, out _);
    }

    internal static async ValueTask WriteAttachHeaderAsync(Stream stream, byte kind, ReadOnlyMemory<byte> ticket,
        CancellationToken token, int? advertisedListenerPort = null)
    {
        if (ticket.Length is not (0 or TicketLength)) throw new ArgumentException("A session ticket must contain 32 bytes.", nameof(ticket));
        if (kind == TcpPeerSession.ControlConnectionKind)
        {
            if (advertisedListenerPort is < 1 or > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(advertisedListenerPort));
        }
        else if (advertisedListenerPort is not null)
            throw new ArgumentException("Only a control attach header carries a listener port.", nameof(advertisedListenerPort));
        byte[] header = [.. Magic, kind];
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (advertisedListenerPort is int port)
        {
            byte[] portBytes = new byte[sizeof(ushort)];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(portBytes, checked((ushort)port));
            await stream.WriteAsync(portBytes, token).ConfigureAwait(false);
        }
        if (!ticket.IsEmpty) await stream.WriteAsync(ticket, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal void Log(string eventName, string operation, IPAddress? remoteAddress,
        DiagnosticLevel level = DiagnosticLevel.Debug, string? errorCode = null, int? hResult = null,
        Exception? exception = null)
        => Options.Diagnostics?.TryWrite(new(eventName, operation)
            { Transport = DiagnosticTransport.Tcp, Level = level, ErrorCode = errorCode,
                HResult = hResult, RemoteAddress = remoteAddress, Exception = exception });

    internal void LogFailure(string eventName, string operation, IPAddress? remoteAddress, Exception error)
    {
        string category = error switch
        {
            AuthenticationException => "authentication_failed",
            SocketException socket => $"socket_{socket.SocketErrorCode}",
            OperationCanceledException => "cancelled_or_timed_out",
            IOException => "io_failed",
            ObjectDisposedException => "disposed",
            _ => "operation_failed",
        };
        int? nativeError = error is SocketException directSocket ? directSocket.NativeErrorCode
            : error.InnerException is SocketException innerSocket ? innerSocket.NativeErrorCode : null;
        Options.Diagnostics?.TryWrite(new(eventName, operation)
        {
            Transport = DiagnosticTransport.Tcp,
            Level = DiagnosticLevel.Warning,
            ErrorCode = category,
            Exception = error, HResult = error.HResult,
            NativeError = nativeError,
            RemoteAddress = remoteAddress,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await lifetime.CancelAsync().ConfigureAwait(false); } catch { }
        listener.Stop();
        try { await acceptLoop.ConfigureAwait(false); } catch { }
        foreach (TcpPeerSession session in sessions.Values.Distinct())
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await Task.WhenAll(handlers.Values).ConfigureAwait(false); } catch { }
        handshakes.Dispose(); lifetime.Dispose();
    }
}
