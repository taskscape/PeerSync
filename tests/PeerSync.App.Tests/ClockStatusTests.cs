using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Security.Principal;
using PeerSync.App.Status;
using PeerSync.Control.Contracts;
using PeerSync.Core.Sync;
using PeerSync.Core.Diagnostics;
using PeerSync.Service.Control;
using Shouldly;
using Xunit;

namespace PeerSync.App.Tests;

public sealed class ClockStatusTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(PeerClockState.Skewed, 11, "+11.00", "exchange blocked")]
    [InlineData(PeerClockState.Skewed, -11, "-11.00", "exchange blocked")]
    [InlineData(PeerClockState.Accepted, 0, "0.00", "Clock accepted")]
    [InlineData(PeerClockState.AwaitingHello, null, "Unavailable", "Waiting for peer clock sample")]
    [InlineData(PeerClockState.InvalidTimestamp, null, "Unavailable", "Invalid peer timestamp")]
    [InlineData(PeerClockState.InvalidElapsed, null, "Unavailable", "Invalid clock elapsed")]
    public void TypedRowsShowSignedMinutesReasonsAndDisconnectedState(PeerClockState state, int? minutes, string offset, string reason)
    {
        var model = new ClockStatusViewModel(); long generation = model.BeginAttempt();
        model.Apply(generation, Snapshot(Guid.NewGuid(), 1, state, minutes)).ShouldBeTrue();
        var row = model.Rows.Single(); row.OffsetMinutes.ShouldBe(offset); row.Reason.ShouldContain(reason); row.Connection.ShouldBe("Disconnected");
        model.Availability.ShouldContain("not available");
    }
    [Fact]
    public void OldGenerationsEpochsSequencesAndLateSuccessCannotResurrectStaleState()
    {
        var model = new ClockStatusViewModel(); var first = Guid.NewGuid(); long old = model.BeginAttempt();
        model.Apply(old, Snapshot(first, 5, PeerClockState.Skewed, 11)).ShouldBeTrue();
        model.Apply(old, Snapshot(first, 4, PeerClockState.Accepted, 0)).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => model.Apply(old, Snapshot(Guid.NewGuid(), 6, PeerClockState.Accepted, 0)));
        model.Fail(old, "disconnected"); model.Rows.ShouldBeEmpty();
        model.Apply(old, Snapshot(first, 7, PeerClockState.Accepted, 0)).ShouldBeFalse();
        long current = model.BeginAttempt(); model.Apply(current, Snapshot(Guid.NewGuid(), 1, PeerClockState.Accepted, 0)).ShouldBeTrue();
        model.Fail(old, "late failure"); model.Apply(old, Snapshot(first, 100, PeerClockState.Skewed, 11)).ShouldBeFalse();
        model.Rows.Single().Reason.ShouldBe("Clock accepted");
    }
    [Fact]
    public void EmptyTruncatedAndRecoveredSnapshotsReplaceAllPriorWarnings()
    {
        var model = new ClockStatusViewModel(); long attempt = model.BeginAttempt(); var epoch = Guid.NewGuid(); int notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;
        model.Apply(attempt, Snapshot(epoch, 1, PeerClockState.Skewed, -11));
        model.Apply(attempt, new(epoch, 2, false, [], true)); model.Rows.ShouldBeEmpty(); model.Detail.ShouldContain("omitted");
        model.Apply(attempt, new(epoch, 3, true, [], false)); model.Detail.ShouldBe("No peer clock sessions reported."); model.Availability.ShouldBe("Production sync is available.");
        notifications.ShouldBe(3); model.Fail(attempt, "failed"); model.Availability.ShouldBe("Production sync status is unavailable.");
    }
    [Fact]
    public async Task CleanStreamEndClearsSuccessAndReconnectsWithFreshEpochThenCancellationDisposesTheChannel()
    {
        var model = new ClockStatusViewModel(); var clock = new ManualClock(); int connects = 0;
        var first = new FakeConnection(Snapshot(Guid.NewGuid(), 1, PeerClockState.Skewed, 11), end: true);
        var second = new FakeConnection(Snapshot(Guid.NewGuid(), 1, PeerClockState.Accepted, 0));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var controller = new ClockStatusController(model, Dispatch, _ => Task.FromResult<IControlStatusConnection>(Interlocked.Increment(ref connects) == 1 ? first : second), clock);
        var run = controller.RunAsync(stop.Token);
        try
        {
            var retry = await clock.NextAsync(Token); retry.Due.ShouldBe(TimeSpan.FromSeconds(1)); model.Rows.ShouldBeEmpty(); model.Connection.ShouldContain("unavailable"); first.Disposed.ShouldBeTrue();
            retry.Fire(); await Until(() => model.Rows.Count == 1 && model.Rows[0].Reason == "Clock accepted");
            connects.ShouldBe(2); stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5), Token); second.Disposed.ShouldBeTrue(); second.StreamCancelled.ShouldBeTrue();
        }
        finally { stop.Cancel(); await run; }
    }
    [Fact]
    public async Task ActualPipePidFailureShowsIdentityReasonAndLogsBoundedSanitizedRpcCauses()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-UiLogs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new DiagnosticJournal(fixture, "app", debugEnabled: true); using var identity = WindowsIdentity.GetCurrent();
            string pipe = "PeerSync.Control.IdentityUi-" + Guid.NewGuid().ToString("N");
            await using var host = await ControlApiHost.StartFixtureAsync(pipe, new(), [identity.User!], cancellationToken: Token);
            var model = new ClockStatusViewModel(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var controller = new ClockStatusController(model, Dispatch, token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<IControlStatusConnection>(new VerifiedStatusConnection(ControlStatusClient.CreateFixture(new(pipe, identity.User!, (uint)Environment.ProcessId + 1))));
            }, diagnostics: journal);
            var run = controller.RunAsync(stop.Token);
            try { await Until(() => model.Detail.Contains("identity verification failed", StringComparison.Ordinal)); model.Rows.ShouldBeEmpty(); }
            finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5), Token); }
            string log = File.ReadAllText(Path.Combine(fixture, "debug.jsonl")); log.ShouldContain("app.control_failed"); log.ShouldContain("RpcException"); log.ShouldContain("app.control_cause"); log.ShouldContain("SecurityException");
            log.ShouldNotContain(identity.User!.Value); log.ShouldNotContain(pipe); log.ShouldNotContain(fixture);
        }
        finally { DeleteLogFixture(fixture); }
    }
    [Fact]
    public async Task SilentStreamTimesOutAndMissingServiceRetriesWithBoundedBackoff()
    {
        var model = new ClockStatusViewModel(); var clock = new ManualClock(); int connects = 0;
        var connection = new FakeConnection(Snapshot(Guid.NewGuid(), 1, PeerClockState.Accepted, 0));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<IControlStatusConnection> Connect(CancellationToken _) => Interlocked.Increment(ref connects) == 1
            ? Task.FromResult<IControlStatusConnection>(connection) : throw new Win32Exception(1060);
        var controller = new ClockStatusController(model, Dispatch, Connect, clock); var run = controller.RunAsync(stop.Token);
        try
        {
            var timeout = await clock.NextAsync(Token); timeout.Due.ShouldBe(TimeSpan.FromSeconds(15)); timeout.Fire();
            var retry = await clock.NextAsync(Token); model.Rows.ShouldBeEmpty(); model.Detail.ShouldContain("timed out"); connection.StreamCancelled.ShouldBeTrue();
            retry.Fire();
            for (int failure = 1; failure <= 7; failure++)
            {
                var timer = await clock.NextAsync(Token); timer.Due.ShouldBe(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failure, 5))));
                model.Detail.ShouldContain("1060"); timer.Fire();
            }
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }
    internal static ControlStatusSnapshot Snapshot(Guid epoch, ulong sequence, PeerClockState state, int? minutes) => new(epoch, sequence, false,
        [new(new byte[32], new(Guid.NewGuid(), new byte[32], new(state, minutes is { } value ? value * TimeSpan.TicksPerMinute : null), false))], false);
    private static void DeleteLogFixture(string fixture)
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-UiLogs-", StringComparison.Ordinal)) throw new IOException("Unexpected fixture root.");
        if (!Directory.Exists(resolved)) return;
        if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected fixture link.");
        Directory.Delete(resolved, true);
    }
    private static Task Dispatch(Action action, CancellationToken token) { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    private static async Task Until(Func<bool> predicate)
    { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private sealed class FakeConnection(ControlStatusSnapshot initial, bool end = false) : IControlStatusConnection
    {
        private readonly Channel<ControlStatusSnapshot> updates = Channel.CreateBounded<ControlStatusSnapshot>(1);
        internal bool Disposed { get; private set; }
        internal bool StreamCancelled { get; private set; }
        public Task<ControlStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(initial); }
        public async IAsyncEnumerable<ControlStatusSnapshot> WatchEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try { if (end) yield break; await foreach (var status in updates.Reader.ReadAllAsync(cancellationToken)) yield return status; }
            finally { StreamCancelled = cancellationToken.IsCancellationRequested; }
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class ManualClock : TimeProvider
    {
        private readonly Channel<ManualTimer> timers = Channel.CreateUnbounded<ManualTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state, dueTime); timers.Writer.TryWrite(timer); return timer; }
        internal async Task<ManualTimer> NextAsync(CancellationToken token) => await timers.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
    }
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        private int disposed; internal TimeSpan Due { get; } = due;
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref disposed) == 0;
        internal void Fire() { if (Interlocked.Exchange(ref disposed, 1) == 0) callback(state); }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
