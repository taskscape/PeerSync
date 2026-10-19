using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class BlockReferenceSnapshotTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ReferenceDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => Enumerable.Repeat((byte)3, 24).ToArray();
    private static BlockReferenceRow Row(byte value = 1, BlockReferenceKind kind = BlockReferenceKind.Current) => new(Enumerable.Repeat(value, 32).ToArray(), 1024, kind);
    public BlockReferenceSnapshotTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task BoundedStagingIsInvisibleUntilActivationAndPersistsMergedKindsAcrossRestart()
    {
        Guid id;
        await using (var index = await PrepareAsync())
        {
            var state = await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token); id = state.Id;
            await index.AppendBlockReferencesAsync(id, [Row(), Row(kind: BlockReferenceKind.Needed)], Token);
            index.ReadActiveBlockReferenceSnapshot().ShouldBeNull(); index.ReadBlockReferences().ShouldBeEmpty();
            (await index.ActivateBlockReferenceSnapshotAsync(id, Token)).ShouldBeTrue();
            index.ReadBlockReferences().Single().Kinds.ShouldBe(BlockReferenceKind.Current | BlockReferenceKind.Needed);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var active = reopened.ReadActiveBlockReferenceSnapshot()!;
        active.Id.ShouldBe(id); active.RootId.ShouldBe(Root); active.CreatedTicks.ShouldBe(100); active.References.ShouldBe(1); active.Active.ShouldBeTrue();
        (await reopened.ActivateBlockReferenceSnapshotAsync(id, Token)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => reopened.AppendBlockReferencesAsync(id, [Row(2)], Token));
        var bytes = reopened.ReadBlockReferences().Single().Hash; bytes[0] ^= 1; reopened.ReadBlockReferences().Single().Hash.ShouldBe(Row().Hash);
    }

    [Fact]
    public async Task NewBuildCleansOnlyAbandonedStagingAndEmptyActivationRetiresOldSnapshotAtomically()
    {
        Guid activeId, abandonedId;
        await using (var index = await PrepareAsync())
        {
            activeId = (await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token)).Id;
            await index.AppendBlockReferencesAsync(activeId, [Row()], Token); await index.ActivateBlockReferenceSnapshotAsync(activeId, Token);
            abandonedId = (await index.BeginBlockReferenceSnapshotAsync(Root, 200, Token)).Id;
            await index.AppendBlockReferencesAsync(abandonedId, [Row(2)], Token);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        reopened.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(activeId); reopened.ReadBlockReferences().Single().Hash.ShouldBe(Row().Hash);
        var next = await reopened.BeginBlockReferenceSnapshotAsync(Root, 300, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => reopened.ActivateBlockReferenceSnapshotAsync(abandonedId, Token));
        reopened.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(activeId);
        await reopened.ActivateBlockReferenceSnapshotAsync(next.Id, Token); reopened.ReadBlockReferences().ShouldBeEmpty();
        reopened.ReadActiveBlockReferenceSnapshot()!.References.ShouldBe(0);
    }

    [Fact]
    public async Task RealActivationFailureRollsBackPreviousSnapshotDeletion()
    {
        await using var index = await PrepareAsync(); var first = await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token);
        await index.AppendBlockReferencesAsync(first.Id, [Row()], Token); await index.ActivateBlockReferenceSnapshotAsync(first.Id, Token);
        var next = await index.BeginBlockReferenceSnapshotAsync(Root, 200, Token); await index.AppendBlockReferencesAsync(next.Id, [Row(2)], Token);
        Sql("CREATE TRIGGER reject_reference BEFORE UPDATE ON block_reference_snapshots WHEN NEW.active=1 BEGIN SELECT RAISE(ABORT,'test activation failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.ActivateBlockReferenceSnapshotAsync(next.Id, Token));
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(first.Id); index.ReadBlockReferences().Single().Hash.ShouldBe(Row().Hash);
        Sql("DROP TRIGGER reject_reference;"); await index.ActivateBlockReferenceSnapshotAsync(next.Id, Token);
        index.ReadBlockReferences().Single().Hash.ShouldBe(Row(2).Hash);
    }

    [Fact]
    public async Task ConflictingLengthsRollBackTheWholeBatchAndCancellationCannotActivate()
    {
        await using var index = await PrepareAsync(); var state = await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token);
        await index.AppendBlockReferencesAsync(state.Id, [Row()], Token);
        await Should.ThrowAsync<InvalidDataException>(() => index.AppendBlockReferencesAsync(state.Id, [Row(2), Row() with { Length = 1025 }], Token));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.ActivateBlockReferenceSnapshotAsync(state.Id, stop.Token));
        index.ReadActiveBlockReferenceSnapshot().ShouldBeNull();
        await index.ActivateBlockReferenceSnapshotAsync(state.Id, Token); index.ReadBlockReferences().Single().Hash.ShouldBe(Row().Hash);
    }

    [Fact]
    public async Task RootAndPendingApplicationChecksProtectStagedAndActiveSnapshots()
    {
        await using var index = await PrepareAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => index.BeginBlockReferenceSnapshotAsync(new byte[24], 100, Token));
        var state = await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token);
        var intent = new ApplyIntent(Guid.NewGuid(), "pending", null, [1]); await index.ReserveApplyAsync(intent, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ActivateBlockReferenceSnapshotAsync(state.Id, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => index.BeginBlockReferenceSnapshotAsync(Root, 200, Token));
        await index.AbandonApplyAsync(intent, Token); await index.ActivateBlockReferenceSnapshotAsync(state.Id, Token);
        await index.Meta.UpsertAsync([new("_root-identity", new byte[24])], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.BeginBlockReferenceSnapshotAsync(new byte[24], 200, Token));
        index.ReadActiveBlockReferenceSnapshot()!.Id.ShouldBe(state.Id);
    }

    [Fact]
    public async Task SubmittedHashesAreOwnedBeforeWriterQueueAndAllSourceKindsMerge()
    {
        await using var index = await PrepareAsync(); var state = await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token); var row = Row();
        Task<int> pending = index.AppendBlockReferencesAsync(state.Id, [row], Token); row.Hash[0] ^= 1; await pending;
        await index.AppendBlockReferencesAsync(state.Id, [Row(kind: BlockReferenceKind.Needed | BlockReferenceKind.History | BlockReferenceKind.Archive)], Token);
        await index.ActivateBlockReferenceSnapshotAsync(state.Id, Token); var saved = index.ReadBlockReferences().Single();
        saved.Hash.ShouldBe(Row().Hash); saved.Kinds.ShouldBe((BlockReferenceKind)15);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveCoverageMatchesExactLocationsAndCannotLeakAcrossSnapshotEpochs(bool abandoned)
    {
        await using var index = await PrepareAsync();
        await index.Archive.UpsertAsync([new(1, "one", ".psp/archive/one", 100, 1024, Row().Hash), new(2, "two", ".psp/archive/two", 100, 1024, Row().Hash)], Token);
        var state = await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token);
        (await index.AppendBlockArchiveCoverageAsync(state.Id, [new("wrong", ".psp/archive/one", 1024, Row().Hash)], Token)).ShouldBe(0);
        (await index.AppendBlockArchiveCoverageAsync(state.Id, [new("one", ".psp/archive/one", 1024, Row().Hash)], Token)).ShouldBe(1);
        index.CountUnmanifestedReferenceArchives(state.Id).ShouldBe(1);
        if (!abandoned)
        {
            await index.ActivateBlockReferenceSnapshotAsync(state.Id, Token);
            await Should.ThrowAsync<InvalidOperationException>(() => index.AppendBlockArchiveCoverageAsync(state.Id, [new("two", ".psp/archive/two", 1024, Row().Hash)], Token));
        }
        var next = await index.BeginBlockReferenceSnapshotAsync(Root, 200, Token); index.CountUnmanifestedReferenceArchives(next.Id).ShouldBe(2);
        await index.ActivateBlockReferenceSnapshotAsync(next.Id, Token); index.CountUnmanifestedReferenceArchives(next.Id).ShouldBe(2);
        Should.Throw<InvalidOperationException>(() => index.CountUnmanifestedReferenceArchives(state.Id));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("oversize")]
    [InlineData("kind-zero")]
    [InlineData("kind-extra")]
    [InlineData("batch")]
    public async Task InvalidReferenceBatchesCannotAlterStaging(string invalid)
    {
        await using var index = await PrepareAsync(); var state = await index.BeginBlockReferenceSnapshotAsync(Root, 100, Token);
        var row = invalid switch
        {
            "hash" => Row() with { Hash = new byte[31] }, "length" => Row() with { Length = 0 }, "oversize" => Row() with { Length = 8388609 },
            "kind-zero" => Row() with { Kinds = 0 }, "kind-extra" => Row() with { Kinds = (BlockReferenceKind)16 }, _ => Row(),
        };
        await Should.ThrowAsync<ArgumentException>(() => index.AppendBlockReferencesAsync(state.Id, invalid == "batch" ? Enumerable.Repeat(row, 1001).ToArray() : [row], Token));
        await index.ActivateBlockReferenceSnapshotAsync(state.Id, Token); index.ReadBlockReferences().ShouldBeEmpty();
    }

    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; } catch { await index.DisposeAsync(); throw; }
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
            || !Path.GetFileName(resolved).StartsWith("PeerSync-ReferenceDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
