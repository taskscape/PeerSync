using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerSync.Spikes;

public static class DeviceIdentity
{
    public static byte[] FromCertificate(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return SHA256.HashData(loaded.PublicKey.ExportSubjectPublicKeyInfo());
    }

    public static bool IsDeviceCertificate(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var publicKey = loaded.GetECDsaPublicKey();
        return publicKey?.KeySize == 256 && loaded.NotBefore.ToUniversalTime() <= DateTime.UtcNow
            && loaded.NotAfter.ToUniversalTime() > DateTime.UtcNow;
    }
}
