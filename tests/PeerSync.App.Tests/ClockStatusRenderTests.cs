using System.Buffers.Binary;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using PeerSync.App.Status;
using PeerSync.Control.Contracts;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Net;
using PeerSync.Net.InMemory;
using PeerSync.Service.Control;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.App.Tests;

public sealed class ClockStatusRenderTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ClockUi-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public Task RealEnginePipeClientAndWpfRenderSkewRecoveryAndServiceFailure() => OnUiAsync(async () =>
    {
        await using var a = await Node(1); var clock = new Clock(); clock.Jump(TimeSpan.FromMinutes(11)); await using var b = await Node(2, clock);
        var runtime = new ControlStatusRuntime(); using var registration = runtime.RegisterFolder(a.Engine);
        using var identity = WindowsIdentity.GetCurrent(); string pipe = "PeerSync.Control.UiTests-" + Guid.NewGuid().ToString("N");
        await using var host = await ControlApiHost.StartFixtureAsync(pipe, runtime, [identity.User!], cancellationToken: Token);
        var network = new InMemoryNetwork(); var endpoint = network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        await Should.ThrowAsync<PeerClockException>(() => a.Engine.ConnectAsync(endpoint.Connect(b.Id), Token));
        var model = new ClockStatusViewModel(); var window = Window(model); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var controller = new ClockStatusController(model, (action, token) => window.Dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, token).Task,
            token => { token.ThrowIfCancellationRequested(); return Task.FromResult<IControlStatusConnection>(new VerifiedStatusConnection(ControlStatusClient.CreateFixture(new(pipe, identity.User!, (uint)Environment.ProcessId)))); });
        window.Show(); var watching = controller.RunAsync(stop.Token);
        try
        {
            await Until(() => model.Rows.Count == 1 && model.Rows[0].Reason.Contains("exchange blocked", StringComparison.Ordinal));
            model.Rows.Single().OffsetMinutes.ShouldBe("+11.00"); await Render(window, "runtime-skewed"); Text(window).ShouldContain("exchange blocked");
            clock.Jump(TimeSpan.FromMinutes(-11)); await using var connected = await a.Engine.ConnectAsync(endpoint.Connect(b.Id), Token);
            await Until(() => model.Rows.Count == 1 && model.Rows[0].Reason == "Clock accepted");
            model.Rows.Single().Connection.ShouldBe("Connected"); await Render(window, "runtime-recovered"); Text(window).ShouldNotContain("exchange blocked"); Text(window).ShouldContain("Clock accepted");
            await host.DisposeAsync(); await Until(() => model.Connection.Contains("unavailable", StringComparison.Ordinal));
            model.Rows.ShouldBeEmpty(); await Render(window, "service-unavailable"); Text(window).ShouldNotContain("Clock accepted"); Text(window).ShouldContain("reconnecting");
        }
        finally { stop.Cancel(); await watching.WaitAsync(TimeSpan.FromSeconds(5), Token); window.Close(); }
        host.ActiveSubscriptions.ShouldBe(0);
    });
    [Fact]
    public Task ActualWpfControlsRenderMissingInvalidNegativeOffsetAndTruncatedStates() => OnUiAsync(async () =>
    {
        var model = new ClockStatusViewModel(); var window = Window(model); window.Show();
        try
        {
            long generation = model.BeginAttempt(); var epoch = Guid.NewGuid(); ulong sequence = 0;
            foreach (var state in new[] { PeerClockState.AwaitingHello, PeerClockState.InvalidTimestamp, PeerClockState.InvalidElapsed, PeerClockState.Skewed })
            {
                model.Apply(generation, ClockStatusTests.Snapshot(epoch, ++sequence, state, state == PeerClockState.Skewed ? -11 : null));
                await Render(window, "typed-" + state); Text(window).ShouldContain(model.Rows.Single().Reason); Text(window).ShouldContain(model.Rows.Single().OffsetMinutes);
            }
            model.Apply(generation, new(epoch, ++sequence, false, [], true)); await Render(window, "typed-truncated"); Text(window).ShouldContain("omitted");
        }
        finally { window.Close(); }
    });
    private static ClockStatusWindow Window(ClockStatusViewModel model) => new(model) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
    private static async Task Render(Window window, string name)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source) source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        byte[] title = new byte[280 * 40 * 4]; bitmap.CopyPixels(new Int32Rect(20, 20, 280, 40), title, 280 * 4, 0);
        int dark = 0, white = 0;
        for (int offset = 0; offset < title.Length; offset += 4)
        {
            if (title[offset + 3] != 255) continue;
            if (title[offset] < 128 && title[offset + 1] < 128 && title[offset + 2] < 128) dark++;
            if (title[offset] > 240 && title[offset + 1] > 240 && title[offset + 2] > 240) white++;
        }
        dark.ShouldBeGreaterThan(100); white.ShouldBeGreaterThan(5000);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "plan.md"))) root = root.Parent;
        if (root is null) throw new IOException("Cannot locate render evidence directory."); string directory = Path.Combine(root.FullName, "artifacts", "clock-ui"); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
    }
    private static string Text(DependencyObject root)
    {
        var text = new List<string>(); Visit(root); return string.Join('\n', text);
        void Visit(DependencyObject node)
        { if (node is TextBlock block) text.Add(block.Text); for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i)); }
    }
    private static Task OnUiAsync(Func<Task> work)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            { try { await work(); finished.TrySetResult(); } catch (Exception error) { finished.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); } }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return finished.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }
    private async Task<NodeState> Node(ulong value, TimeProvider? clock = null)
    {
        string directory = Path.Combine(fixture, value.ToString(System.Globalization.CultureInfo.InvariantCulture)); string root = Path.Combine(directory, "tree"); Directory.CreateDirectory(root);
        var index = await FolderIndex.OpenAsync(Path.Combine(directory, "index.db"), Token); byte[] id = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id, value);
        var engine = new TransportFolder(root, index, keys, id, timeProvider: clock); await engine.RunPassAsync(Token); return new(index, engine, id);
    }
    private static async Task Until(Func<bool> predicate)
    { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(10)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private sealed record NodeState(FolderIndex Index, TransportFolder Engine, byte[] Id) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Index.DisposeAsync(); } }
    private sealed class Clock : TimeProvider
    { private long offset; public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offset)); internal void Jump(TimeSpan duration) => Interlocked.Add(ref offset, duration.Ticks); }
    public void Dispose()
    {
        keys.Dispose(); if (!Directory.Exists(fixture)) return; string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-ClockUi-", StringComparison.Ordinal)) throw new IOException("Unexpected fixture root.");
        Directory.Delete(resolved, true);
    }
}
