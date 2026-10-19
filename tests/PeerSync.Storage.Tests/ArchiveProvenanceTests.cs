using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class ArchiveProvenanceTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ArchiveSources-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => Enumerable.Repeat((byte)7, 24).ToArray();
    private static byte[] Hash => Enumerable.Repeat((byte)8, 32).ToArray();
    private static ArchiveRow Row(string name = "file") => new(0, name, ".psp/archive/" + name + "~old", 100, 3, Hash);
    public ArchiveProvenanceTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task MissingRowRemovalPreservesExactSourceRootAndStableIdentityAcrossReopening()
    {
        Guid sourceId; ArchiveRow row;
        await using (var index = await PrepareAsync())
        {
            await index.Archive.UpsertAsync([Row("Łódź ' ; --")], Token); row = index.Archive.ReadAll().Single();
            var source = index.ReadArchiveProvenance().Single(); sourceId = source.SourceId;
            sourceId.ShouldNotBe(Guid.Empty); source.Attached.ShouldBeTrue(); source.RootId.ShouldBe(Root); Same(source.Archive, row);
            source.RootId![0] ^= 1; source.Archive.FileHash![0] ^= 1;
            index.ReadArchiveProvenance().Single().RootId.ShouldBe(Root); index.ReadArchiveProvenance().Single().Archive.FileHash.ShouldBe(Hash);
            await index.Archive.DeleteAsync([row.Id], Token); index.Archive.Count().ShouldBe(0);
            index.ReadArchiveProvenance().Single().Attached.ShouldBeFalse();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var retained = reopened.ReadArchiveProvenance().Single();
        retained.SourceId.ShouldBe(sourceId); retained.Attached.ShouldBeFalse(); retained.RootId.ShouldBe(Root); Same(retained.Archive, row);
        retained.ToString().ShouldNotContain(row.OriginalPath);
    }

    [Fact]
    public async Task UnchangedUpsertsDoNotDuplicateButReplacementDetachesThePriorExactSource()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Row()], Token);
        var old = index.Archive.ReadAll().Single(); var source = index.ReadArchiveProvenance().Single();
        await index.Archive.UpsertAsync([old with { FileHash = old.FileHash!.ToArray() }], Token);
        index.ReadArchiveProvenance().Single().SourceId.ShouldBe(source.SourceId);
        byte[] nextRoot = Enumerable.Repeat((byte)9, 24).ToArray(); await index.Meta.UpsertAsync([new("_root-identity", nextRoot)], Token);
        var next = old with { OriginalPath = "other", ArchivePath = ".psp/archive/other~new", ArchivedTicks = 200, Size = 4, FileHash = new byte[32] };
        await index.Archive.UpsertAsync([next], Token);
        var sources = index.ReadArchiveProvenance().ToArray(); sources.Length.ShouldBe(2);
        var detached = sources.Single(s => !s.Attached); detached.SourceId.ShouldBe(source.SourceId); detached.RootId.ShouldBe(Root); Same(detached.Archive, old);
        var attached = sources.Single(s => s.Attached); attached.SourceId.ShouldNotBe(source.SourceId); attached.RootId.ShouldBe(nextRoot); Same(attached.Archive, next);
    }

    [Fact]
    public async Task ReusedArchiveRowIdsCannotConflateDifferentSourceLifetimes()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Row()], Token);
        var original = index.ReadArchiveProvenance().Single(); await index.Archive.DeleteAsync([original.Archive.Id], Token);
        await index.Archive.UpsertAsync([Row("second")], Token);
        var sources = index.ReadArchiveProvenance().ToArray(); sources.Length.ShouldBe(2);
        sources.Single(s => s.Attached).Archive.Id.ShouldBe(original.Archive.Id);
        sources.Single(s => s.Attached).SourceId.ShouldNotBe(original.SourceId);
        sources.Single(s => !s.Attached).SourceId.ShouldBe(original.SourceId);
    }

    [Fact]
    public async Task UnknownRootAndNullHashRemainExplicitlyUnverifiedAfterLaterRootBinding()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await index.Archive.UpsertAsync([Row() with { Size = 0, FileHash = null }], Token);
        await index.Meta.UpsertAsync([new("_root-identity", Root)], Token);
        var source = index.ReadArchiveProvenance().Single(); source.RootId.ShouldBeNull(); source.Archive.FileHash.ShouldBeNull();
        await index.Archive.DeleteAsync([source.Archive.Id], Token);
        var retained = index.ReadArchiveProvenance().Single(); retained.Attached.ShouldBeFalse(); retained.Archive.Size.ShouldBe(0);
    }

    [Fact]
    public async Task FailedSourceInsertionRollsBackTheEntireArchiveBatch()
    {
        await using var index = await PrepareAsync();
        Sql("CREATE TRIGGER fail_source BEFORE INSERT ON archive_provenance WHEN NEW.original_path='bad' BEGIN SELECT RAISE(ABORT,'test source failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.Archive.UpsertAsync([Row("good"), Row("bad")], Token));
        index.Archive.Count().ShouldBe(0); index.ReadArchiveProvenance().ShouldBeEmpty();
    }

    [Fact]
    public async Task FailedSourceDetachmentPreventsArchiveReplacementAndRemoval()
    {
        await using var index = await PrepareAsync(); await index.Archive.UpsertAsync([Row()], Token);
        var row = index.Archive.ReadAll().Single(); var source = index.ReadArchiveProvenance().Single();
        Sql("CREATE TRIGGER fail_detach BEFORE UPDATE ON archive_provenance BEGIN SELECT RAISE(ABORT,'test detach failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.Archive.UpsertAsync([row with { Size = 4 }], Token));
        await Should.ThrowAsync<SqliteException>(() => index.Archive.DeleteAsync([row.Id], Token));
        Same(index.Archive.ReadAll().Single(), row); index.ReadArchiveProvenance().Single().SourceId.ShouldBe(source.SourceId);
        index.ReadArchiveProvenance().Single().Attached.ShouldBeTrue();
    }

    [Fact]
    public async Task MigrationSeedsExistingArchivesAndPreservesTheOriginalDatabaseBackup()
    {
        var migrations = new List<DbMigration>();
        foreach (string resource in typeof(FolderIndex).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("PeerSync.Storage.FolderDb.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(10))
        {
            using var stream = typeof(FolderIndex).Assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream);
            migrations.Add(new(migrations.Count + 1, await reader.ReadToEndAsync(Token)));
        }
        await using (var legacy = await Db.OpenAsync(Database, migrations, Token))
            await legacy.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO meta VALUES('_root-identity',$root); INSERT INTO archive VALUES(1,'old','.psp/archive/old',100,3,$hash);";
                command.Parameters.AddWithValue("$root", Root); command.Parameters.AddWithValue("$hash", Hash); return command.ExecuteNonQuery();
            }, Token);
        await using var upgraded = await FolderIndex.OpenAsync(Database, Token);
        var source = upgraded.ReadArchiveProvenance().Single(); source.Attached.ShouldBeTrue(); source.RootId.ShouldBe(Root);
        Same(source.Archive, upgraded.Archive.ReadAll().Single());
        string backup = Directory.GetFiles(fixture, "*.pre-v10-to-v11-*.bak").Single();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version;"; ((long)command.ExecuteScalar()!).ShouldBe(10);
        command.CommandText = "SELECT COUNT(*) FROM archive;"; ((long)command.ExecuteScalar()!).ShouldBe(1);
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("history")]
    [InlineData("defer")]
    public async Task DurableApplyHistoryAndDeferralCommitSourceTogetherAndRetryIsIdempotent(string mode)
    {
        await using var index = await PrepareAsync(); var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [1]);
        await index.ReserveApplyAsync(intent, Token);
        var marker = new MetaRow("_history-" + new string('A', 64), [1]);
        Task<bool> Complete() => mode switch
        {
            "history" => index.CommitHistoryAsync(intent, marker, Row(), Token),
            "defer" => index.DeferApplyAsync(intent, [Row()], Token),
            _ => index.CommitApplyAsync(intent, new("file", 0, 3, 0, 0, false, 0, Hash, new Dictionary<ulong, ulong> { [1] = 1 }, new(Hash, new byte[64]), new byte[16]), [Row()], _ => null, Token),
        };
        (await Complete()).ShouldBeTrue(); var source = index.ReadArchiveProvenance().Single();
        source.Attached.ShouldBeTrue(); source.RootId.ShouldBe(Root); Same(source.Archive, index.Archive.ReadAll().Single());
        (await Complete()).ShouldBeFalse(); index.ReadArchiveProvenance().Single().SourceId.ShouldBe(source.SourceId); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("history")]
    [InlineData("defer")]
    public async Task SourceFailureRollsBackDurableCompletionAndKeepsTheIntent(string mode)
    {
        await using var index = await PrepareAsync(); var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [1]); await index.ReserveApplyAsync(intent, Token);
        Sql("CREATE TRIGGER fail_source BEFORE INSERT ON archive_provenance BEGIN SELECT RAISE(ABORT,'test source failure'); END;");
        Task<bool> Complete() => mode switch
        {
            "history" => index.CommitHistoryAsync(intent, new("_history-" + new string('A', 64), [1]), Row(), Token),
            "defer" => index.DeferApplyAsync(intent, [Row()], Token),
            _ => index.CommitApplyAsync(intent, new("file", 0, 3, 0, 0, false, 0, Hash, new Dictionary<ulong, ulong> { [1] = 1 }, new(Hash, new byte[64]), new byte[16]), [Row()], _ => null, Token),
        };
        await Should.ThrowAsync<SqliteException>(Complete); index.Archive.Count().ShouldBe(0); index.ReadArchiveProvenance().ShouldBeEmpty();
        index.LocalFiles.Count().ShouldBe(0); index.ReadApplyIntents().Single().Id.ShouldBe(intent.Id); index.Meta.Find("_history-" + new string('A', 64)).ShouldBeNull();
    }

    [Fact]
    public async Task CancelledArchiveCreationAndRemovalCannotLoseProvenance()
    {
        await using var index = await PrepareAsync(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.Archive.UpsertAsync([Row()], stop.Token));
        index.ReadArchiveProvenance().ShouldBeEmpty(); await index.Archive.UpsertAsync([Row()], Token);
        var source = index.ReadArchiveProvenance().Single();
        await Should.ThrowAsync<OperationCanceledException>(() => index.Archive.DeleteAsync([source.Archive.Id], stop.Token));
        index.Archive.Count().ShouldBe(1); index.ReadArchiveProvenance().Single().SourceId.ShouldBe(source.SourceId); index.ReadArchiveProvenance().Single().Attached.ShouldBeTrue();
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
    private static void Same(ArchiveRow actual, ArchiveRow expected)
    {
        actual.Id.ShouldBe(expected.Id); actual.OriginalPath.ShouldBe(expected.OriginalPath); actual.ArchivePath.ShouldBe(expected.ArchivePath);
        actual.ArchivedTicks.ShouldBe(expected.ArchivedTicks); actual.Size.ShouldBe(expected.Size); actual.FileHash.ShouldBe(expected.FileHash);
    }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-ArchiveSources-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
