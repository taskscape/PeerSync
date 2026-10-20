using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Directories;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;
using SignedRecord = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Tests;

public sealed class DirectorySnapshotBlocksTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryBlocks-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] device = [1, .. new byte[31]];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(129)]
    public async Task StreamingCaptureFlushesDeterministicRootAndAllAuthenticatedChildren(int count)
    {
        await using var index = await BindAsync("source"); using var blocks = Store("source", index);
        IEnumerable<SignedRecord> Children(ulong sequence)
        {
            for (int value = 0; value < count; value++) yield return Sign("topic/child" + value.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), 0).WithLocalSequence(sequence);
        }
        var root = await blocks.CaptureDirectoryAsync("topic", Children(1), Token);
        int objects = index.ReadBlockObjects().Count();
        (await blocks.CaptureDirectoryAsync("TOPIC", Children(99), Token)).Hash.ShouldBe(root.Hash);
        index.ReadBlockObjects().Count().ShouldBe(objects);
        var work = new Stack<DirectorySnapshotLink>(); work.Push(root); var names = new HashSet<string>(StringComparer.Ordinal);
        while (work.TryPop(out var link))
        {
            var page = await blocks.ReadNodeAsync(link, NoFetch, Token);
            foreach (var child in page.Links)
                if (page.Kind == DirectorySnapshotNodeKind.Entries) names.Add((await blocks.ReadMemberAsync("TOPIC", child, NoFetch, Token)).Path).ShouldBeTrue();
                else { page.Kind.ShouldBe(DirectorySnapshotNodeKind.Branch); work.Push(child); }
        }
        names.Count.ShouldBe(count);
        Directory.EnumerateFiles(Path.Combine(Root("source"), BlockCaptureProof.Directory)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("topic/CHILD", "topic/child")]
    [InlineData("topic/z", "topic/a")]
    public async Task StreamingCaptureRejectsDuplicateOrDescendingCanonicalChildKeys(string first, string second)
    {
        await using var index = await BindAsync("source"); using var blocks = Store("source", index);
        await Should.ThrowAsync<InvalidDataException>(() => blocks.CaptureDirectoryAsync("topic", [Sign(first, 0), Sign(second, 0)], Token));
        // Partial captures are not a completed graph or publication authority.
        index.ReadIndexBatch(0).Records.ShouldBeEmpty(); await blocks.FlushAsync(Token);
    }

    [Fact]
    public async Task CapturedOriginalEnvelopesAndRootSurviveReopeningWithoutNetworkOrMutableFiles()
    {
        DirectorySnapshotLink root; var original = Sign("topic/child", 0).WithLocalSequence(987);
        await using (var index = await BindAsync("source"))
        {
            using var blocks = Store("source", index);
            var child = await blocks.CaptureMemberAsync("TOPIC", original, Token);
            root = await blocks.PutAsync(DirectorySnapshotNode.Entries([child]), Token);
            await blocks.FlushAsync(Token); index.ReadBlockObjects().Count().ShouldBe(3);
            Directory.EnumerateFiles(Path.Combine(Root("source"), BlockCaptureProof.Directory)).ShouldBeEmpty();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database("source"), Token);
        using var reader = Store("source", reopened);
        var page = await reader.ReadRootAsync(Sign("topic", 1, root), NoFetch, Token);
        var childVersion = await reader.ReadMemberAsync("TOPIC", page.Links.Single(), NoFetch, Token);
        childVersion.Record.Body.ToArray().ShouldBe(original.Body.ToArray());
        childVersion.Record.Signature.ToArray().ShouldBe(original.Signature.ToArray());
        childVersion.Record.LocalSequence.ShouldBe(0ul); childVersion.Path.ShouldBe("topic/child");
        File.Exists(Path.Combine(Root("source"), "topic", "child")).ShouldBeFalse();
    }

    [Fact]
    public async Task ReceiverFetchesExactMetadataAndKeepsOwnedVerifiedBytesAfterProviderBufferMutation()
    {
        await using var source = await BindAsync("source"); await using var receiver = await BindAsync("receiver");
        using var writer = Store("source", source); using var reader = Store("receiver", receiver);
        var member = await writer.CaptureMemberAsync("topic", Sign("topic/child", 0), Token);
        var root = await writer.PutAsync(DirectorySnapshotNode.Entries([member]), Token); await writer.FlushAsync(Token);
        using var cache = new PublishedBlocks(Root("source"), source); var responses = new List<byte[]>();
        ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> hash, int length, CancellationToken token)
        {
            var bytes = cache.TryRead(hash.Span, length, token)!; responses.Add(bytes); return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);
        }
        var page = await reader.ReadRootAsync(Sign("topic", 1, root), Fetch, Token);
        (await reader.ReadMemberAsync("topic", page.Links.Single(), Fetch, Token)).Path.ShouldBe("topic/child");
        responses.Count.ShouldBe(3); foreach (var response in responses) Array.Clear(response);
        (await reader.ReadRootAsync(Sign("topic", 1, root), NoFetch, Token)).Links.Count.ShouldBe(1);
        (await reader.ReadMemberAsync("topic", member, NoFetch, Token)).Path.ShouldBe("topic/child");
        await reader.FlushAsync(Token); receiver.ReadBlockObjects().Count().ShouldBe(3);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("format")]
    [InlineData("cancel")]
    public async Task InvalidOrCancelledResponsesCannotEnterPublishedStorage(string failure)
    {
        await using var index = await BindAsync("receiver"); using var blocks = Store("receiver", index);
        byte[] bytes = DirectorySnapshotNode.Entries([]).Encode();
        if (failure == "format") bytes[4] = 99;
        var expected = new DirectorySnapshotLink(Hash(bytes), bytes.Length);
        if (failure == "length") bytes = [.. bytes, 0];
        if (failure == "hash") bytes[0] ^= 1;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> _, int length, CancellationToken token)
        {
            if (failure == "cancel") stop.Cancel();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);
        }
        if (failure == "cancel") await Should.ThrowAsync<OperationCanceledException>(() => blocks.ReadNodeAsync(expected, Fetch, stop.Token));
        else await Should.ThrowAsync<InvalidDataException>(() => blocks.ReadNodeAsync(expected, Fetch, Token));
        await blocks.FlushAsync(Token); index.ReadBlockObjects().ShouldBeEmpty();
        Directory.Exists(Path.Combine(Root("receiver"), ".psp", "blocks")).ShouldBeFalse();
    }

    [Fact]
    public async Task CaptureRejectsUnrelatedDeletedForeignAndLegacyDirectoryMembersBeforeWriting()
    {
        await using var index = await BindAsync("source"); using var blocks = Store("source", index);
        await Should.ThrowAsync<InvalidDataException>(() => blocks.CaptureMemberAsync("topic", Sign("other/child", 0), Token));
        await Should.ThrowAsync<InvalidDataException>(() => blocks.CaptureMemberAsync("topic", Sign("child", 0), Token));
        await Should.ThrowAsync<InvalidDataException>(() => blocks.CaptureMemberAsync("topic", Sign("topic/child", 0, deleted: true), Token));
        await Should.ThrowAsync<InvalidDataException>(() => blocks.CaptureMemberAsync("topic", Sign("topic/nested", 1), Token));
        using var foreign = FolderKeys.CreateNew();
        var foreignRecord = SignedRecordFactory.Create(Body("topic/child", 0), foreign);
        await Should.ThrowAsync<CryptographicException>(() => blocks.CaptureMemberAsync("topic", foreignRecord, Token));
        index.ReadBlockObjects().ShouldBeEmpty(); Directory.Exists(Path.Combine(Root("source"), ".psp", "blocks")).ShouldBeFalse();
    }

    [Fact]
    public async Task GraphPositionsRejectWrongKindsAndAuthenticatesDecodedEnvelopesAndParents()
    {
        await using var index = await BindAsync("source"); using var blocks = Store("source", index);
        var wrongRoot = await blocks.PutAsync(DirectorySnapshotNode.Blob(new byte[36]), Token);
        await Should.ThrowAsync<InvalidDataException>(() => blocks.ReadRootAsync(Sign("topic", 1, wrongRoot), NoFetch, Token));
        await Should.ThrowAsync<InvalidDataException>(() => blocks.ReadMemberAsync("topic", wrongRoot, NoFetch, Token));
        var original = Sign("other/child", 0);
        var wrongParent = await RawMemberAsync(blocks, original);
        await Should.ThrowAsync<InvalidDataException>(() => blocks.ReadMemberAsync("topic", wrongParent, NoFetch, Token));
        var corrupt = await RawMemberAsync(blocks, new SignedRecord(original.Body, new byte[64]));
        await Should.ThrowAsync<CryptographicException>(() => blocks.ReadMemberAsync("other", corrupt, NoFetch, Token));
        await blocks.FlushAsync(Token);
    }

    [Fact]
    public async Task NestedDirectoryRequiresItsOwnAuthenticatedRootButCanKeepOldParentCaseSpelling()
    {
        await using var index = await BindAsync("source"); using var blocks = Store("source", index);
        var empty = await blocks.PutAsync(DirectorySnapshotNode.Entries([]), Token);
        var nested = await blocks.CaptureMemberAsync("TOPIC", Sign("topic/nested", 1, empty), Token);
        var view = await blocks.ReadMemberAsync("TOPIC", nested, NoFetch, Token);
        DirectorySnapshotBlocks.SnapshotOf(view).Hash.ShouldBe(empty.Hash);
        (await blocks.ReadRootAsync(view.Record, NoFetch, Token)).Links.ShouldBeEmpty();
        await Should.ThrowAsync<InvalidDataException>(() => blocks.ReadRootAsync(Sign("topic", 1), NoFetch, Token));
        await blocks.FlushAsync(Token);
    }

    [Fact]
    public async Task IncompleteDirectoryCoveragePreservesActiveReferencesAndPreventsNativeSweeping()
    {
        await using var index = await BindAsync("source");
        var before = await new BlockReferences(Root("source"), index, keys, device).RefreshAsync(Token);
        using var blocks = Store("source", index);
        var empty = await blocks.PutAsync(DirectorySnapshotNode.Entries([]), Token); await blocks.FlushAsync(Token);
        var missing = DirectorySnapshotNode.Entries([new DirectorySnapshotLink(new byte[32], 48)]);
        var directory = Sign("topic", 1, new(missing.CalculateHash(), missing.EncodedLength)); var view = VersionView.Open(directory, keys, device);
        await index.LocalFiles.UpsertAsync([new("topic", 1, 0, view.MtimeTicks, 0, false, view.ChangedTicks, null, view.Vector, directory, null)], Token);
        var pending = await new BlockReferences(Root("source"), index, keys, device).RefreshAsync(Token);
        pending.WaitingForDirectorySnapshots.ShouldBeTrue(); pending.AllKnownSourcesCovered.ShouldBeFalse(); pending.SnapshotId.ShouldBeNull();
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value);
        (await new BlockRetention(Root("source"), index, keys, device) { CapacityOverride = () => 1 }.SweepAsync(Token)).WaitingForRecovery.ShouldBeTrue();
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId.Value);
        using var cache = new PublishedBlocks(Root("source"), index);
        cache.TryRead(empty.Hash, empty.EncodedLength, Token).ShouldBe(DirectorySnapshotNode.Entries([]).Encode());
        index.ReadBlockDeletionIntents().ShouldBeEmpty();
    }

    private static async Task<DirectorySnapshotLink> RawMemberAsync(DirectorySnapshotBlocks blocks, SignedRecord record)
    {
        var envelope = DirectorySnapshotEnvelope.Encode(record);
        var blob = await blocks.PutAsync(DirectorySnapshotNode.Blob(envelope), Token);
        return await blocks.PutAsync(DirectorySnapshotNode.Record([blob], envelope.Length), Token);
    }
    private SignedRecord Sign(string path, uint type, DirectorySnapshotLink? root = null, bool deleted = false)
    {
        var body = Body(path, type); body.Deleted = deleted;
        if (deleted) body.FileHash = ByteString.Empty;
        if (root is not null) { body.DirectorySnapshotHash = ByteString.CopyFrom(root.Hash); body.DirectorySnapshotLength = (uint)root.EncodedLength; }
        return SignedRecordFactory.Create(body, keys);
    }
    private FileRecordBody Body(string path, uint type)
    {
        var body = new FileRecordBody { Path = path, Type = type, MtimeTicks = 638000000000000000, ChangedTicks = 638000000000000000,
            ModifiedBy = ByteString.CopyFrom(device) };
        body.Vv.Add(new VvEntry { Device = 1, Counter = 1 }); if (type == 0) body.FileHash = ByteString.CopyFrom(Hash([])); return body;
    }
    private DirectorySnapshotBlocks Store(string name, FolderIndex index) => new(Root(name), index, keys, device);
    private async Task<FolderIndex> BindAsync(string name)
    {
        Directory.CreateDirectory(Root(name)); var index = await FolderIndex.OpenAsync(Database(name), Token);
        try { using var scanner = new Scanner(Root(name), index, keys, device); (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty(); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private string Root(string name) => Path.Combine(fixture, name);
    private string Database(string name) => Path.Combine(fixture, name + ".db");
    private static byte[] Hash(ReadOnlySpan<byte> bytes) => Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    private static ValueTask<ReadOnlyMemory<byte>> NoFetch(ReadOnlyMemory<byte> hash, int length, CancellationToken token) =>
        throw new InvalidOperationException("Metadata must be available independently.");
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-DirectoryBlocks-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
