using System.Security.Principal;
using PeerSync.Core.Diagnostics;
using PeerSync.Service.Control;
using PeerSync.Service.Hosting;
using PeerSync.Service.Init;
using PeerSync.Service.Settings;

if (args.Length > 0 && string.Equals(args[0], "--init", StringComparison.OrdinalIgnoreCase))
    return InitCommand.Run(args);
if (args.Length > 0 && string.Equals(args[0], "--remove-data", StringComparison.OrdinalIgnoreCase))
    return RemoveDataCommand.Run(args);

string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeerSync", "logs", "service");
DiagnosticJournal? journal = null;
DiagnosticRunJournal? run = null;
try
{
    run = DiagnosticRunJournal.Open(logs, "service", debugEnabled: true); journal = run.Journal;
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
    journal?.TryWrite(new("service.control_group_missing", "startup") { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
    Console.Error.WriteLine("The PeerSync Users group is missing. Service installation must provision the control group and service paths."); return 1;
}
catch (Exception error)
{
    journal?.TryWrite(new("service.start_failed", "startup") { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
    Console.Error.WriteLine("PeerSync service failed: " + error.GetType().Name); return 1;
}
finally { run?.Dispose(); }
