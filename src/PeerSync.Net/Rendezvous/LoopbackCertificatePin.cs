using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerSync.Net.Rendezvous;

internal static class LoopbackCertificatePin
{
    internal static void Apply(SocketsHttpHandler handler, Uri server, X509Certificate2? certificate)
    {
        if (certificate is null) return;
        if (!(server.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(server.Host, out IPAddress? address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("A test certificate pin is permitted only for loopback servers.", nameof(server));
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented is not null && presented.GetCertHashString(HashAlgorithmName.SHA256)
                .Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), StringComparison.OrdinalIgnoreCase);
    }
}
