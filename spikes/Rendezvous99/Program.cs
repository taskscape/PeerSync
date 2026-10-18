using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using PeerSync.Spikes;

if (args.Length < 2 || args[0] is not ("server" or "client"))
{
    Console.Error.WriteLine("server <port> | client <https-url> <2|3>");
    return 2;
}
using var certificate = new SpikeCertificate();
if (args[0] == "server")
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Logging.ClearProviders();
    builder.Services.AddGrpc();
    builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(int.Parse(args[1], CultureInfo.InvariantCulture), o =>
    {
        o.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
        o.UseHttps(https =>
        {
            https.ServerCertificate = certificate.Certificate;
            https.SslProtocols = SslProtocols.Tls13;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, _, _) => DeviceIdentity.IsDeviceCertificate(cert);
        });
    }));
    await using var app = builder.Build();
    app.MapGrpcService<ProbeService>();
    await app.StartAsync();
    Console.WriteLine("READY");
    Console.Out.Flush();
    await app.WaitForShutdownAsync();
    return 0;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var handler = new SocketsHttpHandler
{
    SslOptions = new()
    {
        EnabledSslProtocols = SslProtocols.Tls13,
        ClientCertificates = new() { certificate.Certificate },
        RemoteCertificateValidationCallback = (_, cert, _, _) => DeviceIdentity.IsDeviceCertificate(cert),
    },
};
using var channel = GrpcChannel.ForAddress(args[1], new GrpcChannelOptions
{
    HttpHandler = handler,
    HttpVersion = args[2] == "3" ? HttpVersion.Version30 : HttpVersion.Version20,
    HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
});
try
{
    using var call = new Probe.ProbeClient(channel).Session(cancellationToken: timeout.Token);
    for (int sequence = 0; sequence < 3; sequence++)
    {
        await call.RequestStream.WriteAsync(new ProbeMessage { Sequence = sequence }, timeout.Token);
        if (!await call.ResponseStream.MoveNext(timeout.Token)) throw new InvalidDataException("Missing response.");
        if (call.ResponseStream.Current.Sequence != sequence || !call.ResponseStream.Current.DeviceId.Span.SequenceEqual(DeviceIdentity.FromCertificate(certificate.Certificate)))
            throw new InvalidDataException("Device identity did not match the TLS certificate.");
    }
    await call.RequestStream.CompleteAsync();
    if (await call.ResponseStream.MoveNext(timeout.Token)) throw new InvalidDataException("Unexpected response.");
    Console.WriteLine(JsonSerializer.Serialize(new { httpVersion = args[2], mutualTls = true, deviceIdentityVerified = true, bidirectionalMessages = 3 }));
    return 0;
}
catch (Exception ex) when (ex is Grpc.Core.RpcException or HttpRequestException or OperationCanceledException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { httpVersion = args[2], mutualTls = false, error = ex.GetType().Name, nativeError = ex.HResult }));
    return 1;
}
