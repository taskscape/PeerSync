using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace PeerSync.Spikes;

public static class PeerTls
{
    public static readonly SslApplicationProtocol Alpn = new("psp/1");
    public static SslServerAuthenticationOptions Server(X509Certificate2 certificate) => new()
    {
        ApplicationProtocols = [Alpn], ServerCertificate = certificate, ClientCertificateRequired = true,
        EnabledSslProtocols = SslProtocols.Tls13,
        RemoteCertificateValidationCallback = (_, cert, _, _) => DeviceIdentity.IsDeviceCertificate(cert),
    };
    public static SslClientAuthenticationOptions Client(X509Certificate2 certificate) => new()
    {
        ApplicationProtocols = [Alpn], TargetHost = "peersync", ClientCertificates = new() { certificate },
        EnabledSslProtocols = SslProtocols.Tls13,
        RemoteCertificateValidationCallback = (_, cert, _, _) => DeviceIdentity.IsDeviceCertificate(cert),
    };
}
