using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class FolderWatcherTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Watcher-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(fixture, "tree");
    public FolderWatcherTests() => Directory.CreateDirectory(Root);

    [Fact]
    public void QuietAndMaximumDebounceUsePerPathMonotonicTimeAndBoundPendingState()
    {
        var pending = new PathDebouncer(2);
        pending.Add("file", TimeSpan.Zero).ShouldBeTrue();
        pending.Add("FILE", TimeSpan.FromSeconds(1)).ShouldBeTrue();
        pending.Ready(TimeSpan.FromSeconds(2.99)).ShouldBeEmpty();
        pending.Ready(TimeSpan.FromSeconds(3)).ShouldBe(["file"]);
        pending.Ready(TimeSpan.FromSeconds(4)).ShouldBeEmpty();
        pending.Add("hot", TimeSpan.FromSeconds(10)).ShouldBeTrue();
        for (int i = 11; i <= 39; i++)
        {
            pending.Add("hot", TimeSpan.FromSeconds(i)).ShouldBeTrue();
            pending.Ready(TimeSpan.FromSeconds(i)).ShouldBeEmpty();
        }
        pending.Ready(TimeSpan.FromSeconds(40)).ShouldBe(["hot"]);
        pending.Add("unordered", TimeSpan.FromSeconds(69)).ShouldBeTrue();
        pending.Add("unordered", TimeSpan.FromSeconds(40)).ShouldBeTrue();
        pending.Ready(TimeSpan.FromSeconds(70)).ShouldBe(["unordered"]);
        pending.Add("a", TimeSpan.FromSeconds(50)).ShouldBeTrue();
        pending.Add("b", TimeSpan.FromSeconds(50)).ShouldBeTrue();
        pending.Add("c", TimeSpan.FromSeconds(50)).ShouldBeFalse();
        pending.Clear(); pending.Add("c", TimeSpan.FromSeconds(50)).ShouldBeTrue();
    }

    [Fact]
    public async Task ActualNativeRenameIncludesBothPathsAndInternalWritesArePruned()
    {
        using var watcher = new FolderWatcher(Root);
        Directory.CreateDirectory(Path.Combine(Root, ".psp"));
        await File.WriteAllTextAsync(Path.Combine(Root, ".psp", "ignore"), "private", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Root, "temp.!psp"), "private", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Root, "before"), "data", TestContext.Current.CancellationToken);
        File.Move(Path.Combine(Root, "before"), Path.Combine(Root, "after"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var batches = watcher.ReadyBatchesAsync(timeout.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await batches.MoveNextAsync()).ShouldBeTrue();
        batches.Current.FullScan.ShouldBeFalse();
        batches.Current.RelativePaths.ShouldContain("before"); batches.Current.RelativePaths.ShouldContain("after");
        batches.Current.RelativePaths.ShouldAllBe(p => !p.Contains(".psp", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".!psp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("queue")]
    [InlineData("pending")]
    public async Task EveryOverflowSourceRequestsAFullScanFromTheConsumer(string source)
    {
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "watcher");
        using var watcher = new FolderWatcher(Root, source == "queue" ? 1 : 8, 1, journal: journal);
        if (source == "native") watcher.NotifyOverflow();
        else
        {
            watcher.Notify(Path.Combine(Root, "first")); watcher.Notify(Path.Combine(Root, "second"));
        }
        // No callback-side journal I/O, including native overflow notifications.
        File.Exists(Path.Combine(fixture, "logs", "debug.jsonl")).ShouldBeFalse();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var batches = watcher.ReadyBatchesAsync(timeout.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await batches.MoveNextAsync()).ShouldBeTrue();
        batches.Current.FullScan.ShouldBeTrue(); batches.Current.RelativePaths.ShouldBeEmpty();
        batches.Current.Reason.ShouldBe(source == "native" ? "watcher-overflow" : source + "-overflow");
        File.Exists(Path.Combine(fixture, "logs", "debug.jsonl")).ShouldBeTrue();
    }

    [Fact]
    public async Task DisposalCompletesIdleConsumerAndCancellationReleasesSingleConsumerOwnership()
    {
        using var watcher = new FolderWatcher(Root);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using (var first = watcher.ReadyBatchesAsync(cancellation.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            var waiting = first.MoveNextAsync().AsTask();
            await using (var second = watcher.ReadyBatchesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
                await Should.ThrowAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());
            cancellation.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        }
        await using var restarted = watcher.ReadyBatchesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var pending = restarted.MoveNextAsync().AsTask();
        watcher.Dispose();
        (await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task CommittedOwnWriteMetadataProducesNoNewVersionAfterDebouncedNotification()
    {
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), TestContext.Current.CancellationToken);
        using var keys = FolderKeys.CreateNew();
        using var scanner = new Scanner(Root, index, keys, new byte[32]);
        using var watcher = new FolderWatcher(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, "file"), "own write", TestContext.Current.CancellationToken);
        // Models the puller's required metadata commit before its queued event is consumed.
        await scanner.ScanAsync(TestContext.Current.CancellationToken);
        long sequence = index.ReadIndexBatch(0).LastSequence;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var batches = watcher.ReadyBatchesAsync(timeout.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await batches.MoveNextAsync()).ShouldBeTrue(); batches.Current.RelativePaths.ShouldContain("file");
        var result = await scanner.ScanAsync(TestContext.Current.CancellationToken);
        result.Hashed.ShouldBe(0); result.Updated.ShouldBe(0);
        index.ReadIndexBatch(0).LastSequence.ShouldBe(sequence);
    }

    public void Dispose() => Directory.Delete(fixture, recursive: true);
}
