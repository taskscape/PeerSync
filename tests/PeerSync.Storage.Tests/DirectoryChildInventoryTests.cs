using System.Buffers.Binary;
using System.Text;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed partial class DirectoryChildrenTests
{
    [Fact]
    public async Task InventoryMatchesCompleteNativeUnicodeKeysAndNeverLoadsMalformedBodies()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        string[] paths = ["topic/a", "topic/Żółć", "topic/\uE000", "topic/\U00010000", "topic/\U0001F600"];
        await index.LocalFiles.UpsertAsync(paths.Select(path => Row(path, 512 * 1024)), Token);
        using var native = new DirectoryChildFingerprint("ToPiC");
        foreach (string key in paths.Select(PathRules.Key).Order(StringComparer.Ordinal)) native.Add(key);
        var expected = native.Complete(0); var actual = index.ReadDirectoryChildInventory("topic", Token);
        actual.Hash.ShouldBe(expected.Hash); actual.Count.ShouldBe(paths.Length); actual.Epoch.ShouldBe(index.ReadDirectoryCaptureEpoch());
        actual.ToString().ShouldNotContain("TOPIC");
    }
    [Fact]
    public async Task MembershipExcludesUnsignedIgnoredDeletedAndOtherParentsButRetainsUnavailableSignatures()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await index.LocalFiles.UpsertAsync([Row("topic/a"), Row("topic/unavailable") with { ContentReady = false, Flags = LocalFileState.NeedsRescan },
            Row("topic/unsigned") with { Record = null }, Row("topic/ignored") with { Flags = LocalFileState.Ignored },
            Row("topic/deleted") with { Deleted = true }, Row("topic/reparse") with { Type = 2, FileHash = null }, Row("topic/nested/deep")], Token);
        using var native = new DirectoryChildFingerprint("topic"); native.Add("TOPIC/A"); native.Add("TOPIC/UNAVAILABLE");
        var expected = native.Complete(0); var actual = index.ReadDirectoryChildInventory("topic", Token);
        actual.Count.ShouldBe(2); actual.Hash.ShouldBe(expected.Hash);
        // Missing unsigned supported membership must not match a complete walk.
        using var complete = new DirectoryChildFingerprint("topic"); complete.Add("TOPIC/A"); complete.Add("TOPIC/UNAVAILABLE"); complete.Add("TOPIC/UNSIGNED");
        actual.Hash.ShouldNotBe(complete.Complete(0).Hash);
    }
    [Fact]
    public async Task ContentOnlyMutationChangesEpochButNotMembershipAndInsertDeleteChangeBoth()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await index.LocalFiles.UpsertAsync([Row("topic/a")], Token);
        var first = index.ReadDirectoryChildInventory("topic", Token);
        await index.LocalFiles.UpsertAsync([Row("topic/a", 5) with { ContentReady = false }], Token);
        var content = index.ReadDirectoryChildInventory("topic", Token); content.Hash.ShouldBe(first.Hash); content.Epoch.ShouldBeGreaterThan(first.Epoch);
        await index.LocalFiles.UpsertAsync([Row("topic/b")], Token);
        var added = index.ReadDirectoryChildInventory("topic", Token); added.Hash.ShouldNotBe(content.Hash); added.Count.ShouldBe(2); added.Epoch.ShouldBeGreaterThan(content.Epoch);
        await index.LocalFiles.UpsertAsync([Row("topic/b") with { Deleted = true }], Token);
        var removed = index.ReadDirectoryChildInventory("topic", Token); removed.Hash.ShouldBe(first.Hash); removed.Epoch.ShouldBeGreaterThan(added.Epoch);
    }
    [Fact]
    public async Task InventoryCancellationDoesNotYieldAnEmptyOrPartialProof()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Should.Throw<OperationCanceledException>(() => index.ReadDirectoryChildInventory("topic", cancelled.Token));
        await index.LocalFiles.UpsertAsync(Enumerable.Range(0, 1001).Select(value => Row("topic/" + value.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))), Token);
        Should.Throw<OperationCanceledException>(() => index.ReadDirectoryChildInventory("topic", cancelled.Token));
        index.ReadDirectoryChildInventory("topic", Token).Count.ShouldBe(1001);
    }
    [Fact]
    public void FingerprintWireIsDomainBoundedOrderedAndCannotBeExtendedAfterCompletion()
    {
        using var native = new DirectoryChildFingerprint("topic"); native.Add("TOPIC/A");
        Should.Throw<ArgumentException>(() => native.Add("TOPIC/A")); Should.Throw<ArgumentException>(() => native.Add("TOPIC/0"));
        Should.Throw<ArgumentException>(() => native.Add("topic/b")); Should.Throw<ArgumentException>(() => native.Add("TOPIC/NESTED/C"));
        var result = native.Complete(8); result.Count.ShouldBe(1); result.Epoch.ShouldBe(8);
        // Independent encoded fixture fixes the cross-layer fingerprint format.
        using var encoded = new MemoryStream(); encoded.Write("PeerSync directory child keys v1"u8);
        foreach (string key in new[] { "TOPIC", "TOPIC/A" }) { byte[] bytes = Encoding.BigEndianUnicode.GetBytes(key); encoded.Write(BitConverter.GetBytes((uint)bytes.Length)); encoded.Write(bytes); }
        Span<byte> count = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(count, 1); encoded.Write(count);
        result.Hash.ShouldBe(Blake3.Hasher.Hash(encoded.ToArray()).AsSpan().ToArray());
        Should.Throw<InvalidOperationException>(() => native.Add("TOPIC/B")); Should.Throw<InvalidOperationException>(() => native.Complete(8));
        using var other = new DirectoryChildFingerprint("other"); other.Add("OTHER/A"); other.Complete(8).Hash.ShouldNotBe(result.Hash);
    }
}
