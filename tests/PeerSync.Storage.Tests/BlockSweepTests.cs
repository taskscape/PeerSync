using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class BlockSweepTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-BlockSweepDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => Enumerable.Repeat((byte)1, 24).ToArray();
    private static long Start => TimeSpan.TicksPerDay * 100;
    private static long Day(int days) => Start + days * TimeSpan.TicksPerDay;
    public BlockSweepTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task OrphansRequireDurableGraceAndNormalAgeOrActualCapacityPressure()
    {
        await using var index = await PrepareAsync(); var snapshot = await SnapshotAsync(index); await ObserveAsync(index, snapshot, Start);
        index.ReadBlockSweepCandidates(snapshot, Root, Day(1) - 1, 30, 0).ShouldBeEmpty();
        index.ReadBlockSweepCandidates(snapshot, Root, Day(2), 30, long.MaxValue).ShouldBeEmpty();
        var candidate = index.ReadBlockSweepCandidates(snapshot, Root, Day(2), 30, 0).Single();
        var premature = Intent(candidate, snapshot, Day(1) - 1, BlockDeletionReason.Capacity);
        (await index.ReserveBlockDeletionAsync(premature, 30, 0, Token)).ShouldBeFalse();
        (await index.ReserveBlockDeletionAsync(Intent(candidate, snapshot, Day(2), BlockDeletionReason.Capacity), 30, long.MaxValue, Token)).ShouldBeFalse();
        var intent = Intent(candidate, snapshot, Day(2), BlockDeletionReason.Capacity); (await index.ReserveBlockDeletionAsync(intent, 30, 0, Token)).ShouldBeTrue();
        index.ReadBlockDeletionIntents().Single().Id.ShouldBe(intent.Id);
        (await index.CanContinueBlockDeletionAsync(intent, snapshot, Day(2), 30, long.MaxValue, Token)).ShouldBeFalse();
        await index.CancelBlockDeletionAsync(intent, snapshot, Token);
        index.ReadBlockSweepCandidates(snapshot, Root, Day(31), 30, long.MaxValue).Count().ShouldBe(1);
    }

    [Fact]
    public async Task FreshReferenceActivationResetsGraceAndMakesOldDeletionUnusable()
    {
        await using var index = await PrepareAsync(); var snapshot = await SnapshotAsync(index); await ObserveAsync(index, snapshot, Start);
        var candidate = index.ReadBlockSweepCandidates(snapshot, Root, Day(2), 1, 0).Single(); var intent = Intent(candidate, snapshot, Day(2));
        await index.ReserveBlockDeletionAsync(intent, 1, 0, Token); var live = await SnapshotAsync(index, protect: true);
        (await index.CanContinueBlockDeletionAsync(intent, live, Day(3), 1, 0, Token)).ShouldBeFalse();
        (await index.CancelBlockDeletionAsync(intent, live, Token)).ShouldBeTrue(); index.ReadBlockObjects().Count().ShouldBe(1);
        var unreferenced = await SnapshotAsync(index); await ObserveAsync(index, unreferenced, Day(3));
        index.ReadBlockSweepCandidates(unreferenced, Root, Day(3), 1, 0).ShouldBeEmpty();
        index.ReadBlockSweepCandidates(unreferenced, Root, Day(5), 1, 0).Single().FirstUnreferencedTicks.ShouldBe(Day(3));
    }

    [Fact]
    public async Task ReregistrationOrReplacementResetsObservationAndOldCompletionCannotRemoveNewOwnership()
    {
        await using var index = await PrepareAsync(); var snapshot = await SnapshotAsync(index); await ObserveAsync(index, snapshot, Start);
        var candidate = index.ReadBlockSweepCandidates(snapshot, Root, Day(2), 1, 0).Single(); var intent = Intent(candidate, snapshot, Day(2)); await index.ReserveBlockDeletionAsync(intent, 1, 0, Token);
        var replacement = Row(1) with { FileId = new byte[16], CapturedTicks = Day(3), LastUsedTicks = Day(3) };
        await index.RegisterBlockObjectsAsync([replacement], Token); await ObserveAsync(index, snapshot, Day(3));
        (await index.CanContinueBlockDeletionAsync(intent, snapshot, Day(3), 1, 0, Token)).ShouldBeFalse();
        (await index.CompleteBlockDeletionAsync(intent, snapshot, Token)).CatalogRemoved.ShouldBeFalse(); index.ReadBlockObjects().Single().FileId.ShouldBe(replacement.FileId);
        var later = index.ReadBlockSweepCandidates(snapshot, Root, Day(5), 1, 0).Single();
        await index.RegisterBlockObjectsAsync([replacement with { LastUsedTicks = Day(5) }], Token);
        index.ReadBlockSweepCandidates(snapshot, Root, Day(5), 1, 0).ShouldBeEmpty(); later.FirstUnreferencedTicks.ShouldBe(Day(3));
    }

    [Fact]
    public async Task IntentAndCompletionAreExactDurableAtomicAndIdempotentWithRealSqliteFailure()
    {
        await using var index = await PrepareAsync(); var snapshot = await SnapshotAsync(index); await ObserveAsync(index, snapshot, Start);
        var candidate = index.ReadBlockSweepCandidates(snapshot, Root, Day(2), 1, 0).Single(); var intent = Intent(candidate, snapshot, Day(2));
        Sql("CREATE TRIGGER reject_delete_intent BEFORE INSERT ON block_deletions BEGIN SELECT RAISE(ABORT,'test reservation'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.ReserveBlockDeletionAsync(intent, 1, 0, Token)); index.ReadBlockDeletionIntents().ShouldBeEmpty();
        Sql("DROP TRIGGER reject_delete_intent;"); await index.ReserveBlockDeletionAsync(intent, 1, 0, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.CompleteBlockDeletionAsync(intent with { PreparedTicks = intent.PreparedTicks + 1 }, snapshot, Token));
        Sql("CREATE TRIGGER reject_delete_completion BEFORE DELETE ON block_objects BEGIN SELECT RAISE(ABORT,'test completion'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.CompleteBlockDeletionAsync(intent, snapshot, Token));
        index.ReadBlockObjects().Count().ShouldBe(1); index.ReadBlockDeletionIntents().Single().Id.ShouldBe(intent.Id);
        Sql("DROP TRIGGER reject_delete_completion;"); var result = await index.CompleteBlockDeletionAsync(intent, snapshot, Token);
        result.Completed.ShouldBeTrue(); result.CatalogRemoved.ShouldBeTrue(); result.Referenced.ShouldBeFalse();
        index.ReadBlockObjects().ShouldBeEmpty(); index.ReadBlockDeletionIntents().ShouldBeEmpty();
        (await index.CompleteBlockDeletionAsync(intent, snapshot, Token)).Completed.ShouldBeFalse();
    }

    [Fact]
    public async Task LegacyMigrationProtectsEveryOldRowAndPersistsTheOnceInitializedWindowAcrossReopen()
    {
        var migrations = new List<DbMigration>();
        foreach (string resource in typeof(FolderIndex).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("PeerSync.Storage.FolderDb.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(15))
        {
            using var stream = typeof(FolderIndex).Assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream); migrations.Add(new(migrations.Count + 1, await reader.ReadToEndAsync(Token)));
        }
        await using (var old = await Db.OpenAsync(Database, migrations, Token))
            await old.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO meta VALUES('_root-identity',$root); INSERT INTO block_objects VALUES($hash,3,$root,$file,1,1);";
                command.Parameters.AddWithValue("$root", Root); command.Parameters.AddWithValue("$hash", Row(1).Hash); command.Parameters.AddWithValue("$file", Row(1).FileId); return command.ExecuteNonQuery();
            }, Token);
        await using (var upgraded = await FolderIndex.OpenAsync(Database, Token))
        {
            var snapshot = await SnapshotAsync(upgraded); await ObserveAsync(upgraded, snapshot, Start);
            // Even a recent same-object verification must preserve legacy grace.
            await upgraded.RegisterBlockObjectsAsync([Row(1) with { CapturedTicks = 1, LastUsedTicks = Day(1) }], Token);
            upgraded.ReadBlockSweepCandidates(snapshot, Root, Day(2), 30, 0).ShouldBeEmpty();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var current = await SnapshotAsync(reopened); await ObserveAsync(reopened, current, Day(2));
        reopened.ReadBlockSweepCandidates(current, Root, Day(29), 30, 0).ShouldBeEmpty(); reopened.ReadBlockSweepCandidates(current, Root, Day(31), 30, 0).Count().ShouldBe(1);
        Directory.GetFiles(fixture, "*.pre-v15-to-v16-*.bak").Length.ShouldBe(1);
    }

    [Fact]
    public async Task ObservationBatchesCursorAcrossMoreThanOneThousandRowsAndJournalNeverExceedsItsBound()
    {
        await using var index = await PrepareAsync(register: false);
        foreach (var batch in Enumerable.Range(1, 1001).Select(Row).Chunk(1000)) await index.RegisterBlockObjectsAsync(batch, Token);
        var snapshot = await SnapshotAsync(index); var first = await index.ObserveBlockOrphansAsync(snapshot, Root, Start, 1, cancellationToken: Token); first.Visited.ShouldBe(1000);
        var second = await index.ObserveBlockOrphansAsync(snapshot, Root, Start, 1, first.NextHash, cancellationToken: Token); second.Visited.ShouldBe(1);
        var candidates = index.ReadBlockSweepCandidates(snapshot, Root, Day(2), 1, 0).ToArray(); candidates.Length.ShouldBe(1000);
        foreach (var candidate in candidates) (await index.ReserveBlockDeletionAsync(Intent(candidate, snapshot, Day(2)), 1, 0, Token)).ShouldBeTrue();
        var last = index.ReadBlockSweepCandidates(snapshot, Root, Day(2), 1, 0).Single();
        (await index.ReserveBlockDeletionAsync(Intent(last, snapshot, Day(2)), 1, 0, Token)).ShouldBeFalse(); index.ReadBlockDeletionIntents().Count().ShouldBe(1000);
    }

    [Fact]
    public async Task IncompleteStaleForeignPendingAndCancelledStateCannotPrepareDeletionOrObservation()
    {
        await using var index = await PrepareAsync(); var snapshot = await SnapshotAsync(index); await ObserveAsync(index, snapshot, Start);
        var candidate = index.ReadBlockSweepCandidates(snapshot, Root, Day(2), 1, 0).Single(); var intent = Intent(candidate, snapshot, Day(2));
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Token))
        { stop.Cancel(); await Should.ThrowAsync<OperationCanceledException>(() => index.ReserveBlockDeletionAsync(intent, 1, 0, stop.Token)); }
        await Should.ThrowAsync<InvalidOperationException>(() => index.ObserveBlockOrphansAsync(snapshot, new byte[24], Start, 1, cancellationToken: Token));
        var pending = new ApplyIntent(Guid.NewGuid(), "pending", null, [1]); await index.ReserveApplyAsync(pending, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveBlockDeletionAsync(intent, 1, 0, Token)); await index.AbandonApplyAsync(pending, Token);
        await SnapshotAsync(index); await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveBlockDeletionAsync(intent, 1, 0, Token));
        var fresh = index.ReadActiveBlockReferenceSnapshot()!; await index.Archive.UpsertAsync([new(0, "unknown", ".psp/archive/unknown", Start, 3, Row(1).Hash)], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ObserveBlockOrphansAsync(fresh.Id, Root, Day(2), 1, cancellationToken: Token));
    }

    [Fact]
    public async Task AbsentCatalogReconciliationPreservesReferencedOrReregisteredObjects()
    {
        await using var index = await PrepareAsync(); var snapshot = await SnapshotAsync(index); await ObserveAsync(index, snapshot, Start);
        var candidate = index.ReadBlockSweepCandidates(snapshot, Root, Day(2), 1, 0).Single(); var live = await SnapshotAsync(index, protect: true);
        (await index.ForgetMissingBlockAsync(candidate, live, Token)).ShouldBeFalse(); var unreferenced = await SnapshotAsync(index);
        await index.RegisterBlockObjectsAsync([Row(1) with { LastUsedTicks = Day(3) }], Token);
        (await index.ForgetMissingBlockAsync(candidate, unreferenced, Token)).ShouldBeFalse(); index.ReadBlockObjects().Count().ShouldBe(1);
    }

    private async Task<FolderIndex> PrepareAsync(bool register = true)
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); if (register) await index.RegisterBlockObjectsAsync([Row(1)], Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private static BlockObjectRow Row(int number)
    { byte[] hash = new byte[32], file = new byte[16]; BinaryPrimitives.WriteInt32BigEndian(hash, number); BinaryPrimitives.WriteInt32BigEndian(file, number); return new(hash, 3, Root, file, 1, 1); }
    private static BlockDeletionIntent Intent(BlockSweepCandidate candidate, Guid snapshot, long now, BlockDeletionReason reason = BlockDeletionReason.Age) => new(Guid.NewGuid(), candidate, now, snapshot, reason);
    private static async Task<Guid> SnapshotAsync(FolderIndex index, bool protect = false)
    {
        var snapshot = await index.BeginBlockReferenceSnapshotAsync(Root, Start, Token);
        if (protect) await index.AppendBlockReferencesAsync(snapshot.Id, [new(Row(1).Hash, 3, BlockReferenceKind.Current)], Token);
        await index.ActivateBlockReferenceSnapshotAsync(snapshot.Id, Token); return snapshot.Id;
    }
    private static Task<BlockObservationBatch> ObserveAsync(FolderIndex index, Guid snapshot, long now) => index.ObserveBlockOrphansAsync(snapshot, Root, now, 30, cancellationToken: Token);
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-BlockSweepDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
