using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;

namespace PeerSync.App.Dashboard;

internal static class ElevatedActions
{
    internal static async Task AuthorizeCurrentUserAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Windows account has no SID.");
        if (await RunElevatedAsync("--authorize-user", sid) != 0)
            throw new IOException("The elevated authorization failed.");
    }

    internal static Task<int> GrantFolderAsync(string path)
        => RunElevatedAsync("--grant-folder", path);

    internal static async Task StartServiceAsync()
    {
        if (await RunElevatedAsync("--start-service", null) != 0)
            throw new IOException("The PeerSync service could not be started.");
    }

    private static async Task<int> RunElevatedAsync(string operation, string? value)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("Application path is unavailable."))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(operation);
        if (value is not null) start.ArgumentList.Add(value);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The elevated helper did not start.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    internal static async Task<int> RunHelperAsync(string[] args)
    {
        if (args.Length == 0 || (args[0] == "--start-service" ? args.Length != 1 : args.Length != 2)) return 2;
        DiagnosticRunJournal? run = null;
        try
        {
            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PeerSync", "logs", "app-helper");
            run = DiagnosticRunJournal.Open(logs, "app-helper", debugEnabled: true);
            run.Journal.TryWrite(new("app.helper_started", "helper"));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
            switch (args[0])
            {
                case "--grant-folder":
                    await FolderAccessGrant.GrantAsync(args[1], null, CancellationToken.None).ConfigureAwait(false);
                    return 0;
                case "--authorize-user":
                    using (var client = ControlStatusClient.CreateForInstalledService())
                    {
                        var result = await client.AuthorizeUserAsync(args[1]).ConfigureAwait(false);
                        if (!result.Completed) return 1;
                    }
                    RestartService(run?.Journal);
                    run?.Journal.TryWrite(new("app.account_authorized", "helper"));
                    return 0;
                case "--start-service":
                    var start = new ProcessStartInfo("sc.exe") { UseShellExecute = false,
                        CreateNoWindow = true };
                    start.ArgumentList.Add("start"); start.ArgumentList.Add("PeerSync");
                    using (var process = Process.Start(start))
                    {
                        if (process is null) return 1;
                        await process.WaitForExitAsync().ConfigureAwait(false);
                        return process.ExitCode is 0 or 1056 ? 0 : process.ExitCode;
                    }
                default: return 2;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            run?.Journal.TryWrite(new("app.helper_failed", "helper")
            {
                Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name,
                HResult = error.HResult,
                NativeError = (error as Win32Exception ?? error.InnerException as Win32Exception)?.NativeErrorCode,
            });
            return 1;
        }
        finally { run?.Dispose(); }
    }

    private static void RestartService(DiagnosticJournal? diagnostics)
    {
        using var service = new ServiceController("PeerSync");
        service.Refresh();
        if (service.Status != ServiceControllerStatus.Stopped)
        {
            diagnostics?.TryWrite(new("app.service_stop_requested", "helper"));
            if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        diagnostics?.TryWrite(new("app.service_stopped", "helper"));
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                    diagnostics?.TryWrite(new("app.service_start_requested", "helper"));
                }
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(3));
                diagnostics?.TryWrite(new("app.service_running", "helper"));
                return;
            }
            catch (Exception error) when (attempt < 9 &&
                (error is System.TimeoutException or System.ServiceProcess.TimeoutException ||
                 error is InvalidOperationException &&
                 error.InnerException is Win32Exception { NativeErrorCode: 1053 or 1056 }))
            {
                diagnostics?.TryWrite(new("app.service_start_retry", "helper")
                {
                    Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name,
                    NativeError = (error as Win32Exception ?? error.InnerException as Win32Exception)?.NativeErrorCode,
                    RetryInMilliseconds = 500,
                });
                Thread.Sleep(500);
            }
        }
    }
}
