using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class CaptureRetirementTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-CaptureRetirementDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => Enumerable.Repeat((byte)1, 24).ToArray();
    private static byte[] Hash => Enumerable.Repeat((byte)2, 32).ToArray();
    private static long Start => 100 * TimeSpan.TicksPerDay;
    public CaptureRetirementTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task RetirementRequiresAgedUnreferencedPrefixAndFreshCompleteRootBoundCoverage()
    {
        await using var index = await PrepareAsync(); Guid snapshot = await SnapshotAsync(index); var row = Row(snapshot);
        (await index.ReserveCaptureRetirementAsync(row with { PreparedTicks = Start + TimeSpan.TicksPerDay - 1 }, Token)).ShouldBeFalse();
        (await index.ReserveCaptureRetirementAsync(row, Token)).ShouldBeTrue(); index.HasCaptureRetirement(row.Id).ShouldBeTrue();
        (await index.ReserveCaptureRetirementAsync(row, Token)).ShouldBeFalse();
        Guid live = await SnapshotAsync(index, protect: true); index.IsCaptureHashReferenced(live, Root, Hash).ShouldBeTrue();
        (await index.ReserveCaptureRetirementAsync(Row(live), Token)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => index.CancelCaptureRetirementAsync(row, snapshot, Token));
        (await index.CancelCaptureRetirementAsync(row, live, Token)).ShouldBeTrue(); index.HasCaptureRetirement(row.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task ExactCompletionSurvivesReopeningAndReportsNewDemandWithoutRemovingItsReferences()
    {
        CaptureRetirement row;
        await using (var index = await PrepareAsync()) { Guid snapshot = await SnapshotAsync(index); row = Row(snapshot); await index.ReserveCaptureRetirementAsync(row, Token); }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); Guid fresh = await SnapshotAsync(reopened, protect: true);
        var saved = reopened.ReadCaptureRetirements().Single(); saved.Id.ShouldBe(row.Id); saved.PrefixHash.ShouldBe(row.PrefixHash); saved.MarkerId.ShouldBe(row.MarkerId);
        await Should.ThrowAsync<InvalidOperationException>(() => reopened.CompleteCaptureRetirementAsync(row with { PrefixLength = 1 }, fresh, Token));
        var complete = await reopened.CompleteCaptureRetirementAsync(row, fresh, Token); complete.Completed.ShouldBeTrue(); complete.Referenced.ShouldBeTrue();
        reopened.ReadBlockReferences().Single().Hash.ShouldBe(Hash); reopened.ReadCaptureRetirements().ShouldBeEmpty();
        (await reopened.CompleteCaptureRetirementAsync(row, fresh, Token)).Completed.ShouldBeFalse();
    }

    [Fact]
    public async Task RealSqliteReservationAndCompletionFailuresRollBackTheExactJournal()
    {
        await using var index = await PrepareAsync(); Guid snapshot = await SnapshotAsync(index); var row = Row(snapshot);
        Sql("CREATE TRIGGER reject_capture BEFORE INSERT ON capture_retirements BEGIN SELECT RAISE(ABORT,'private SQL test'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.ReserveCaptureRetirementAsync(row, Token)); index.ReadCaptureRetirements().ShouldBeEmpty();
        Sql("DROP TRIGGER reject_capture;"); await index.ReserveCaptureRetirementAsync(row, Token);
        Sql("CREATE TRIGGER reject_capture BEFORE DELETE ON capture_retirements BEGIN SELECT RAISE(ABORT,'private SQL test'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.CompleteCaptureRetirementAsync(row, snapshot, Token)); index.ReadCaptureRetirements().Single().Id.ShouldBe(row.Id);
        Sql("DROP TRIGGER reject_capture;"); (await index.CompleteCaptureRetirementAsync(row, snapshot, Token)).Completed.ShouldBeTrue();
    }

    [Fact]
    public async Task QueuedBuffersAreOwnedAndCancellationCannotCreatePartialAuthority()
    {
        await using var index = await PrepareAsync(); Guid snapshot = await SnapshotAsync(index); var row = Row(snapshot); byte[] expectedPrefix = row.PrefixHash.ToArray();
        Task<bool> pending = index.ReserveCaptureRetirementAsync(row, Token); row.RootId[0] ^= 1; row.MarkerId[0] ^= 1; row.DataId[0] ^= 1; row.ExpectedHash[0] ^= 1; row.PrefixHash[0] ^= 1;
        (await pending).ShouldBeTrue(); index.ReadCaptureRetirements().Single().PrefixHash.ShouldBe(expectedPrefix);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.ReserveCaptureRetirementAsync(Row(snapshot), stop.Token)); index.ReadCaptureRetirements().Count().ShouldBe(1);
    }

    [Fact]
    public async Task UnknownArchivePendingApplicationAndForeignRootPreventReservation()
    {
        await using var index = await PrepareAsync(); Guid snapshot = await SnapshotAsync(index); var row = Row(snapshot);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveCaptureRetirementAsync(row with { RootId = new byte[24] }, Token));
        var apply = new ApplyIntent(Guid.NewGuid(), "pending", null, [1]); await index.ReserveApplyAsync(apply, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveCaptureRetirementAsync(row, Token)); await index.AbandonApplyAsync(apply, Token);
        await index.Archive.UpsertAsync([new(0, "old", ".psp/archive/old", Start, 3, Hash)], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveCaptureRetirementAsync(row, Token)); index.ReadCaptureRetirements().ShouldBeEmpty();
    }

    [Fact]
    public async Task ReferencedDuplicateRequiresItsDurableReplacementAndRetainsReferenceProtection()
    {
        await using var index = await PrepareAsync(); Guid snapshot = await SnapshotAsync(index, protect: true);
        var row = Row(snapshot) with { ReplacementId = Enumerable.Repeat((byte)7, 16).ToArray() };
        (await index.ReserveCaptureRetirementAsync(row, Token)).ShouldBeFalse();
        await index.RegisterBlockObjectsAsync([new(Hash, row.ExpectedLength, Root, row.ReplacementId, Start, Start)], Token);
        (await index.ReserveCaptureRetirementAsync(row, Token)).ShouldBeTrue();
        var saved = index.ReadCaptureRetirements().Single(); saved.ReplacementId.ShouldBe(row.ReplacementId);
        var result = await index.CompleteProtectedCaptureRetirementAsync(row, snapshot, true, Token); result.Referenced.ShouldBeTrue(); result.Completed.ShouldBeTrue();
        index.ReadBlockObjects().Single().FileId.ShouldBe(row.ReplacementId); index.ReadBlockReferences().Single().Hash.ShouldBe(Hash);
    }

    [Fact]
    public async Task ChangedReplacementCannotAuthorizeProtectedCompletionOrEraseItsNewOwnership()
    {
        await using var index = await PrepareAsync(); Guid snapshot = await SnapshotAsync(index, protect: true);
        var row = Row(snapshot) with { ReplacementId = Enumerable.Repeat((byte)7, 16).ToArray() };
        await index.RegisterBlockObjectsAsync([new(Hash, row.ExpectedLength, Root, row.ReplacementId, Start, Start)], Token); await index.ReserveCaptureRetirementAsync(row, Token);
        byte[] replacement = Enumerable.Repeat((byte)8, 16).ToArray(); await index.RegisterBlockObjectsAsync([new(Hash, row.ExpectedLength, Root, replacement, Start, Start)], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.CompleteProtectedCaptureRetirementAsync(row, snapshot, true, Token));
        index.ReadCaptureRetirements().Count().ShouldBe(1); index.ReadBlockObjects().Single().FileId.ShouldBe(replacement);
        (await index.CompleteProtectedCaptureRetirementAsync(row, snapshot, false, Token)).Completed.ShouldBeTrue();
    }

    [Fact]
    public async Task SharedJournalBoundIncludesBothCompletedAndPartialBlocks()
    {
        await using var index = await PrepareAsync(); Guid snapshot = await SnapshotAsync(index);
        for (int i = 0; i < 999; i++) (await index.ReserveCaptureRetirementAsync(Row(snapshot), Token)).ShouldBeTrue();
        byte[] otherHash = Enumerable.Repeat((byte)8, 32).ToArray(); var owned = new BlockObjectRow(otherHash, 3, Root, new byte[16], Start, Start);
        await index.RegisterBlockObjectsAsync([owned], Token); await index.ObserveBlockOrphansAsync(snapshot, Root, Start, 1, cancellationToken: Token);
        var candidate = index.ReadBlockSweepCandidates(snapshot, Root, Start + 2 * TimeSpan.TicksPerDay, 1, 0).Single();
        var deletion = new BlockDeletionIntent(Guid.NewGuid(), candidate, Start + 2 * TimeSpan.TicksPerDay, snapshot, BlockDeletionReason.Age);
        (await index.ReserveBlockDeletionAsync(deletion, 1, 0, Token)).ShouldBeTrue(); (await index.ReserveCaptureRetirementAsync(Row(snapshot), Token)).ShouldBeFalse();
        index.ReadCaptureRetirements().Count().ShouldBe(999); index.ReadBlockDeletionIntents().Count().ShouldBe(1);
        await index.CancelBlockDeletionAsync(deletion, snapshot, Token); (await index.ReserveCaptureRetirementAsync(Row(snapshot), Token)).ShouldBeTrue();
        (await index.ReserveBlockDeletionAsync(deletion, 1, 0, Token)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("full")]
    [InlineData("negative")]
    [InlineData("hash")]
    public async Task InvalidPartialShapesFailBeforeEnqueue(string kind)
    {
        await using var index = await PrepareAsync(); Guid snapshot = await SnapshotAsync(index); var row = Row(snapshot);
        row = kind == "full" ? row with { PrefixLength = row.ExpectedLength } : kind == "negative" ? row with { PrefixLength = -1 } : row with { PrefixHash = [1] };
        await Should.ThrowAsync<ArgumentException>(() => index.ReserveCaptureRetirementAsync(row, Token)); index.ReadCaptureRetirements().ShouldBeEmpty();
    }

    private static CaptureRetirement Row(Guid snapshot) => new(Guid.NewGuid(), Root, Enumerable.Repeat((byte)3, 16).ToArray(), Enumerable.Repeat((byte)4, 16).ToArray(),
        Hash, 10, Start, 2, Enumerable.Repeat((byte)5, 32).ToArray(), Start + TimeSpan.TicksPerDay, snapshot);
    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    private static async Task<Guid> SnapshotAsync(FolderIndex index, bool protect = false)
    {
        var snapshot = await index.BeginBlockReferenceSnapshotAsync(Root, Start, Token);
        if (protect) await index.AppendBlockReferencesAsync(snapshot.Id, [new(Hash, 10, BlockReferenceKind.Needed)], Token);
        await index.ActivateBlockReferenceSnapshotAsync(snapshot.Id, Token); return snapshot.Id;
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
            || !Path.GetFileName(resolved).StartsWith("PeerSync-CaptureRetirementDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
