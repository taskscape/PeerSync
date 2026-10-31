using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Telemetry;

/// <summary>Exports an allowlisted copy of diagnostic metadata without blocking the journal writer.</summary>
public sealed class AzureDiagnosticSink : IAsyncDisposable
{
    private const int QueueCapacity = 10_000;
    private const int MaximumPerMinute = 2_000;
    private readonly DiagnosticJournal journal;
    private readonly ILoggerFactory factory;
    private readonly ExporterEvents exporterEvents;
    private readonly ILogger logger;
    private readonly Channel<IReadOnlyList<KeyValuePair<string, object?>>> queue =
        Channel.CreateBounded<IReadOnlyList<KeyValuePair<string, object?>>>(new BoundedChannelOptions(QueueCapacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task worker;
    private readonly Task heartbeat;
    private readonly CancellationTokenSource workerStopping = new();
    private readonly CancellationTokenSource heartbeatStopping = new();
    private readonly string originDeviceFingerprint;
    private readonly string component;
    private readonly bool fullDiagnosis;
    private readonly object rateGate = new();
    private DateTime minuteUtc = DateTime.UtcNow;
    private int minuteCount;
    private long dropped;
    private long reportedDropped;
    private bool disposed;

    public AzureDiagnosticSink(DiagnosticJournal journal, string component, string originDeviceFingerprint,
        bool fullDiagnosis, string storageDirectory, string? connectionString = null)
    {
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(originDeviceFingerprint);
        this.component = component;
        this.originDeviceFingerprint = originDeviceFingerprint;
        this.fullDiagnosis = fullDiagnosis;
        Directory.CreateDirectory(storageDirectory);
        string destination = connectionString ?? AzureDiagnosticSettings.ConnectionString;
        factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.ParseStateValues = true;
                logging.SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(component == "service" ? "PeerSync.Service" : "PeerSync.Rendezvous",
                        serviceInstanceId: originDeviceFingerprint));
                logging.AddAzureMonitorLogExporter(options =>
                {
                    options.ConnectionString = destination;
                    options.StorageDirectory = storageDirectory;
                    options.EnableLiveMetrics = false;
                    options.EnablePerformanceCounters = false;
                    options.EnableStandardMetrics = false;
                });
            });
        });
        logger = factory.CreateLogger("PeerSync.Diagnostics");
        exporterEvents = new ExporterEvents(journal);
        worker = Task.Run(ExportAsync);
        heartbeat = Task.Run(HeartbeatAsync);
        journal.EntryWritten += OnEntry;
        journal.TryWrite(new("azure.capture_started", "telemetry")
        { ItemsChanged = fullDiagnosis ? 1 : 0 });
    }

    public long Dropped => Interlocked.Read(ref dropped);

    private void OnEntry(DiagnosticEvent entry, DateTime occurredUtc)
    {
        if (!ShouldSend(entry, fullDiagnosis)) return;
        lock (rateGate)
        {
            if (occurredUtc - minuteUtc >= TimeSpan.FromMinutes(1))
            {
                minuteUtc = occurredUtc;
                minuteCount = 0;
                long currentDropped = Interlocked.Read(ref dropped);
                long difference = currentDropped - reportedDropped;
                reportedDropped = currentDropped;
                if (difference > 0)
                    journal.TryWrite(new("azure.capture_dropped", "telemetry")
                    { Level = DiagnosticLevel.Warning, ItemsChanged = difference });
            }
            if (minuteCount >= MaximumPerMinute)
            {
                Interlocked.Increment(ref dropped);
                return;
            }
            minuteCount++;
        }
        var fields = CreateFields(entry, occurredUtc, journal.RunId, originDeviceFingerprint,
            includeNetworkAddresses: fullDiagnosis);
        if (!queue.Writer.TryWrite(fields)) Interlocked.Increment(ref dropped);
    }

    public static bool ShouldSend(DiagnosticEvent entry, bool fullDiagnosis)
    {
        if (entry.Event.StartsWith("azure.", StringComparison.Ordinal)) return false;
        if (fullDiagnosis) return true;
        if (entry.Level is DiagnosticLevel.Warning or DiagnosticLevel.Error) return true;
        if (entry.Event is "control.status_read" or "directory.snapshot_cache_hit"
            or "directory.publication_pass" or "index.range_complete"
            or "puller.pass_start" or "puller.pass_complete"
            or "transport.request_start" or "transport.request_complete"
            or "transport.request_received" or "transport.request_served"
            or "rendezvous.health_checked" or "rendezvous.probed") return false;
        if (entry.Event == "service.folder_sync_pass")
            return entry.ItemsChanged > 0 || entry.ItemsDeferred > 0;
        return true;
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> CreateFields(DiagnosticEvent entry,
        DateTime occurredUtc, string runId, string originDeviceFingerprint,
        bool includeNetworkAddresses = false)
    {
        var fields = new List<KeyValuePair<string, object?>>(30);
        void Add(string name, object? value)
        { if (value is not null) fields.Add(new(name, value)); }

        Add("eventName", entry.Event);
        Add("operation", entry.Operation);
        Add("level", entry.Level.ToString());
        Add("component", entry.Event.Split('.')[0]);
        Add("originDeviceFingerprint", originDeviceFingerprint);
        Add("diagnosticSchemaVersion", 1);
        Add("appVersion", Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown");
        Add("occurredUtc", occurredUtc.ToString("O"));
        Add("runId", runId);
        Add("sessionId", entry.SessionId?.ToString());
        Add("traceId", Activity.Current?.TraceId.ToString());
        Add("folderFingerprint", entry.FolderFingerprint);
        Add("peerFingerprint", entry.PeerFingerprint);
        Add("transport", entry.Transport?.ToString());
        if (entry.RemoteAddress is { } address)
        {
            Add("remoteAddressClass", AddressClass(address));
            if (includeNetworkAddresses)
            {
                Add("remoteAddress", address.ToString());
                Add("remotePort", entry.RemotePort);
            }
        }
        Add("bytes", entry.Bytes);
        Add("elapsedMs", entry.ElapsedMilliseconds);
        Add("clockSkewSeconds", entry.ClockSkewSeconds);
        Add("errorCode", entry.ErrorCode);
        Add("hResult", entry.HResult);
        Add("nativeError", entry.NativeError);
        Add("transportErrorCode", entry.TransportErrorCode);
        Add("applicationErrorCode", entry.ApplicationErrorCode);
        Add("retryInMs", entry.RetryInMilliseconds);
        Add("itemsVisited", entry.ItemsVisited);
        Add("itemsChanged", entry.ItemsChanged);
        Add("itemsHashed", entry.ItemsHashed);
        Add("itemsSkipped", entry.ItemsSkipped);
        Add("itemsDeferred", entry.ItemsDeferred);
        return fields;
    }

    private static string AddressClass(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return "loopback";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return "shared_address_space";
            if (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168) return "private";
            return "public";
        }
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.GetAddressBytes()[0] is >= 0xFC and <= 0xFD)
            return "private";
        return "public";
    }

    private async Task ExportAsync()
    {
        await foreach (var fields in queue.Reader.ReadAllAsync(workerStopping.Token).ConfigureAwait(false))
        {
            var severity = fields.First(pair => pair.Key == "level").Value as string;
            LogLevel level = severity switch
            {
                "Error" => LogLevel.Error,
                "Warning" => LogLevel.Warning,
                "Debug" => LogLevel.Debug,
                _ => LogLevel.Information,
            };
            try
            {
                logger.Log(level, new EventId(7000, "PeerSyncDiagnostic"), fields, null,
                    static (state, _) => state[0].Value?.ToString() ?? "PeerSync diagnostic");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Interlocked.Increment(ref dropped);
                journal.TryWrite(new("azure.export_enqueue_failed", "telemetry")
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name });
            }
        }
    }

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(heartbeatStopping.Token).ConfigureAwait(false))
            {
                var heartbeatEntry = new DiagnosticEvent(component == "service"
                    ? "service.telemetry_heartbeat" : "rendezvous.telemetry_heartbeat", "telemetry")
                { ItemsDeferred = Dropped };
                var fields = CreateFields(heartbeatEntry, DateTime.UtcNow, journal.RunId,
                    originDeviceFingerprint);
                try
                {
                    logger.Log(LogLevel.Information, new EventId(7001, "PeerSyncHeartbeat"), fields,
                        null, static (state, _) => state[0].Value?.ToString() ?? "PeerSync heartbeat");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    journal.TryWrite(new("azure.heartbeat_failed", "telemetry")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name });
                }
            }
        }
        catch (OperationCanceledException) when (heartbeatStopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        journal.EntryWritten -= OnEntry;
        await heartbeatStopping.CancelAsync().ConfigureAwait(false);
        await heartbeat.ConfigureAwait(false);
        queue.Writer.TryComplete();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await workerStopping.CancelAsync().ConfigureAwait(false);
            try { await worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            exporterEvents.Dispose();
            factory.Dispose();
            workerStopping.Dispose();
            heartbeatStopping.Dispose();
        }
    }

    private sealed class ExporterEvents(DiagnosticJournal journal) : EventListener
    {
        private long lastReportTick;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "OpenTelemetry-AzureMonitor-Exporter")
                EnableEvents(source, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            long now = Environment.TickCount64;
            long previous = Interlocked.Read(ref lastReportTick);
            if (now - previous < 60_000 || Interlocked.CompareExchange(ref lastReportTick, now, previous) != previous)
                return;
            journal.TryWrite(new("azure.exporter_warning", "telemetry")
            { Level = DiagnosticLevel.Warning, ErrorCode = "event_" + eventData.EventId });
        }
    }
}
