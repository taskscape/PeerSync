using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    private string MetadataRoot => Path.Combine(fixture, "metadata-receiver");
    private string MetadataDatabase => Path.Combine(fixture, "metadata-receiver.db");
    private static byte[] MetadataDevice => [9, .. new byte[31]];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataFetchingFailsOverCorruptAndDisconnectedHintsWithoutDownloadingPayloadOrSigning(bool readOnly)
    {
        File.WriteAllText(Path.Combine(Root, "topic", "a"), "private original payload");
        await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer, buildGraph: false);
        using var access = FolderKeys.FromText(readOnly ? keys.ExportReadOnlyText() : keys.ExportText());
        await using var receiver = await OpenMetadataReceiver(access); var source = DirectoryMetadataSources.FromVersion(VersionView.Open(original, keys, device))! with { RouteHint = [7, .. new byte[31]] };
        await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [source], DateTime.UtcNow.Ticks, Token);
        await receiver.Devices.UpsertAsync([new([2, .. new byte[31]])], Token);
        using var published = new PublishedBlocks(Root, producer); var bad = new List<int>(); var buffers = new List<byte[]>();
        byte[] payloadHash = producer.LocalFiles.Find("topic/a")!.FileHash!;
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "metadata-logs"), "metadata-fetch", debugEnabled: true);
        var worker = new DirectoryMetadataWorker(MetadataRoot, receiver, access, MetadataDevice, TimeProvider.System, async (peer, hash, length, token) =>
        {
            hash.Span.SequenceEqual(payloadHash).ShouldBeFalse("graph fetching must not request file payload chunks");
            using (await FolderReferenceGate.AcquireAsync(receiver, token)) { } // A network await must not hold this gate.
            if (peer.Span[0] == 7) { bad.Add(7); return new byte[length]; }
            if (peer.Span[0] != 2) { bad.Add(peer.Span[0]); throw new IOException("private disconnected route"); }
            byte[] bytes = published.TryRead(hash.Span, length, token)!; buffers.Add(bytes); return bytes;
        }, journal);
        (await worker.RunAsync(1, token: Token)).Completed.ShouldBe(0);
        receiver.ReadDirectoryMetadataSource(source.SourceId)!.Source.RouteHint[0].ShouldBe((byte)2); int failures = bad.Count;
        (await worker.RunAsync(token: Token)).Completed.ShouldBe(1); bad.Count.ShouldBe(failures);
        foreach (var buffer in buffers) Array.Clear(buffer);
        receiver.ReadDirectoryGraph(source.SourceId)!.Complete.ShouldBeTrue(); receiver.ReadDirectoryMetadataSource(source.SourceId).ShouldBeNull();
        receiver.LocalFiles.Count().ShouldBe(0); receiver.ReadIndexBatch(0).Records.ShouldBeEmpty(); Directory.Exists(Path.Combine(MetadataRoot, "topic")).ShouldBeFalse();
        using var owned = new PublishedBlocks(MetadataRoot, receiver); owned.TryRead(source.Hash, source.Length, Token).ShouldNotBeNull();
        owned.TryRead(payloadHash, (int)producer.LocalFiles.Find("topic/a")!.Size, Token).ShouldBeNull();
        string log = File.ReadAllText(Path.Combine(fixture, "metadata-logs", "debug.jsonl")); log.ShouldContain("metadata_route-failover"); log.ShouldContain("traceId");
        log.ShouldNotContain("private disconnected route"); log.ShouldNotContain("topic"); log.ShouldNotContain(keys.ExportText()); log.ShouldNotContain(Convert.ToHexString(source.Hash)); journal.DroppedEvents.ShouldBe(0);
    }

    [Fact]
    public async Task MetadataRouteCandidatesStayWithinOnePageForTheEntireSourceAttempt()
    {
        WriteFiles("a", "b"); await using var producer = await FolderIndex.OpenAsync(Database, Token);
        var original = await PrepareProjection(producer, buildGraph: false); var source = DirectoryMetadataSources.FromVersion(VersionView.Open(original, keys, device))!;
        await using var receiver = await OpenMetadataReceiver(keys);
        await receiver.Devices.UpsertAsync(Enumerable.Range(2, 19).Select(value => new DeviceRow([(byte)value, .. new byte[31]])).ToArray(), Token);
        await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [source], DateTime.UtcNow.Ticks, Token);
        using var published = new PublishedBlocks(Root, producer); var tried = new List<int>();
        var worker = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, TimeProvider.System, (peer, hash, length, token) =>
        {
            tried.Add(peer.Span[0]);
            if (peer.Span[0] == 3 || peer.Span[0] == 2 && hash.Span.SequenceEqual(source.Hash))
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!);
            throw new IOException("This peer lacks that metadata node.");
        });
        (await worker.RunAsync(token: Token)).Completed.ShouldBe(1); tried.ShouldContain(3); tried.ShouldNotContain(value => value > 7);
        receiver.ReadDirectoryMetadataSource(source.SourceId).ShouldBeNull();
    }

    [Fact]
    public async Task MetadataStalledPreferredHintLeavesTimeForAWorkingForwarder()
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token);
        var original = await PrepareProjection(producer, buildGraph: false);
        var source = DirectoryMetadataSources.FromVersion(VersionView.Open(original, keys, device))!;
        await using var receiver = await OpenMetadataReceiver(keys);
        await receiver.Devices.UpsertAsync([new([2, .. new byte[31]])], Token);
        await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [source], DateTime.UtcNow.Ticks, Token);
        using var published = new PublishedBlocks(Root, producer); int stalled = 0, forwarded = 0;
        var worker = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, TimeProvider.System, async (peer, hash, length, token) =>
        {
            if (peer.Span[0] != 2) { stalled++; await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            forwarded++; return published.TryRead(hash.Span, length, token)!;
        });
        (await worker.RunAsync(token: Token)).Completed.ShouldBe(1); stalled.ShouldBe(1); forwarded.ShouldBeGreaterThan(0);
        receiver.ReadDirectoryMetadataSource(source.SourceId).ShouldBeNull();
    }

    [Fact]
    public async Task MetadataRoutesReachAForwarderBeyondTheFirstPageAcrossWorkerRestarts()
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token);
        var original = await PrepareProjection(producer, buildGraph: false);
        var source = DirectoryMetadataSources.FromVersion(VersionView.Open(original, keys, device))!;
        var clock = new CompletionClock(DateTimeOffset.UtcNow); using var published = new PublishedBlocks(Root, producer);
        await using (var receiver = await OpenMetadataReceiver(keys))
        {
            await receiver.Devices.UpsertAsync(Enumerable.Range(2, 19).Select(value => new DeviceRow([(byte)value, .. new byte[31]])).ToArray(), Token);
            await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [source], clock.GetUtcNow().UtcTicks, Token);
        }
        int attempts = 0; var tried = new List<int>();
        for (; attempts < 5; attempts++)
        {
            await using var receiver = await FolderIndex.OpenAsync(MetadataDatabase, Token);
            var worker = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, clock, (peer, hash, length, token) =>
            {
                tried.Add(peer.Span[0]);
                return peer.Span[0] == 20 ? ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!) : throw new IOException("route unavailable");
            });
            var pass = await worker.RunAsync(token: Token); var pending = receiver.ReadDirectoryMetadataSource(source.SourceId);
            if (pending is null) { pass.Completed.ShouldBe(1); receiver.ReadDirectoryGraph(source.SourceId)!.Complete.ShouldBeTrue(); break; }
            pass.Deferred.ShouldBe(1); clock.AdvanceTo(pending.NextTicks!.Value);
        }
        attempts.ShouldBe(3); tried.ShouldContain(20); tried.ShouldNotContain(9);
        tried.Take(7).ShouldNotContain(20);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataRestartAndCancellationRetainExactEnvelopeAndVerifiedGraphPrefix(bool cancel)
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer, buildGraph: false);
        var source = DirectoryMetadataSources.FromVersion(VersionView.Open(original, keys, device))!;
        using var published = new PublishedBlocks(Root, producer); using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using (var receiver = await OpenMetadataReceiver(keys))
        {
            await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [source], DateTime.UtcNow.Ticks, Token);
            var first = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, TimeProvider.System,
                (_, hash, length, token) => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!))
            { Checkpoint = stage => { if (stage == (cancel ? "metadata-graph-before-progress" : "metadata-graph-progress")) { if (cancel) cancelled.Cancel(); else throw new Stop(); } } };
            if (cancel) await Should.ThrowAsync<OperationCanceledException>(() => first.RunAsync(1, token: cancelled.Token));
            else await Should.ThrowAsync<Stop>(() => first.RunAsync(1, token: Token));
            receiver.ReadDirectoryMetadataSource(source.SourceId)!.Source.Record.Body.ToArray().ShouldBe(original.Body.ToArray());
            receiver.ReadDirectoryGraph(source.SourceId)!.Complete.ShouldBeFalse();
        }
        await using var reopened = await FolderIndex.OpenAsync(MetadataDatabase, Token);
        var resumed = new DirectoryMetadataWorker(MetadataRoot, reopened, keys, MetadataDevice, TimeProvider.System,
            (_, hash, length, token) => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!));
        (await resumed.RunAsync(token: Token)).Completed.ShouldBe(1); reopened.ReadDirectoryGraph(source.SourceId)!.Complete.ShouldBeTrue();
        reopened.ReadDirectoryMetadataSource(source.SourceId).ShouldBeNull(); reopened.LocalFiles.Count().ShouldBe(0);
    }

    [Fact]
    public async Task MetadataFailureBackoffDoesNotStarveAnIndependentSourceWithAFrozenClock()
    {
        await using var producer = await FolderIndex.OpenAsync(Database, Token); var root = await PrepareProjection(producer, buildGraph: false);
        var first = DirectoryMetadataSources.FromVersion(VersionView.Open(root, keys, device))!;
        var body = VersionView.Open(root, keys, device).ExportBody(); body.ChangedTicks++;
        var second = DirectoryMetadataSources.FromVersion(VersionView.Open(SignedRecordFactory.Create(body, keys), keys, device))!;
        var sorted = new[] { first, second }.OrderBy(source => Convert.ToHexString(source.SourceId), StringComparer.Ordinal).ToArray();
        var unavailable = sorted[0] with { RouteHint = device }; var available = sorted[1] with { RouteHint = [2, .. new byte[31]] };
        await using var receiver = await OpenMetadataReceiver(keys); var clock = new CompletionClock(DateTimeOffset.UtcNow);
        await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [unavailable, available], clock.GetUtcNow().UtcTicks, Token);
        using var published = new PublishedBlocks(Root, producer);
        var worker = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, clock, (peer, hash, length, token) =>
            peer.Span[0] == 2 ? ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!) : throw new IOException("route unavailable"));
        (await worker.RunAsync(maximumSources: 1, token: Token)).Deferred.ShouldBe(1);
        var failed = receiver.ReadDirectoryMetadataSource(unavailable.SourceId)!; failed.NextTicks.ShouldBe(clock.GetUtcNow().UtcTicks + 10 * TimeSpan.TicksPerSecond);
        (await worker.RunAsync(maximumSources: 1, token: Token)).Completed.ShouldBe(1);
        receiver.ReadDirectoryMetadataSource(available.SourceId).ShouldBeNull(); receiver.ReadDirectoryMetadataSource(unavailable.SourceId)!.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task MetadataRetainedDirectoryRecoveryRestartsAfterAdmissionWithoutNativeOrSignedChanges()
    {
        WriteFiles("a"); using var access = FolderKeys.FromText(keys.ExportReadOnlyText()); byte[] sourceId;
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            var pending = await PrepareCompletion(index, access); sourceId = pending.Source.SourceId;
            (await CompletionApplier(index, access).CompleteCaseCopiesAsync(Token)).ShouldBe(["TOPIC"]);
            Sql("DELETE FROM directory_metadata_sources; DELETE FROM directory_graph_work; DELETE FROM directory_graph_references; DELETE FROM directory_graph_members; DELETE FROM directory_graphs; DELETE FROM local_files; DELETE FROM remote_files; DELETE FROM needed;");
            var first = new DirectoryMetadataWorker(Root, index, access, MetadataDevice, TimeProvider.System,
                (_, _, _, _) => throw new IOException("retained metadata should be owned"))
            { Checkpoint = stage => { if (stage == "metadata-history-admitted") throw new Stop(); } };
            await Should.ThrowAsync<Stop>(() => first.RunAsync(token: Token));
            index.ReadDirectoryMetadataSource(sourceId).ShouldNotBeNull(); index.Meta.Find("_directory-metadata-retained-cursor").ShouldBeNull();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var resumed = new DirectoryMetadataWorker(Root, reopened, access, MetadataDevice, TimeProvider.System, (_, _, _, _) => throw new IOException("metadata should be owned"));
        (await resumed.RunAsync(token: Token)).Completed.ShouldBe(1); reopened.ReadDirectoryMetadataSource(sourceId).ShouldBeNull();
        reopened.ReadDirectoryGraph(sourceId)!.Complete.ShouldBeTrue(); reopened.LocalFiles.Count().ShouldBe(0);
        Directory.GetDirectories(Root).Any(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal)).ShouldBeTrue();
        (await new BlockReferences(Root, reopened, access, MetadataDevice).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
    }

    private async Task<FolderIndex> OpenMetadataReceiver(FolderKeys access)
    {
        Directory.CreateDirectory(MetadataRoot); var index = await FolderIndex.OpenAsync(MetadataDatabase, Token);
        try { using var scanner = new Scanner(MetadataRoot, index, access, MetadataDevice); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }

    [Fact]
    public async Task MetadataCompletionSqlFailureRetainsTheVerifiedGraphAndRetriesWithoutFetchingAgain()
    {
        await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer, buildGraph: false);
        var source = DirectoryMetadataSources.FromVersion(VersionView.Open(original, keys, device))!;
        await using var receiver = await OpenMetadataReceiver(keys); var clock = new CompletionClock(DateTimeOffset.UtcNow);
        await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [source], clock.GetUtcNow().UtcTicks, Token);
        using var published = new PublishedBlocks(Root, producer); int fetched = 0;
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "metadata-sql-logs"), "metadata-sql", debugEnabled: true);
        var worker = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, clock, (_, hash, length, token) =>
        { fetched++; return ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!); }, journal);
        MetadataSql("CREATE TRIGGER fail_metadata_completion BEFORE DELETE ON directory_metadata_sources BEGIN SELECT RAISE(ABORT,'private metadata SQL'); END;");
        (await worker.RunAsync(token: Token)).Deferred.ShouldBe(1); receiver.ReadDirectoryGraph(source.SourceId)!.Complete.ShouldBeTrue();
        var retained = receiver.ReadDirectoryMetadataSource(source.SourceId)!; retained.Source.Record.Body.ToArray().ShouldBe(original.Body.ToArray()); retained.Attempts.ShouldBe(1);
        MetadataSql("DROP TRIGGER fail_metadata_completion;"); clock.AdvanceTo(retained.NextTicks!.Value); int earlier = fetched;
        (await worker.RunAsync(token: Token)).Completed.ShouldBe(1); fetched.ShouldBe(earlier); receiver.ReadDirectoryMetadataSource(source.SourceId).ShouldBeNull();
        string log = File.ReadAllText(Path.Combine(fixture, "metadata-sql-logs", "debug.jsonl")); log.ShouldContain("\"nativeError\":1811"); log.ShouldNotContain("private metadata SQL");
    }

    [Fact]
    public async Task MetadataWorkerRevalidatesQueuedSignaturesBeforeFetchingOrReleasingSource()
    {
        await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer, buildGraph: false);
        var source = DirectoryMetadataSources.FromVersion(VersionView.Open(original, keys, device))!;
        byte[] signature = original.Signature.ToArray(); signature[0] ^= 1; var corrupted = new SignedRecord(original.Body, signature);
        using var digest = Blake3.Hasher.New(); digest.Update("PeerSync case source v1"u8); digest.Update(corrupted.Body); digest.Update(corrupted.Signature);
        source = source with { Record = corrupted, SourceId = digest.Finalize().AsSpan().ToArray() };
        await using var receiver = await OpenMetadataReceiver(keys);
        await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [source], DateTime.UtcNow.Ticks, Token);
        var worker = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, TimeProvider.System,
            (_, _, _, _) => throw new InvalidOperationException("Unverified source must not use the network."));
        (await worker.RunAsync(token: Token)).Deferred.ShouldBe(1); receiver.ReadDirectoryMetadataSource(source.SourceId)!.Attempts.ShouldBe(1);
        receiver.ReadDirectoryGraph(source.SourceId).ShouldBeNull(); receiver.LocalFiles.Count().ShouldBe(0);
    }

    [Fact]
    public async Task MetadataWorkerFinishesInterruptedRetirementWithinItsSharedGraphBudget()
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer, buildGraph: false);
        var source = DirectoryMetadataSources.FromVersion(VersionView.Open(original, keys, device))!; await using var receiver = await OpenMetadataReceiver(keys);
        await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [source], DateTime.UtcNow.Ticks, Token);
        using var published = new PublishedBlocks(Root, producer);
        var worker = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, TimeProvider.System,
            (_, hash, length, token) => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!));
        (await worker.RunAsync(1, token: Token)).Completed.ShouldBe(0); MetadataSql("UPDATE directory_graphs SET retiring=1;");
        (await worker.RunAsync(1, token: Token)).Visited.ShouldBe(0);
        for (int pass = 0; pass < 12 && receiver.ReadDirectoryMetadataSource(source.SourceId) is not null; pass++) await worker.RunAsync(1, token: Token);
        receiver.ReadDirectoryMetadataSource(source.SourceId).ShouldBeNull(); receiver.ReadDirectoryGraph(source.SourceId)!.Complete.ShouldBeTrue();
    }

    private void MetadataSql(string sql)
    { using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = MetadataDatabase, Pooling = false }.ToString()); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }

    [Fact]
    public async Task MetadataRestartRegistersVerifiedCachedBytesBeforeAdvancingTheirGraphWork()
    {
        WriteFiles("a"); await using var producer = await FolderIndex.OpenAsync(Database, Token); var original = await PrepareProjection(producer, buildGraph: false);
        var source = DirectoryMetadataSources.FromVersion(VersionView.Open(original, keys, device))!;
        using var published = new PublishedBlocks(Root, producer);
        await using (var receiver = await OpenMetadataReceiver(keys))
        {
            await receiver.AdmitDirectoryMetadataSourcesAsync(receiver.Meta.Find("_root-identity")!.Value!, [source], DateTime.UtcNow.Ticks, Token);
            var first = new DirectoryMetadataWorker(MetadataRoot, receiver, keys, MetadataDevice, TimeProvider.System,
                (_, hash, length, token) => ValueTask.FromResult<ReadOnlyMemory<byte>>(published.TryRead(hash.Span, length, token)!))
            { Checkpoint = stage => { if (stage == "metadata-graph-references") throw new Stop(); } };
            await Should.ThrowAsync<Stop>(() => first.RunAsync(1, token: Token)); receiver.ReadBlockObjects().ShouldBeEmpty();
            using var cached = new PublishedBlocks(MetadataRoot, receiver); cached.TryRead(source.Hash, source.Length, Token).ShouldNotBeNull();
        }
        await using var reopened = await FolderIndex.OpenAsync(MetadataDatabase, Token);
        var resumed = new DirectoryMetadataWorker(MetadataRoot, reopened, keys, MetadataDevice, TimeProvider.System,
            (_, _, _, _) => throw new InvalidOperationException("The first graph node is already cached."));
        (await resumed.RunAsync(1, token: Token)).Deferred.ShouldBe(0);
        reopened.ReadBlockObjects().Any(row => row.Hash.AsSpan().SequenceEqual(source.Hash)).ShouldBeTrue();
        reopened.ReadDirectoryGraphWork(source.SourceId)!.Kind.ShouldBe(DirectoryGraphWorkKind.Record);
    }
}
