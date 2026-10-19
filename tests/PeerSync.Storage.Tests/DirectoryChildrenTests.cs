using System.Text;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed partial class DirectoryChildrenTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryChildrenDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public DirectoryChildrenTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task ParentPagesUseOrdinalUtf16OrderForUnicodeAndRetainExactOriginalRecords()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        string[] paths = ["topic/a", "topic/Żółć", "topic/É", "topic/\uE000", "topic/\U00010000", "topic/\U0001F600"];
        await index.LocalFiles.UpsertAsync(paths.Select(path => Row(path)), Token);
        await index.LocalFiles.UpsertAsync([Row("topic/nested/deep"), Row("other/a")], Token);
        var actual = Read(index, 2); actual.Select(row => row.Path).ShouldBe(paths.OrderBy(PathRules.Key, StringComparer.Ordinal));
        foreach (var row in actual) { row.Record!.Body.ToArray().ShouldBe(new byte[] { 1 }); row.Record.Signature.ToArray().ShouldBe(new byte[64]); }
        actual.Select(row => row.LocalSequence).Distinct().Count().ShouldBe(paths.Length);
        Should.Throw<ArgumentException>(() => index.ReadDirectorySnapshotChildren("topic", "other/a"));
    }

    [Fact]
    public async Task ByteBudgetContinuesAfterTheLastIncludedRowAndAllowsOneLargeBoundedRecord()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await index.LocalFiles.UpsertAsync([Row("topic/a", 131072), Row("topic/b"), Row("topic/c")], Token);
        var first = index.ReadDirectorySnapshotChildren("topic", byteBudget: 512);
        first.Rows.Single().Path.ShouldBe("topic/a"); first.More.ShouldBeTrue(); first.NextKey.ShouldBe("TOPIC/A");
        var next = index.ReadDirectorySnapshotChildren("TOPIC", first.NextKey, byteBudget: 512);
        next.Rows.Select(row => row.Path).ShouldBe(["topic/b", "topic/c"]); next.More.ShouldBeFalse();
        Read(index, 1000, 1).Select(row => row.Path).ShouldBe(["topic/a", "topic/b", "topic/c"]);
    }

    [Fact]
    public async Task LogicalChildFilterKeepsSignedUnavailableVersionsAndExcludesUnsupportedIgnoredDeletedOrUnsignedRows()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await index.LocalFiles.UpsertAsync([
            Row("topic/ready"), Row("topic/needs-rescan") with { Flags = LocalFileState.NeedsRescan },
            Row("topic/ignored") with { Flags = LocalFileState.Ignored }, Row("topic/withheld") with { ContentReady = false },
            Row("topic/deleted") with { Deleted = true }, Row("topic/unsigned") with { Record = null },
            Row("topic/unsupported") with { Type = 2, FileHash = null },
            Row("topic/directory") with { Type = 1, FileHash = null }, Row("topic/directory/deep")], Token);
        Read(index, 2).Select(row => row.Path).ShouldBe(["topic/directory", "topic/needs-rescan", "topic/ready", "topic/withheld"]);
        await index.LocalFiles.UpsertAsync([Row("topic/ready") with { Deleted = true }, Row("elsewhere/ready")], Token);
        Read(index, 2).Select(row => row.Path).ShouldNotContain("topic/ready");
        index.ReadDirectorySnapshotChildren("elsewhere").Rows.Single().Path.ShouldBe("elsewhere/ready");
    }

    [Fact]
    public async Task LegacyParentAndSortBackfillPreservePublicationBytesVectorsAndSequences()
    {
        long high; LocalFileRow[] before;
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            await index.LocalFiles.UpsertAsync([Row("root"), Row("topic/\uE000"), Row("topic/\U00010000"), Row("topic/nested/a")], Token);
            high = index.ReadIndexHighWater(); before = index.LocalFiles.ReadAll().ToArray();
        }
        Sql("DROP TABLE directory_case_members; DROP TABLE directory_case_progress; DROP TABLE directory_publication_cache; DROP TABLE directory_publications; DROP TRIGGER directory_capture_insert; DROP TRIGGER directory_capture_update; DROP TRIGGER directory_capture_delete; DROP TABLE directory_capture_clock; ALTER TABLE directory_captures DROP COLUMN input_epoch; DROP INDEX ix_local_directory_children; DROP INDEX ix_local_directory_sort_backfill; ALTER TABLE local_files DROP COLUMN parent_key; ALTER TABLE local_files DROP COLUMN path_sort_key; PRAGMA user_version=24;");
        await using var upgraded = await FolderIndex.OpenAsync(Database, Token); upgraded.ReadIndexHighWater().ShouldBe(high);
        foreach (var old in before)
        {
            var row = upgraded.LocalFiles.Find(old.Path)!; row.LocalSequence.ShouldBe(old.LocalSequence); row.Vector.ShouldBe(old.Vector);
            row.Record!.Body.ToArray().ShouldBe(old.Record!.Body.ToArray()); row.Record.Signature.ToArray().ShouldBe(old.Record.Signature.ToArray());
        }
        Read(upgraded, 1).Select(row => row.Path).ShouldBe(["topic/\U00010000", "topic/\uE000"]);
        upgraded.ReadDirectorySnapshotChildren("topic/nested").Rows.Single().Path.ShouldBe("topic/nested/a");
        Directory.GetFiles(fixture, "*.pre-v24-to-v25-*.bak").Length.ShouldBe(1);
    }

    [Fact]
    public async Task InterruptedThousandRowBackfillResumesWithoutChangingAnyPublishedSequence()
    {
        long high;
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            await index.LocalFiles.UpsertAsync(Enumerable.Range(0, 1001).Select(value => Row("topic/file" + value.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))), Token);
            high = index.ReadIndexHighWater();
        }
        Sql("UPDATE local_files SET path_sort_key=NULL; CREATE TRIGGER reject_sort BEFORE UPDATE OF path_sort_key ON local_files WHEN NEW.path_key='TOPIC/FILE1000' BEGIN SELECT RAISE(ABORT,'sort backfill failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => FolderIndex.OpenAsync(Database, Token));
        Scalar("SELECT COUNT(*) FROM local_files WHERE path_sort_key IS NOT NULL;").ShouldBe(1000);
        Sql("DROP TRIGGER reject_sort;");
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); reopened.ReadIndexHighWater().ShouldBe(high);
        Read(reopened, 1000).Count.ShouldBe(1001); Scalar("SELECT COUNT(*) FROM local_files WHERE path_sort_key IS NULL;").ShouldBe(0);
    }

    [Fact]
    public async Task ChildAndBackfillQueriesUseTheirBoundedIndexes()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await index.LocalFiles.UpsertAsync([Row("topic/a")], Token);
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT path_key FROM local_files WHERE parent_key=$parent AND path_sort_key>$after ORDER BY path_sort_key LIMIT 1000;";
        command.Parameters.AddWithValue("$parent", "TOPIC"); command.Parameters.AddWithValue("$after", Encoding.BigEndianUnicode.GetBytes("TOPIC/A"));
        var details = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) details.Add(reader.GetString(3));
        string.Join('\n', details).ShouldContain("ix_local_directory_children"); string.Join('\n', details).ShouldNotContain("TEMP B-TREE");
        command.CommandText = "EXPLAIN QUERY PLAN SELECT path_key FROM local_files WHERE path_sort_key IS NULL ORDER BY path_key LIMIT 1000;"; command.Parameters.Clear(); details.Clear();
        using (var reader = command.ExecuteReader()) while (reader.Read()) details.Add(reader.GetString(3));
        string.Join('\n', details).ShouldContain("ix_local_directory_sort_backfill");
    }
    private static List<LocalFileRow> Read(FolderIndex index, int limit, int bytes = 8 * 1024 * 1024)
    {
        var rows = new List<LocalFileRow>(); string? after = null; DirectoryChildBatch batch;
        do { batch = index.ReadDirectorySnapshotChildren("TOPIC", after, limit, bytes); rows.AddRange(batch.Rows); after = batch.NextKey; } while (batch.More);
        return rows;
    }
    private static LocalFileRow Row(string path, int bodyBytes = 1) => new(path, 0, 0, 638000000000000000, 0, false, 638000000000000000,
        new byte[32], new Dictionary<ulong, ulong> { [1] = 1 }, new SignedRecord(Enumerable.Repeat((byte)1, bodyBytes).ToArray(), new byte[64]), null) { ContentReady = true };
    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open(); return connection;
    }
    private void Sql(string sql) { using var connection = Connect(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private long Scalar(string sql) { using var connection = Connect(); using var command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-DirectoryChildrenDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
