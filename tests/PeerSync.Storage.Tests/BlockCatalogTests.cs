using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class BlockCatalogTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-BlockCatalog-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => Enumerable.Repeat((byte)1, 24).ToArray();
    public BlockCatalogTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task RootBindingAndBatchValidationAreAtomic()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); var row = Row();
        await Should.ThrowAsync<InvalidOperationException>(() => index.RegisterBlockObjectsAsync([row], Token));
        await Bind(index);
        await Should.ThrowAsync<InvalidOperationException>(() => index.RegisterBlockObjectsAsync([row, row with { Hash = new byte[32], RootId = new byte[24] }], Token));
        index.ReadBlockObjects().ShouldBeEmpty();
        await index.RegisterBlockObjectsAsync([row], Token); index.ReadBlockObjects().Single().RootId.ShouldBe(Root);
    }

    [Fact]
    public async Task StableOwnershipPreservesEarliestCaptureAndLatestUseAndNewIdentityResetsTimes()
    {
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            await Bind(index); var row = Row(); await index.RegisterBlockObjectsAsync([row], Token);
            await index.RegisterBlockObjectsAsync([row with { CapturedTicks = 50, LastUsedTicks = 400 }], Token);
            await index.RegisterBlockObjectsAsync([row with { CapturedTicks = 150, LastUsedTicks = 180 }], Token);
            var stable = index.ReadBlockObjects().Single(); stable.CapturedTicks.ShouldBe(50); stable.LastUsedTicks.ShouldBe(400);
            await index.RegisterBlockObjectsAsync([row with { FileId = new byte[16], CapturedTicks = 300, LastUsedTicks = 350 }], Token);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var saved = reopened.ReadBlockObjects().Single();
        saved.CapturedTicks.ShouldBe(300); saved.LastUsedTicks.ShouldBe(350); saved.FileId.ShouldBe(new byte[16]);
        saved.Hash[0] ^= 1; reopened.ReadBlockObjects().Single().Hash.ShouldBe(Row().Hash);
    }

    [Fact]
    public async Task InputsAreFrozenBeforeQueuedRegistrationAndCancellationCannotPartiallyCommit()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await Bind(index);
        var row = Row(); byte[] expected = row.Hash.ToArray();
        Task<int> pending = index.RegisterBlockObjectsAsync([row], Token);
        row.Hash[0] ^= 1; row.RootId[0] ^= 1; row.FileId[0] ^= 1;
        (await pending).ShouldBe(1); index.ReadBlockObjects().Single().Hash.ShouldBe(expected);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token); cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.RegisterBlockObjectsAsync([Row() with { Hash = new byte[32] }], cancelled.Token));
        index.ReadBlockObjects().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("root")]
    [InlineData("identity")]
    [InlineData("empty")]
    [InlineData("oversize")]
    [InlineData("capture")]
    [InlineData("used")]
    [InlineData("future")]
    public async Task InvalidDescriptorsCannotEnterCatalog(string invalid)
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await Bind(index); var row = Row();
        row = invalid switch
        {
            "hash" => row with { Hash = new byte[31] }, "root" => row with { RootId = new byte[23] },
            "identity" => row with { FileId = new byte[15] }, "empty" => row with { Length = 0 },
            "oversize" => row with { Length = 8388609 }, "capture" => row with { CapturedTicks = 0 },
            "used" => row with { LastUsedTicks = 50 }, _ => row with { LastUsedTicks = long.MaxValue },
        };
        await Should.ThrowAsync<ArgumentException>(() => index.RegisterBlockObjectsAsync([row], Token)); index.ReadBlockObjects().ShouldBeEmpty();
    }

    [Fact]
    public async Task VersionSixMigrationWithholdsOnlySignedLiveFilesAndKeepsSignedBytesAndSequences()
    {
        await using (var index = await FolderIndex.OpenAsync(Database, Token)) { }
        int priorBackups = Directory.GetFiles(fixture, "*.pre-v6-to-v7-*.bak").Length;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            // The old schema is identical except for the ownership table/index.
            command.CommandText = "DROP INDEX ix_archive_path; ALTER TABLE archive DROP COLUMN archive_path_key; DROP TRIGGER history_archive_link_invalidate; DROP TABLE history_archive_links; DROP TABLE archive_retirement_sources; DROP TABLE block_reference_sources; DROP TABLE archive_content_chunks; DROP TABLE archive_contents; DROP TRIGGER archive_provenance_insert; DROP TRIGGER archive_provenance_delete; DROP TRIGGER archive_provenance_before_update; DROP TRIGGER archive_provenance_after_update; DROP TABLE archive_provenance; DROP TABLE block_reference_archives; DROP TABLE block_references; DROP TABLE block_reference_snapshots; ALTER TABLE local_files DROP COLUMN change_filetime; DROP TABLE archive_retirements; DROP TABLE block_objects; PRAGMA user_version=6; "
                + "INSERT INTO local_files(path_key,path,type,size,mtime_ticks,attributes,deleted,changed_ticks,vv,local_seq,flags,record_body,signature,content_ready) "
                + "VALUES('LIVE','live',0,1,1,0,0,1,X'00',7,0,X'0102',X'0304',1),"
                + "('DELETED','deleted',0,0,1,0,1,1,X'00',8,0,X'0506',X'0708',1),"
                + "('DIRECTORY','directory',1,0,1,0,0,1,X'00',9,0,X'090A',X'0B0C',1),"
                + "('UNSIGNED','unsigned',0,1,1,0,0,1,X'00',10,0,NULL,NULL,1);";
            command.ExecuteNonQuery();
        }
        await using var migrated = await FolderIndex.OpenAsync(Database, Token);
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); reader.Open();
        using var query = reader.CreateCommand(); query.CommandText = "SELECT path,content_ready,local_seq,hex(record_body),hex(signature) FROM local_files ORDER BY local_seq;";
        using var rows = query.ExecuteReader(); rows.Read().ShouldBeTrue(); rows.GetString(0).ShouldBe("live"); rows.GetInt32(1).ShouldBe(0);
        rows.GetInt64(2).ShouldBe(7); rows.GetString(3).ShouldBe("0102"); rows.GetString(4).ShouldBe("0304");
        while (rows.Read()) rows.GetInt32(1).ShouldBe(1);
        Directory.GetFiles(fixture, "*.pre-v6-to-v7-*.bak").Length.ShouldBe(priorBackups + 1); migrated.ReadBlockObjects().ShouldBeEmpty();
    }

    private static BlockObjectRow Row() => new(Enumerable.Repeat((byte)2, 32).ToArray(), 1024, Root, Enumerable.Repeat((byte)3, 16).ToArray(), 100, 200);
    private static Task<int> Bind(FolderIndex index) => index.Meta.UpsertAsync([new("_root-identity", Root)], Token);
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-BlockCatalog-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
