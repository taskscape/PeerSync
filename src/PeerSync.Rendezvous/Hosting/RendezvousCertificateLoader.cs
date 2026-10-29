using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PeerSync.Rendezvous.Hosting;

internal static class RendezvousCertificateLoader
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private static readonly byte[] KeyCheck = Encoding.ASCII.GetBytes("PeerSync rendezvous TLS key check");

    internal static X509Certificate2 Load(string dnsName)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return SelectUsable(store.Certificates.OfType<X509Certificate2>(), dnsName, DateTime.UtcNow)
            ?? throw new InvalidOperationException(
                $"A current server-authentication certificate for {dnsName} with a usable machine private key is required in LocalMachine\\My.");
    }

    internal static X509Certificate2? SelectUsable(IEnumerable<X509Certificate2> certificates,
        string dnsName, DateTime now)
        => certificates
            .Where(cert => cert.HasPrivateKey && cert.NotBefore.ToUniversalTime() <= now
                && cert.NotAfter.ToUniversalTime() > now
                && string.Equals(cert.GetNameInfo(X509NameType.DnsName, false), dnsName,
                    StringComparison.OrdinalIgnoreCase)
                && cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(eku =>
                    eku.EnhancedKeyUsages.OfType<Oid>().Any(oid => oid.Value == ServerAuthenticationOid)))
            .OrderByDescending(cert => cert.NotAfter)
            .FirstOrDefault(CanSignAndVerify);

    private static bool CanSignAndVerify(X509Certificate2 certificate)
    {
        try
        {
            using RSA? rsa = certificate.GetRSAPrivateKey();
            if (rsa is not null)
            {
                using RSA? publicKey = certificate.GetRSAPublicKey();
                return publicKey is not null && publicKey.VerifyData(KeyCheck,
                    rsa.SignData(KeyCheck, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            using ECDsa? ecdsa = certificate.GetECDsaPrivateKey();
            if (ecdsa is null) return false;
            using ECDsa? ecdsaPublicKey = certificate.GetECDsaPublicKey();
            return ecdsaPublicKey is not null && ecdsaPublicKey.VerifyData(KeyCheck,
                ecdsa.SignData(KeyCheck, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256);
        }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException
            or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
