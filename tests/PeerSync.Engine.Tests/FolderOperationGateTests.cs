using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class FolderOperationGateTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-FolderGate-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private string Root => Path.Combine(fixture, "tree");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public FolderOperationGateTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task AFilesystemOperationBlocksScanningAndDoubleLeaseDisposalDoesNotReleaseTwice()
    {
        File.WriteAllText(Path.Combine(Root, "file"), "content");
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        using var scanner = new Scanner(Root, index, keys, new byte[32]);
        using var operation = await FolderOperationGate.AcquireAsync(index, Token);
        Task<ScanResult> scan = scanner.ScanAsync(Token);
        scan.IsCompleted.ShouldBeFalse(); index.LocalFiles.Count().ShouldBe(0);
        operation.Dispose(); operation.Dispose();
        (await scan).Hashed.ShouldBe(1);
        using var first = await FolderOperationGate.AcquireAsync(index, Token);
        var second = FolderOperationGate.AcquireAsync(index, Token); second.IsCompleted.ShouldBeFalse();
        first.Dispose(); using var completed = await second;
    }

    [Fact]
    public async Task CancelledSharedGateWaitReleasesTheScannersPrivateGate()
    {
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        using var scanner = new Scanner(Root, index, keys, new byte[32]);
        using var operation = await FolderOperationGate.AcquireAsync(index, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<ScanResult> blocked = scanner.ScanAsync(cancellation.Token); blocked.IsCompleted.ShouldBeFalse();
        cancellation.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => blocked);
        operation.Dispose(); (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty();
    }

    [Fact]
    public async Task PendingIntentPreventsAVisibleFilesystemGapFromBecomingASignedDeletion()
    {
        string path = Path.Combine(Root, "file"); File.WriteAllText(path, "preserve original bytes");
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        using var scanner = new Scanner(Root, index, keys, new byte[32]); await scanner.ScanAsync(Token);
        var original = index.LocalFiles.Find("file")!;
        var intent = new ApplyIntent(Guid.NewGuid(), "file", original.LocalSequence, [1]);
        await index.ReserveApplyAsync(intent, Token);
        string retained = Path.Combine(Root, ".psp", "archive", "retained"); Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        File.Move(path, retained);
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        index.LocalFiles.Find("file")!.Deleted.ShouldBeFalse(); index.LocalFiles.Find("file")!.LocalSequence.ShouldBe(original.LocalSequence);
        // Restore first, then explicitly abandon the test's intent and resume scans.
        File.Move(retained, path); await index.AbandonApplyAsync(intent, Token);
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
        File.ReadAllText(path).ShouldBe("preserve original bytes");
        index.LocalFiles.Find("file")!.Record!.Body.ToArray().ShouldBe(original.Record!.Body.ToArray());
    }

    [Fact]
    public async Task DifferentFolderIndexOwnersHaveIndependentOperationGates()
    {
        await using var first = await FolderIndex.OpenAsync(Path.Combine(fixture, "first.db"), Token);
        await using var second = await FolderIndex.OpenAsync(Path.Combine(fixture, "second.db"), Token);
        using var held = await FolderOperationGate.AcquireAsync(first, Token);
        using var scanner = new Scanner(Root, second, keys, new byte[32]);
        (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty();
    }

    public void Dispose() { keys.Dispose(); Directory.Delete(fixture, recursive: true); }
}
