using System.Security.Principal;
using PeerSync.Core.Diagnostics;
using PeerSync.Service.Control;

string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeerSync", "logs", "service", Guid.NewGuid().ToString("N"));
DiagnosticJournal? journal = null;
try
{
    journal = new(logs, "service", debugEnabled: true);
    journal.TryWrite(new("service.status_only_start", "startup") { ErrorCode = "production_sync_unavailable" });
    Console.WriteLine("LOGS " + logs);
    await using var host = await ControlApiHost.StartInstalledAsync(new(), journal);
    journal.TryWrite(new("service.control_ready", "startup") { Transport = DiagnosticTransport.NamedPipe });
    await host.WaitForShutdownAsync(); return 0;
}
catch (IdentityNotMappedException error)
{
    journal?.TryWrite(new("service.control_group_missing", "startup") { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
    Console.Error.WriteLine("The PeerSync Users group is missing. Service installation must provision the control group and service paths."); return 1;
}
catch (Exception error)
{
    journal?.TryWrite(new("service.start_failed", "startup") { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
    Console.Error.WriteLine("PeerSync status service failed: " + error.GetType().Name); return 1;
}
finally { journal?.Dispose(); }
