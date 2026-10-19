using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class MetadataCleanupTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-MetadataCleanupDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => Enumerable.Repeat((byte)1, 24).ToArray();
    private static byte[] Hash => Enumerable.Repeat((byte)2, 32).ToArray();
    private static byte[] FileId => Enumerable.Repeat((byte)3, 16).ToArray();
    private static long Now => 200 * TimeSpan.TicksPerDay;
    public MetadataCleanupTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task LargeManifestDrainsInBoundedDurableBatchesAndDoesNotChangeByteReferences()
    {
        Guid source, content, snapshot;
        await using (var index = await PrepareAsync())
        {
            (source, content) = await AddAsync(index, 1001, detach: true); await ReleaseAsync(index, source); snapshot = await SnapshotAsync(index);
            var result = await index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token);
            result.Mutations.ShouldBe(1000); result.ChunkRowsRemoved.ShouldBe(999); result.MoreWork.ShouldBeTrue(); Count("archive_content_chunks").ShouldBe(2);
            index.FindActiveArchiveContent(source).ShouldBeNull(); index.ReadArchiveContentChunks(content).ShouldBeEmpty();
            index.ReadBlockReferences().Single().Kinds.ShouldBe(BlockReferenceKind.Current); index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(snapshot);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var done = await reopened.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token);
        done.ChunkRowsRemoved.ShouldBe(2); done.ManifestsRemoved.ShouldBe(1); done.SourcesRemoved.ShouldBe(1); done.MoreWork.ShouldBeFalse();
        reopened.ReadArchiveProvenance().ShouldBeEmpty(); Count("archive_policy_releases").ShouldBe(0); reopened.ReadBlockReferences().Single().Hash.ShouldBe(Hash);
    }

    [Fact]
    public async Task AttachedArchivesKeepTheirOriginalRowsAndSmallProvenanceWhileReleasedContentExpires()
    {
        await using var index = await PrepareAsync(); var (source, _) = await AddAsync(index, 1); var before = index.Archive.ReadAll().Single();
        await ReleaseAsync(index, source); Guid snapshot = await SnapshotAsync(index);
        var result = await index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token);
        result.ManifestsRemoved.ShouldBe(1); result.SourcesRemoved.ShouldBe(0); result.MoreWork.ShouldBeFalse();
        index.Archive.ReadAll().Single().ArchivePath.ShouldBe(before.ArchivePath); index.ReadArchiveProvenance().Single().Attached.ShouldBeTrue();
        index.IsArchiveSourceReleased(source, Root).ShouldBeTrue();
    }

    [Fact]
    public async Task ReactivatedHistoryCanRecaptureANewManifestWhileAnOldPartialDrainFinishes()
    {
        await using var index = await PrepareAsync(); var (source, old) = await AddAsync(index, 1001); await ReleaseAsync(index, source); Guid snapshot = await SnapshotAsync(index);
        (await index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, limit: 3, cancellationToken: Token)).Mutations.ShouldBe(3); Count("archive_content_chunks").ShouldBe(999);
        var marker = new MetaRow("_history-" + new string('A', 64), [4, 1]); await index.Meta.UpsertAsync([marker], Token);
        var row = index.Archive.ReadAll().Single(); await index.AccountExistingHistoryAsync(marker, new(Root, FileId, row.OriginalPath, row.ArchivePath, row.Size, Hash, Now, false), Token);
        var provenance = index.ReadArchiveProvenance().Single(); var replacement = await index.BeginArchiveContentAsync(provenance, Root, FileId, Now, Token);
        await ChunksAsync(index, replacement.Id, 1001); await index.ActivateArchiveContentAsync(replacement.Id, Token);
        Count("archive_content_chunks").ShouldBe(2000); index.FindActiveArchiveContent(source)!.Id.ShouldBe(replacement.Id);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ActivateArchiveContentAsync(old, Token));
        snapshot = await SnapshotAsync(index, replacement.Id);
        var result = await index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token);
        result.ChunkRowsRemoved.ShouldBe(999); result.ManifestsRemoved.ShouldBe(1); result.SourcesRemoved.ShouldBe(0); result.MoreWork.ShouldBeFalse();
        index.ReadArchiveContentChunks(replacement.Id).Count().ShouldBe(1001); index.Meta.Find(marker.Key).ShouldNotBeNull();
    }

    [Fact]
    public async Task UnreleasedDetachedSourcesDoNotLoseContentByAbsenceAlone()
    {
        await using var index = await PrepareAsync(); var (source, content) = await AddAsync(index, 1, detach: true); Guid snapshot = await SnapshotAsync(index, content);
        (await index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token)).Mutations.ShouldBe(0);
        index.FindActiveArchiveContent(source)!.Id.ShouldBe(content); index.ReadArchiveProvenance().Single().Attached.ShouldBeFalse();
    }

    [Fact]
    public async Task PendingAndForeignRecoveryMetadataRemainProtected()
    {
        await using var index = await PrepareAsync(); var (source, content) = await AddAsync(index, 1); await ReleaseAsync(index, source);
        var row = index.Archive.ReadAll().Single(); var witness = new ArchiveRetirement(Guid.NewGuid(), row, Root, FileId, ArchiveRetirementReason.Capacity, Now);
        await index.ReserveArchiveRetirementAsync(witness, Token); Guid snapshot = await SnapshotAsync(index);
        (await index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token)).Mutations.ShouldBe(0);
        index.FindActiveArchiveContent(source)!.Id.ShouldBe(content); index.ReadArchiveRetirements().Single().CompletedTicks.ShouldBeNull();
    }

    [Fact]
    public async Task ForeignSnapshotCoverageNeverGetsDroppedAsLocalGarbage()
    {
        await using var index = await PrepareAsync(); var (source, content) = await AddAsync(index, 1, detach: true); await ReleaseAsync(index, source); Guid snapshot = await SnapshotAsync(index);
        Sql("INSERT INTO block_reference_snapshots VALUES('" + Guid.NewGuid().ToString("N") + "',zeroblob(24),1,0); "
            + "INSERT INTO block_reference_sources SELECT id,'" + source.ToString("N") + "' FROM block_reference_snapshots WHERE root_id=zeroblob(24);");
        (await index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token)).Mutations.ShouldBe(0);
        index.FindActiveArchiveContent(source)!.Id.ShouldBe(content); index.ReadArchiveProvenance().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedWitnessesExpireOnlyAfterTheirLinksAndAuditAgePermit(bool aged)
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([new(0, "old", ".psp/archive/old", 1, 1, Hash)], Token);
        var row = index.Archive.ReadAll().Single(); var source = index.ReadArchiveProvenance().Single();
        long completed = aged ? Now - 40 * TimeSpan.TicksPerDay : Now - TimeSpan.TicksPerDay;
        var witness = new ArchiveRetirement(Guid.NewGuid(), row, Root, FileId, ArchiveRetirementReason.Capacity, completed - 1);
        await index.ReserveArchiveRetirementAsync(witness, Token); await index.CompleteArchiveRetirementAsync(witness, completed, Token); await ReleaseAsync(index, source.SourceId);
        Guid snapshot = await SnapshotAsync(index); var result = await index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token);
        result.SourcesRemoved.ShouldBe(1); result.WitnessesRemoved.ShouldBe(aged ? 1 : 0); index.ReadArchiveRetirements().Count().ShouldBe(aged ? 0 : 1);
    }

    [Fact]
    public async Task SqliteFailureRollsBackTheRetiringFlagAndAllEarlierDeletesInTheBatch()
    {
        await using var index = await PrepareAsync(); var (source, content) = await AddAsync(index, 4, detach: true); await ReleaseAsync(index, source); Guid snapshot = await SnapshotAsync(index);
        Sql("CREATE TRIGGER reject_metadata BEFORE DELETE ON archive_content_chunks WHEN OLD.file_offset=2 BEGIN SELECT RAISE(ABORT,'test metadata rollback'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token));
        index.FindActiveArchiveContent(source)!.Id.ShouldBe(content); index.ReadArchiveContentChunks(content).Count().ShouldBe(4);
        Sql("DROP TRIGGER reject_metadata;"); (await index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token)).SourcesRemoved.ShouldBe(1);
    }

    [Fact]
    public async Task StaleForeignPendingAndCancelledRequestsCannotForgetProtection()
    {
        await using var index = await PrepareAsync(); var (source, _) = await AddAsync(index, 1, detach: true); await ReleaseAsync(index, source); Guid snapshot = await SnapshotAsync(index);
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Token))
        { stop.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: stop.Token)); }
        await Should.ThrowAsync<InvalidOperationException>(() => index.PruneRetiredMetadataAsync(snapshot, new byte[24], Now, 30, cancellationToken: Token));
        var apply = new ApplyIntent(Guid.NewGuid(), "pending", null, [1]); await index.ReserveApplyAsync(apply, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token)); await index.AbandonApplyAsync(apply, Token);
        await SnapshotAsync(index); await Should.ThrowAsync<InvalidOperationException>(() => index.PruneRetiredMetadataAsync(snapshot, Root, Now, 30, cancellationToken: Token));
        index.ReadArchiveProvenance().Count().ShouldBe(1);
    }

    [Fact]
    public async Task UpgradeFromVersionSeventeenPreservesActiveContentChunksAndStableSourceIdentity()
    {
        var migrations = new List<DbMigration>();
        foreach (string resource in typeof(FolderIndex).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("PeerSync.Storage.FolderDb.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(17))
        {
            using var stream = typeof(FolderIndex).Assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream);
            migrations.Add(new(migrations.Count + 1, await reader.ReadToEndAsync(Token)));
        }
        Guid content = Guid.NewGuid(); string? source = null;
        await using (var legacy = await Db.OpenAsync(Database, migrations, Token))
            await legacy.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO meta VALUES('_root-identity',$root); INSERT INTO archive(id,original_path,archive_path,archived_ticks,size,file_hash,archive_path_key) VALUES(1,'old','.psp/archive/old',1,2,$hash,'.PSP/ARCHIVE/OLD');";
                command.Parameters.AddWithValue("$root", Root); command.Parameters.AddWithValue("$hash", Hash); command.ExecuteNonQuery();
                command.CommandText = "SELECT source_id FROM archive_provenance;"; source = (string)command.ExecuteScalar()!;
                command.CommandText = "INSERT INTO archive_contents VALUES($id,$source,$root,$file,2,$hash,100,1,2,2); INSERT INTO archive_content_chunks VALUES($id,0,$hash,1); INSERT INTO archive_content_chunks VALUES($id,1,$hash,1);";
                command.Parameters.AddWithValue("$id", content.ToString("N")); command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$file", FileId); return command.ExecuteNonQuery();
            }, Token);
        await using var upgraded = await FolderIndex.OpenAsync(Database, Token); var preserved = upgraded.ReadArchiveProvenance().Single(); preserved.SourceId.ToString("N").ShouldBe(source);
        upgraded.FindActiveArchiveContent(preserved.SourceId)!.Id.ShouldBe(content); upgraded.ReadArchiveContentChunks(content).Count().ShouldBe(2);
        Directory.GetFiles(fixture, "*.pre-v17-to-v18-*.bak").Length.ShouldBe(1);
    }

    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    private static async Task<(Guid Source, Guid Content)> AddAsync(FolderIndex index, int chunks, bool detach = false)
    {
        await index.Archive.UpsertAsync([new(0, "old", ".psp/archive/old", 1, chunks, Hash)], Token); var source = index.ReadArchiveProvenance().Single();
        var content = await index.BeginArchiveContentAsync(source, Root, FileId, 100, Token); await ChunksAsync(index, content.Id, chunks); await index.ActivateArchiveContentAsync(content.Id, Token);
        if (detach) await index.Archive.DeleteAsync([source.Archive.Id], Token); return (source.SourceId, content.Id);
    }
    private static async Task ChunksAsync(FolderIndex index, Guid id, int count)
    { foreach (var batch in Enumerable.Range(0, count).Select(i => new LocalChunk(i, 1, Hash)).Chunk(1000)) await index.AppendArchiveContentChunksAsync(id, batch, Token); }
    private static Task<bool> ReleaseAsync(FolderIndex index, Guid source) => index.ReleaseArchiveSourceAsync(source, Root, Now - 30 * TimeSpan.TicksPerDay, Now, Token);
    private static async Task<Guid> SnapshotAsync(FolderIndex index, Guid? content = null)
    {
        var snapshot = await index.BeginBlockReferenceSnapshotAsync(Root, Now, Token); await index.AppendBlockReferencesAsync(snapshot.Id, [new(Hash, 1, BlockReferenceKind.Current)], Token);
        if (content is { } id) await index.AppendBlockArchiveContentCoverageAsync(snapshot.Id, id, Token);
        await index.ActivateBlockReferenceSnapshotAsync(snapshot.Id, Token); return snapshot.Id;
    }
    private long Count(string table)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table + ";"; return (long)command.ExecuteScalar()!;
    }
    private SqliteConnection Open()
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open(); return connection; }
    private void Sql(string sql)
    { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-MetadataCleanupDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
