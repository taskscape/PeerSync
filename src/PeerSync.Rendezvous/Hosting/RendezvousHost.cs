using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging.EventLog;
using OpenTelemetry.Metrics;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Security;
using PeerSync.Rendezvous.Relay;
using PeerSync.Rendezvous.Tracker;

namespace PeerSync.Rendezvous.Hosting;

internal static class RendezvousHost
{
    private static readonly Action<ILogger, string, string, Guid?, string?, Exception?> LogCentralWarning =
        LoggerMessage.Define<string, string, Guid?, string?>(LogLevel.Warning,
            new EventId(1001, "RendezvousWarning"), "{Event} {Operation} {SessionId} {ErrorCode}");
    private static readonly Action<ILogger, string, string, Guid?, string?, Exception?> LogCentralError =
        LoggerMessage.Define<string, string, Guid?, string?>(LogLevel.Error,
            new EventId(1002, "RendezvousError"), "{Event} {Operation} {SessionId} {ErrorCode}");
    internal static async Task RunAsync(string[] args)
    {
        await using WebApplication app = Build(args);
        await app.RunAsync().ConfigureAwait(false);
    }

    internal static WebApplication Build(string[] args, X509Certificate2? testCertificate = null,
        IPAddress? testListenAddress = null)
    {
        if (testListenAddress is not null && !IPAddress.IsLoopback(testListenAddress))
            throw new ArgumentException("Test binding must be loopback.", nameof(testListenAddress));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddWindowsService(options => options.ServiceName = "PeerSyncRendezvous");
        builder.Logging.AddEventLog(settings =>
        {
            settings.SourceName = "PeerSyncRendezvous";
            settings.LogName = "Application";
            settings.Filter = (category, level) => category == "PeerSync.Rendezvous.Central"
                && level >= LogLevel.Warning;
        });
        builder.Configuration.AddEnvironmentVariables(prefix: "PEERSYNC_");
        var options = builder.Configuration.GetSection("Rendezvous").Get<RendezvousOptions>() ?? new();
        options.Validate();
        X509Certificate2 serverCertificate = testCertificate ?? RendezvousCertificateLoader.Load(options.CertificateSubject);

        builder.Services.AddSingleton(options);
        builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
        {
            metrics.AddMeter(RendezvousMetrics.MeterName);
            if (options.OtlpEndpoint is not null)
                metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
        });
        builder.Services.AddSingleton<PresenceRegistry>();
        builder.Services.AddSingleton<RelayRegistry>();
        builder.Services.AddHostedService<PresenceExpiryWorker>();
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = 256 * 1024;
            grpc.MaxSendMessageSize = 256 * 1024;
        });
        string logs = options.LogDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PeerSync.Rendezvous", "logs");
        builder.Services.AddSingleton(new DiagnosticJournal(logs, "rendezvous", options.DebugLogging, exceptions: new ExceptionLog(logs)));
        var ipRateKeys = new EphemeralIpRateKey();
        builder.Services.AddSingleton(ipRateKeys);
        builder.Services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limits.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetTokenBucketLimiter(
                ipRateKeys.For(context.Connection.RemoteIpAddress), _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 120,
                    TokensPerPeriod = 60,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true,
                    QueueLimit = 0,
                }));
            limits.AddPolicy("per-device", context => RateLimitPartition.GetTokenBucketLimiter(
                context.Connection.ClientCertificate is { } certificate
                    && PeerTls.IsDeviceCertificate(certificate)
                    ? Convert.ToHexString(PeerTls.DeviceId(certificate)) : "anonymous",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 60,
                    TokensPerPeriod = 60,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true,
                    QueueLimit = 0,
                }));
        });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (testListenAddress is null) kestrel.ListenAnyIP(options.Port, ConfigureListener);
            else kestrel.Listen(testListenAddress, options.Port, ConfigureListener);
        });

        void ConfigureListener(ListenOptions listen)
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
            listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.SslProtocols = SslProtocols.Tls13;
                // Public health/bootstrap/probe routes share this port with certificate-bound RPCs.
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.ClientCertificateValidation = (certificate, _, _) => PeerTls.IsDeviceCertificate(certificate);
            });
        }

        WebApplication app = builder.Build();
        ILogger centralLog = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PeerSync.Rendezvous.Central");
        app.Services.GetRequiredService<DiagnosticJournal>().EntryWritten += (entry, _) =>
        {
            if (entry.Level == DiagnosticLevel.Warning)
                LogCentralWarning(centralLog, entry.Event, entry.Operation, entry.SessionId, entry.ErrorCode, null);
            else if (entry.Level == DiagnosticLevel.Error)
                LogCentralError(centralLog, entry.Event, entry.Operation, entry.SessionId, entry.ErrorCode, null);
        };
        app.UseRouting();
        app.UseRateLimiter();
        app.MapGrpcService<TrackerService>().RequireRateLimiting("per-device");
        app.MapGrpcService<RelayService>().RequireRateLimiting("per-device");
        app.MapGet("/.well-known/peersync.json", () => Results.Json(new
        {
            schema = "peersync-bootstrap-v1", tracker = options.PublicUrl,
            relay = options.PublicUrl, refreshSeconds = 3600,
        }));
        app.MapGet("/healthz", (HttpContext context, DiagnosticJournal journal) =>
        {
            journal.TryWrite(new("rendezvous.health_checked", "health")
            { Level = DiagnosticLevel.Debug, Transport = Transport(context),
                RemoteAddress = context.Connection.RemoteIpAddress, RemotePort = context.Connection.RemotePort });
            return Results.Json(new { status = "ok" });
        });
        app.MapGet("/probe", (HttpContext context, DiagnosticJournal journal) =>
        {
            journal.TryWrite(new("rendezvous.probed", "probe")
            { Level = DiagnosticLevel.Debug, Transport = Transport(context),
                RemoteAddress = context.Connection.RemoteIpAddress, RemotePort = context.Connection.RemotePort });
            RendezvousMetrics.Probes.Add(1);
            return Results.Json(new
            {
                observedAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                observedPort = context.Connection.RemotePort,
                httpVersion = context.Request.Protocol,
            });
        });
        app.Lifetime.ApplicationStarted.Register(() =>
            app.Services.GetRequiredService<DiagnosticJournal>().TryWrite(new("rendezvous.started", "host")
            { ItemsChanged = options.Port }));
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            app.Services.GetRequiredService<DiagnosticJournal>().TryWrite(new("rendezvous.stopping", "host"));
            // End long-lived gRPC streams before Kestrel's shutdown grace period.
            app.Services.GetRequiredService<PresenceRegistry>().CloseAll();
            app.Services.GetRequiredService<RelayRegistry>().AbortAll();
        });
        return app;
    }

    private static DiagnosticTransport Transport(HttpContext context)
        => context.Request.Protocol switch
        {
            "HTTP/3" => DiagnosticTransport.Http3,
            "HTTP/2" => DiagnosticTransport.Http2,
            _ => DiagnosticTransport.Tcp,
        };
}
