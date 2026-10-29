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
            for (int attempt = 0; attempt < 128; attempt++)
            {
                // Windows can exclude much of its ephemeral range for UDP.
                // Probe both protocols in the configured peer-listener range.
                int port = Random.Shared.Next(20000, 45001);
                if (Reserved.Contains(port)) continue;
                var tcp = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    tcp.Start();
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
