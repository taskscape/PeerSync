using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class PhysicalRootTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-PhysicalRoot-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PhysicalRootTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task BindingFreezesItsInputSurvivesRestartAndRejectsASecondIdentity()
    {
        byte[] root = [1, .. new byte[23]];
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            var write = index.BindPhysicalRootAsync(root, Token); root[0] = 9; await write;
            index.Meta.Find("_root-identity")!.Value!.ShouldBe([1, .. new byte[23]]);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        await reopened.BindPhysicalRootAsync([1, .. new byte[23]], Token);
        await Should.ThrowAsync<InvalidDataException>(() => reopened.BindPhysicalRootAsync(root, Token));
        reopened.Meta.Find("_root-identity")!.Value!.ShouldBe([1, .. new byte[23]]);
    }

    [Fact]
    public async Task InitialBindingCannotAdoptAnUnboundRecoveryIntent()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [1]);
        await index.ReserveApplyAsync(intent, Token);
        await Should.ThrowAsync<InvalidDataException>(() => index.BindPhysicalRootAsync(new byte[24], Token));
        index.Meta.Find("_root-identity").ShouldBeNull(); index.ReadApplyIntents().Single().Id.ShouldBe(intent.Id);
    }

    [Fact]
    public async Task FailedBindingRollsBackWithoutChangingOtherMetadata()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await index.Meta.UpsertAsync([new("unrelated", [42])], Token);
        Sql("CREATE TRIGGER fail_root BEFORE INSERT ON meta WHEN NEW.key='_root-identity' BEGIN SELECT RAISE(ABORT,'root binding'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.BindPhysicalRootAsync(new byte[24], Token));
        index.Meta.Find("_root-identity").ShouldBeNull(); index.Meta.Find("unrelated")!.Value!.ShouldBe(new byte[] { 42 });
        Sql("DROP TRIGGER fail_root;"); await index.BindPhysicalRootAsync(new byte[24], Token);
        index.Meta.Find("_root-identity")!.Value!.Length.ShouldBe(24);
    }
    private void Sql(string sql)
    { using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose() => Directory.Delete(fixture, recursive: true);
}
