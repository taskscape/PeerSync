using System.Net;
using System.Net.Sockets;

namespace PeerSync.Rendezvous.Tests;

internal static class RendezvousTestPort
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> Reserved = [];

    internal static int Allocate()
    {
        lock (Gate)
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var tcp = new TcpListener(IPAddress.Loopback, 0);
                tcp.Start();
                try
                {
                    int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                    if (Reserved.Contains(port)) continue;
                    // Kestrel binds both TCP and QUIC UDP on this port. A free
                    // TCP port alone can still collide with another UDP user.
                    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                    Reserved.Add(port);
                    return port;
                }
                catch (SocketException) { }
                finally { tcp.Stop(); }
            }
        }
        throw new IOException("Could not reserve a free TCP and UDP test port.");
    }
}
