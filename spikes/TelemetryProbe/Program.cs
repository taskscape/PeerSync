using System.Net;
using PeerSync.Core.Diagnostics;
using PeerSync.Telemetry;

string logs = Path.Combine(Path.GetTempPath(), "PeerSync-azure-smoke-" + Guid.NewGuid().ToString("N"));
bool fullDiagnosis = args.Contains("--full", StringComparer.Ordinal);
using var journal = new DiagnosticJournal(logs, "service", debugEnabled: true);
await using (var sink = new AzureDiagnosticSink(journal, "service", "A1B2C3D4E5F60708",
    fullDiagnosis, Path.Combine(logs, "azure-cache")))
{
    journal.Write(new(fullDiagnosis ? "probe.azure_round_trip_full" : "probe.azure_round_trip", "telemetry")
    {
        Level = DiagnosticLevel.Warning,
        FolderFingerprint = "0123456789ABCDEF",
        ErrorCode = "synthetic_probe",
        RemoteAddress = IPAddress.Parse("203.0.113.42"),
        RemotePort = 443,
        Exception = new IOException("PEERSYNC_PRIVACY_SENTINEL must stay local"),
    });
    await Task.Delay(TimeSpan.FromSeconds(args.Contains("--heartbeat", StringComparer.Ordinal) ? 75 : 15));
}
Console.WriteLine("Synthetic Azure diagnostic sent. Local log directory: " + logs);
