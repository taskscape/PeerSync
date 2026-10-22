using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Text;
using System.Text.Json;
using PeerSync.Service.Update;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class UpdateManifestVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PinnedSignedOfferAcceptsOnlyNewerBoundedHttpsSetup()
    {
        using var signer = Signer();
        byte[] pin = SHA256.HashData(signer.RawData);
        byte[] expected = SHA256.HashData("setup"u8);
        byte[] envelope = Offer(signer, "1.0.7", "https://updates.example.test/PeerSync-Setup.exe",
            Convert.ToHexString(expected), 5, Now.AddDays(2).UtcTicks);

        var accepted = UpdateManifestVerifier.Verify(envelope, signer, pin, new Version(1, 0, 6), Now);

        accepted.Version.ShouldBe(new Version(1, 0, 7));
        accepted.SetupUrl.Scheme.ShouldBe("https");
        accepted.Sha256.ShouldBe(expected);
        accepted.Size.ShouldBe(5);
    }

    [Fact]
    public void ModifiedOrDifferentPublisherOffersAreRejectedBeforeDownload()
    {
        using var signer = Signer(); using var other = Signer();
        byte[] pin = SHA256.HashData(signer.RawData);
        byte[] envelope = Offer(signer, "1.0.7", "https://updates.example.test/setup.exe",
            new string('A', 64), 5, Now.AddDays(2).UtcTicks);
        using var parsed = JsonDocument.Parse(envelope);
        byte[] payload = Convert.FromBase64String(parsed.RootElement.GetProperty("payload").GetString()!);
        string changed = Encoding.UTF8.GetString(payload).Replace("setup.exe", "other.exe", StringComparison.Ordinal);
        byte[] tampered = JsonSerializer.SerializeToUtf8Bytes(new
        { payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(changed)), signature = parsed.RootElement.GetProperty("signature").GetString() });

        Should.Throw<InvalidDataException>(() => UpdateManifestVerifier.Verify(tampered, signer, pin, new Version(1, 0, 6), Now));
        Should.Throw<InvalidDataException>(() => UpdateManifestVerifier.Verify(envelope, other, pin, new Version(1, 0, 6), Now));
        Should.Throw<InvalidDataException>(() => UpdateManifestVerifier.Verify(envelope, signer, SHA256.HashData(other.RawData), new Version(1, 0, 6), Now));
    }

    [Theory]
    [InlineData("1.0.6", "https://updates.example.test/setup.exe", 5, 2)]
    [InlineData("1.0.5", "https://updates.example.test/setup.exe", 5, 2)]
    [InlineData("1.0.7", "http://updates.example.test/setup.exe", 5, 2)]
    [InlineData("1.0.7", "https://user@updates.example.test/setup.exe", 5, 2)]
    [InlineData("1.0.7", "https://updates.example.test/setup.exe#fragment", 5, 2)]
    [InlineData("1.0.7", "https://updates.example.test/setup.exe", 0, 2)]
    [InlineData("1.0.7", "https://updates.example.test/setup.exe", 5, -1)]
    [InlineData("1.0.7", "https://updates.example.test/setup.exe", 5, 31)]
    public void SignedButUnsafeOffersAreRejected(string version, string url, long size, int expiryDays)
    {
        using var signer = Signer();
        byte[] offer = Offer(signer, version, url, new string('A', 64), size, Now.AddDays(expiryDays).UtcTicks);
        Should.Throw<InvalidDataException>(() => UpdateManifestVerifier.Verify(offer, signer,
            SHA256.HashData(signer.RawData), new Version(1, 0, 6), Now));
    }

    [Fact]
    public void OversizedAndDuplicateEnvelopesAreRejected()
    {
        using var signer = Signer(); byte[] pin = SHA256.HashData(signer.RawData);
        byte[] oversized = new byte[32 * 1024 + 1];
        Should.Throw<InvalidDataException>(() => UpdateManifestVerifier.Verify(oversized, signer, pin,
            new Version(1, 0, 6), Now));
        byte[] duplicate = Encoding.UTF8.GetBytes("{\"payload\":\"AA==\",\"payload\":\"AA==\",\"signature\":\"AA==\"}");
        Should.Throw<InvalidDataException>(() => UpdateManifestVerifier.Verify(duplicate, signer, pin,
            new Version(1, 0, 6), Now));
    }

    [Fact]
    public void AdminPolicyPinsItsCertificateAndCanDisableUpdates()
    {
        using var signer = Signer();
        string path = Path.Combine(Path.GetTempPath(), "PeerSync-update-policy-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            string certificate = Convert.ToBase64String(signer.Export(X509ContentType.Cert));
            string pin = Convert.ToHexString(SHA256.HashData(signer.Export(X509ContentType.Cert)));
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
            { enabled = false, manifestUrl = "https://updates.example.test/manifest.json",
                publisherCertificate = certificate, publisherCertificateSha256 = pin }));
            UpdatePolicy policy = new UpdatePolicyStore(path).ReadIfPresent()!;
            policy.Enabled.ShouldBeFalse();
            using (var opened = policy.OpenPublisher()) opened.Thumbprint.ShouldBe(signer.Thumbprint);

            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
            { enabled = true, manifestUrl = "https://updates.example.test/manifest.json",
                publisherCertificate = certificate, publisherCertificateSha256 = new string('0', 64) }));
            Should.Throw<InvalidDataException>(() => new UpdatePolicyStore(path).ReadIfPresent());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DownloaderStagesOnlyExactSignedSetupAndDisabledPolicyMakesNoRequest()
    {
        using var signer = Signer();
        byte[] cert = signer.Export(X509ContentType.Cert);
        var manifestUrl = new Uri("https://updates.example.test/manifest.json");
        var setupUrl = new Uri("https://updates.example.test/setup.exe");
        byte[] setup = "signed setup bytes"u8.ToArray();
        byte[] envelope = Offer(signer, "1.0.7", setupUrl.AbsoluteUri,
            Convert.ToHexString(SHA256.HashData(setup)), setup.Length, Now.AddDays(2).UtcTicks);
        var policy = new UpdatePolicy(true, manifestUrl, cert, SHA256.HashData(cert));
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-updates-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(new FixedResponses(manifestUrl, envelope, setupUrl, setup));
            var downloader = new UpdateDownloader(client, root, new Version(1, 0, 6), new FixedClock(Now));
            (await downloader.CheckOnceAsync(policy, TestContext.Current.CancellationToken)).ShouldBeTrue();
            string ready = Directory.GetDirectories(root, "*.ready").Single();
            File.ReadAllBytes(Path.Combine(ready, "setup.exe")).ShouldBe(setup);
            File.ReadAllBytes(Path.Combine(ready, "manifest.json")).ShouldBe(envelope);
            (await downloader.CheckOnceAsync(policy with { Enabled = false }, TestContext.Current.CancellationToken))
                .ShouldBeFalse();
        }
        finally { DeleteOwnedFixture(root); }
    }

    [Fact]
    public async Task DownloaderRejectsModifiedSetupAndLeavesNoReadySlot()
    {
        using var signer = Signer(); byte[] cert = signer.Export(X509ContentType.Cert);
        var manifestUrl = new Uri("https://updates.example.test/manifest.json");
        var setupUrl = new Uri("https://updates.example.test/setup.exe");
        byte[] setup = "correct setup"u8.ToArray();
        byte[] envelope = Offer(signer, "1.0.7", setupUrl.AbsoluteUri,
            Convert.ToHexString(SHA256.HashData(setup)), setup.Length, Now.AddDays(2).UtcTicks);
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-updates-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(new FixedResponses(manifestUrl, envelope, setupUrl,
                "altered setup"u8.ToArray()));
            var policy = new UpdatePolicy(true, manifestUrl, cert, SHA256.HashData(cert));
            var downloader = new UpdateDownloader(client, root, new Version(1, 0, 6), new FixedClock(Now));
            await Should.ThrowAsync<InvalidDataException>(() => downloader.CheckOnceAsync(policy,
                TestContext.Current.CancellationToken));
            Directory.GetDirectories(root).ShouldBeEmpty();
        }
        finally { DeleteOwnedFixture(root); }
    }

    [Fact]
    public async Task CurrentSignedOfferIsAQuietNoopAfterUpgrade()
    {
        using var signer = Signer(); byte[] cert = signer.Export(X509ContentType.Cert);
        var manifestUrl = new Uri("https://updates.example.test/manifest.json");
        var setupUrl = new Uri("https://updates.example.test/setup.exe");
        byte[] setup = "already installed"u8.ToArray();
        byte[] envelope = Offer(signer, "1.0.10.0", setupUrl.AbsoluteUri,
            Convert.ToHexString(SHA256.HashData(setup)), setup.Length, Now.AddDays(2).UtcTicks);
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-updates-" + Guid.NewGuid().ToString("N"));
        try
        {
            var responses = new FixedResponses(manifestUrl, envelope, setupUrl, setup);
            using var client = new HttpClient(responses);
            var policy = new UpdatePolicy(true, manifestUrl, cert, SHA256.HashData(cert));
            var downloader = new UpdateDownloader(client, root, new Version(1, 0, 10, 0), new FixedClock(Now));
            (await downloader.CheckOnceAsync(policy, TestContext.Current.CancellationToken)).ShouldBeFalse();
            responses.Calls.ShouldBe(1);
            Directory.GetDirectories(root).ShouldBeEmpty();
        }
        finally { DeleteOwnedFixture(root); }
    }

    [Fact]
    public void ShortLivedPreflightReverifiesStagedBytesAndEnabledPolicy()
    {
        using var signer = Signer(); byte[] cert = signer.Export(X509ContentType.Cert);
        byte[] setup = "preflight setup"u8.ToArray();
        byte[] envelope = Offer(signer, "99.0.0", "https://updates.example.test/setup.exe",
            Convert.ToHexString(SHA256.HashData(setup)), setup.Length, Now.AddDays(2).UtcTicks);
        string root = Path.Combine(Path.GetTempPath(), "PeerSync-updates-" + Guid.NewGuid().ToString("N"));
        string policyDir = Path.Combine(root, "update-policy"), stage = Path.Combine(root, "staged");
        Directory.CreateDirectory(policyDir); Directory.CreateDirectory(stage);
        try
        {
            string policyPath = Path.Combine(policyDir, "update-policy.json");
            File.WriteAllBytes(policyPath, JsonSerializer.SerializeToUtf8Bytes(new
            { enabled = true, manifestUrl = "https://updates.example.test/manifest.json",
                publisherCertificate = Convert.ToBase64String(cert),
                publisherCertificateSha256 = Convert.ToHexString(SHA256.HashData(cert)) }));
            File.WriteAllBytes(Path.Combine(stage, "manifest.json"), envelope);
            string setupPath = Path.Combine(stage, "setup.exe"); File.WriteAllBytes(setupPath, setup);
            using var output = new StringWriter(); using var error = new StringWriter();
            UpdateVerificationCommand.Run(["--verify-update", stage], root, output, error, Now).ShouldBe(0);
            File.WriteAllBytes(setupPath, "bad contents!!!"u8.ToArray());
            UpdateVerificationCommand.Run(["--verify-update", stage], root, output, error, Now).ShouldBe(1);
        }
        finally { DeleteOwnedFixture(root); }
    }

    private static void DeleteOwnedFixture(string root)
    {
        string full = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("PeerSync-updates-", StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(full)["PeerSync-updates-".Length..], "N", out _))
            throw new IOException("Unexpected update-test fixture root.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedResponses(Uri manifestUrl, byte[] manifest, Uri setupUrl, byte[] setup) : HttpMessageHandler
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            byte[] bytes = request.RequestUri == manifestUrl ? manifest
                : request.RequestUri == setupUrl ? setup : throw new IOException("Unexpected update URL.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes), RequestMessage = request });
        }
    }

    private static X509Certificate2 Signer()
    {
        using RSA key = RSA.Create(3072);
        var request = new CertificateRequest("CN=PeerSync update test", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 temporary = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx, "update-test"),
            "update-test", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static byte[] Offer(X509Certificate2 signer, string version, string url, string hash,
        long size, long expiryTicks)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        { version, url, sha256 = hash, size, expiresUtcTicks = expiryTicks });
        using RSA key = signer.GetRSAPrivateKey()!;
        byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return JsonSerializer.SerializeToUtf8Bytes(new
        { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) });
    }
}
