using Microsoft.Data.Sqlite;
using PeerSync.Core.Settings;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class PullWorkTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-PullWork-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static readonly byte[] Root = new byte[24], Peer = new byte[32];
    private const long Now = 638000000000000000;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PullWorkTests() => Directory.CreateDirectory(fixture);

    [Theory]
    [InlineData(TransferPriority.Default, "A", "B", "C")]
    [InlineData(TransferPriority.SmallerFirst, "B", "C", "A")]
    [InlineData(TransferPriority.LargerFirst, "A", "C", "B")]
    [InlineData(TransferPriority.OlderFirst, "C", "A", "B")]
    [InlineData(TransferPriority.NewerFirst, "B", "A", "C")]
    public async Task OrderedWorkKeepsDirectoriesFirstAndDirectoryDeletesDeepestLastStage(TransferPriority priority, string first, string second, string third)
    {
        await using var index = await PrepareAsync();
        await index.Needed.UpsertAsync([
            Row("z/child", 0), Row("z", 0), Row("a", size: 30, mtime: 20), Row("b", size: 10, mtime: 30), Row("c", size: 20, mtime: 10),
            Row("delete-file", 2) with { SourceDevice = Hash(9) }, Row("delete-dir", 3) with { SourceDevice = Hash(9) },
            Row("delete-dir/child", 3) with { SourceDevice = Hash(9) }, new("legacy", Peer, 0)
        ], Token);
        string[] expected = ["Z", "Z/CHILD", first, second, third, "DELETE-FILE", "DELETE-DIR/CHILD", "DELETE-DIR"];
        index.ReadDuePullWork(Root, Now, priority).Select(r => r.PathKey).ShouldBe(expected);
        index.ReadDuePullWork(Root, Now, priority, 3).Select(r => r.PathKey).ShouldBe(expected.Take(3));
        await index.LocalFiles.UpsertAsync([new LocalFileRow("z", 1, 0, Now, 0, false, Now, null,
            new Dictionary<ulong, ulong>(), null, new byte[16])], Token);
        index.ReadScheduledPullWork(Root, Hash(9), Now, priority).Select(r => r.PathKey).ShouldBe(expected);
        index.ReadScheduledPullWork(Root, Hash(9), Now, priority, 3).Select(r => r.PathKey).ShouldBe(expected.Take(3));
        index.Needed.Find("legacy")!.Work.ShouldBeNull();
    }

    [Fact]
    public async Task PathPriorityPrecedesFileSizeAndDescriptorsOwnTheirFingerprint()
    {
        await using var index = await PrepareAsync(); byte[] bytes = Hash(1);
        var work = new PullWorkDescriptor(1, 500, 20, bytes); bytes[0] = 99;
        work.Fingerprint.ShouldBe(Hash(1)); work.Fingerprint[0] = 44; work.Fingerprint.ShouldBe(Hash(1));
        await index.Needed.UpsertAsync([new("important", Peer, 9) { Work = work }, Row("small", size: 1)], Token);
        var due = index.ReadDuePullWork(Root, Now, TransferPriority.SmallerFirst);
        due.Select(r => r.PathKey).ShouldBe(["IMPORTANT", "SMALL"]);
        due[0].Work!.Fingerprint.ShouldBe(Hash(1)); work.ToString().ShouldNotContain(Convert.ToHexString(bytes));
    }

    [Fact]
    public async Task BackoffSurvivesReopeningAndCapsAtTenMinutes()
    {
        var index = await PrepareAsync(); await index.Needed.UpsertAsync([Row("file")], Token);
        long now = Now;
        try
        {
            int[] delays = [10, 20, 40, 80, 160, 320, 600, 600];
            for (int i = 0; i < delays.Length; i++)
            {
                var retry = (await index.RecordPullFailureAsync("file", Root, Hash(1), now, Token))!;
                retry.Attempts.ShouldBe(Math.Min(7, i + 1)); retry.NextTicks.ShouldBe(now + delays[i] * TimeSpan.TicksPerSecond);
                index.ReadDuePullWork(Root, now).ShouldBeEmpty();
                index.ReadDuePullWork(Root, retry.NextTicks - 1).ShouldBeEmpty();
                index.ReadDuePullWork(Root, retry.NextTicks).Single().PathKey.ShouldBe("FILE"); now = retry.NextTicks;
                await index.DisposeAsync(); index = await FolderIndex.OpenAsync(Database, Token);
                index.ReadPullRetry("file")!.NextTicks.ShouldBe(now);
            }
        }
        finally { await index.DisposeAsync(); }
    }

    [Fact]
    public async Task NewFingerprintAndRemovedWorkInvalidateRetryWhileStaleCompletionCannotClearNewWork()
    {
        await using var index = await PrepareAsync(); await index.Needed.UpsertAsync([Row("file"), Row("other")], Token);
        await index.RecordPullFailureAsync("file", Root, Hash(1), Now, Token);
        await index.Needed.UpsertAsync([Row("file", fingerprint: 1)], Token);
        index.ReadDuePullWork(Root, Now).Select(r => r.PathKey).ShouldBe(["OTHER"]);
        await index.Needed.UpsertAsync([Row("file", fingerprint: 2)], Token);
        index.ReadPullRetry("file").ShouldBeNull(); index.ReadDuePullWork(Root, Now).Count.ShouldBe(2);
        (await index.RecordPullFailureAsync("file", Root, Hash(1), Now, Token)).ShouldBeNull();
        await index.RecordPullFailureAsync("file", Root, Hash(2), Now, Token);
        (await index.ClearPullRetryAsync("file", Hash(1), Token)).ShouldBeFalse();
        index.ReadPullRetry("file")!.Fingerprint.ShouldBe(Hash(2));
        await index.Needed.DeleteAsync(["file"], Token); index.ReadPullRetry("file").ShouldBeNull();
    }

    [Fact]
    public async Task ClockRollbackRestartsBoundedDelayAndMaximumTicksCannotOverflow()
    {
        await using var index = await PrepareAsync(); await index.Needed.UpsertAsync([Row("file")], Token);
        await index.RecordPullFailureAsync("file", Root, Hash(1), Now, Token);
        index.ReadDuePullWork(Root, Now - 1).Count.ShouldBe(1);
        var rollback = (await index.RecordPullFailureAsync("file", Root, Hash(1), Now - 1, Token))!;
        rollback.Attempts.ShouldBe(1); rollback.NextTicks.ShouldBe(Now - 1 + 10 * TimeSpan.TicksPerSecond);
        var nearEnd = (await index.RecordPullFailureAsync("file", Root, Hash(1), DateTime.MaxValue.Ticks - 1, Token))!;
        nearEnd.NextTicks.ShouldBe(DateTime.MaxValue.Ticks);
    }

    [Fact]
    public async Task CancellationRollsBackDescriptorsAndRetryInvalidationTogether()
    {
        await using var index = await PrepareAsync(); await index.Needed.UpsertAsync([Row("a"), Row("b")], Token);
        await index.RecordPullFailureAsync("a", Root, Hash(1), Now, Token); await index.RecordPullFailureAsync("b", Root, Hash(1), Now, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token); int count = 0;
        await Should.ThrowAsync<OperationCanceledException>(() => index.RecomputeNeededAsync(["a", "b"], snapshot =>
        {
            if (++count == 2) cancellation.Cancel(); return Row(snapshot.PathKey, fingerprint: 2);
        }, cancellation.Token));
        foreach (string path in new[] { "a", "b" })
        {
            index.Needed.Find(path)!.Work!.Fingerprint.ShouldBe(Hash(1)); index.ReadPullRetry(path)!.Attempts.ShouldBe(1);
        }
        index.ReadDuePullWork(Root, Now).ShouldBeEmpty();
    }

    [Fact]
    public async Task SqliteFailurePreservesRetryAndWriterRemainsUsable()
    {
        await using var index = await PrepareAsync(); await index.Needed.UpsertAsync([Row("file")], Token);
        await index.RecordPullFailureAsync("file", Root, Hash(1), Now, Token);
        Sql("CREATE TRIGGER reject_retry BEFORE UPDATE ON pull_retries BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.RecordPullFailureAsync("file", Root, Hash(1), Now + 1, Token));
        index.ReadPullRetry("file")!.Attempts.ShouldBe(1); Sql("DROP TRIGGER reject_retry;");
        (await index.ClearPullRetryAsync("file", Hash(1), Token)).ShouldBeTrue();
        index.ReadDuePullWork(Root, Now).Count.ShouldBe(1);
    }

    [Fact]
    public async Task SenderTargetIsMonotonicDurableAndOnlyContiguousReceivedBatchesCompleteIt()
    {
        var index = await PrepareAsync();
        try
        {
            index.ReadRemoteIndexRange(Peer).ShouldBeNull();
            await index.Devices.UpsertAsync([new(Peer, "known", Approval: DeviceApproval.Approved)], Token);
            (await index.AnnounceRemoteIndexRangeAsync(Peer, 10, Token)).Complete.ShouldBeFalse();
            (await index.AnnounceRemoteIndexRangeAsync(Peer, 7, Token)).TargetSequence.ShouldBe(10);
            await index.ApplyRemoteIndexAsync(Peer, 0, 5, [], _ => null, Token);
            index.ReadRemoteIndexRange(Peer)!.Complete.ShouldBeFalse();
            await Should.ThrowAsync<InvalidOperationException>(() => index.ApplyRemoteIndexAsync(Peer, 4, 10, [], _ => null, Token));
            index.ReadRemoteIndexRange(Peer)!.ReceivedSequence.ShouldBe(5);
            await index.DisposeAsync(); index = await FolderIndex.OpenAsync(Database, Token);
            await index.ApplyRemoteIndexAsync(Peer, 5, 10, [], _ => null, Token);
            index.ReadRemoteIndexRange(Peer)!.Complete.ShouldBeTrue();
            await Should.ThrowAsync<InvalidOperationException>(() => index.AnnounceRemoteIndexRangeAsync(Peer, 9, Token));
            index.Devices.Find(Peer)!.Approval.ShouldBe(DeviceApproval.Approved); index.Devices.Find(Peer)!.Name.ShouldBe("known");
            (await index.AnnounceRemoteIndexRangeAsync(Peer, 11, Token)).Complete.ShouldBeFalse();
            await index.Devices.DeleteAsync([Peer], Token); index.ReadRemoteIndexRange(Peer).ShouldBeNull();
        }
        finally { await index.DisposeAsync(); }
    }

    [Fact]
    public async Task QueuedWritesFreezePeerRootAndFingerprintBuffersBeforeReturning()
    {
        await using var index = await PrepareAsync(); await index.Needed.UpsertAsync([Row("file")], Token);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = index.RecomputeNeededAsync(["other"], _ => { entered.SetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException(); return null; }, Token);
        Task<RemoteIndexRange>? range = null; Task<PullRetry?>? retry = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Token); byte[] peer = Peer.ToArray(), root = Root.ToArray(), hash = Hash(1);
            range = index.AnnounceRemoteIndexRangeAsync(peer, 10, Token); retry = index.RecordPullFailureAsync("file", root, hash, Now, Token);
            Array.Fill(peer, (byte)8); Array.Fill(root, (byte)8); Array.Fill(hash, (byte)8);
        }
        finally { release.Set(); }
        await blocking; (await range!)!.DeviceId.ShouldBe(Peer); (await retry!)!.RootId.ShouldBe(Root);
        index.ReadPullRetry("file")!.Fingerprint.ShouldBe(Hash(1));
    }

    [Fact]
    public async Task InvalidInputsForeignRootAndCancelledWritesCannotChangeWork()
    {
        await using var index = await PrepareAsync(); await index.Needed.UpsertAsync([Row("file")], Token);
        byte[] foreign = Root.ToArray(); foreign[0] = 1;
        Should.Throw<InvalidOperationException>(() => index.ReadDuePullWork(foreign, Now));
        await Should.ThrowAsync<InvalidOperationException>(() => index.RecordPullFailureAsync("file", foreign, Hash(1), Now, Token));
        Should.Throw<ArgumentOutOfRangeException>(() => index.ReadDuePullWork(Root, Now, limit: 1001));
        Should.Throw<ArgumentOutOfRangeException>(() => index.ReadDuePullWork(Root, Now, (TransferPriority)99));
        Should.Throw<ArgumentException>(() => new PullWorkDescriptor(4, 0, 0, Hash(1)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token); cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.RecordPullFailureAsync("file", Root, Hash(1), Now, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => index.AnnounceRemoteIndexRangeAsync(Peer, 10, cancellation.Token));
        index.ReadPullRetry("file").ShouldBeNull(); index.ReadRemoteIndexRange(Peer).ShouldBeNull(); index.ReadDuePullWork(Root, Now).Count.ShouldBe(1);
    }

    [Fact]
    public async Task MigrationPreservesLegacyNeededAndOnlyRecomputedDescriptorsBecomeSchedulable()
    {
        var assembly = typeof(FolderIndex).Assembly;
        DbMigration[] migrations = Enumerable.Range(1, 18).Select(version =>
        {
            string resource = assembly.GetManifestResourceNames().Single(n => n.StartsWith($"PeerSync.Storage.FolderDb.Migrations.{version:D4}_", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!; using var text = new StreamReader(stream);
            return new DbMigration(version, text.ReadToEnd());
        }).ToArray();
        await using (var old = await Db.OpenAsync(Database, migrations, Token))
        {
            await old.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO needed(path_key,source_device,priority) VALUES('LEGACY',$peer,0); INSERT INTO meta(key,value) VALUES('_root-identity',$root);";
                command.Parameters.AddWithValue("$peer", Peer); command.Parameters.AddWithValue("$root", Root); return command.ExecuteNonQuery();
            }, Token);
        }
        await using var upgraded = await FolderIndex.OpenAsync(Database, Token);
        upgraded.Needed.Find("legacy")!.Work.ShouldBeNull(); upgraded.ReadDuePullWork(Root, Now).ShouldBeEmpty();
        string backup = Directory.GetFiles(fixture, "*.pre-v18-to-v19-*.bak").Single();
        using (var saved = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            saved.Open(); using var command = saved.CreateCommand(); command.CommandText = "SELECT count(*) FROM needed WHERE path_key='LEGACY';";
            ((long)command.ExecuteScalar()!).ShouldBe(1); command.CommandText = "PRAGMA user_version;"; ((long)command.ExecuteScalar()!).ShouldBe(18);
        }
        await upgraded.RecomputeNeededAsync(["legacy"], snapshot => Row(snapshot.PathKey), Token);
        upgraded.ReadDuePullWork(Root, Now).Single().PathKey.ShouldBe("LEGACY");
    }

    [Fact]
    public async Task ScheduledSelectionFiltersOneThousandWaitingDeletesBeforeLimitAndComputesExactWake()
    {
        await using var index=await PrepareAsync(); byte[] own=Hash(9), ready=Hash(3);
        await index.Needed.UpsertAsync(Enumerable.Range(0,1000).Select(i=>Row($"blocked{i:D4}",2)).ToArray(),Token);
        await index.Needed.UpsertAsync([Row("z-ready",2) with { SourceDevice=ready },Row("z-local",2) with { SourceDevice=own }],Token);
        await index.AnnounceRemoteIndexRangeAsync(Peer,2,Token); await index.AnnounceRemoteIndexRangeAsync(ready,1,Token);
        await index.ApplyRemoteIndexAsync(ready,0,1,[],_=>null,Token);
        index.ReadScheduledPullWork(Root,own,Now,limit:1).Single().PathKey.ShouldBe("Z-LOCAL");
        index.ReadScheduledPullWork(Root,own,Now).Select(r=>r.PathKey).ShouldBe(["Z-LOCAL","Z-READY"]);
        index.ReadNextScheduledPullTicks(Root,own,Now).ShouldBe(Now);
        await index.RecordPullFailureAsync("z-local",Root,Hash(1),Now,Token); await index.RecordPullFailureAsync("z-ready",Root,Hash(1),Now,Token);
        index.ReadScheduledPullWork(Root,own,Now).ShouldBeEmpty(); index.ReadNextScheduledPullTicks(Root,own,Now).ShouldBe(Now+10*TimeSpan.TicksPerSecond);
        index.ReadNextScheduledPullTicks(Root,own,Now-1).ShouldBe(Now-1);
        await index.ApplyRemoteIndexAsync(Peer,0,2,[],_=>null,Token);
        index.ReadScheduledPullWork(Root,own,Now).Count.ShouldBe(1000); index.ReadNextScheduledPullTicks(Root,own,Now).ShouldBe(Now);
    }

    [Fact]
    public async Task PendingOrUndeclaredSenderHasNoScheduledWakeUntilCompleteAndInvalidRootsAreRejected()
    {
        await using var index=await PrepareAsync(); byte[] own=Hash(9);
        await index.Needed.UpsertAsync([Row("deleted",3)],Token);
        index.ReadScheduledPullWork(Root,own,Now).ShouldBeEmpty(); index.ReadNextScheduledPullTicks(Root,own,Now).ShouldBeNull();
        await index.AnnounceRemoteIndexRangeAsync(Peer,1,Token);
        index.ReadNextScheduledPullTicks(Root,own,Now).ShouldBeNull();
        byte[] foreign=Root.ToArray(); foreign[0]=1;
        Should.Throw<InvalidOperationException>(()=>index.ReadNextScheduledPullTicks(foreign,own,Now));
        Should.Throw<ArgumentException>(()=>index.ReadScheduledPullWork(Root,new byte[1],Now));
        await index.ApplyRemoteIndexAsync(Peer,0,1,[],_=>null,Token);
        index.ReadNextScheduledPullTicks(Root,own,Now).ShouldBe(Now);
    }

    [Fact]
    public async Task ScheduledChildrenWaitForInstalledParentWithoutHidingRootWorkOrDeletes()
    {
        await using var index = await PrepareAsync(); byte[] own = Hash(9);
        await index.Needed.UpsertAsync(Enumerable.Range(0, 1000)
            .Select(i => Row($"missing/{i:D4}")).Append(Row("folder/sub", 0))
            .Append(Row("folder/child")).Append(Row("root-dir", 0))
            .Append(Row("root-file")).Append(Row("gone/child", 2)).ToArray(), Token);

        index.ReadScheduledPullWork(Root, own, Now, limit: 2).Select(row => row.PathKey)
            .ShouldBe(["ROOT-DIR", "ROOT-FILE"]);
        index.ReadNextScheduledPullTicks(Root, own, Now).ShouldBe(Now);
        await index.Needed.DeleteAsync(["root-dir"], Token);
        await index.Needed.DeleteAsync(["root-file"], Token);
        await index.Needed.DeleteAsync(["gone/child"], Token);
        index.ReadScheduledPullWork(Root, own, Now).ShouldBeEmpty();
        index.ReadNextScheduledPullTicks(Root, own, Now).ShouldBeNull();

        var parent = new LocalFileRow("folder", 1, 0, Now, 0, false, Now, null,
            new Dictionary<ulong, ulong>(), null, null);
        await index.LocalFiles.UpsertAsync([parent], Token);
        index.ReadNextScheduledPullTicks(Root, own, Now).ShouldBeNull();
        await index.LocalFiles.UpsertAsync([parent with { FileId = new byte[16], Flags = LocalFileState.NeedsRescan }], Token);
        index.ReadNextScheduledPullTicks(Root, own, Now).ShouldBeNull();
        await index.LocalFiles.UpsertAsync([parent with { FileId = new byte[16] }], Token);
        index.ReadScheduledPullWork(Root, own, Now, limit: 2).Select(row => row.PathKey)
            .ShouldBe(["FOLDER/SUB", "FOLDER/CHILD"]);
        index.ReadNextScheduledPullTicks(Root, own, Now).ShouldBe(Now);
        await index.LocalFiles.UpsertAsync([parent with { FileId = new byte[16], Deleted = true }], Token);
        index.ReadNextScheduledPullTicks(Root, own, Now).ShouldBeNull();
    }

    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private static NeededRow Row(string path, int stage = 1, long size = 3, long mtime = 1, int fingerprint = 1) =>
        new(path, Peer, 0) { Work = new(stage, size, mtime, Hash(fingerprint)) };
    private static byte[] Hash(int value) { byte[] hash = new byte[32]; hash[0] = (byte)value; return hash; }
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose() => Directory.Delete(fixture, recursive: true);
}
