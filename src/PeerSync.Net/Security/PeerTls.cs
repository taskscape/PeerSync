using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerSync.Net.Security;

public static class PeerTls
{
    public static readonly SslApplicationProtocol Alpn = new("psp/1");

    public static byte[] DeviceId(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return SHA256.HashData(loaded.PublicKey.ExportSubjectPublicKeyInfo());
    }

    public static bool IsDeviceCertificate(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        try
        {
            using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var key = loaded.GetECDsaPublicKey();
            return key is not null && key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7"
                && loaded.NotBefore.ToUniversalTime() <= DateTime.UtcNow && loaded.NotAfter.ToUniversalTime() > DateTime.UtcNow;
        }
        catch (CryptographicException) { return false; }
    }

    public static SslServerAuthenticationOptions Server(X509Certificate2 certificate)
    {
        RequireOwn(certificate);
        return new() { ApplicationProtocols = [Alpn], ServerCertificate = certificate, ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls13, RemoteCertificateValidationCallback = (_, peer, _, _) => IsDeviceCertificate(peer) };
    }
    public static SslClientAuthenticationOptions Client(X509Certificate2 certificate)
    {
        RequireOwn(certificate);
        return new() { ApplicationProtocols = [Alpn], TargetHost = "peersync", ClientCertificates = new() { certificate },
            EnabledSslProtocols = SslProtocols.Tls13, RemoteCertificateValidationCallback = (_, peer, _, _) => IsDeviceCertificate(peer) };
    }
    private static void RequireOwn(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey || !IsDeviceCertificate(certificate)) throw new ArgumentException("A current private P-256 device certificate is required.", nameof(certificate));
    }
}
