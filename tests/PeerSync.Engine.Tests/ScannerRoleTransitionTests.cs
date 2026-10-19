using System.Buffers.Binary;
using PeerSync.Core.Ignore;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class ScannerRoleTransitionTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-RoleScan-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static readonly byte[] Device = Enumerable.Repeat((byte)1, 32).ToArray();
    private static ulong ShortDevice => BinaryPrimitives.ReadUInt64LittleEndian(Device);
    private string Root => Path.Combine(fixture, "tree");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ScannerRoleTransitionTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task UpgradingReadOnlyTrackingPublishesUnchangedUnsignedFilesAndDirectories()
    {
        Directory.CreateDirectory(Path.Combine(Root, "dir")); File.WriteAllText(Path.Combine(Root, "dir", "file"), "content");
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        using (var scanner = new Scanner(Root, index, readOnly, Device)) await scanner.ScanAsync(Token);
        index.LocalFiles.ReadAll().ShouldAllBe(r => r.Record == null);
        using var writer = new Scanner(Root, index, keys, Device);
        (await writer.ScanAsync(Token)).Updated.ShouldBe(2);
        foreach (var row in index.ReadIndexBatch(0).Records)
        {
            SignedRecordFactory.TryOpen(row.Record!, keys, out _).ShouldBeTrue(); row.Vector[ShortDevice].ShouldBe(1ul);
        }
        index.ReadIndexBatch(0).Records.Count.ShouldBe(2); (await writer.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    [Fact]
    public async Task APendingReadOnlyDeletionBecomesASignedCausalTombstoneWhenWritable()
    {
        File.WriteAllText(Path.Combine(Root, "file"), "published");
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        using (var writer = new Scanner(Root, index, keys, Device)) await writer.ScanAsync(Token);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        File.Delete(Path.Combine(Root, "file"));
        using (var reader = new Scanner(Root, index, readOnly, Device)) await reader.ScanAsync(Token);
        var pending = index.LocalFiles.Find("file")!; pending.Deleted.ShouldBeTrue(); pending.Record.ShouldBeNull(); pending.Vector[ShortDevice].ShouldBe(1ul);
        using var scanner = new Scanner(Root, index, keys, Device);
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(1);
        var published = index.LocalFiles.Find("file")!;
        SignedRecordFactory.TryOpen(published.Record!, keys, out var body).ShouldBeTrue(); body!.Deleted.ShouldBeTrue();
        published.Vector[ShortDevice].ShouldBe(2ul); published.ChangedTicks.ShouldBe(pending.ChangedTicks);
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    [Fact]
    public async Task NeverPublishedAndUnsyncableReadOnlyDeletionsDoNotInventPublishedTombstones()
    {
        File.WriteAllText(Path.Combine(Root, "file"), "never published"); File.WriteAllText(Path.Combine(Root, "e\u0301"), "invalid normalized name");
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        using (var reader = new Scanner(Root, index, readOnly, Device)) await reader.ScanAsync(Token);
        File.Delete(Path.Combine(Root, "file")); File.Delete(Path.Combine(Root, "e\u0301"));
        using (var reader = new Scanner(Root, index, readOnly, Device)) await reader.ScanAsync(Token);
        using var writer = new Scanner(Root, index, keys, Device); await writer.ScanAsync(Token);
        index.ReadIndexBatch(0).Records.ShouldBeEmpty(); index.LocalFiles.ReadAll().ShouldAllBe(r => r.Deleted && r.Record == null);
    }

    [Fact]
    public async Task AWritableScanCanPublishDeletionOfAPreviouslyPublishedReadOnlyLocalEditImmediately()
    {
        string path = Path.Combine(Root, "file"); File.WriteAllText(path, "published");
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        using (var writer = new Scanner(Root, index, keys, Device)) await writer.ScanAsync(Token);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        File.WriteAllText(path, "longer unsigned local edit");
        using (var reader = new Scanner(Root, index, readOnly, Device)) await reader.ScanAsync(Token);
        index.LocalFiles.Find("file")!.Record.ShouldBeNull(); File.Delete(path);
        using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token);
        var saved = index.LocalFiles.Find("file")!; saved.Deleted.ShouldBeTrue(); saved.Vector[ShortDevice].ShouldBe(2ul);
        SignedRecordFactory.TryOpen(saved.Record!, keys, out var body).ShouldBeTrue(); body!.Deleted.ShouldBeTrue();
    }

    [Theory]
    [InlineData("ignore")]
    [InlineData("retry")]
    public async Task SignedTombstonesSuppressedTemporarilyAreRepublishedWithoutInventingAnotherEdit(string suppression)
    {
        Directory.CreateDirectory(Path.Combine(Root, "dir")); string path = Path.Combine(Root, "dir", "file"); File.WriteAllText(path, "published");
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
        using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token);
        File.Delete(path); await scanner.ScanAsync(Token); var tombstone = index.LocalFiles.Find("dir/file")!;
        if (suppression == "ignore")
        {
            using var ignored = new Scanner(Root, index, keys, Device, new IgnoreMatcher("dir/")); await ignored.ScanAsync(Token);
        }
        else
        {
            File.WriteAllText(path, "recreated but unreadable");
            using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) await scanner.ScanAsync(Token);
            File.Delete(path);
        }
        var suppressed = index.LocalFiles.Find("dir/file")!; suppressed.Flags.ShouldNotBe(LocalFileState.None);
        await scanner.ScanAsync(Token); var revived = index.LocalFiles.Find("dir/file")!;
        revived.Flags.ShouldBe(LocalFileState.None); revived.LocalSequence.ShouldBeGreaterThan(suppressed.LocalSequence);
        revived.Record!.Body.ToArray().ShouldBe(tombstone.Record!.Body.ToArray());
        VersionVector.Compare(revived.Vector, tombstone.Vector).ShouldBe(VvOrder.Equal);
        index.ReadIndexBatch(suppressed.LocalSequence).Records.ShouldContain(r => r.Path == "dir/file" && r.Deleted);
    }

    public void Dispose() { keys.Dispose(); Directory.Delete(fixture, recursive: true); }
}
