using System.Security.Cryptography;
using PeerSync.Net.Quic;
using PeerSync.Net.Tcp;

namespace PeerSync.Net.Connections;

/// <summary>Authenticated connection ownership shared by the manager and the transport-specific session.</summary>
public sealed class ManagedPeerConnection : IAsyncDisposable
{
    private readonly byte[] localDeviceId;
    private readonly byte[] remoteDeviceId;
    private readonly IAsyncDisposable session;
    private readonly Task closed;
    private int disposed;

    public ManagedPeerConnection(Guid sessionId, ReadOnlySpan<byte> localDeviceId, ReadOnlySpan<byte> remoteDeviceId,
        bool isInitiator, IAsyncDisposable session, Task closed)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("An authenticated session id is required.", nameof(sessionId));
        if (localDeviceId.Length != 32 || remoteDeviceId.Length != 32) throw new ArgumentException("Device ids must contain 32 bytes.");
        if (CryptographicOperations.FixedTimeEquals(localDeviceId, remoteDeviceId)) throw new ArgumentException("A peer cannot connect to itself.");
        SessionId = sessionId;
        this.localDeviceId = localDeviceId.ToArray();
        this.remoteDeviceId = remoteDeviceId.ToArray();
        IsInitiator = isInitiator;
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.closed = closed ?? throw new ArgumentNullException(nameof(closed));
    }

    public Guid SessionId { get; }
    public byte[] LocalDeviceId => localDeviceId.ToArray();
    public byte[] RemoteDeviceId => remoteDeviceId.ToArray();
    public bool IsInitiator { get; }
    public Task Closed => closed;

    public static ManagedPeerConnection FromQuic(QuicPeerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(session.SessionId, session.LocalDeviceId, session.RemoteDeviceId, session.IsInitiator, session, session.Closed);
    }

    public static ManagedPeerConnection FromTcp(TcpPeerProtocolSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(session.SessionId, session.LocalDeviceId, session.RemoteDeviceId, session.IsInitiator, session, session.Closed);
    }

    internal ReadOnlySpan<byte> RemoteId => remoteDeviceId;
    internal IAsyncDisposable Session => session;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        return session.DisposeAsync();
    }
}
