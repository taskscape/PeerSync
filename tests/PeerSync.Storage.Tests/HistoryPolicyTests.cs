using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class HistoryPolicyTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-HistoryPolicyDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => Enumerable.Repeat((byte)1, 24).ToArray();
    private static byte[] FileId => Enumerable.Repeat((byte)2, 16).ToArray();
    private static byte[] Hash => Enumerable.Repeat((byte)3, 32).ToArray();
    private static MetaRow Marker => new("_history-" + new string('A', 64), [4, 1]);
    private static ArchiveRow Archive => new(0, "file", ".psp/archive/file", 100, 3, Hash);
    private static HistoryArchiveProtection Protection => new(Root, FileId, "file", ".psp/archive/file", 3, Hash, 200, false);
    public HistoryPolicyTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task NewerHoldPreventsSourceReleaseAndExpiryUsesTheObservationTimeWithExactMarkerCas()
    {
        await using var index = await PrepareAsync(); await HoldAsync(index); var source = index.ReadArchiveProvenance().Single();
        (await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 300, 500, Token)).ShouldBeFalse();
        (await index.RetireHistoryMarkerAsync(Marker, Root, 200, 500, cancellationToken: Token)).ShouldBeFalse();
        (await index.RetireHistoryMarkerAsync(Marker with { Value = [9] }, Root, 300, 500, cancellationToken: Token)).ShouldBeFalse();
        (await index.RetireHistoryMarkerAsync(Marker, Root, 201, 500, cancellationToken: Token)).ShouldBeTrue();
        (await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 201, 500, Token)).ShouldBeTrue();
        index.ReadRetainedArchiveProvenance(Root).ShouldBeEmpty(); index.ReadArchiveProvenance().Single().SourceId.ShouldBe(source.SourceId);
        index.Archive.Count().ShouldBe(1); index.IsArchiveSourceReleased(source.SourceId, Root).ShouldBeTrue();
    }

    [Theory]
    [InlineData(150, false)]
    [InlineData(300, true)]
    public async Task CompletedBoundWitnessReleasesBeforeAgeButPreparedOrOlderWitnessCannotRemoveANewerHold(long preparedTicks, bool authorized)
    {
        await using var index = await PrepareAsync(); await HoldAsync(index); var source = index.ReadArchiveProvenance().Single();
        var witness = new ArchiveRetirement(Guid.NewGuid(), index.Archive.ReadAll().Single(), Root, FileId, ArchiveRetirementReason.Capacity, preparedTicks);
        await index.ReserveArchiveRetirementAsync(witness, Token);
        (await index.RetireHistoryMarkerAsync(Marker, Root, 50, 500, cancellationToken: Token)).ShouldBeFalse();
        (await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 201, 500, Token)).ShouldBeFalse();
        await index.CompleteArchiveRetirementAsync(witness, 400, Token);
        (await index.RetireHistoryMarkerAsync(Marker, Root, 50, 399, cancellationToken: Token)).ShouldBeFalse();
        (await index.RetireHistoryMarkerAsync(Marker, Root, 50, 500, cancellationToken: Token)).ShouldBe(authorized);
        if (!authorized) { (await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 50, 500, Token)).ShouldBeFalse(); return; }
        (await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 50, 500, Token)).ShouldBeTrue();
        index.FindArchiveRetirementSource(witness.Id).ShouldBe(source.SourceId);
    }

    [Fact]
    public async Task ReusedNumericArchiveIdDoesNotInheritTheCompletedSourcePolicy()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Archive with { ArchivedTicks = 450 }], Token);
        var row = index.Archive.ReadAll().Single(); var old = index.ReadArchiveProvenance().Single();
        var witness = new ArchiveRetirement(Guid.NewGuid(), row, Root, FileId, ArchiveRetirementReason.Capacity, 460);
        await index.ReserveArchiveRetirementAsync(witness, Token); await index.CompleteArchiveRetirementAsync(witness, 470, Token);
        await index.Archive.UpsertAsync([row], Token); var replacement = index.ReadArchiveProvenance().Single(p => p.Attached);
        (await index.ReleaseArchiveSourceAsync(old.SourceId, Root, 200, 500, Token)).ShouldBeTrue();
        (await index.ReleaseArchiveSourceAsync(replacement.SourceId, Root, 200, 500, Token)).ShouldBeFalse();
        index.ReadRetainedArchiveProvenance(Root).Single().SourceId.ShouldBe(replacement.SourceId);
    }

    [Fact]
    public async Task NewHistoryReuseAtomicallyReactivatesAPreviouslyReleasedSourceAndFailureRollsBackBothChanges()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Archive], Token); var source = index.ReadArchiveProvenance().Single();
        await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 200, 500, Token);
        await index.Meta.UpsertAsync([Marker], Token);
        Sql("CREATE TRIGGER reject_policy_hold BEFORE INSERT ON history_archive_links BEGIN SELECT RAISE(ABORT,'test policy failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.AccountExistingHistoryAsync(Marker, Protection, Token));
        index.IsArchiveSourceReleased(source.SourceId, Root).ShouldBeTrue(); index.HasUnaccountedHistory(Root).ShouldBeTrue();
        Sql("DROP TRIGGER reject_policy_hold;"); await index.AccountExistingHistoryAsync(Marker, Protection, Token);
        index.IsArchiveSourceReleased(source.SourceId, Root).ShouldBeFalse(); index.ReadRetainedArchiveProvenance(Root).Single().SourceId.ShouldBe(source.SourceId);
    }

    [Fact]
    public async Task PublicMarkersRequireExpiredAgeAndExplicitObservationAndNeverUsePrivateWitnesses()
    {
        await using var index = await PrepareAsync(); var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [4]); await index.ReserveApplyAsync(intent, Token);
        await index.CommitProtectedHistoryAsync(intent, Marker, null, Protection with { PublicCopy = true, Destination = "file.sync-conflict-copy" }, Token);
        (await index.RetireHistoryMarkerAsync(Marker, Root, 300, 500, cancellationToken: Token)).ShouldBeFalse();
        (await index.RetireHistoryMarkerAsync(Marker, Root, 200, 500, true, Token)).ShouldBeFalse();
        (await index.RetireHistoryMarkerAsync(Marker, Root, 201, 500, true, Token)).ShouldBeTrue(); index.Meta.Find(Marker.Key).ShouldBeNull();
    }

    [Fact]
    public async Task MissingRowAloneAndForeignRootsDoNotAuthorizeReleaseButExplicitAgeCanReleaseDetachedSources()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Archive], Token); var source = index.ReadArchiveProvenance().Single();
        await index.Archive.DeleteAsync([source.Archive.Id], Token);
        (await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 100, 500, Token)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReleaseArchiveSourceAsync(source.SourceId, new byte[24], 200, 500, Token));
        (await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 200, 500, Token)).ShouldBeTrue();
    }

    [Fact]
    public async Task UnaccountedHistoryPendingApplyCancellationAndSqliteFailurePreserveProtection()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Archive], Token); var source = index.ReadArchiveProvenance().Single();
        await index.Meta.UpsertAsync([Marker], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReleaseArchiveSourceAsync(source.SourceId, Root, 300, 500, Token));
        await index.Meta.DeleteAsync([Marker.Key], Token); var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [4]); await index.ReserveApplyAsync(intent, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReleaseArchiveSourceAsync(source.SourceId, Root, 300, 500, Token)); await index.AbandonApplyAsync(intent, Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.ReleaseArchiveSourceAsync(source.SourceId, Root, 300, 500, stop.Token));
        Sql("CREATE TRIGGER reject_release BEFORE INSERT ON archive_policy_releases BEGIN SELECT RAISE(ABORT,'test release failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.ReleaseArchiveSourceAsync(source.SourceId, Root, 300, 500, Token));
        index.IsArchiveSourceReleased(source.SourceId, Root).ShouldBeFalse(); Sql("DROP TRIGGER reject_release;");
        (await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 300, 500, Token)).ShouldBeTrue();
    }

    [Fact]
    public async Task ReleasedSourcesDoNotBlockNewCoverageButDoNotChangeAnOlderActiveSnapshot()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Archive], Token); var source = index.ReadArchiveProvenance().Single();
        var snapshot = await index.BeginBlockReferenceSnapshotAsync(Root, 300, Token); await index.AppendBlockReferencesAsync(snapshot.Id, [new(Hash, 3, BlockReferenceKind.Current)], Token);
        await index.ActivateBlockReferenceSnapshotAsync(snapshot.Id, Token); index.CountUnmanifestedReferenceArchives(snapshot.Id).ShouldBe(1);
        await index.ReleaseArchiveSourceAsync(source.SourceId, Root, 200, 500, Token); index.ReadBlockReferences().Single().Kinds.ShouldBe(BlockReferenceKind.Current);
        var refreshed = await index.BeginBlockReferenceSnapshotAsync(Root, 500, Token); index.CountUnmanifestedReferenceArchives(refreshed.Id).ShouldBe(0);
    }

    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    private static async Task HoldAsync(FolderIndex index)
    {
        var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [4]); await index.ReserveApplyAsync(intent, Token);
        await index.CommitProtectedHistoryAsync(intent, Marker, Archive, Protection, Token);
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
            || !Path.GetFileName(resolved).StartsWith("PeerSync-HistoryPolicyDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
