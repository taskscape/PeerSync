using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using PeerSync.Core.Diagnostics;
using PeerSync.Rendezvous.Contracts.V1;
using PeerSync.Rendezvous.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Rendezvous.Tests;

public sealed class RendezvousHostTests
{
    [Fact]
    public async Task PublicHttp2AndHttp3RoutesWorkWhileTrackerRequiresDeviceCertificate()
    {
        if (!System.Net.Quic.QuicListener.IsSupported) return;
        using RSA serverKey = RSA.Create(2048);
        using X509Certificate2 serverCertificate = ServerCertificate(serverKey);
        using ECDsa deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 deviceCertificate = DeviceCertificate(deviceKey);
        int port = RendezvousTestPort.Allocate();
        string origin = $"https://127.0.0.1:{port}";
        string logs = Path.GetFullPath(Path.Combine("artifacts", "tests", "rendezvous-host"));
        await using WebApplication app = RendezvousHost.Build(
        ["--Rendezvous:Port=" + port, "--Rendezvous:PublicUrl=" + origin,
            "--Rendezvous:LogDirectory=" + logs,
            "--Logging:LogLevel:Microsoft.AspNetCore.Server.Kestrel=Debug"], serverCertificate, IPAddress.Loopback);
        await app.StartAsync(TestContext.Current.CancellationToken);
        Guid probeSession = Guid.NewGuid();
        var probes = new System.Collections.Concurrent.ConcurrentQueue<DiagnosticEvent>();
        app.Services.GetRequiredService<DiagnosticJournal>().EntryWritten += (entry, _) =>
        {
            if (entry.Event == "rendezvous.probed") probes.Enqueue(entry);
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        using (var handler = Handler(serverCertificate))
        using (var client = new HttpClient(handler))
        {
            using var health = new HttpRequestMessage(HttpMethod.Get, origin + "/healthz")
            { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using HttpResponseMessage healthResponse = await client.SendAsync(health, timeout.Token);
            healthResponse.IsSuccessStatusCode.ShouldBeTrue();
            healthResponse.Version.ShouldBe(HttpVersion.Version20);

            using var probe = new HttpRequestMessage(HttpMethod.Get, origin + "/probe")
            { Version = HttpVersion.Version30, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            probe.Headers.Add(ProbeCorrelation.HeaderName, ProbeCorrelation.Format(probeSession));
            using HttpResponseMessage probeResponse = await client.SendAsync(probe, timeout.Token);
            probeResponse.IsSuccessStatusCode.ShouldBeTrue();
            probeResponse.Version.ShouldBe(HttpVersion.Version30);
            using JsonDocument body = JsonDocument.Parse(await probeResponse.Content.ReadAsStringAsync(timeout.Token));
            body.RootElement.GetProperty("httpVersion").GetString().ShouldBe("HTTP/3");
            body.RootElement.GetProperty("observedAddress").GetString().ShouldBe("127.0.0.1");
            body.RootElement.GetProperty("probeSessionId").GetString().ShouldBe(ProbeCorrelation.Format(probeSession));
            probes.Count.ShouldBe(1);
            probes.Single().SessionId.ShouldBe(probeSession);
            probes.Single().Transport.ShouldBe(DiagnosticTransport.Http3);

            using var invalidProbe = new HttpRequestMessage(HttpMethod.Get, origin + "/probe")
            { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            invalidProbe.Headers.Add(ProbeCorrelation.HeaderName, "invalid-id");
            using HttpResponseMessage invalidResponse = await client.SendAsync(invalidProbe, timeout.Token);
            invalidResponse.IsSuccessStatusCode.ShouldBeTrue();
            using JsonDocument invalidBody = JsonDocument.Parse(
                await invalidResponse.Content.ReadAsStringAsync(timeout.Token));
            invalidBody.RootElement.GetProperty("probeSessionId").ValueKind.ShouldBe(JsonValueKind.Null);
            probes.Count.ShouldBe(2);
            probes.Last().SessionId.ShouldBeNull();
        }

        using (var anonymousHandler = Handler(serverCertificate))
        using (var anonymousChannel = Channel(origin, anonymousHandler))
        using (var anonymous = new PeerSync.Rendezvous.Contracts.V1.Rendezvous.RendezvousClient(anonymousChannel).Session(cancellationToken: timeout.Token))
        {
            RpcException rejected = await Should.ThrowAsync<RpcException>(async () =>
                await anonymous.ResponseStream.MoveNext(timeout.Token));
            rejected.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        }

        using var deviceHandler = Handler(serverCertificate, deviceCertificate);
        using var deviceChannel = Channel(origin, deviceHandler);
        using var session = new PeerSync.Rendezvous.Contracts.V1.Rendezvous.RendezvousClient(deviceChannel).Session(cancellationToken: timeout.Token);
        (await session.ResponseStream.MoveNext(timeout.Token)).ShouldBeTrue();
        ObservedAddress observed = session.ResponseStream.Current.ObservedAddress;
        observed.SessionId.Length.ShouldBe(16);
        observed.Endpoint.Address.ShouldBe("127.0.0.1");
        var announce = new Announce { ListenPort = 47821 };
        announce.FolderIds.Add(ByteString.CopyFrom(new byte[32]));
        await session.RequestStream.WriteAsync(new ClientMessage { Announce = announce }, timeout.Token);
        (await session.ResponseStream.MoveNext(timeout.Token)).ShouldBeTrue();
        session.ResponseStream.Current.PeerList.FolderId.Length.ShouldBe(32);
        session.ResponseStream.Current.PeerList.Peers.ShouldBeEmpty();
        await session.RequestStream.CompleteAsync();
    }

    private static GrpcChannel Channel(string origin, SocketsHttpHandler handler)
        => GrpcChannel.ForAddress(origin, new GrpcChannelOptions
        { HttpHandler = handler, HttpVersion = HttpVersion.Version20,
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact });

    private static SocketsHttpHandler Handler(X509Certificate2 server, X509Certificate2? device = null)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new()
            {
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256)
                        .Equals(server.GetCertHashString(HashAlgorithmName.SHA256), StringComparison.OrdinalIgnoreCase),
            },
        };
        if (device is not null) handler.SslOptions.ClientCertificates = new X509CertificateCollection { device };
        return handler;
    }

    private static X509Certificate2 ServerCertificate(RSA key)
    {
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "host-test"),
            "host-test", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 DeviceCertificate(ECDsa key)
    {
        var request = new CertificateRequest("CN=PeerSync Device", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "device-test"),
            "device-test", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
}
