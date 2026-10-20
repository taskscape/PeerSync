using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed partial class CasePreservationTests
{
    [Fact]
    public async Task CaseRoutePagesContinueAcrossRestartAndWrapWithoutChangingOriginals()
    {
        await using (var index = await Open())
        {
            await Receive(index, [Source(1)]);
            await index.Devices.UpsertAsync(Enumerable.Range(1, 20).Select(n => new DeviceRow(Id(n))).ToArray(), Token);
            var pending = index.ReadPendingCasePreservations().Single();
            var first = await index.AdvanceCaseBlockRoutesAsync(pending, Token);
            first.Devices.Count.ShouldBe(6); first.Devices[^1].ShouldBe(Id(5)); // Receipt also owns device zero.
            byte[] hint = Id(20); var remembered = index.RememberCaseBlockRouteAsync(pending, hint, Token); hint[0] = 99; await remembered;
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var original = reopened.ReadPendingCasePreservations().Single();
        var second = await reopened.AdvanceCaseBlockRoutesAsync(original, Token); second.Hint.ShouldBe(Id(20));
        second.Devices[0].ShouldBe(Id(6)); second.Devices[^1].ShouldBe(Id(11)); second.Devices[0][0] = 99;
        (await reopened.AdvanceCaseBlockRoutesAsync(original, Token)).Devices[0].ShouldBe(Id(12));
        (await reopened.AdvanceCaseBlockRoutesAsync(original, Token)).Devices[0].ShouldBe(Id(18));
        (await reopened.AdvanceCaseBlockRoutesAsync(original, Token)).Devices[0].ShouldBe(Device);
        reopened.ReadPendingCasePreservations().Single().Source.Record.Body.ToArray().ShouldBe(new byte[] { 1 });
    }

    [Fact]
    public async Task CaseRouteRollbackAndCancellationLeaveTheSameNextPage()
    {
        await using var index = await Open(); await Receive(index, [Source(1)]);
        await index.Devices.UpsertAsync(Enumerable.Range(1, 10).Select(n => new DeviceRow(Id(n))).ToArray(), Token);
        var pending = index.ReadPendingCasePreservations().Single();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.AdvanceCaseBlockRoutesAsync(pending, stop.Token));
        RouteSql("CREATE TRIGGER fail_case_route BEFORE INSERT ON case_block_routes BEGIN SELECT RAISE(ABORT,'fixture route rollback'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.AdvanceCaseBlockRoutesAsync(pending, Token));
        RouteSql("DROP TRIGGER fail_case_route;");
        (await index.AdvanceCaseBlockRoutesAsync(pending, Token)).Devices[0].ShouldBe(Device);
        RouteSql("UPDATE case_preservations SET completed=1,completed_ticks=1;");
        using var connection = RouteConnection(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM case_block_routes;";
        ((long)command.ExecuteScalar()!).ShouldBe(0);
        await Should.ThrowAsync<InvalidOperationException>(() => index.AdvanceCaseBlockRoutesAsync(pending, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => index.RememberCaseBlockRouteAsync(pending, Id(3), Token));
    }

    [Fact]
    public async Task CaseRoutesRejectChangedOriginalsAndRootBindings()
    {
        await using var index = await Open(); await Receive(index, [Source(1)]); var pending = index.ReadPendingCasePreservations().Single();
        RouteSql("UPDATE case_preservations SET record_body=X'02';");
        await Should.ThrowAsync<InvalidOperationException>(() => index.AdvanceCaseBlockRoutesAsync(pending, Token));
        RouteSql("UPDATE case_preservations SET record_body=X'01';");
        await index.Meta.UpsertAsync([new("_root-identity", new byte[24].Select(_ => (byte)1).ToArray())], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.AdvanceCaseBlockRoutesAsync(pending, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => index.RememberCaseBlockRouteAsync(pending, Id(3), Token));
    }

    private SqliteConnection RouteConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open(); return connection;
    }
    private void RouteSql(string sql) { using var connection = RouteConnection(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
}
