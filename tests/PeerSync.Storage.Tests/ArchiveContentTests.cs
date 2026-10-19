using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class ArchiveContentTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ArchiveContentDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => Enumerable.Repeat((byte)7, 24).ToArray();
    private static byte[] FileId => Enumerable.Repeat((byte)8, 16).ToArray();
    private static byte[] Hash => Enumerable.Repeat((byte)9, 32).ToArray();
    private static readonly int[] SplitLengths = [1, 2];
    private static LocalChunk Chunk(long offset = 0, int length = 3) => new(offset, length, Hash);
    public ArchiveContentTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task IncompleteStagingIsInvisibleAndFullActivationSurvivesArchiveRemovalAndReopening()
    {
        Guid contentId, sourceId;
        await using (var index = await PrepareAsync())
        {
            var source = index.ReadArchiveProvenance().Single(); sourceId = source.SourceId;
            var build = await index.BeginArchiveContentAsync(source, Root, FileId, 100, Token); contentId = build.Id;
            index.FindActiveArchiveContent(sourceId).ShouldBeNull(); index.ReadArchiveContentChunks(contentId).ShouldBeEmpty();
            await Should.ThrowAsync<InvalidOperationException>(() => index.ActivateArchiveContentAsync(contentId, Token));
            await index.AppendArchiveContentChunksAsync(contentId, [Chunk()], Token);
            index.ReadArchiveContentChunks(contentId).ShouldBeEmpty(); (await index.ActivateArchiveContentAsync(contentId, Token)).ShouldBeTrue();
            await index.Archive.DeleteAsync([source.Archive.Id], Token);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var active = reopened.FindActiveArchiveContent(sourceId)!;
        active.Id.ShouldBe(contentId); active.RootId.ShouldBe(Root); active.FileId.ShouldBe(FileId); active.Size.ShouldBe(3); active.FileHash.ShouldBe(Hash);
        active.NextOffset.ShouldBe(3); active.ChunkCount.ShouldBe(1); active.VerifiedTicks.ShouldBe(100); active.Active.ShouldBeTrue();
        reopened.ReadArchiveContentChunks(contentId).Single().Hash.ShouldBe(Hash); reopened.ReadArchiveProvenance().Single().Attached.ShouldBeFalse();
        (await reopened.ActivateArchiveContentAsync(contentId, Token)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => reopened.AppendArchiveContentChunksAsync(contentId, [Chunk()], Token));
    }

    [Fact]
    public async Task FailedActivationRollsBackReplacementAndNewBuildRemovesOnlyAbandonedChunks()
    {
        await using var index = await PrepareAsync(); var source = index.ReadArchiveProvenance().Single();
        var original = await ActivateAsync(index, source, [Chunk()]);
        var abandoned = await index.BeginArchiveContentAsync(source, Root, FileId, 200, Token);
        await index.AppendArchiveContentChunksAsync(abandoned.Id, [Chunk(0, 1)], Token);
        var next = await index.BeginArchiveContentAsync(source, Root, FileId, 300, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ActivateArchiveContentAsync(abandoned.Id, Token));
        await index.AppendArchiveContentChunksAsync(next.Id, [Chunk(0, 1), Chunk(1, 2)], Token);
        Sql("CREATE TRIGGER reject_content BEFORE UPDATE ON archive_contents WHEN NEW.active=1 BEGIN SELECT RAISE(ABORT,'test content activation failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.ActivateArchiveContentAsync(next.Id, Token));
        index.FindActiveArchiveContent(source.SourceId)!.Id.ShouldBe(original.Id); index.ReadArchiveContentChunks(original.Id).Count().ShouldBe(1);
        Sql("DROP TRIGGER reject_content;"); await index.ActivateArchiveContentAsync(next.Id, Token);
        index.FindActiveArchiveContent(source.SourceId)!.Id.ShouldBe(next.Id); index.ReadArchiveContentChunks(original.Id).ShouldBeEmpty();
        index.ReadArchiveContentChunks(next.Id).Select(c => c.Length).ShouldBe(SplitLengths);
    }

    [Fact]
    public async Task BoundedBatchesFreezeBuffersAndRequireContiguousOffsetsWithoutPartialCommit()
    {
        await using var index = await PrepareAsync(1001); var source = index.ReadArchiveProvenance().Single();
        byte[] root = Root, file = FileId; Task<ArchiveContentSnapshot> pending = index.BeginArchiveContentAsync(source, root, file, 100, Token);
        root[0] ^= 1; file[0] ^= 1; source.Archive.FileHash![0] ^= 1; var state = await pending;
        state.RootId.ShouldBe(Root); state.FileId.ShouldBe(FileId); state.FileHash.ShouldBe(Hash);
        var first = Enumerable.Range(0, 1000).Select(i => Chunk(i, 1)).ToArray(); Task<int> append = index.AppendArchiveContentChunksAsync(state.Id, first, Token);
        first[0].Hash[0] ^= 1; await append;
        await Should.ThrowAsync<InvalidDataException>(() => index.AppendArchiveContentChunksAsync(state.Id, [Chunk(1000, 1), Chunk(1001, 1)], Token));
        await Should.ThrowAsync<ArgumentException>(() => index.AppendArchiveContentChunksAsync(state.Id, Enumerable.Repeat(Chunk(), 1001).ToArray(), Token));
        await index.AppendArchiveContentChunksAsync(state.Id, [Chunk(1000, 1)], Token); await index.ActivateArchiveContentAsync(state.Id, Token);
        index.ReadArchiveContentChunks(state.Id).Count().ShouldBe(1001); index.ReadArchiveContentChunks(state.Id).First().Hash.ShouldBe(Hash);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("pending")]
    [InlineData("source")]
    public async Task ChangedRootPendingApplicationOrChangedSourceCannotReplacePriorActiveProtection(string change)
    {
        await using var index = await PrepareAsync(); var source = index.ReadArchiveProvenance().Single(); var original = await ActivateAsync(index, source, [Chunk()]);
        var next = await index.BeginArchiveContentAsync(source, Root, FileId, 200, Token); await index.AppendArchiveContentChunksAsync(next.Id, [Chunk()], Token);
        if (change == "root") await index.Meta.UpsertAsync([new("_root-identity", new byte[24])], Token);
        else if (change == "pending") await index.ReserveApplyAsync(new(Guid.NewGuid(), "other", null, [1]), Token);
        else Sql("UPDATE archive_provenance SET size=4;");
        await Should.ThrowAsync<InvalidOperationException>(() => index.ActivateArchiveContentAsync(next.Id, Token));
        index.FindActiveArchiveContent(source.SourceId)!.Id.ShouldBe(original.Id); index.ReadArchiveContentChunks(original.Id).Single().Hash.ShouldBe(Hash);
    }

    [Fact]
    public async Task SourceValidationRejectsSpoofedMetadataAndForeignRootWithoutDiscardingActiveContent()
    {
        await using var index = await PrepareAsync(); var source = index.ReadArchiveProvenance().Single(); var original = await ActivateAsync(index, source, [Chunk()]);
        await Should.ThrowAsync<InvalidOperationException>(() => index.BeginArchiveContentAsync(source with { Archive = source.Archive with { Size = 4 } }, Root, FileId, 200, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => index.BeginArchiveContentAsync(source with { SourceId = Guid.NewGuid() }, Root, FileId, 200, Token));
        await index.Meta.UpsertAsync([new("_root-identity", new byte[24])], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.BeginArchiveContentAsync(source, new byte[24], FileId, 200, Token));
        index.FindActiveArchiveContent(source.SourceId)!.Id.ShouldBe(original.Id);
    }

    [Fact]
    public async Task StableSourceCoverageSurvivesRowDeletionAndDoesNotCoverAnUnrelatedReusedId()
    {
        await using var index = await PrepareAsync(); var source = index.ReadArchiveProvenance().Single(); var content = await ActivateAsync(index, source, [Chunk()]);
        await index.Archive.DeleteAsync([source.Archive.Id], Token);
        await index.Archive.UpsertAsync([source.Archive with { OriginalPath = "other", FileHash = new byte[32] }], Token);
        var nextSource = index.ReadArchiveProvenance().Single(s => s.Attached); nextSource.Archive.Id.ShouldBe(source.Archive.Id);
        var snapshot = await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token); index.CountUnmanifestedReferenceArchives(snapshot.Id).ShouldBe(2);
        await index.AppendBlockReferencesAsync(snapshot.Id, [new(Hash, 3, BlockReferenceKind.Archive)], Token);
        (await index.AppendBlockArchiveContentCoverageAsync(snapshot.Id, content.Id, Token)).ShouldBeTrue();
        index.CountUnmanifestedReferenceArchives(snapshot.Id).ShouldBe(1); index.IsBlockArchiveSourceCovered(snapshot.Id, source.SourceId).ShouldBeTrue();
        index.IsBlockArchiveSourceCovered(snapshot.Id, nextSource.SourceId).ShouldBeFalse();
        await index.ActivateBlockReferenceSnapshotAsync(snapshot.Id, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.AppendBlockArchiveContentCoverageAsync(snapshot.Id, content.Id, Token));
        var next = await index.BeginBlockReferenceSnapshotAsync(Root, 200, Token); index.CountUnmanifestedReferenceArchives(next.Id).ShouldBe(2);
    }

    [Fact]
    public async Task SignedHistoryCoverageCanProtectDetachedSourcesWithoutAdoptingTheirMissingFiles()
    {
        await using var index = await PrepareAsync(); var source = index.ReadArchiveProvenance().Single(); await index.Archive.DeleteAsync([source.Archive.Id], Token);
        var state = await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token); index.CountUnmanifestedReferenceArchives(state.Id).ShouldBe(1);
        await index.AppendBlockReferencesAsync(state.Id, [new(Hash, 3, BlockReferenceKind.History)], Token);
        (await index.AppendBlockArchiveCoverageAsync(state.Id, [new(source.Archive.OriginalPath, source.Archive.ArchivePath, 3, Hash)], Token)).ShouldBe(1);
        index.CountUnmanifestedReferenceArchives(state.Id).ShouldBe(0); index.IsBlockArchiveSourceCovered(state.Id, source.SourceId).ShouldBeTrue();
    }

    [Fact]
    public async Task EmptyVerifiedContentRequiresNoChunksAndCancellationRetainsPriorManifest()
    {
        await using var index = await PrepareAsync(0); var source = index.ReadArchiveProvenance().Single(); var original = await ActivateAsync(index, source, []);
        var next = await index.BeginArchiveContentAsync(source, Root, FileId, 200, Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.ActivateArchiveContentAsync(next.Id, stop.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => index.AppendArchiveContentChunksAsync(next.Id, [], stop.Token));
        index.FindActiveArchiveContent(source.SourceId)!.Id.ShouldBe(original.Id); index.ReadArchiveContentChunks(original.Id).ShouldBeEmpty();
        index.FindActiveArchiveContent(source.SourceId)!.ChunkCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationPreservesCompatibleLegacyCoverageAndExcludesForeignSourceRoots(bool foreign)
    {
        var migrations = new List<DbMigration>();
        foreach (string resource in typeof(FolderIndex).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("PeerSync.Storage.FolderDb.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(11))
        {
            using var stream = typeof(FolderIndex).Assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream);
            migrations.Add(new(migrations.Count + 1, await reader.ReadToEndAsync(Token)));
        }
        Guid snapshotId = Guid.NewGuid();
        await using (var legacy = await Db.OpenAsync(Database, migrations, Token))
            await legacy.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO meta VALUES('_root-identity',$root); INSERT INTO archive VALUES(1,'old','.psp/archive/old',100,3,$hash); "
                    + "INSERT INTO block_reference_snapshots VALUES($id,$root,100,1); INSERT INTO block_reference_archives VALUES($id,1);"
                    + (foreign ? "UPDATE archive_provenance SET root_id=zeroblob(24);" : "");
                command.Parameters.AddWithValue("$root", Root); command.Parameters.AddWithValue("$hash", Hash); command.Parameters.AddWithValue("$id", snapshotId.ToString("N"));
                return command.ExecuteNonQuery();
            }, Token);
        await using var upgraded = await FolderIndex.OpenAsync(Database, Token);
        upgraded.CountUnmanifestedReferenceArchives(snapshotId).ShouldBe(foreign ? 1 : 0);
        Directory.GetFiles(fixture, "*.pre-v11-to-v12-*.bak").Length.ShouldBe(1);
    }

    [Fact]
    public async Task ManifestChunkBudgetCannotBeExceededOrMadeVisibleAsACompleteSource()
    {
        await using var index = await PrepareAsync(ArchiveContentSnapshot.MaximumChunks + 1);
        var source = index.ReadArchiveProvenance().Single(); var state = await index.BeginArchiveContentAsync(source, Root, FileId, 100, Token);
        for (int offset = 0; offset < ArchiveContentSnapshot.MaximumChunks; offset += 1000)
            await index.AppendArchiveContentChunksAsync(state.Id, Enumerable.Range(offset, 1000).Select(i => Chunk(i, 1)).ToArray(), Token);
        await Should.ThrowAsync<InvalidDataException>(() => index.AppendArchiveContentChunksAsync(state.Id, [Chunk(ArchiveContentSnapshot.MaximumChunks, 1)], Token));
        var pending = index.ReadPendingArchiveContents().Single(); pending.ChunkCount.ShouldBe(ArchiveContentSnapshot.MaximumChunks);
        pending.NextOffset.ShouldBe(ArchiveContentSnapshot.MaximumChunks); index.FindActiveArchiveContent(source.SourceId).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => index.ActivateArchiveContentAsync(state.Id, Token));
    }

    private async Task<FolderIndex> PrepareAsync(long size = 3)
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try
        {
            await index.Meta.UpsertAsync([new("_root-identity", Root)], Token);
            await index.Archive.UpsertAsync([new(0, "file", ".psp/archive/file~old", 100, size, Hash)], Token); return index;
        }
        catch { await index.DisposeAsync(); throw; }
    }
    private static async Task<ArchiveContentSnapshot> ActivateAsync(FolderIndex index, ArchiveProvenance source, LocalChunk[] chunks)
    {
        var state = await index.BeginArchiveContentAsync(source, Root, FileId, 100, Token);
        foreach (var batch in chunks.Chunk(1000)) await index.AppendArchiveContentChunksAsync(state.Id, batch, Token);
        await index.ActivateArchiveContentAsync(state.Id, Token); return index.FindActiveArchiveContent(source.SourceId)!;
    }
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-ArchiveContentDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
