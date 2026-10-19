using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using PeerSync.Spikes;
using PeerSync.Core.Diagnostics;

if (args.Length < 2 || args[0] is not ("server" or "client"))
{
    Console.Error.WriteLine("server <port> [log-directory] | client <https-url> <2|3>");
    return 2;
}
using var certificate = new SpikeCertificate();
if (args[0] == "server")
{
    string logs = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeerSync", "spikes", "Rendezvous99", Guid.NewGuid().ToString("N"));
    using var journal = new DiagnosticJournal(logs, "Rendezvous99", true);
    journal.Write(new("server.starting", "listen"));
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Logging.ClearProviders();
    builder.Services.AddGrpc();
    builder.Services.AddSingleton(journal);
    builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(int.Parse(args[1], CultureInfo.InvariantCulture), o =>
    {
        o.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
        o.UseHttps(https =>
        {
            https.ServerCertificate = certificate.Certificate;
            https.SslProtocols = SslProtocols.Tls13;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, _, _) =>
            {
                bool valid = DeviceIdentity.IsDeviceCertificate(cert);
                journal.Write(new(valid ? "tls.client.accepted" : "tls.client.rejected", "tls")
                    { Level = valid ? DiagnosticLevel.Debug : DiagnosticLevel.Warning });
                return valid;
            };
        });
    }));
    await using var app = builder.Build();
    app.MapGrpcService<ProbeService>();
    await app.StartAsync();
    journal.Write(new("server.ready", "listen"));
    Console.WriteLine($"LOGS {Path.GetFullPath(logs)}");
    Console.WriteLine("READY");
    Console.Out.Flush();
    await app.WaitForShutdownAsync();
    journal.Write(new("server.stopped", "shutdown"));
    return 0;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
string sessionId = Guid.NewGuid().ToString("N");
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
        await call.RequestStream.WriteAsync(new ProbeMessage { Sequence = sequence, SessionId = sessionId }, timeout.Token);
        if (!await call.ResponseStream.MoveNext(timeout.Token)) throw new InvalidDataException("Missing response.");
        if (call.ResponseStream.Current.Sequence != sequence || call.ResponseStream.Current.SessionId != sessionId || !call.ResponseStream.Current.DeviceId.Span.SequenceEqual(DeviceIdentity.FromCertificate(certificate.Certificate)))
            throw new InvalidDataException("Device identity did not match the TLS certificate.");
    }
    await call.RequestStream.CompleteAsync();
    if (await call.ResponseStream.MoveNext(timeout.Token)) throw new InvalidDataException("Unexpected response.");
    Console.WriteLine(JsonSerializer.Serialize(new { sessionId, httpVersion = args[2], mutualTls = true, deviceIdentityVerified = true, bidirectionalMessages = 3 }));
    return 0;
}
catch (Exception ex) when (ex is Grpc.Core.RpcException or HttpRequestException or OperationCanceledException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { sessionId, httpVersion = args[2], mutualTls = false, error = ex.GetType().Name,
        rpcStatus = (ex as Grpc.Core.RpcException)?.StatusCode.ToString(), hResult = ex.HResult,
        nativeError = ((ex as Grpc.Core.RpcException)?.Status.DebugException?.GetBaseException() as System.Net.Sockets.SocketException)?.NativeErrorCode
            ?? (ex.GetBaseException() as System.Net.Sockets.SocketException)?.NativeErrorCode,
        cause = (ex as Grpc.Core.RpcException)?.Status.DebugException?.GetBaseException().GetType().Name,
        causeHResult = (ex as Grpc.Core.RpcException)?.Status.DebugException?.GetBaseException().HResult }));
    return 1;
}
