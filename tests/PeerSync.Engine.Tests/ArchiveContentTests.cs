using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Storage.FolderDb;
using PeerSync.Crypto;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class ArchiveContentTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ArchiveContentNative-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly List<string> links = [];
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Device => Enumerable.Repeat((byte)1, 32).ToArray();
    private static byte[] Bytes { get { var bytes = new byte[2 * 1024 * 1024]; new Random(42).NextBytes(bytes); return bytes; } }
    public ArchiveContentTests() => Directory.CreateDirectory(Root);

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("replaced")]
    public async Task VerifiedIndependentContentSurvivesRowRemovalAndSubsequentArchiveChanges(string change)
    {
        Guid contentId, sourceId;
        await using (var index = await PrepareAsync())
        {
            var row = await AddAsync(index, Bytes); sourceId = index.ReadArchiveProvenance().Single().SourceId;
            var result = await Builder(index).RefreshAsync(Token); result.AllKnownSourcesCovered.ShouldBeTrue();
            var content = index.FindActiveArchiveContent(sourceId)!; contentId = content.Id;
            content.ChunkCount.ShouldBeGreaterThan(1); content.Size.ShouldBe(Bytes.Length); ReadContent(index, content).ShouldBe(Bytes);
            foreach (var chunk in index.ReadArchiveContentChunks(contentId))
                index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(chunk.Hash)).Kinds.ShouldBe(BlockReferenceKind.Archive);
            await index.Archive.DeleteAsync([row.Id], Token);
            if (change == "missing") File.Delete(Disk(row.ArchivePath));
            else
            {
                if (change == "replaced") File.Move(Disk(row.ArchivePath), Disk(".psp/archive/unknown-old"));
                File.WriteAllText(Disk(row.ArchivePath), "unknown private bytes");
            }
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var refreshed = await Builder(reopened).RefreshAsync(Token); refreshed.AllKnownSourcesCovered.ShouldBeTrue(); refreshed.ArchivesWithoutManifest.ShouldBe(0);
        var retained = reopened.FindActiveArchiveContent(sourceId)!; retained.Id.ShouldBe(contentId); ReadContent(reopened, retained).ShouldBe(Bytes);
        reopened.ReadArchiveProvenance().Single().Attached.ShouldBeFalse();
        if (change != "missing") File.ReadAllText(Disk(".psp/archive/raw")).ShouldBe("unknown private bytes");
        if (change == "replaced") File.ReadAllBytes(Disk(".psp/archive/unknown-old")).ShouldBe(Bytes);
    }

    [Theory]
    [InlineData("archive-blocks")]
    [InlineData("archive-content-begun")]
    [InlineData("archive-content-batch")]
    [InlineData("archive-content-before-activate")]
    [InlineData("archive-content-activated")]
    public async Task EveryCaptureBoundaryRetainsPriorSnapshotAndRecoversAfterDatabaseReopening(string boundary)
    {
        Guid prior;
        await using (var index = await PrepareAsync())
        {
            prior = (await Builder(index).RefreshAsync(Token)).SnapshotId!.Value; await AddAsync(index, Bytes);
            var builder = new BlockReferences(Root, index, keys, Device) { Checkpoint = stage => { if (stage == boundary) throw new InvalidOperationException("process boundary"); } };
            await Should.ThrowAsync<InvalidOperationException>(() => builder.RefreshAsync(Token)); index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(prior);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); (await Builder(reopened).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
        ReadContent(reopened, reopened.FindActiveArchiveContent(reopened.ReadArchiveProvenance().Single().SourceId)!).ShouldBe(Bytes);
    }

    [Fact]
    public async Task CompleteDurableStagingCanRecoverFromVerifiedBlocksAfterTheOriginalDisappears()
    {
        Guid id, sourceId; ArchiveRow row;
        await using (var index = await PrepareAsync())
        {
            row = await AddAsync(index, Bytes); sourceId = index.ReadArchiveProvenance().Single().SourceId;
            var builder = new BlockReferences(Root, index, keys, Device)
            { Checkpoint = stage => { if (stage == "archive-content-before-activate") throw new InvalidOperationException("process boundary"); } };
            await Should.ThrowAsync<InvalidOperationException>(() => builder.RefreshAsync(Token)); var staged = index.ReadPendingArchiveContents().Single(); id = staged.Id;
            staged.NextOffset.ShouldBe(Bytes.Length); index.FindActiveArchiveContent(sourceId).ShouldBeNull();
            File.Delete(Disk(row.ArchivePath)); await index.Archive.DeleteAsync([row.Id], Token);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); (await Builder(reopened).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
        reopened.FindActiveArchiveContent(sourceId)!.Id.ShouldBe(id); reopened.ReadPendingArchiveContents().ShouldBeEmpty();
        ReadContent(reopened, reopened.FindActiveArchiveContent(sourceId)!).ShouldBe(Bytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("locked")]
    [InlineData("linked")]
    [InlineData("reparse")]
    [InlineData("foreign-root")]
    public async Task UnverifiableSourcesRemainUncoveredAndUnknownDataIsNeverChanged(string failure)
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "retained version"u8.ToArray());
        var source = index.ReadArchiveProvenance().Single(); FileStream? held = null;
        if (failure == "missing") File.Delete(Disk(row.ArchivePath));
        else if (failure == "length") File.WriteAllText(Disk(row.ArchivePath), "short");
        else if (failure == "hash") File.WriteAllBytes(Disk(row.ArchivePath), new byte[row.Size]);
        else if (failure == "locked") held = File.Open(Disk(row.ArchivePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        else if (failure == "linked")
        {
            using var tree = new SafeFileTree(Root); using var archive = tree.Directory(".psp/archive", privatePath: true); using var destination = tree.Directory("");
            using var file = archive.OpenFile("raw", mutation: true); destination.LinkHere(file, "unknown-link");
        }
        else if (failure == "reparse")
        {
            string outside = Path.Combine(fixture, "outside"); Directory.Move(Disk(".psp/archive"), outside);
            Directory.CreateSymbolicLink(Disk(".psp/archive"), outside); links.Add(Disk(".psp/archive"));
        }
        else if (failure == "foreign-root") Sql("UPDATE archive_provenance SET root_id=zeroblob(24);");
        try
        {
            var result = await Builder(index).RefreshAsync(Token); result.ArchivesWithoutManifest.ShouldBe(1); result.AllKnownSourcesCovered.ShouldBeFalse();
            index.FindActiveArchiveContent(source.SourceId).ShouldBeNull(); index.ReadArchiveProvenance().Single().SourceId.ShouldBe(source.SourceId);
            index.ReadBlockReferences().Any(r => r.Kinds.HasFlag(BlockReferenceKind.Current)).ShouldBeTrue();
        }
        finally { held?.Dispose(); }
        if (failure == "missing") File.Exists(Disk(row.ArchivePath)).ShouldBeFalse();
        else if (failure == "length") File.ReadAllText(Disk(row.ArchivePath)).ShouldBe("short");
        else if (failure == "hash") File.ReadAllBytes(Disk(row.ArchivePath)).ShouldBe(new byte[row.Size]);
        else File.ReadAllBytes(Disk(row.ArchivePath)).ShouldBe("retained version"u8.ToArray());
        if (failure == "linked") File.ReadAllText(Disk("unknown-link")).ShouldBe("retained version");
    }

    [Fact]
    public async Task NativeReadGuardRemainsHeldUntilTheCompleteManifestIsDurable()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, Bytes); bool checkedGuard = false;
        var builder = new BlockReferences(Root, index, keys, Device)
        {
            Checkpoint = stage =>
            {
                if (stage != "archive-content-before-activate") return;
                Should.Throw<IOException>(() => File.Open(Disk(row.ArchivePath), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
                Should.Throw<IOException>(() => File.Move(Disk(row.ArchivePath), Disk(".psp/archive/moved"))); checkedGuard = true;
            },
        };
        (await builder.RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue(); checkedGuard.ShouldBeTrue();
        File.ReadAllBytes(Disk(row.ArchivePath)).ShouldBe(Bytes);
    }

    [Fact]
    public async Task SignedHistoryProtectsDetachedArchiveWithoutCreatingAnUnnecessaryContentManifest()
    {
        await using var index = await PrepareAsync(); byte[] original = File.ReadAllBytes(Disk("current")); File.WriteAllText(Disk("current"), "new current bytes");
        using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
        var row = index.Archive.ReadAll().Single(); var source = index.ReadArchiveProvenance().Single(); File.Delete(Disk(row.ArchivePath)); await index.Archive.DeleteAsync([row.Id], Token);
        var result = await Builder(index).RefreshAsync(Token); result.AllKnownSourcesCovered.ShouldBeTrue();
        index.FindActiveArchiveContent(source.SourceId).ShouldBeNull(); index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash(original))).Kinds.ShouldBe(BlockReferenceKind.History);
    }

    [Fact]
    public async Task EmptyReadOnlyArchiveIsRepresentedWithoutInventingABlock()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, []); File.SetAttributes(Disk(row.ArchivePath), FileAttributes.ReadOnly);
        try
        {
            (await Builder(index).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
            var content = index.FindActiveArchiveContent(index.ReadArchiveProvenance().Single().SourceId)!;
            content.Size.ShouldBe(0); content.FileHash.ShouldBe(Hash([])); content.ChunkCount.ShouldBe(0); index.ReadArchiveContentChunks(content.Id).ShouldBeEmpty();
            index.ReadBlockObjects().Count().ShouldBe(1);
        }
        finally { File.SetAttributes(Disk(row.ArchivePath), FileAttributes.Normal); }
    }

    [Fact]
    public async Task CatalogFailurePreservesThePriorSnapshotAndOwnedProofsRecoverBeforeRetry()
    {
        await using var index = await PrepareAsync(); var before = await Builder(index).RefreshAsync(Token); await AddAsync(index, Bytes);
        Sql("CREATE TRIGGER reject_archive_block BEFORE INSERT ON block_objects BEGIN SELECT RAISE(ABORT,'private SQL source text'); END;");
        await Should.ThrowAsync<SqliteException>(() => Builder(index).RefreshAsync(Token)); index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value);
        index.FindActiveArchiveContent(index.ReadArchiveProvenance().Single().SourceId).ShouldBeNull();
        Directory.GetFiles(Disk(".psp/block-captures"), "*.capture").Length.ShouldBeGreaterThan(0);
        Sql("DROP TRIGGER reject_archive_block;"); (await Builder(index).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
        ReadContent(index, index.FindActiveArchiveContent(index.ReadArchiveProvenance().Single().SourceId)!).ShouldBe(Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationFailureKeepsOldReferencesAndRecoveryRejectsMissingOrCorruptStagedBlocks(bool corrupt)
    {
        await using var index = await PrepareAsync(); var before = await Builder(index).RefreshAsync(Token); var row = await AddAsync(index, Bytes);
        Sql("CREATE TRIGGER reject_archive_content BEFORE UPDATE ON archive_contents WHEN NEW.active=1 BEGIN SELECT RAISE(ABORT,'private SQL activation text'); END;");
        await Should.ThrowAsync<SqliteException>(() => Builder(index).RefreshAsync(Token)); index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value);
        Sql("DROP TRIGGER reject_archive_content;"); var staged = index.ReadPendingArchiveContents().Single(); var first = index.ReadPendingArchiveContentChunks(staged.Id).First();
        string name = Convert.ToHexString(first.Hash), blockPath = Disk(".psp/blocks/" + name[..2] + "/" + name);
        if (corrupt)
        {
            // Captured blocks retain the staging Hidden attribute. OPEN_EXISTING
            // permits same-object corruption without CREATE_ALWAYS rejecting it.
            using var altered = File.Open(blockPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            altered.Write(new byte[first.Length]); altered.Flush(flushToDisk: true);
        }
        else File.Delete(blockPath);
        File.Delete(Disk(row.ArchivePath));
        (await Builder(index).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeFalse(); index.FindActiveArchiveContent(staged.SourceId).ShouldBeNull();
        index.ReadPendingArchiveContents().Single().Id.ShouldBe(staged.Id);
        if (corrupt) File.ReadAllBytes(blockPath).ShouldBe(new byte[first.Length]);
    }

    [Fact]
    public async Task CancellationDuringManifestStagingRetainsProtectionAndReleasesBothGates()
    {
        await using var index = await PrepareAsync(); var before = await Builder(index).RefreshAsync(Token); await AddAsync(index, Bytes);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var builder = new BlockReferences(Root, index, keys, Device) { Checkpoint = stage => { if (stage == "archive-content-batch") stop.Cancel(); } };
        await Should.ThrowAsync<OperationCanceledException>(() => builder.RefreshAsync(stop.Token)); index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(before.SnapshotId!.Value);
        (await Builder(index).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
    }

    [Fact]
    public async Task IncompletePendingManifestProtectsItsCapturedPrefixWhenTheArchiveIsUnavailable()
    {
        await using var index = await PrepareAsync(); var data = Bytes; var row = await AddAsync(index, data); var source = index.ReadArchiveProvenance().Single();
        using (var tree = new SafeFileTree(Root))
        using (var parent = tree.Directory(".psp/archive", privatePath: true))
        using (var file = parent.OpenReadGuard("raw"))
        using (var blocks = new PublishedBlocks(tree))
        {
            GuardedFile.Hash(file, Token).ShouldBe(Hash(data));
            blocks.Put(data.AsSpan(0, 1024), Hash(data.AsSpan(0, 1024)), Token); blocks.Put(data.AsSpan(1024), Hash(data.AsSpan(1024)), Token);
            await blocks.FlushAsync(index, Token);
            var stage = await index.BeginArchiveContentAsync(source, tree.Identity.ToArray(), BlockCaptureProof.Identity(file), DateTime.UtcNow.Ticks, Token);
            await index.AppendArchiveContentChunksAsync(stage.Id, [new(0, 1024, Hash(data.AsSpan(0, 1024)))], Token);
        }
        File.Delete(Disk(row.ArchivePath)); var refreshed = await Builder(index).RefreshAsync(Token);
        refreshed.AllKnownSourcesCovered.ShouldBeFalse(); index.FindActiveArchiveContent(source.SourceId).ShouldBeNull();
        index.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash(data.AsSpan(0, 1024)))).Kinds.ShouldBe(BlockReferenceKind.Archive);
        index.ReadPendingArchiveContents().Single().NextOffset.ShouldBe(1024);
    }

    [Fact]
    public async Task DiagnosticsExposeCountsAndCodesWithoutArchiveNamesHashesOrContent()
    {
        await using var index = await PrepareAsync(); var row = await AddAsync(index, "private archive bytes"u8.ToArray());
        using (var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "archive-content", debugEnabled: true))
        {
            (await new BlockReferences(Root, index, keys, Device, journal).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
            await index.Archive.UpsertAsync([row with { OriginalPath = "other", ArchivePath = ".psp/archive/missing-private" }], Token);
            (await new BlockReferences(Root, index, keys, Device, journal).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeFalse();
        }
        string text = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        text.ShouldContain("archive.content_captured"); text.ShouldContain("archive.content_deferred");
        text.ShouldNotContain(Root); text.ShouldNotContain("missing-private"); text.ShouldNotContain("private archive bytes"); text.ShouldNotContain(Convert.ToHexString(row.FileHash!));
    }

    private BlockReferences Builder(FolderIndex index) => new(Root, index, keys, Device);
    private async Task<FolderIndex> PrepareAsync()
    {
        File.WriteAllText(Disk("current"), "current bytes"); var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private async Task<ArchiveRow> AddAsync(FolderIndex index, byte[] bytes)
    {
        Directory.CreateDirectory(Disk(".psp/archive")); File.WriteAllBytes(Disk(".psp/archive/raw"), bytes);
        await index.Archive.UpsertAsync([new(0, "private-original", ".psp/archive/raw", DateTime.UtcNow.Ticks, bytes.Length, Hash(bytes))], Token);
        return index.Archive.ReadAll().Single();
    }
    private byte[] ReadContent(FolderIndex index, ArchiveContentSnapshot content)
    {
        using var blocks = new PublishedBlocks(Root, index); using var output = new MemoryStream();
        foreach (var chunk in index.ReadArchiveContentChunks(content.Id)) output.Write(blocks.TryRead(chunk.Hash, chunk.Length, Token)!);
        return output.ToArray();
    }
    private static byte[] Hash(ReadOnlySpan<byte> bytes) => Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose()
    {
        keys.Dispose();
        foreach (string link in links) if (Directory.Exists(link)) Directory.Delete(link);
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-ArchiveContentNative-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
