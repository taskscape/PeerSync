using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PeerSync.Spikes;

if (args.Length < 2)
{
    Console.Error.WriteLine("create <serviceSID> | delete <thumbprint> | serve <thumbprint> <port> | verify <thumbprint> <IP> <port>");
    return 2;
}
if (args[0] == "create")
{
    using var created = new SpikeCertificate(args[1]);
    created.AddToMachineStore();
    Console.WriteLine(created.Certificate.Thumbprint);
    return 0;
}
using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
store.Open(args[0] == "delete" ? OpenFlags.ReadWrite : OpenFlags.ReadOnly);
var certificates = store.Certificates.Find(X509FindType.FindByThumbprint, args[1], validOnly: false);
if (certificates.Count != 1) throw new InvalidOperationException("Spike certificate not found.");
using var certificate = certificates[0];
if (certificate.Subject != "CN=PeerSync Spike") throw new InvalidOperationException("Refusing to operate on a non-spike certificate.");
if (args[0] == "delete")
{
    using var ecdsa = certificate.GetECDsaPrivateKey() as ECDsaCng ?? throw new InvalidOperationException("Not a CNG key.");
    store.Remove(certificate);
    ecdsa.Key.Delete();
    return 0;
}
if (args[0] == "assert-key-denied")
{
    try
    {
        using var signer = certificate.GetECDsaPrivateKey();
        _ = signer!.SignData(new byte[] { 1 }, HashAlgorithmName.SHA256);
        Console.WriteLine("{\"privateKeyDenied\":false}");
        return 1;
    }
    catch (CryptographicException)
    {
        Console.WriteLine("{\"privateKeyDenied\":true}");
        return 0;
    }
}
if (args[0] == "serve")
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Logging.ClearProviders();
    builder.Services.AddWindowsService(o => o.ServiceName = "PeerSyncSpike");
    builder.Services.AddSingleton(certificate);
    builder.Services.AddSingleton(new ListenPort(int.Parse(args[2], CultureInfo.InvariantCulture)));
    builder.Services.AddHostedService<CertificateWorker>();
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
if (args[0] != "verify") return 2;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
using var clientCertificate = new SpikeCertificate();
var remote = new IPEndPoint(IPAddress.Parse(args[2]), int.Parse(args[3], CultureInfo.InvariantCulture));
await using (var connection = await QuicProbe.DialAsync(remote, clientCertificate.Certificate, timeout.Token))
{
    if (!DeviceIdentity.FromCertificate(connection.RemoteCertificate!).AsSpan().SequenceEqual(DeviceIdentity.FromCertificate(certificate)))
        throw new InvalidDataException("Wrong server identity.");
    await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token);
    await stream.WriteAsync(new byte[] { 1 }, timeout.Token);
    stream.CompleteWrites();
    byte[] proof = new byte[32];
    await stream.ReadExactlyAsync(proof, timeout.Token);
    if (!proof.AsSpan().SequenceEqual(DeviceIdentity.FromCertificate(clientCertificate.Certificate))) throw new InvalidDataException("Wrong client identity.");
}
using (var tcp = new TcpClient())
{
    await tcp.ConnectAsync(remote, timeout.Token);
    await using var tls = new SslStream(tcp.GetStream());
    await tls.AuthenticateAsClientAsync(PeerTls.Client(clientCertificate.Certificate), timeout.Token);
    if (!DeviceIdentity.FromCertificate(tls.RemoteCertificate!).AsSpan().SequenceEqual(DeviceIdentity.FromCertificate(certificate))) throw new InvalidDataException("Wrong server identity.");
    await tls.WriteAsync(new byte[] { 1 }, timeout.Token);
    byte[] proof = new byte[32];
    await tls.ReadExactlyAsync(proof, timeout.Token);
    if (!proof.AsSpan().SequenceEqual(DeviceIdentity.FromCertificate(clientCertificate.Certificate))) throw new InvalidDataException("Wrong client identity.");
}
Console.WriteLine(JsonSerializer.Serialize(new { quic = true, tcpTls13 = true, mutualCertificateIdentitiesVerified = true }));
return 0;

internal sealed record ListenPort(int Value);

internal sealed class CertificateWorker(X509Certificate2 certificate, ListenPort port) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var listener = await QuicProbe.ListenAsync(port.Value, certificate, stoppingToken);
        using var tcp = new TcpListener(IPAddress.IPv6Any, port.Value);
        tcp.Server.DualMode = true;
        tcp.Start();
        await Task.WhenAll(ServeQuicAsync(listener, stoppingToken), ServeTcpAsync(tcp, stoppingToken));
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private static async Task ServeQuicAsync(QuicListener listener, CancellationToken ct)
    {
        await using var connection = await listener.AcceptConnectionAsync(ct);
        await using var stream = await connection.AcceptInboundStreamAsync(ct);
        byte[] ping = new byte[1];
        await stream.ReadExactlyAsync(ping, ct);
        await stream.WriteAsync(DeviceIdentity.FromCertificate(connection.RemoteCertificate!), ct);
        stream.CompleteWrites();
        await stream.WritesClosed.WaitAsync(ct);
    }

    private async Task ServeTcpAsync(TcpListener listener, CancellationToken ct)
    {
        using var connection = await listener.AcceptTcpClientAsync(ct);
        await using var tls = new SslStream(connection.GetStream());
        await tls.AuthenticateAsServerAsync(PeerTls.Server(certificate), ct);
        byte[] ping = new byte[1];
        await tls.ReadExactlyAsync(ping, ct);
        await tls.WriteAsync(DeviceIdentity.FromCertificate(tls.RemoteCertificate!), ct);
    }
}
