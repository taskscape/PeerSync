using System.IO;
using System.Windows;
using System.Windows.Threading;
using PeerSync.App.Status;
using PeerSync.Core.Diagnostics;

namespace PeerSync.App;
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeerSync", "logs", "app", Guid.NewGuid().ToString("N"));
        using var journal = new DiagnosticJournal(logs, "app", debugEnabled: true);
        using var stop = new CancellationTokenSource(); var app = new Application(); var model = new ClockStatusViewModel(); var window = new ClockStatusWindow(model);
        var controller = new ClockStatusController(model, (action, token) => window.Dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, token).Task,
            VerifiedStatusConnection.OpenInstalledAsync, diagnostics: journal);
        var watching = controller.RunAsync(stop.Token); window.Closed += (_, _) => stop.Cancel();
        try { app.Run(window); }
        finally { stop.Cancel(); watching.GetAwaiter().GetResult(); }
    }
}
