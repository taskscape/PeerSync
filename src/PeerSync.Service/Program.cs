using System.Security.Principal;
using System.Security.Cryptography;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Security;
using PeerSync.Service.Control;
using PeerSync.Service.Hosting;
using PeerSync.Service.Init;
using PeerSync.Service.Settings;
using PeerSync.Telemetry;

if (args.Length > 0 && string.Equals(args[0], "--init", StringComparison.OrdinalIgnoreCase))
    return InitCommand.Run(args);
if (args.Length > 0 && string.Equals(args[0], "--remove-data", StringComparison.OrdinalIgnoreCase))
    return RemoveDataCommand.Run(args);

string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeerSync", "logs", "service");
DiagnosticJournal? journal = null;
DiagnosticRunJournal? run = null;
AzureDiagnosticSink? azureSink = null;
try
{
    run = DiagnosticRunJournal.Open(logs, "service", debugEnabled: true); journal = run.Journal;
    AppDomain.CurrentDomain.UnhandledException += (_, args) =>
    {
        if (args.ExceptionObject is Exception unhandled)
            journal.TryWrite(new("service.unhandled", "runtime")
            { Level = DiagnosticLevel.Error, ErrorCode = unhandled.GetType().Name, HResult = unhandled.HResult, Exception = unhandled });
    };
    TaskScheduler.UnobservedTaskException += (_, args) =>
    {
        journal.TryWrite(new("service.unobserved_task", "runtime")
        { Level = DiagnosticLevel.Error, ErrorCode = args.Exception.GetType().Name, HResult = args.Exception.HResult, Exception = args.Exception });
        args.SetObserved();
    };
    journal.TryWrite(new("service.starting", "startup"));
    var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeerSync", "config", "settings.json");
    var settings = new SettingsStore(settingsPath).ReadIfPresent(out var settingsRecovered);
    if (settings is null)
    {
        journal.TryWrite(new("service.settings_unavailable", "startup") { Level = DiagnosticLevel.Error, ErrorCode = "run_init" });
        throw new InvalidOperationException("No usable settings.json. Run PeerSync.Service.exe --init as administrator.");
    }
    journal.TryWrite(new(settingsRecovered ? "service.settings_recovered" : "service.settings_loaded", "startup")
    { Level = settingsRecovered ? DiagnosticLevel.Warning : DiagnosticLevel.Information });
    if (settings.SendDiagnosticsToAzure)
    {
        try
        {
            using var certificate = InstalledFolderHost.LoadDeviceCertificate(settings.DeviceCertificateThumbprint);
            byte[] deviceId = PeerTls.DeviceId(certificate);
            string origin = DiagnosticFingerprint.FromId(deviceId);
            CryptographicOperations.ZeroMemory(deviceId);
            azureSink = new AzureDiagnosticSink(journal, "service", origin, settings.FullDiagnostics,
                Path.Combine(logs, "azure-cache"));
            journal.TryWrite(new("service.telemetry_ready", "telemetry"));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            journal.TryWrite(new("azure.capture_start_failed", "telemetry")
            { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult,
                Exception = error });
        }
    }
    else journal.TryWrite(new("azure.capture_disabled", "telemetry"));
    Console.WriteLine("LOGS " + run.DirectoryPath);
    var runtime = new ControlStatusRuntime(journal);
    string statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeerSync", "state", "peersync.db");
    await using var folderHost = await InstalledFolderHost.StartAsync(statePath, settings.DeviceCertificateThumbprint,
        settings, runtime, journal);
    runtime.AttachFolderHost(folderHost);
    while (true)
    {
        await using var host = await ControlApiHost.StartInstalledAsync(runtime, journal);
        journal.TryWrite(new("service.control_ready", "startup") { Transport = DiagnosticTransport.NamedPipe });
        Task controlShutdown = host.WaitForShutdownAsync();
        Task finished = await Task.WhenAny(controlShutdown, folderHost.Completion, host.RestartRequested).ConfigureAwait(false);
        if (finished == folderHost.Completion)
        {
            await folderHost.Completion.ConfigureAwait(false);
            throw new InvalidOperationException("The installed folder scheduler stopped unexpectedly.");
        }
        if (finished == host.RestartRequested)
        {
            journal.TryWrite(new("service.control_restarting", "control") { Transport = DiagnosticTransport.NamedPipe });
            continue;
        }
        await controlShutdown.ConfigureAwait(false);
        break;
    }
    journal.TryWrite(new("service.stopped", "shutdown"));
    return 0;
}
catch (IdentityNotMappedException error)
{
    journal?.TryWrite(new("service.control_group_missing", "startup") { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
    Console.Error.WriteLine("The PeerSync Users group is missing. Service installation must provision the control group and service paths."); return 1;
}
catch (Exception error)
{
    journal?.TryWrite(new("service.start_failed", "startup") { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
    Console.Error.WriteLine("PeerSync service failed: " + error.GetType().Name); return 1;
}
finally
{
    if (azureSink is not null) await azureSink.DisposeAsync();
    run?.Dispose();
}
