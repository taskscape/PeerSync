using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PeerSync.Rendezvous.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Rendezvous.Tests;

public sealed class RendezvousCertificateLoaderTests
{
    [Fact]
    public void SelectUsableSkipsNewerCertificateWithoutItsPrivateKey()
    {
        DateTime now = DateTime.UtcNow;
        using RSA olderKey = RSA.Create(2048);
        using RSA newerKey = RSA.Create(2048);
        using X509Certificate2 older = RsaCertificate(olderKey, "sync.taskscape.com", now.AddDays(30));
        using X509Certificate2 newer = RsaCertificate(newerKey, "sync.taskscape.com", now.AddDays(60));
        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(
            newer.Export(X509ContentType.Cert));

        publicOnly.HasPrivateKey.ShouldBeFalse();
        RendezvousCertificateLoader.SelectUsable([publicOnly, older], "sync.taskscape.com", now)
            .ShouldBeSameAs(older);
        RendezvousCertificateLoader.SelectUsable([publicOnly], "sync.taskscape.com", now)
            .ShouldBeNull();
    }

    [Fact]
    public void SelectUsableRequiresNameAndAcceptsEcdsaKey()
    {
        DateTime now = DateTime.UtcNow;
        using ECDsa wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa validKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 wrong = EcdsaCertificate(wrongKey, "other.taskscape.com", now.AddDays(60));
        using X509Certificate2 valid = EcdsaCertificate(validKey, "sync.taskscape.com", now.AddDays(30));

        RendezvousCertificateLoader.SelectUsable([wrong, valid], "sync.taskscape.com", now)
            .ShouldBeSameAs(valid);
    }

    private static X509Certificate2 RsaCertificate(RSA key, string dnsName, DateTime expiry)
    {
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        AddServerExtensions(request, dnsName);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), new DateTimeOffset(expiry));
    }

    private static X509Certificate2 EcdsaCertificate(ECDsa key, string dnsName, DateTime expiry)
    {
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256);
        AddServerExtensions(request, dnsName);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), new DateTimeOffset(expiry));
    }

    private static void AddServerExtensions(CertificateRequest request, string dnsName)
    {
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
    }
}
