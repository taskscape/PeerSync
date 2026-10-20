using Microsoft.Data.Sqlite;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class FileAncestorTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-FileAncestor-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public FileAncestorTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task NeededRewriteObservesTheNearestUsableFileAncestorWithExactLiteralAndDeepKeys()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await index.LocalFiles.UpsertAsync([FileRow("literal%_"), FileRow("literal%_/x")], Token);
        string deep = "literal%_/" + string.Join('/', Enumerable.Repeat("x", 900));
        var observed = new Dictionary<string, string?>();
        await index.RecomputeNeededAsync(["literal%_/x/a", "literal%_/a", "literalAB/x/a", "literal%_2/a", "literal%_", deep], snapshot =>
        { observed.Add(snapshot.PathKey, snapshot.BlockingFileAncestor?.Path); return null; }, Token);
        observed["LITERAL%_/X/A"].ShouldBe("literal%_/x"); observed["LITERAL%_/A"].ShouldBe("literal%_");
        observed["LITERALAB/X/A"].ShouldBeNull(); observed["LITERAL%_2/A"].ShouldBeNull(); observed["LITERAL%_"].ShouldBeNull();
        observed[deep.ToUpperInvariant()].ShouldBe("literal%_/x");
        var nearest = index.LocalFiles.Find("literal%_/x")!;
        foreach (var unusable in new[] { nearest with { Flags = LocalFileState.NeedsRescan }, nearest with { Record = null }, nearest with { FileId = null }, nearest with { Deleted = true } })
        {
            await index.LocalFiles.UpsertAsync([unusable], Token);
            await index.RecomputeNeededAsync(["literal%_/x/a"], snapshot => { snapshot.BlockingFileAncestor!.Path.ShouldBe("literal%_"); return null; }, Token);
        }
    }

    [Fact]
    public async Task FreshFileCommitAtomicallyWithdrawsLiveChildrenButPreservesDeletionAndUnrelatedWork()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await index.Needed.UpsertAsync([Work("literal%_/a", 1), Work("literal%_/old", 2), Work("literalZZ/a", 1)], Token);
        var intent = new ApplyIntent(Guid.NewGuid(), "literal%_", null, [1]); await index.ReserveApplyAsync(intent, Token);
        Sql("CREATE TRIGGER fail_live_withdraw BEFORE DELETE ON needed WHEN OLD.path_key='LITERAL%_/A' BEGIN SELECT RAISE(ABORT,'withdrawal failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.CommitApplyAsync(intent, FileRow("literal%_"), [], _ => null, Token));
        index.LocalFiles.Find("literal%_").ShouldBeNull(); index.ReadApplyIntents().Single().Id.ShouldBe(intent.Id); index.Needed.Count().ShouldBe(3);
        Sql("DROP TRIGGER fail_live_withdraw;");
        (await index.CommitApplyAsync(intent, FileRow("literal%_"), [], _ => null, Token)).ShouldBeTrue();
        index.Needed.Find("literal%_/a").ShouldBeNull(); index.Needed.Find("literal%_/old")!.Work!.Stage.ShouldBe(2);
        index.Needed.Find("literalZZ/a").ShouldNotBeNull(); index.ReadApplyIntents().ShouldBeEmpty(); index.LocalFiles.Find("literal%_").ShouldNotBeNull();
    }

    private static LocalFileRow FileRow(string path) => new(path, 0, 0, 638000000000000000, 0, false, 638000000000000000,
        new byte[32], new Dictionary<ulong, ulong>(), new SignedRecord([1], new byte[64]), new byte[16]) { ContentReady = true };
    private static NeededRow Work(string path, int stage) => new(path, new byte[32], 0) { Work = new(stage, 0, 638000000000000000, new byte[32]) };
    private void Sql(string sql)
    { using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose() => Directory.Delete(fixture, true);
}
