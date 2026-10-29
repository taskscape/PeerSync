using PeerSync.Service.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class NetworkSessionRecoveryTests
{
    [Fact]
    public async Task RecoveryStartsEverySessionResetBeforeWaitingForOneToClose()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        var sessions = Enumerable.Range(0, 3).Select(index =>
            new KeyValuePair<Guid, IAsyncDisposable>(Guid.NewGuid(),
                new BlockingSession(release.Task, () =>
                {
                    if (Interlocked.Increment(ref started) == 3) allStarted.TrySetResult();
                }, fail: index == 2))).ToArray();

        Task reset = PeerNetworkRuntime.ResetActiveSessionsAsync(sessions, "network_change", null);
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            reset.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult();
        }

        // One failed session must not strand the other resets or the recovery pass.
        await reset.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        started.ShouldBe(3);
        foreach (BlockingSession session in sessions.Select(pair => pair.Value).Cast<BlockingSession>())
            session.DisposeCount.ShouldBe(1);
    }

    private sealed class BlockingSession(Task release, Action started, bool fail) : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            started();
            await release.ConfigureAwait(false);
            if (fail) throw new IOException("simulated session close failure");
        }
    }
}
