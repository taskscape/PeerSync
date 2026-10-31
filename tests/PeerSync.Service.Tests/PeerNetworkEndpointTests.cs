using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Net.Security;
using PeerSync.Service.Control;
using PeerSync.Service.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class PeerNetworkEndpointTests
{
    [Fact]
    public async Task ListenerWaitsForPreviousTcpOwnerToReleasePort()
    {
        if (!QuicListener.IsSupported) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        string logDirectory = Path.Combine(Path.GetTempPath(), "PeerSync-bind-retry-" + Guid.NewGuid().ToString("N"));
        using var journal = new DiagnosticJournal(logDirectory, "service_test", debugEnabled: true);
        var retrySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        journal.EntryWritten += (entry, _) =>
        {
            if (entry.Event == "service.listener_bind_retry") retrySeen.TrySetResult();
            if (entry.Event == "service.listener_bind_recovered") recovered.TrySetResult();
        };
        // The service binds QUIC before TCP. Choose from its configured port
        // range: Windows may allocate TCP ephemeral ports inside a contiguous
        // UDP exclusion range, so repeated port-zero binds do not escape it.
        using var blocker = ReserveTcpPortWithAvailableUdp();
        int port = ((IPEndPoint)blocker.LocalEndPoint!).Port;

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PeerSync listener retry test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, "service-test"), "service-test",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        byte[] deviceId = PeerTls.DeviceId(certificate);
        Task<PeerNetworkRuntime> startup = PeerNetworkRuntime.StartAsync(certificate, deviceId,
            () => [], new DeviceSettings { ListenPort = port, LanDiscovery = false },
            new ControlStatusRuntime(), (_, _) => { }, journal, timeout.Token);
        try
        {
            await retrySeen.Task.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
        }
        finally { blocker.Dispose(); }
        await using var runtime = await startup;
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
        runtime.Completion.IsFaulted.ShouldBeFalse();
    }

    private static Socket ReserveTcpPortWithAvailableUdp()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            { ExclusiveAddressUse = true };
            try
            {
                blocker.Bind(new IPEndPoint(IPAddress.Any, Random.Shared.Next(20000, 45001)));
                blocker.Listen(1);
                int port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
                using var udpProbe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                { ExclusiveAddressUse = true };
                udpProbe.Bind(new IPEndPoint(IPAddress.Any, port));
                return blocker;
            }
            catch (SocketException)
            {
                blocker.Dispose();
            }
        }
        throw new IOException("No TCP listener port was also available for QUIC after 32 attempts.");
    }

    [Theory]
    [InlineData("192.168.0.183", true)]
    [InlineData("100.72.94.33", true)]
    [InlineData("203.0.113.8", true)]
    [InlineData("::ffff:203.0.113.8", true)]
    [InlineData("2001:db8::8", true)]
    [InlineData("0.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("239.1.2.3", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::ffff:239.1.2.3", false)]
    [InlineData("ff02::1", false)]
    [InlineData("fe80::1", false)]
    public void OnlyUnicastPeerAddressesAreDialCandidates(string value, bool expected)
    {
        PeerNetworkRuntime.IsDialableUnicast(IPAddress.Parse(value)).ShouldBe(expected);
    }
}
