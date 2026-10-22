using System.Net;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Net.Connections;

public enum ConnectionSource
{
    Lan = 0,
    Public = 1,
    Punch = 2,
    Relay = 3,
}

/// <summary>A single already-pinned transport path. Authentication must complete before the delegate returns.</summary>
public sealed class ConnectionCandidate
{
    public ConnectionCandidate(ConnectionSource source,
        Func<CancellationToken, ValueTask<ManagedPeerConnection>> connectAsync)
        : this(source, (DiagnosticTransport?)null, (IPEndPoint?)null, connectAsync, validateEndpoint: false) { }

    internal ConnectionCandidate(ConnectionSource source, DiagnosticTransport transport, IPEndPoint remoteEndpoint,
        Func<CancellationToken, ValueTask<ManagedPeerConnection>> connectAsync)
        : this(source, transport, remoteEndpoint, connectAsync, validateEndpoint: true) { }

    private ConnectionCandidate(ConnectionSource source, DiagnosticTransport? transport, IPEndPoint? remoteEndpoint,
        Func<CancellationToken, ValueTask<ManagedPeerConnection>> connectAsync, bool validateEndpoint = false)
    {
        if (validateEndpoint && remoteEndpoint is null) throw new ArgumentNullException(nameof(remoteEndpoint));
        Source = source;
        Transport = transport;
        RemoteAddress = remoteEndpoint?.Address;
        RemotePort = remoteEndpoint?.Port;
        ConnectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
    }

    public ConnectionSource Source { get; }
    internal DiagnosticTransport? Transport { get; }
    internal IPAddress? RemoteAddress { get; }
    internal int? RemotePort { get; }
    internal Func<CancellationToken, ValueTask<ManagedPeerConnection>> ConnectAsync { get; }
}
