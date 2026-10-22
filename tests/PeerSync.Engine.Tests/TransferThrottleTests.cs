using System.Diagnostics;
using PeerSync.Engine.Sync;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class TransferThrottleTests
{
    [Fact]
    public async Task LowConfiguredRateCannotSpendA64KiBBurstImmediately()
    {
        using var throttle = new TransferThrottle(1_000);
        await throttle.AcquireAsync(250, TestContext.Current.CancellationToken);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<TimeSpan> waiting = throttle.AcquireAsync(250, cancellation.Token).AsTask();
        waiting.IsCompleted.ShouldBeFalse();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task LargeChunksAreAcquiredInBoundedSlicesAtTheConfiguredRate()
    {
        using var throttle = new TransferThrottle(128 * 1024);
        var elapsed = Stopwatch.StartNew();
        await throttle.AcquireAsync(256 * 1024, TestContext.Current.CancellationToken);
        elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1250));
    }

    [Fact]
    public async Task WaitingForTransferPermitsHonorsCallerCancellation()
    {
        using var throttle = new TransferThrottle(64 * 1024);
        await throttle.AcquireAsync(64 * 1024, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));
        await Should.ThrowAsync<OperationCanceledException>(() => throttle.AcquireAsync(64 * 1024, cancellation.Token).AsTask());
    }
}
