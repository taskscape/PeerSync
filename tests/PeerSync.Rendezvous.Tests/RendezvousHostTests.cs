using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
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
        int port = AvailablePort();
        string origin = $"https://127.0.0.1:{port}";
        string logs = Path.GetFullPath(Path.Combine("artifacts", "tests", "rendezvous-host"));
        await using WebApplication app = RendezvousHost.Build(
        ["--Rendezvous:Port=" + port, "--Rendezvous:PublicUrl=" + origin,
            "--Rendezvous:LogDirectory=" + logs,
            "--Logging:LogLevel:Microsoft.AspNetCore.Server.Kestrel=Debug"], serverCertificate, IPAddress.Loopback);
        await app.StartAsync(TestContext.Current.CancellationToken);
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
            using HttpResponseMessage probeResponse = await client.SendAsync(probe, timeout.Token);
            probeResponse.IsSuccessStatusCode.ShouldBeTrue();
            probeResponse.Version.ShouldBe(HttpVersion.Version30);
            string body = await probeResponse.Content.ReadAsStringAsync(timeout.Token);
            body.ShouldContain("HTTP/3");
            body.ShouldContain("127.0.0.1");
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

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
