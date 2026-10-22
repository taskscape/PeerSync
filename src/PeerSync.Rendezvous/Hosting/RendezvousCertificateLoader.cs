using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerSync.Rendezvous.Hosting;

internal static class RendezvousCertificateLoader
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    internal static X509Certificate2 Load(string dnsName)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        DateTime now = DateTime.UtcNow;
        X509Certificate2? certificate = store.Certificates.OfType<X509Certificate2>()
            .Where(cert => cert.HasPrivateKey && cert.NotBefore.ToUniversalTime() <= now
                && cert.NotAfter.ToUniversalTime() > now
                && string.Equals(cert.GetNameInfo(X509NameType.DnsName, false), dnsName,
                    StringComparison.OrdinalIgnoreCase)
                && cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(eku =>
                    eku.EnhancedKeyUsages.OfType<Oid>().Any(oid => oid.Value == ServerAuthenticationOid)))
            .OrderByDescending(cert => cert.NotAfter).FirstOrDefault();
        return certificate ?? throw new InvalidOperationException(
            $"A current server-authentication certificate for {dnsName} with an accessible private key is required in LocalMachine\\My.");
    }
}
