using System.Security.Principal;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeerSync.App.Dashboard;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;
using Application = System.Windows.Application;

namespace PeerSync.App;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--grant-folder" or "--authorize-user" or "--start-service")
            return ElevatedActions.RunHelperAsync(args).GetAwaiter().GetResult();
        if (args.Length > 1 || args.Length == 1 && args[0] != "--tray") return 2;

        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Windows account has no SID.");
        string suffix = sid.Replace('-', '_');
        using var singleton = new Mutex(true, @"Local\PeerSync.App." + suffix, out bool first);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Local\PeerSync.App.Activate." + suffix);
        if (!first) { activation.Set(); return 0; }

        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PeerSync", "logs", "app");
        using var run = DiagnosticRunJournal.Open(logs, "app", debugEnabled: true);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(run.Journal);
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton(provider => new MainWindow(
            provider.GetRequiredService<DashboardViewModel>(), run.Journal, args.Length == 1));
        using var host = builder.Build();
        host.StartAsync().GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
#pragma warning disable WPF0001
        app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        var window = host.Services.GetRequiredService<MainWindow>();
        using var stop = new CancellationTokenSource();
        var controller = new DashboardController(host.Services.GetRequiredService<DashboardViewModel>(),
            (action, token) => window.Dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, token).Task,
            ControlStatusClient.CreateForInstalledService, run.Journal);
        Task watching = controller.RunAsync(stop.Token);
        Task activationLoop = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                if (activation.WaitOne(500)) window.Dispatcher.Invoke(window.ShowAndActivate);
        });
        try { app.Run(window); return 0; }
        finally
        {
            stop.Cancel(); watching.GetAwaiter().GetResult(); activationLoop.GetAwaiter().GetResult();
            host.StopAsync().GetAwaiter().GetResult();
        }
    }
}
