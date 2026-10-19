using System.ComponentModel;
using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class PublishedBlocksTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-PublishedBlocks-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] device = new byte[32];
    private readonly List<string> links = [];
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PublishedBlocksTests() { device[0] = 1; Directory.CreateDirectory(Root); }

    [Theory]
    [InlineData(1)]
    [InlineData(300000)]
    [InlineData(3000000)]
    public async Task PublishedBytesSurviveUserOverwriteDeletionAndDatabaseReopening(int length)
    {
        byte[] original = RandomNumberGenerator.GetBytes(length); File.WriteAllBytes(Disk("file"), original);
        LocalFileRow published;
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            using var scanner = Scan(index); (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty();
            published = index.ReadIndexBatch(0).Records.Single(); published.ContentReady.ShouldBeTrue();
            File.WriteAllBytes(Disk("file"), new byte[length]); await scanner.ScanAsync(Token);
            File.Delete(Disk("file")); await scanner.ScanAsync(Token);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        using var blocks = new PublishedBlocks(Root, reopened); var version = VersionView.Open(published.Record!, keys, device);
        using var all = new MemoryStream();
        foreach (var chunk in version.ExportBody().Chunks) all.Write(blocks.TryRead(chunk.Hash.Span, checked((int)chunk.Length), Token)!);
        all.ToArray().ShouldBe(original); File.Exists(Disk("file")).ShouldBeFalse();
    }

    [Fact]
    public async Task VerifiedStoreSuppliesAssemblyAfterMutableSourcesAndChunkHintsDisappear()
    {
        byte[] original = "published independent bytes"u8.ToArray(); File.WriteAllBytes(Disk("file"), original);
        await using var index = await FolderIndex.OpenAsync(Database, Token); using var scanner = Scan(index); await scanner.ScanAsync(Token);
        var version = VersionView.Open(index.LocalFiles.Find("file")!.Record!, keys, device);
        File.Delete(Disk("file")); await scanner.ScanAsync(Token); index.Chunks.Count().ShouldBe(0);
        using var tree = new SafeFileTree(Root); using var destination = new MemoryStream();
        var result = await new ChunkAssembler(tree, index).AssembleAsync(version, destination,
            (_, _, _) => throw new InvalidOperationException("Published cache must avoid network"), Token);
        result.LocalBytes.ShouldBe(original.Length); result.RemoteBytes.ShouldBe(0); destination.ToArray().ShouldBe(original);
    }

    [Fact]
    public async Task LegacyReadinessBackfillPreservesSignedBodyVectorAndContentWhileAdvancingPublicationSequence()
    {
        File.WriteAllText(Disk("file"), "legacy bytes"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = Scan(index); await scanner.ScanAsync(Token); var before = index.LocalFiles.Find("file")!;
        await index.LocalFiles.UpsertAsync([before with { ContentReady = false }], Token); index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        foreach (string cache in Directory.GetFiles(Disk(".psp/blocks"), "*", SearchOption.AllDirectories)) File.Delete(cache);
        var result = await scanner.ScanAsync(Token); result.Hashed.ShouldBe(1); result.Updated.ShouldBe(1);
        var after = index.ReadIndexBatch(0).Records.Single(); after.ContentReady.ShouldBeTrue();
        after.Vector.ShouldBe(before.Vector); after.Record!.Body.ToArray().ShouldBe(before.Record!.Body.ToArray());
        after.Record.Signature.ToArray().ShouldBe(before.Record.Signature.ToArray()); after.LocalSequence.ShouldBeGreaterThan(before.LocalSequence);
        (await scanner.ScanAsync(Token)).Hashed.ShouldBe(0);
        using var blocks = new PublishedBlocks(Root, index); blocks.TryRead(Hash("legacy bytes"u8), "legacy bytes"u8.Length, Token).ShouldBe("legacy bytes"u8.ToArray());
    }

    [Fact]
    public async Task ReadOnlyLocalChangesAreNotCapturedOrPublishedAndEmptyRwFilesNeedNoBlock()
    {
        File.WriteAllText(Disk("local"), "local RO edit"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        using (var scanner = new Scanner(Root, index, readOnly, device)) await scanner.ScanAsync(Token);
        index.ReadIndexBatch(0).Records.ShouldBeEmpty(); Directory.Exists(Disk(".psp/blocks")).ShouldBeFalse();
        File.Delete(Disk("local")); File.WriteAllBytes(Disk("empty"), []);
        using (var scanner = Scan(index)) await scanner.ScanAsync(Token);
        index.LocalFiles.Find("empty")!.ContentReady.ShouldBeTrue(); index.ReadIndexBatch(0).Records.Single(r => r.Path == "empty").Size.ShouldBe(0);
        Directory.Exists(Disk(".psp/blocks")).ShouldBeFalse();
    }

    [Fact]
    public async Task ParallelIdenticalCapturesDeduplicateAndReadBuffersAreIndependent()
    {
        await using var index = await BindAsync(); using var tree = new SafeFileTree(Root); using var blocks = new PublishedBlocks(tree);
        byte[] bytes = RandomNumberGenerator.GetBytes(100000), hash = Hash(bytes);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => blocks.Put(bytes, hash, Token), Token)));
        Directory.GetFiles(Disk(".psp/blocks"), "*", SearchOption.AllDirectories).Length.ShouldBe(1);
        byte[] first = blocks.TryRead(hash, bytes.Length, Token)!; first[0] ^= 1;
        blocks.TryRead(hash, bytes.Length, Token).ShouldBe(bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingCorruptOrWrongLengthEntriesArePreservedAndNeverAccepted(bool wrongLength)
    {
        await using var index = await BindAsync(); byte[] bytes = "verified bytes"u8.ToArray(), hash = Hash(bytes);
        string target = CachePath(hash); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        byte[] unknown = new byte[bytes.Length + (wrongLength ? 1 : 0)]; File.WriteAllBytes(target, unknown);
        using var tree = new SafeFileTree(Root); using var blocks = new PublishedBlocks(tree);
        Should.Throw<CryptographicException>(() => blocks.Put(bytes, hash, Token));
        Should.Throw<CryptographicException>(() => blocks.TryRead(hash, bytes.Length, Token));
        File.ReadAllBytes(target).ShouldBe(unknown); Directory.GetFiles(Path.GetDirectoryName(target)!, "*.!psp").ShouldBeEmpty();
    }

    [Fact]
    public async Task ACacheFailureWithholdsNewPublicationAndDoesNotBlockOtherPaths()
    {
        byte[] bytes = "bad cache collision"u8.ToArray(), hash = Hash(bytes);
        await using var index = await BindAsync(); string target = CachePath(hash); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, new byte[bytes.Length]); File.WriteAllBytes(Disk("bad"), bytes); File.WriteAllText(Disk("good"), "good bytes");
        using var scanner = Scan(index); var result = await scanner.ScanAsync(Token);
        result.Problems.Single().RelativePath.ShouldBe("bad"); result.Problems.Single().Code.ShouldBe("published-content-verification-failed");
        index.ReadIndexBatch(0).Records.Select(r => r.Path).ShouldBe(["good"]);
        index.LocalFiles.Find("bad").ShouldBeNull(); File.ReadAllBytes(target).ShouldBe(new byte[bytes.Length]);
    }

    [Fact]
    public async Task CacheReparsePathsCannotWriteOrReadExternalFiles()
    {
        await using var index = await BindAsync(); string outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Disk(".psp")); Directory.CreateSymbolicLink(Disk(".psp/blocks"), outside); links.Add(Disk(".psp/blocks"));
        byte[] bytes = "private bytes"u8.ToArray(); using var blocks = new PublishedBlocks(Root, index);
        Should.Throw<IOException>(() => blocks.TryRead(Hash(bytes), bytes.Length, Token));
        File.WriteAllBytes(Disk("file"), bytes); using var scanner = Scan(index); (await scanner.ScanAsync(Token)).Problems.ShouldNotBeEmpty();
        index.ReadIndexBatch(0).Records.ShouldBeEmpty(); Directory.GetFileSystemEntries(outside).ShouldBeEmpty();
    }

    [Fact]
    public async Task RootReplacementCannotServeTheOldIndexFromAnotherRoot()
    {
        await using var index = await BindAsync(); Directory.Move(Root, Path.Combine(fixture, "old-root")); Directory.CreateDirectory(Root);
        Should.Throw<IOException>(() => new PublishedBlocks(Root, index));
    }

    [Fact]
    public async Task InvalidOrCancelledCapturesCannotInstallDataAndMissingReadsReturnNull()
    {
        await using var index = await BindAsync(); using var tree = new SafeFileTree(Root); using var blocks = new PublishedBlocks(tree);
        byte[] bytes = "cancelled"u8.ToArray(), hash = Hash(bytes);
        blocks.TryRead(hash, bytes.Length, Token).ShouldBeNull();
        Should.Throw<ArgumentException>(() => blocks.TryRead(new byte[31], 1, Token));
        Should.Throw<ArgumentOutOfRangeException>(() => blocks.TryRead(hash, 0, Token));
        Should.Throw<ArgumentOutOfRangeException>(() => blocks.TryRead(hash, PublishedBlocks.MaximumBlockBytes + 1, Token));
        Should.Throw<CryptographicException>(() => blocks.Put(bytes, new byte[32], Token));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token); cancelled.Cancel();
        Should.Throw<OperationCanceledException>(() => blocks.Put(bytes, hash, cancelled.Token));
        Directory.Exists(Disk(".psp/blocks")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("flushed")]
    [InlineData("installed")]
    public async Task InterruptedCaptureLeavesOnlyIndependentVerifiedDataAndCanBeRetried(string boundary)
    {
        await using var index = await BindAsync(); byte[] bytes = "interrupted capture"u8.ToArray(), hash = Hash(bytes);
        using var tree = new SafeFileTree(Root);
        using (var blocks = new PublishedBlocks(tree) { Checkpoint = stage => { if (stage == boundary) throw new IOException("stop"); } })
            Should.Throw<IOException>(() => blocks.Put(bytes, hash, Token));
        Directory.GetFiles(Disk(".psp/blocks"), "*.!psp", SearchOption.AllDirectories).ShouldBeEmpty();
        using var reopened = new PublishedBlocks(tree); reopened.Put(bytes, hash, Token);
        reopened.TryRead(hash, bytes.Length, Token).ShouldBe(bytes); index.ReadIndexBatch(0).Records.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("flushed")]
    public async Task CancellationDuringCaptureRemovesOnlyTheOwnedStagingObject(string boundary)
    {
        await using var index = await BindAsync(); byte[] bytes = "cancel inside write"u8.ToArray(), hash = Hash(bytes);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var tree = new SafeFileTree(Root);
        using var blocks = new PublishedBlocks(tree) { Checkpoint = stage => { if (stage == boundary) cancelled.Cancel(); } };
        Should.Throw<OperationCanceledException>(() => blocks.Put(bytes, hash, cancelled.Token));
        Directory.GetFiles(Disk(".psp/blocks"), "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task MutationDuringHashingCannotPublishAPartialCapturedVersion()
    {
        await using var index = await BindAsync(); byte[] bytes = RandomNumberGenerator.GetBytes(600000); File.WriteAllBytes(Disk("file"), bytes);
        using var root = new NativeRoot(Root); using var blocks = new PublishedBlocks(root); bool changed = false;
        var result = await FileHasher.HashRelativeAsync(root, "file", capture: (chunk, hash) =>
        {
            blocks.Put(chunk, hash, Token);
            if (changed) return; changed = true;
            File.WriteAllBytes(Disk("file"), new byte[bytes.Length]); File.SetLastWriteTimeUtc(Disk("file"), DateTime.UtcNow.AddSeconds(1));
        }, cancellationToken: Token);
        changed.ShouldBeTrue(); result.ShouldBeNull(); index.ReadIndexBatch(0).Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task IncomingFilesCaptureBeforeIndexCompletionAndCanBeServedAfterTheirTargetIsEdited()
    {
        await using var index = await BindAsync(); byte[] bytes = "incoming bytes"u8.ToArray(), hash = Hash(bytes), remote = new byte[32]; remote[0] = 2;
        long now = DateTime.UtcNow.Ticks;
        var body = new FileRecordBody { Path = "incoming", Type = 0, Size = (ulong)bytes.Length, MtimeTicks = now, ChangedTicks = now,
            ModifiedBy = ByteString.CopyFrom(remote), FileHash = ByteString.CopyFrom(hash) };
        body.Vv.Add(new VvEntry { Device = 2, Counter = 1 }); body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(hash), Length = (uint)bytes.Length });
        await new NeededSet(index, keys, device).ApplyIndexUpdateAsync(remote, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
        bool checkedBeforeCommit = false;
        var applier = new FileApplier(Root, index, keys, device) { Checkpoint = stage =>
        {
            if (stage != "before-commit") return;
            using var blocks = new PublishedBlocks(Root, index); blocks.TryRead(hash, bytes.Length, Token).ShouldBe(bytes); checkedBeforeCommit = true;
            index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        } };
        (await applier.ApplyAsync("incoming", (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes), Token)).ShouldBe(FileApplyOutcome.Applied);
        checkedBeforeCommit.ShouldBeTrue(); index.ReadIndexBatch(0).Records.Single().ContentReady.ShouldBeTrue();
        File.WriteAllText(Disk("incoming"), "new user data"); using var cache = new PublishedBlocks(Root, index);
        cache.TryRead(hash, bytes.Length, Token).ShouldBe(bytes);
    }

    [Fact]
    public async Task ReadGuardsAllowMultipleReadersAndExcludeWritersOrDeletion()
    {
        await using var index = await BindAsync(); byte[] bytes = "guarded content"u8.ToArray(), hash = Hash(bytes);
        using var tree = new SafeFileTree(Root); using var blocks = new PublishedBlocks(tree); blocks.Put(bytes, hash, Token);
        string name = Convert.ToHexString(hash); using var parent = tree.Directory(".psp/blocks/" + name[..2], privatePath: true);
        using var first = parent.OpenReadGuard(name); using var second = parent.OpenReadGuard(name);
        Should.Throw<IOException>(() =>
        {
            using var writer = File.OpenHandle(CachePath(hash), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }).HResult.ShouldBe(unchecked((int)0x80070020));
        Should.Throw<IOException>(() => File.Delete(CachePath(hash)));
        blocks.TryRead(hash, bytes.Length, Token).ShouldBe(bytes);
    }

    [Fact]
    public async Task AnExistingHardlinkToMutableUserDataCannotBecomePublishedContent()
    {
        await using var index = await BindAsync(); byte[] bytes = "mutable linked original"u8.ToArray(), hash = Hash(bytes);
        File.WriteAllBytes(Disk("file"), bytes); string name = Convert.ToHexString(hash);
        using var tree = new SafeFileTree(Root);
        using (var destination = tree.Directory(".psp/blocks/" + name[..2], create: true, privatePath: true))
        using (var parent = tree.Directory(""))
        using (var file = parent.OpenFile("file", mutation: true)) destination.LinkHere(file, name);
        using var blocks = new PublishedBlocks(tree);
        Should.Throw<IOException>(() => blocks.Put(bytes, hash, Token));
        Should.Throw<IOException>(() => blocks.TryRead(hash, bytes.Length, Token));
        using var scanner = Scan(index); (await scanner.ScanAsync(Token)).Problems.Single().RelativePath.ShouldBe("file");
        index.ReadIndexBatch(0).Records.ShouldBeEmpty(); File.ReadAllBytes(Disk("file")).ShouldBe(bytes);
        File.ReadAllBytes(CachePath(hash)).ShouldBe(bytes);
    }

    private async Task<FolderIndex> BindAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = Scan(index); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private Scanner Scan(FolderIndex index) => new(Root, index, keys, device);
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private string CachePath(byte[] hash) { string hex = Convert.ToHexString(hash); return Disk(".psp/blocks/" + hex[..2] + "/" + hex); }
    private static byte[] Hash(ReadOnlySpan<byte> bytes) => Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-PublishedBlocks-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        foreach (string link in links) Directory.Delete(link);
        Directory.Delete(resolved, recursive: true);
    }
}
