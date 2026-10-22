using System.Security.Cryptography.X509Certificates;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Security;

namespace PeerSync.Net.Tcp;

public sealed class TcpPeerOptions
{
    public required X509Certificate2 Certificate { get; init; }
    public required Func<byte[], bool> IsDevicePinned { get; init; }
    public DiagnosticJournal? Diagnostics { get; init; }
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Certificate);
        ArgumentNullException.ThrowIfNull(IsDevicePinned);
        if (HandshakeTimeout <= TimeSpan.Zero || HandshakeTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout));
        _ = PeerTls.Server(Certificate);
    }
}
