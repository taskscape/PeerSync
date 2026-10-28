using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PeerSync.Net.Security;
using PeerSync.Net.Tcp;
using PeerSync.Net.Quic;
using PeerSync.Protocol.Handshake;
using Shouldly;
using Xunit;

namespace PeerSync.Net.Tests;

[Collection("QuicPortBinding")]
public sealed class TcpPeerSessionTests
{
    [Fact]
    public async Task ControlAndThreeDataLanesUsePinnedMutualTlsAndOneTimeSessionTicket()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        using var serverCertificate = CreateCertificate(); using var clientCertificate = CreateCertificate();
        byte[] serverId = PeerTls.DeviceId(serverCertificate), clientId = PeerTls.DeviceId(clientCertificate);
        await using var serverListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0), Options(serverCertificate, clientId));
        await using var clientListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0), Options(clientCertificate, serverId));

        Task<TcpPeerSession> accepting = serverListener.AcceptAsync(stop.Token).AsTask();
        await using TcpPeerSession client = await clientListener.ConnectAsync(serverListener.LocalEndPoint, stop.Token);
        await using TcpPeerSession server = await accepting;
        client.RemoteDeviceId.ShouldBe(serverId); server.RemoteDeviceId.ShouldBe(clientId);
        client.RemoteEndPoint.Port.ShouldBe(serverListener.LocalEndPoint.Port);
        server.RemoteEndPoint.Port.ShouldBe(clientListener.LocalEndPoint.Port);
        var controlTls = (SslStream)client.ControlStream;
        controlTls.SslProtocol.ShouldBe(SslProtocols.Tls13);
        controlTls.NegotiatedApplicationProtocol.ShouldBe(PeerTls.Alpn);

        var dataConnections = new List<TcpDataConnection>();
        for (int index = 0; index < TcpPeerSession.MaximumDataConnections; index++)
        {
            Task<TcpDataConnection> inboundTask = server.AcceptDataConnectionAsync(stop.Token).AsTask();
            TcpDataConnection outbound = await client.OpenDataConnectionAsync(serverListener.LocalEndPoint, stop.Token);
            TcpDataConnection inbound = await inboundTask;
            dataConnections.Add(outbound); dataConnections.Add(inbound);
            outbound.RemoteDeviceId.ShouldBe(serverId); inbound.RemoteDeviceId.ShouldBe(clientId);
            var dataTls = (SslStream)outbound.Stream;
            dataTls.SslProtocol.ShouldBe(SslProtocols.Tls13);
            dataTls.NegotiatedApplicationProtocol.ShouldBe(PeerTls.Alpn);
            ((SslStream)inbound.Stream).SslProtocol.ShouldBe(SslProtocols.Tls13);
            ((SslStream)inbound.Stream).NegotiatedApplicationProtocol.ShouldBe(PeerTls.Alpn);

            byte[] sent = BitConverter.GetBytes(index);
            await outbound.Stream.WriteAsync(sent, stop.Token);
            await outbound.Stream.FlushAsync(stop.Token);
            byte[] received = new byte[sent.Length];
            await inbound.Stream.ReadExactlyAsync(received, stop.Token);
            received.ShouldBe(sent);
        }

        client.ActiveDataConnections.ShouldBe(TcpPeerSession.MaximumDataConnections);
        server.ActiveDataConnections.ShouldBe(TcpPeerSession.MaximumDataConnections);
        await Should.ThrowAsync<InvalidOperationException>(() => client.OpenDataConnectionAsync(serverListener.LocalEndPoint, stop.Token).AsTask());
        foreach (TcpDataConnection connection in dataConnections) await connection.DisposeAsync();
        client.ActiveDataConnections.ShouldBe(0); server.ActiveDataConnections.ShouldBe(0);
    }

    [Fact]
    public async Task UnpinnedControlCertificateIsRejectedBeforeSessionAdmission()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var serverCertificate = CreateCertificate(); using var clientCertificate = CreateCertificate(); using var unrelated = CreateCertificate();
        byte[] serverId = PeerTls.DeviceId(serverCertificate), clientId = PeerTls.DeviceId(clientCertificate);
        byte[] wrongServerId = PeerTls.DeviceId(unrelated);
        string diagnosticDirectory = Path.Combine(Path.GetTempPath(), "PeerSync-Tcp-Diagnostics-" + Guid.NewGuid().ToString("N"));
        using var diagnostics = new PeerSync.Core.Diagnostics.DiagnosticJournal(diagnosticDirectory, "tcp_test", debugEnabled: true);
        await using var serverListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0), Options(serverCertificate, clientId));
        await using var clientListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0), Options(clientCertificate, wrongServerId, diagnostics));

        await Should.ThrowAsync<AuthenticationException>(() => clientListener.ConnectAsync(serverListener.LocalEndPoint, stop.Token).AsTask());
        serverId.ShouldNotBe(wrongServerId);

        string json = await File.ReadAllTextAsync(Path.Combine(diagnosticDirectory, "debug.jsonl"), stop.Token);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement entry = document.RootElement;
        entry.GetProperty("eventName").GetString().ShouldBe("transport.tcp_connect_failed");
        entry.GetProperty("operation").GetString().ShouldBe("tcp_pin");
        entry.GetProperty("errorCode").GetString().ShouldBe("authentication_failed");
        entry.GetProperty("hResult").GetInt32().ShouldNotBe(0);
        json.ShouldNotContain("PeerSync TCP Test");
        Directory.Delete(diagnosticDirectory, recursive: true);
    }

    [Fact]
    public async Task DataConnectionWithoutTheIssuedTicketIsRejected()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var serverCertificate = CreateCertificate(); using var clientCertificate = CreateCertificate();
        byte[] serverId = PeerTls.DeviceId(serverCertificate), clientId = PeerTls.DeviceId(clientCertificate);
        await using var serverListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0), Options(serverCertificate, clientId));
        await using var clientListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0), Options(clientCertificate, serverId));
        Task<TcpPeerSession> accepting = serverListener.AcceptAsync(stop.Token).AsTask();
        await using TcpPeerSession client = await clientListener.ConnectAsync(serverListener.LocalEndPoint, stop.Token);
        await using TcpPeerSession server = await accepting;

        using var attacker = new TcpClient(AddressFamily.InterNetwork);
        await attacker.ConnectAsync(serverListener.LocalEndPoint, stop.Token);
        await using var ssl = new SslStream(attacker.GetStream(), leaveInnerStreamOpen: true);
        await ssl.AuthenticateAsClientAsync(PeerTls.Client(clientCertificate), stop.Token);
        await ssl.WriteAsync(new byte[] { (byte)'P', (byte)'S', (byte)'P', (byte)'1', 2 }, stop.Token);
        await ssl.WriteAsync(RandomNumberGenerator.GetBytes(32), stop.Token);
        await ssl.FlushAsync(stop.Token);
        byte[] response = new byte[1];
        await ssl.ReadExactlyAsync(response, stop.Token);
        response[0].ShouldBe((byte)0);
        server.ActiveDataConnections.ShouldBe(0);
    }

    [Fact]
    public async Task TcpFallbackCanListenOnTheQuicUdpPort()
    {
        if (!QuicListener.IsSupported) return;
        using var certificate = CreateCertificate();
        byte[] deviceId = PeerTls.DeviceId(certificate);
        for (var attempt = 0; attempt < 256; attempt++)
        {
            try
            {
                // Pick outside Windows' dynamic port range. Port-zero UDP allocation
                // can walk a long consecutive span reserved by TCP, making even
                // hundreds of retries fail despite free ports elsewhere.
                int port = RandomNumberGenerator.GetInt32(20_000, 49_152);
                using var udpProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                await using var tcp = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, port), Options(certificate, deviceId));
                udpProbe.Dispose();
                await using var quic = await QuicPeerListener.ListenAsync(new IPEndPoint(IPAddress.Loopback, port),
                    new QuicPeerOptions
                    {
                        Certificate = certificate,
                        IsDevicePinned = peer => CryptographicOperations.FixedTimeEquals(peer, deviceId),
                        Folders = [],
                        CreateHello = () => new Hello
                        {
                            ProtocolVersion = 1, DeviceId = Google.Protobuf.ByteString.CopyFrom(deviceId),
                            DeviceName = "loopback", ClientVersion = "test",
                            Nonce = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                            UtcTicks = DateTime.UtcNow.Ticks,
                        },
                    }, TestContext.Current.CancellationToken);
                quic.LocalEndPoint.Port.ShouldBe(tcp.LocalEndPoint.Port);
                return;
            }
            catch (SocketException error) when (attempt < 255 && error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                // A separate process can claim the probed port before QUIC binds it.
            }
        }
        throw new InvalidOperationException("Could not obtain a port available to both TCP and QUIC after 256 attempts.");
    }

    private static TcpPeerOptions Options(X509Certificate2 certificate, byte[] pin,
        PeerSync.Core.Diagnostics.DiagnosticJournal? diagnostics = null) => new()
    {
        Certificate = certificate,
        IsDevicePinned = id => CryptographicOperations.FixedTimeEquals(id, pin),
        Diagnostics = diagnostics,
    };

    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PeerSync TCP Test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "test-only"), "test-only",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
}

[CollectionDefinition("QuicPortBinding", DisableParallelization = true)]
public sealed class QuicPortBindingGroup { }
