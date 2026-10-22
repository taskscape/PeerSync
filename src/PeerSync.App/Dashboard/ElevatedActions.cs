using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using PeerSync.Control.Contracts;

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
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        try
        {
            switch (args[0])
            {
                case "--grant-folder":
                    await FolderAccessGrant.GrantAsync(args[1], null, CancellationToken.None).ConfigureAwait(false);
                    return 0;
                case "--authorize-user":
                    using (var client = ControlStatusClient.CreateForInstalledService())
                    {
                        var result = await client.AuthorizeUserAsync(args[1]).ConfigureAwait(false);
                        return result.Completed ? 0 : 1;
                    }
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
        { return 1; }
    }
}
