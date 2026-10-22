using PeerSync.Engine.Sync;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class TransferSchedulerTests
{
    [Fact]
    public async Task PeerWindowsGrowOnSuccessAndContractOnFailureIndependently()
    {
        var scheduler = new TransferScheduler();
        using (var failed = await scheduler.AcquireAsync("peer-a", 1024 * 1024, TestContext.Current.CancellationToken))
            failed.Complete(succeeded: false);

        scheduler.ReadWindow("peer-a").ShouldBe(2 * 1024 * 1024);
        scheduler.ReadWindow("peer-b").ShouldBe(TransferScheduler.InitialWindowBytes);
        using (var succeeded = await scheduler.AcquireAsync("peer-a", 1024 * 1024, TestContext.Current.CancellationToken))
            succeeded.Complete(succeeded: true);
        scheduler.ReadWindow("peer-a").ShouldBe(2 * 1024 * 1024 + 64 * 1024);
    }

    [Fact]
    public async Task PeerAvailabilityErrorsReleaseCapacityWithoutChangingTheWindow()
    {
        var scheduler = new TransferScheduler();
        using (var lease = await scheduler.AcquireAsync("peer-a", 1024 * 1024, TestContext.Current.CancellationToken))
            lease.CompleteWithoutCongestion();

        scheduler.ReadWindow("peer-a").ShouldBe(TransferScheduler.InitialWindowBytes);
        using var next = await scheduler.AcquireAsync("peer-a", TransferScheduler.InitialWindowBytes,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentBytesWaitUntilThePeerWindowCanGrow()
    {
        var scheduler = new TransferScheduler();
        using var initial = await scheduler.AcquireAsync("peer-a", TransferScheduler.InitialWindowBytes,
            TestContext.Current.CancellationToken);
        Task<TransferScheduler.Lease> waiting = scheduler.AcquireAsync("peer-a", 64 * 1024,
            TestContext.Current.CancellationToken).AsTask();

        await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        waiting.IsCompleted.ShouldBeFalse();
        scheduler.WaitingCount.ShouldBe(1);
        initial.Complete(succeeded: true);

        using TransferScheduler.Lease next = await waiting.WaitAsync(TestContext.Current.CancellationToken);
        scheduler.WaitingCount.ShouldBe(0);
        next.Complete(succeeded: true);
    }

    [Fact]
    public async Task CancelingAQueuedTransferDoesNotConsumeWindowCapacity()
    {
        var scheduler = new TransferScheduler();
        using var initial = await scheduler.AcquireAsync("peer-a", TransferScheduler.InitialWindowBytes,
            TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<TransferScheduler.Lease> waiting = scheduler.AcquireAsync("peer-a", 1,
            cancellation.Token).AsTask();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        scheduler.WaitingCount.ShouldBe(0);

        initial.Complete(succeeded: true);
        using var next = await scheduler.AcquireAsync("peer-a", TransferScheduler.InitialWindowBytes,
            TestContext.Current.CancellationToken);
    }
}
