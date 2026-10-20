using Microsoft.Data.Sqlite;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class DirectoryMetadataSourceTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-MetadataSources-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => new byte[24];
    private const long Now = 638000000000000000;
    public DirectoryMetadataSourceTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task ExactFrozenEnvelopeSurvivesOwnerReplacementAndRestartAndRejectsConflictingBindings()
    {
        byte[] id; DirectoryMetadataSource original;
        await using (var index = await Open())
        {
            original = Source(1); var mutable = Source(1); id = original.SourceId.ToArray();
            var admitted = index.AdmitDirectoryMetadataSourcesAsync(Root, [mutable], Now, Token);
            mutable.SourceId[0] ^= 1; mutable.Hash[0] ^= 1; mutable.RouteHint[0] ^= 1; await admitted;
            await index.LocalFiles.UpsertAsync([new("topic", 1, 0, Now, 16, false, Now, null, new Dictionary<ulong, ulong>(), Source(2).Record, new byte[16])], Token);
        }
        await using var reopened = await Open(); var saved = reopened.ReadDirectoryMetadataSource(id)!;
        saved.Source.Record.Body.ToArray().ShouldBe(original.Record.Body.ToArray()); saved.Source.Hash.ShouldBe(original.Hash); saved.Source.RouteHint.ShouldBe(original.RouteHint);
        await Should.ThrowAsync<InvalidDataException>(() => reopened.AdmitDirectoryMetadataSourcesAsync(Root, [original with { Path = "elsewhere" }], Now, Token));
        await Should.ThrowAsync<InvalidDataException>(() => reopened.AdmitDirectoryMetadataSourcesAsync(Root, [original with { Hash = Id(77) }], Now, Token));
        await Should.ThrowAsync<InvalidDataException>(() => reopened.AdmitDirectoryMetadataSourcesAsync(Root, [original with { SourceId = Id(88) }], Now, Token));
        saved.Source.Hash[0] ^= 1; reopened.ReadDirectoryMetadataSource(id)!.Source.Hash.ShouldBe(original.Hash);
        reopened.ReadDueDirectoryMetadataSources(Root, Now, byteBudget: 1).Single().Source.SourceId.ShouldBe(id);
    }

    [Fact]
    public async Task CapacityRollsBackTheEntireRemotePageCursorNeededAndFirstAdmittedEnvelope()
    {
        await using var index = await Open();
        for (int offset = 0; offset < FolderIndex.MaximumDirectoryMetadataSources - 1; offset += 1000)
        {
            int count = Math.Min(1000, FolderIndex.MaximumDirectoryMetadataSources - 1 - offset);
            await index.AdmitDirectoryMetadataSourcesAsync(Root, Enumerable.Range(offset, count).Select(Source).ToArray(), Now, Token);
        }
        var first = Source(5000); var second = Source(5001); byte[] peer = Id(9);
        var row = new RemoteFileRow(peer, "TOPIC", new Dictionary<ulong, ulong> { [9] = 1 }, new SignedRecord([42], new byte[64], 1));
        await Should.ThrowAsync<DirectoryMetadataCapacityException>(() => index.ApplyRemoteIndexAsync(peer, 0, 1, [row],
            _ => new("TOPIC", peer, 0) { DirectoryMetadataSources = [first, second] }, Token));
        index.Devices.Find(peer).ShouldBeNull(); index.RemotesForPath("topic").ShouldBeEmpty(); index.Needed.Count().ShouldBe(0);
        index.ReadDirectoryMetadataSource(first.SourceId).ShouldBeNull(); index.ReadDirectoryMetadataSource(second.SourceId).ShouldBeNull();
        Scalar("SELECT COUNT(*) FROM directory_metadata_sources;").ShouldBe(FolderIndex.MaximumDirectoryMetadataSources - 1);
    }

    [Fact]
    public async Task FairRunOrderSurvivesRestartAndRetryIsBoundedAcrossFrozenAndRolledBackUtc()
    {
        DirectoryMetadataWork first;
        await using (var index = await Open())
        {
            await index.AdmitDirectoryMetadataSourcesAsync(Root, [Source(1), Source(2)], Now, Token);
            first = index.ReadDueDirectoryMetadataSources(Root, Now)[0];
            var progress = await index.RecordDirectoryMetadataAttemptAsync(first, Now, false, TokenRoute(), cancellationToken: Token);
            progress.RunOrder.ShouldBeGreaterThan(0); progress.Source.RouteHint.ShouldBe(TokenRoute());
            index.ReadDueDirectoryMetadataSources(Root, Now)[0].Source.SourceId.ShouldNotBe(first.Source.SourceId);
        }
        await using var reopened = await Open();
        reopened.ReadDueDirectoryMetadataSources(Root, Now)[0].Source.SourceId.ShouldNotBe(first.Source.SourceId);
        DirectoryMetadataWork failed = first;
        for (int attempt = 0; attempt < 8; attempt++) failed = await reopened.RecordDirectoryMetadataAttemptAsync(first, Now, true, cancellationToken: Token);
        failed.Attempts.ShouldBe(7); failed.NextTicks.ShouldBe(Now + 600 * TimeSpan.TicksPerSecond);
        reopened.ReadDueDirectoryMetadataSources(Root, Now).ShouldNotContain(row => row.Source.SourceId.SequenceEqual(first.Source.SourceId));
        var rolledBack = await reopened.RecordDirectoryMetadataAttemptAsync(first, Now - 1, true, cancellationToken: Token);
        rolledBack.Attempts.ShouldBe(1); rolledBack.NextTicks.ShouldBe(Now - 1 + 10 * TimeSpan.TicksPerSecond);
    }

    [Fact]
    public async Task CompletionRequiresTheBoundGraphAndSqlFailureRetainsTheOriginalEnvelope()
    {
        await using var index = await Open(); var source = Source(1); await index.AdmitDirectoryMetadataSourcesAsync(Root, [source], Now, Token);
        var job = index.ReadDirectoryMetadataSource(source.SourceId)!;
        await Should.ThrowAsync<InvalidOperationException>(() => index.CompleteDirectoryMetadataSourceAsync(job, Token));
        await index.BeginDirectoryGraphAsync(source.SourceId, Root, source.Path, source.Hash, source.Length, Token);
        var work = index.ReadDirectoryGraphWork(source.SourceId)!;
        await index.CompleteDirectoryGraphWorkAsync(source.SourceId, work.Id, [], cancellationToken: Token);
        Sql("CREATE TRIGGER fail_metadata_complete BEFORE DELETE ON directory_metadata_sources BEGIN SELECT RAISE(ABORT,'metadata completion'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.CompleteDirectoryMetadataSourceAsync(job, Token));
        index.ReadDirectoryMetadataSource(source.SourceId)!.Source.Record.Body.ToArray().ShouldBe(source.Record.Body.ToArray());
        Sql("DROP TRIGGER fail_metadata_complete;");
        await Should.ThrowAsync<InvalidOperationException>(() => index.CompleteDirectoryMetadataSourceAsync(job with { RootId = [1, .. new byte[23]] }, Token));
        await index.CompleteDirectoryMetadataSourceAsync(job, Token); index.ReadDirectoryMetadataSource(source.SourceId).ShouldBeNull();
        await index.AdmitDirectoryMetadataSourcesAsync(Root, [source], Now, Token); index.ReadDirectoryMetadataSource(source.SourceId).ShouldBeNull();
    }

    [Fact]
    public async Task ExpiryIsBoundedAndRefreshesDoNotReleaseSignedOwnersOrGraphMetadata()
    {
        await using var index = await Open(); var a = Source(1); var b = Source(2);
        await index.AdmitDirectoryMetadataSourcesAsync(Root, [a, b], Now, Token);
        await index.LocalFiles.UpsertAsync([new("topic", 1, 0, Now, 16, false, Now, null, new Dictionary<ulong, ulong>(), a.Record, new byte[16])], Token);
        await index.BeginDirectoryGraphAsync(a.SourceId, Root, a.Path, a.Hash, a.Length, Token);
        await index.AdmitDirectoryMetadataSourcesAsync(Root, [a], Now + TimeSpan.TicksPerDay / 2, Token);
        (await index.PruneDirectoryMetadataSourcesAsync(Root, Now + TimeSpan.TicksPerDay, 1, Token)).ShouldBe(1);
        index.ReadDirectoryMetadataSource(a.SourceId).ShouldNotBeNull(); index.ReadDirectoryMetadataSource(b.SourceId).ShouldBeNull();
        (await index.PruneDirectoryMetadataSourcesAsync(Root, Now + 2 * TimeSpan.TicksPerDay, 1, Token)).ShouldBe(1);
        index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(a.Record.Body.ToArray()); index.ReadDirectoryGraph(a.SourceId).ShouldNotBeNull();
    }

    [Fact]
    public async Task RetainedHistoryPagesUseExactSourceCursorByteBoundsAndRequireTheBoundRoot()
    {
        await using var index = await Open(); var sources = new[] { Source(1), Source(2) }.OrderBy(source => Convert.ToHexString(source.SourceId), StringComparer.Ordinal).ToArray();
        foreach (var source in sources) InsertRetained(source, Now + TimeSpan.TicksPerDay);
        InsertRetained(Source(3), Now - 1);
        var first = index.ReadRetainedDirectoryMetadata(Root, Now, byteBudget: 1); first.Rows.Single().SourceId.ShouldBe(sources[0].SourceId); first.More.ShouldBeTrue();
        var second = index.ReadRetainedDirectoryMetadata(Root, Now, first.NextSourceId, byteBudget: 1); second.Rows.Single().SourceId.ShouldBe(sources[1].SourceId); second.More.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => index.ReadRetainedDirectoryMetadata([1, .. new byte[23]], Now));
        Should.Throw<ArgumentException>(() => index.ReadRetainedDirectoryMetadata(Root, Now, new byte[31]));
    }

    [Fact]
    public async Task RoutePagesAreBoundedRootedAndContinueAfterRestartWithoutChangingFairOrder()
    {
        byte[] cursor;
        await using (var index = await Open())
        {
            await index.Devices.UpsertAsync(Enumerable.Range(1, 20).Select(value => new DeviceRow(Id(value))).ToArray(), Token);
            var page = index.ReadDirectoryMetadataRoutes(Root); page.Devices.Count.ShouldBe(6); page.Devices[0].ShouldBe(Id(1));
            cursor = page.NextAfter!; cursor.ShouldBe(Id(6));
            await index.AdmitDirectoryMetadataSourcesAsync(Root, [Source(1)], Now, Token);
            var job = index.ReadDirectoryMetadataSource(Source(1).SourceId)!;
            var saved = await index.RecordDirectoryMetadataAttemptAsync(job, Now, true, routeAfter: cursor, cancellationToken: Token);
            cursor[0] = 99; saved.RouteAfter.ShouldBe(Id(6));
            saved.RouteAfter![0] = 88; index.ReadDirectoryMetadataSource(job.Source.SourceId)!.RouteAfter.ShouldBe(Id(6));
        }
        await using var reopened = await Open(); var pending = reopened.ReadDirectoryMetadataSource(Source(1).SourceId)!;
        pending.RouteAfter.ShouldBe(Id(6)); pending.RunOrder.ShouldBe(1);
        var second = reopened.ReadDirectoryMetadataRoutes(Root, pending.RouteAfter); second.Devices.Count.ShouldBe(6); second.Devices[0].ShouldBe(Id(7));
        var third = reopened.ReadDirectoryMetadataRoutes(Root, second.NextAfter); third.Devices.Count.ShouldBe(6); third.NextAfter.ShouldBe(Id(18));
        var last = reopened.ReadDirectoryMetadataRoutes(Root, third.NextAfter); last.Devices.Count.ShouldBe(2); last.NextAfter.ShouldBeNull();
        reopened.ReadDirectoryMetadataRoutes(Root, last.NextAfter).Devices[0].ShouldBe(Id(1));
        Should.Throw<InvalidOperationException>(() => reopened.ReadDirectoryMetadataRoutes([1, .. new byte[23]]));
        Should.Throw<ArgumentException>(() => reopened.ReadDirectoryMetadataRoutes(Root, new byte[31]));
    }

    private async Task<FolderIndex> Open()
    { var index = await FolderIndex.OpenAsync(Database, Token); await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; }
    private static DirectoryMetadataSource Source(int value)
    {
        var record = new SignedRecord(BitConverter.GetBytes(value), new byte[64]); using var digest = Blake3.Hasher.New();
        digest.Update("PeerSync case source v1"u8); digest.Update(record.Body); digest.Update(record.Signature);
        return new(digest.Finalize().AsSpan().ToArray(), "topic", record, Id(value), 12, Id(1));
    }
    private static byte[] Id(int value) { byte[] id = new byte[32]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(id, value); return id; }
    private static byte[] TokenRoute() => Id(2);
    private void InsertRetained(DirectoryMetadataSource source, long until)
    {
        using var connection = Connection(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO case_preservations(source_id,root_id,path_key,source_device,record_body,signature,completed,completed_ticks,retained_until_ticks) VALUES($id,$root,'TOPIC',$hint,$body,$signature,1,1,$until);";
        command.Parameters.AddWithValue("$id", source.SourceId); command.Parameters.AddWithValue("$root", Root); command.Parameters.AddWithValue("$hint", source.RouteHint);
        command.Parameters.AddWithValue("$body", source.Record.Body.ToArray()); command.Parameters.AddWithValue("$signature", source.Record.Signature.ToArray()); command.Parameters.AddWithValue("$until", until); command.ExecuteNonQuery();
    }
    private void Sql(string sql) { using var connection = Connection(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private long Scalar(string sql) { using var connection = Connection(); using var command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
    private SqliteConnection Connection() { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open(); return connection; }
    public void Dispose() => Directory.Delete(fixture, true);
}
