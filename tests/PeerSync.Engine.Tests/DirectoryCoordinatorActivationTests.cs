using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class DirectoryCoordinatorActivationTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryActivation-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] own = [1, .. new byte[31]];
    private static readonly string[] DirectoryPaths = ["topic", "topic/nested"];
    private string Root => Path.Combine(fixture, "tree");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DirectoryCoordinatorActivationTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "topic", "nested"));
        File.WriteAllText(Path.Combine(Root, "topic", "nested", "a"), "public coordinator payload");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicPullerPublishesNestedSnapshotsAndRepeatedScansDoNotChangeVersionsOrNativeTimes(bool legacyUpgrade)
    {
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        if (legacyUpgrade) { using var legacy = new Scanner(Root, index, keys, own); await legacy.ScanAsync(Token); }
        var old = index.LocalFiles.Find("topic"); long nativeRoot = Directory.GetLastWriteTimeUtc(Path.Combine(Root, "topic")).Ticks;
        long nativeNested = Directory.GetLastWriteTimeUtc(Path.Combine(Root, "topic", "nested")).Ticks;
        using var puller = new Puller(Root, index, keys, own, (_, _, _, _) => throw new IOException("Local publication needs no peer."));
        for (int pass = 0; pass < 4; pass++) await puller.RunPassAsync(Token);
        index.HasPendingDirectoryPublications().ShouldBeFalse();
        var directories = DirectoryPaths.Select(path => index.LocalFiles.Find(path)!).ToArray();
        foreach (var row in directories) DirectorySnapshotBlocks.SnapshotOf(VersionView.Open(row.Record!, keys, own)).Hash.Length.ShouldBe(32);
        if (old is not null) PeerSync.Core.Sync.VersionVector.Compare(directories[0].Vector, old.Vector).ShouldBe(PeerSync.Core.Sync.VvOrder.Dominates);
        long high = index.ReadIndexHighWater(); puller.RequestScan(); await puller.RunPassAsync(Token); await puller.RunPassAsync(Token);
        index.ReadIndexHighWater().ShouldBe(high);
        foreach (var row in directories) index.LocalFiles.Find(row.Path)!.Record!.Body.ToArray().ShouldBe(row.Record!.Body.ToArray());
        Directory.GetLastWriteTimeUtc(Path.Combine(Root, "topic")).Ticks.ShouldBe(nativeRoot);
        Directory.GetLastWriteTimeUtc(Path.Combine(Root, "topic", "nested")).Ticks.ShouldBe(nativeNested);
        (await new BlockReferences(Root, index, keys, own).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
    }

    [Fact]
    public async Task PublicReadonlyCoordinatorRetainsUnsignedNativeEditsWithoutPublishingOrStagingRoots()
    {
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        using var puller = new Puller(Root, index, readOnly, own, (_, _, _, _) => throw new IOException("No peer is connected."));
        await puller.RunPassAsync(Token); index.ReadIndexBatch(0).Records.ShouldBeEmpty(); index.HasPendingDirectoryPublications().ShouldBeFalse();
        File.ReadAllText(Path.Combine(Root, "topic", "nested", "a")).ShouldBe("public coordinator payload");
    }
    public void Dispose() { keys.Dispose(); Directory.Delete(fixture, recursive: true); }
}
