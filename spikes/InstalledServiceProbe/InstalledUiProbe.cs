using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PeerSync.App.Status;

namespace PeerSync.InstalledProbe;

/// <summary>Renders the real App controls from the default, SCM-verified client.</summary>
internal sealed class InstalledUiProbe : IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly ClockStatusViewModel model;
    private readonly ClockStatusWindow window;
    private readonly CancellationTokenSource stop;
    private readonly Task watching;
    private readonly Thread thread;

    private InstalledUiProbe(Dispatcher dispatcher, ClockStatusViewModel model,
        ClockStatusWindow window, CancellationTokenSource stop, Task watching, Thread thread)
    {
        this.dispatcher = dispatcher;
        this.model = model;
        this.window = window;
        this.stop = stop;
        this.watching = watching;
        this.thread = thread;
    }

    internal static async Task<InstalledUiProbe> StartAsync(CancellationToken token)
    {
        var ready = new TaskCompletionSource<InstalledUiProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? thread = null;
        thread = new Thread(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var model = new ClockStatusViewModel();
                var window = new ClockStatusWindow(model)
                {
                    ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000, Top = -20000,
                };
                var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var controller = new ClockStatusController(model,
                    (action, cancellationToken) => dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, cancellationToken).Task,
                    VerifiedStatusConnection.OpenInstalledAsync);
                window.Show();
                Task watching = controller.RunAsync(stop.Token);
                ready.SetResult(new InstalledUiProbe(dispatcher, model, window, stop, watching, thread!));
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "PeerSync installed UI probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await ready.Task.WaitAsync(token).ConfigureAwait(false);
    }

    internal Task CaptureSkewAsync(CancellationToken token) => CaptureAsync(
        model => model.Rows.Any(row => row.Reason.Contains("exchange blocked", StringComparison.Ordinal) &&
            row.OffsetMinutes.StartsWith("+11", StringComparison.Ordinal)) &&
            model.Availability.Contains("not available", StringComparison.Ordinal),
        "native-scm-skewed", token);

    internal Task CaptureRecoveryAsync(CancellationToken token) => CaptureAsync(
        model => model.Rows.Any(row => row.Reason == "Clock accepted" && row.Connection == "Connected") &&
            model.Availability == "Production sync is available.",
        "native-scm-recovered", token);

    internal Task CaptureDisconnectAsync(CancellationToken token) => CaptureAsync(
        model => model.Availability.Contains("not available", StringComparison.Ordinal) &&
            model.Rows.All(row => row.Connection != "Connected"),
        "native-scm-disconnected", token);

    private async Task CaptureAsync(Func<ClockStatusViewModel, bool> condition, string name, CancellationToken token)
    {
        while (true)
        {
            bool matched = await dispatcher.InvokeAsync(() => condition(model), DispatcherPriority.DataBind, token).Task
                .ConfigureAwait(false);
            if (matched)
            {
                await dispatcher.InvokeAsync(() => Render(name), DispatcherPriority.ApplicationIdle, token).Task
                    .ConfigureAwait(false);
                return;
            }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    private void Render(string name)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
            source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        byte[] crop = new byte[280 * 40 * 4];
        bitmap.CopyPixels(new Int32Rect(20, 20, 280, 40), crop, 280 * 4, 0);
        int dark = 0, white = 0;
        for (int offset = 0; offset < crop.Length; offset += 4)
        {
            if (crop[offset + 3] != 255) continue;
            if (crop[offset] < 128 && crop[offset + 1] < 128 && crop[offset + 2] < 128) dark++;
            if (crop[offset] > 240 && crop[offset + 1] > 240 && crop[offset + 2] > 240) white++;
        }
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "plan.md"))) root = root.Parent;
        if (root is null) throw new IOException("Cannot locate the UI evidence directory.");
        string directory = Path.Combine(root.FullName, "artifacts", "clock-ui");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(path)) encoder.Save(output);
        if (dark <= 100 || white <= 5000)
            throw new IOException($"The WPF title rendered blank; dark={dark}, white={white}, evidence={path}.");
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await watching.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                window.Close();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }).Task.ConfigureAwait(false);
            thread.Join(TimeSpan.FromSeconds(5));
            stop.Dispose();
        }
    }
}
