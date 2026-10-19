using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class ArchivePathTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ArchivePaths-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ArchivePathTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task UnicodeNamesReserveIndexedNamespaceAndReplacementReleasesOnlyTheOldName()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await index.Archive.UpsertAsync([new(0, "file", ".psp/archive/Żółć", 100, 3, null)], Token);
        index.ArchivePathExists(".psp/ARCHIVE/żÓŁĆ").ShouldBeTrue(); index.ArchivePathExists(".psp/archive/zolc").ShouldBeFalse();
        var row = index.Archive.ReadAll().Single(); await index.Archive.UpsertAsync([row with { ArchivePath = ".psp/archive/new" }], Token);
        index.ArchivePathExists(".psp/archive/Żółć").ShouldBeFalse(); index.ArchivePathExists(".psp/archive/NEW").ShouldBeTrue();
        Should.Throw<ArgumentException>(() => index.ArchivePathExists(".psp/archive/../file"));
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT 1 FROM archive WHERE archive_path_key='KEY';";
        using var reader = command.ExecuteReader(); var details = new List<string>(); while (reader.Read()) details.Add(reader.GetString(3));
        string.Join('\n', details).ShouldContain("ix_archive_path"); string.Join('\n', details).ShouldNotContain("SCAN archive");
    }

    [Fact]
    public async Task LegacyBackfillSpansBatchesWithoutChangingSourceIdentitySpellingOrDatesAndResumesNullKeys()
    {
        var migrations = new List<DbMigration>();
        foreach (string resource in typeof(FolderIndex).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("PeerSync.Storage.FolderDb.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(13))
        {
            using var stream = typeof(FolderIndex).Assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream);
            migrations.Add(new(migrations.Count + 1, await reader.ReadToEndAsync(Token)));
        }
        await using (var legacy = await Db.OpenAsync(Database, migrations, Token))
            await legacy.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO archive VALUES($id,'file',$path,100,3,NULL);";
                var id = command.Parameters.Add("$id", SqliteType.Integer); var path = command.Parameters.Add("$path", SqliteType.Text);
                for (int i = 1; i <= 1001; i++) { id.Value = i; path.Value = ".psp/archive/Żółć-" + i; command.ExecuteNonQuery(); }
                return 1001;
            }, Token);
        Dictionary<long, string> sources;
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT original_archive_id,source_id FROM archive_provenance;"; using var reader = command.ExecuteReader();
            sources = []; while (reader.Read()) sources.Add(reader.GetInt64(0), reader.GetString(1));
        }
        await using (var upgraded = await FolderIndex.OpenAsync(Database, Token)) Verify(upgraded);
        Directory.GetFiles(fixture, "*.pre-v13-to-v14-*.bak").Length.ShouldBe(1);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "UPDATE archive SET archive_path_key=NULL WHERE id%2=0;"; command.ExecuteNonQuery(); }
        await using (var resumed = await FolderIndex.OpenAsync(Database, Token)) Verify(resumed);

        void Verify(FolderIndex index)
        {
            index.Archive.Count().ShouldBe(1001);
            foreach (var source in index.ReadArchiveProvenance())
            {
                source.SourceId.ToString("N").ShouldBe(sources[source.Archive.Id]); source.Attached.ShouldBeTrue();
                source.Archive.ArchivePath.ShouldBe(".psp/archive/Żółć-" + source.Archive.Id); source.Archive.ArchivedTicks.ShouldBe(100);
                index.ArchivePathExists(".psp/archive/żÓŁĆ-" + source.Archive.Id).ShouldBeTrue();
            }
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-ArchivePaths-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
