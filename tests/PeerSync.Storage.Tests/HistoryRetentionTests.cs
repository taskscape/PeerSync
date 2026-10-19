using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class HistoryRetentionTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-HistoryRetentionDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => Enumerable.Repeat((byte)1, 24).ToArray();
    private static byte[] FileId => Enumerable.Repeat((byte)2, 16).ToArray();
    private static byte[] Hash => Enumerable.Repeat((byte)3, 32).ToArray();
    private static MetaRow Marker => new("_history-" + new string('A', 64), [4, 1]);
    private static ArchiveRow Archive => new(0, "file", ".psp/archive/file", 100, 3, Hash);
    private static HistoryArchiveProtection Protection => new(Root, FileId, "file", ".psp/archive/file", 3, Hash, 200, false);
    public HistoryRetentionTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task ProtectionAndArchiveAccountingCommitTogetherAndPreserveTheOriginalTimestamp()
    {
        await using var index = await PrepareAsync(); var intent = await ReserveAsync(index);
        (await index.CommitProtectedHistoryAsync(intent, Marker, Archive, Protection, Token)).ShouldBeTrue();
        index.HasUnaccountedHistory(Root).ShouldBeFalse(); var row = index.ArchivesByRetentionTime(Root).Single();
        row.Archive.ArchivedTicks.ShouldBe(100); row.RetainedTicks.ShouldBe(200); index.ReadApplyIntents().ShouldBeEmpty();
        (await index.CommitProtectedHistoryAsync(intent, Marker, Archive, Protection, Token)).ShouldBeFalse(); index.Archive.Count().ShouldBe(1);
    }

    [Fact]
    public async Task PublicHistoryIsAccountedWithoutInventingAPrivateArchiveSource()
    {
        await using var index = await PrepareAsync(); var intent = await ReserveAsync(index);
        await index.CommitProtectedHistoryAsync(intent, Marker, null, Protection with { PublicCopy = true, Destination = "file.sync-conflict-copy" }, Token);
        index.HasUnaccountedHistory(Root).ShouldBeFalse(); index.ReadArchiveProvenance().ShouldBeEmpty(); index.Archive.Count().ShouldBe(0);
    }

    [Fact]
    public async Task RealAccountingFailureRollsBackMarkerArchiveProvenanceAndIntentRemoval()
    {
        await using var index = await PrepareAsync(); var intent = await ReserveAsync(index);
        Sql("CREATE TRIGGER reject_hold BEFORE INSERT ON history_archive_links BEGIN SELECT RAISE(ABORT,'test hold failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.CommitProtectedHistoryAsync(intent, Marker, Archive, Protection, Token));
        index.Meta.Find(Marker.Key).ShouldBeNull(); index.Archive.Count().ShouldBe(0); index.ReadArchiveProvenance().ShouldBeEmpty(); index.ReadApplyIntents().Single().Id.ShouldBe(intent.Id);
        Sql("DROP TRIGGER reject_hold;"); await index.CommitProtectedHistoryAsync(intent, Marker, Archive, Protection, Token); index.HasUnaccountedHistory(Root).ShouldBeFalse();
    }

    [Fact]
    public async Task LegacyAccountingRequiresExactMarkerBytesAndGenericChangesInvalidateIt()
    {
        await using var index = await PrepareAsync(); var intent = await ReserveAsync(index); await index.CommitHistoryAsync(intent, Marker, Archive, Token);
        index.HasUnaccountedHistory(Root).ShouldBeTrue(); (await index.AccountExistingHistoryAsync(Marker, Protection, Token)).ShouldBeTrue();
        index.HasUnaccountedHistory(Root).ShouldBeFalse(); await index.Meta.UpsertAsync([Marker], Token); index.HasUnaccountedHistory(Root).ShouldBeFalse();
        var changed = Marker with { Value = [9] }; await index.Meta.UpsertAsync([changed], Token);
        index.HasUnaccountedHistory(Root).ShouldBeTrue(); (await index.AccountExistingHistoryAsync(Marker, Protection, Token)).ShouldBeFalse();
        (await index.AccountExistingHistoryAsync(changed, Protection, Token)).ShouldBeTrue(); index.HasUnaccountedHistory(Root).ShouldBeFalse();
        await index.Meta.DeleteAsync([Marker.Key], Token); index.ArchivesByRetentionTime(Root).Single().RetainedTicks.ShouldBe(100);
    }

    [Fact]
    public async Task PreparedRetirementPreventsNewHoldsAndIsBoundToTheOriginalStableSource()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Archive], Token); var row = index.Archive.ReadAll().Single();
        var source = index.ReadArchiveProvenance().Single(); var witness = new ArchiveRetirement(Guid.NewGuid(), row, Root, FileId, ArchiveRetirementReason.Capacity, 300);
        await index.ReserveArchiveRetirementAsync(witness, Token); index.FindArchiveRetirementSource(witness.Id).ShouldBe(source.SourceId);
        index.CanRetainHistoryArchive(Protection).ShouldBeFalse(); await index.Meta.UpsertAsync([Marker], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.AccountExistingHistoryAsync(Marker, Protection, Token)); index.HasUnaccountedHistory(Root).ShouldBeTrue();
    }

    [Fact]
    public async Task CompletedWitnessCannotRebindToALaterSourceReusingIdenticalRowMetadata()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Archive], Token); var row = index.Archive.ReadAll().Single();
        var source = index.ReadArchiveProvenance().Single(); var witness = new ArchiveRetirement(Guid.NewGuid(), row, Root, FileId, ArchiveRetirementReason.Capacity, 300);
        await index.ReserveArchiveRetirementAsync(witness, Token); await index.CompleteArchiveRetirementAsync(witness, 400, Token);
        await index.Archive.UpsertAsync([row], Token); var replacement = index.ReadArchiveProvenance().Single(p => p.Attached);
        replacement.SourceId.ShouldNotBe(source.SourceId); index.FindArchiveRetirementSource(witness.Id).ShouldBe(source.SourceId);
        index.FindArchiveRetirementSource(witness.Id).ShouldNotBe(replacement.SourceId); index.CanRetainHistoryArchive(Protection).ShouldBeTrue();
    }

    [Fact]
    public async Task QueuedProtectionOwnsCallerBuffersAndCancellationCannotPartiallyCommit()
    {
        await using var index = await PrepareAsync(); var intent = await ReserveAsync(index); var protection = Protection; var marker = Marker; var archive = Archive;
        Task<bool> pending = index.CommitProtectedHistoryAsync(intent, marker, archive, protection, Token);
        protection.RootId[0] ^= 1; protection.FileId[0] ^= 1; protection.FileHash[0] ^= 1; marker.Value![0] ^= 1; archive.FileHash![0] ^= 1; await pending;
        index.HasUnaccountedHistory(Root).ShouldBeFalse(); index.Meta.Find(Marker.Key)!.Value.ShouldBe(Marker.Value); index.Archive.ReadAll().Single().FileHash.ShouldBe(Hash);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.AccountExistingHistoryAsync(Marker, Protection with { RetainedTicks = 300 }, stop.Token));
        index.ArchivesByRetentionTime(Root).Single().RetainedTicks.ShouldBe(200);
    }

    [Fact]
    public async Task ForeignRootOrUnmatchedSourceCannotCreateProtectionOrDiscardAnIntent()
    {
        await using var index = await PrepareAsync(); var intent = await ReserveAsync(index);
        await Should.ThrowAsync<InvalidOperationException>(() => index.CommitProtectedHistoryAsync(intent, Marker, Archive, Protection with { RootId = new byte[24] }, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => index.CommitProtectedHistoryAsync(intent, Marker, null, Protection, Token));
        index.Archive.Count().ShouldBe(0); index.Meta.Find(Marker.Key).ShouldBeNull(); index.ReadApplyIntents().Single().Id.ShouldBe(intent.Id);
    }

    [Fact]
    public async Task LegacyMigrationDefersUnaccountedHistoryAndNeverGuessesAnOldWitnessSource()
    {
        var migrations = new List<DbMigration>();
        foreach (string resource in typeof(FolderIndex).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("PeerSync.Storage.FolderDb.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(12))
        {
            using var stream = typeof(FolderIndex).Assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream);
            migrations.Add(new(migrations.Count + 1, await reader.ReadToEndAsync(Token)));
        }
        Guid witness = Guid.NewGuid();
        await using (var legacy = await Db.OpenAsync(Database, migrations, Token))
            await legacy.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO meta VALUES('_root-identity',$root); INSERT INTO meta VALUES($marker,$value); INSERT INTO archive VALUES(1,'file','.psp/archive/file',100,3,$hash); "
                    + "INSERT INTO archive_retirements VALUES($witness,1,'file','.psp/archive/file',100,3,$hash,$root,$file,2,300,400);";
                command.Parameters.AddWithValue("$root", Root); command.Parameters.AddWithValue("$marker", Marker.Key); command.Parameters.AddWithValue("$value", Marker.Value!);
                command.Parameters.AddWithValue("$hash", Hash); command.Parameters.AddWithValue("$witness", witness.ToString("N")); command.Parameters.AddWithValue("$file", FileId); return command.ExecuteNonQuery();
            }, Token);
        await using var upgraded = await FolderIndex.OpenAsync(Database, Token); upgraded.HasUnaccountedHistory(Root).ShouldBeTrue(); upgraded.FindArchiveRetirementSource(witness).ShouldBeNull();
        Directory.GetFiles(fixture, "*.pre-v12-to-v13-*.bak").Length.ShouldBe(1); upgraded.Archive.Count().ShouldBe(1);
    }

    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    private static async Task<ApplyIntent> ReserveAsync(FolderIndex index)
    {
        var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [4]); await index.ReserveApplyAsync(intent, Token); return intent;
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
            || !Path.GetFileName(resolved).StartsWith("PeerSync-HistoryRetentionDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
