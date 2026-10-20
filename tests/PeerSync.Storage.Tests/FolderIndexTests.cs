using System.Buffers.Binary;
using System.Text;
using PeerSync.Core.Sync;
using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class FolderIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PeerSync-Index-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(root, "folder.db");
    private static readonly byte[] Hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Peer = Enumerable.Repeat((byte)42, 32).ToArray();
    public FolderIndexTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task NativeChangeCacheRoundTripsAndCompareAndSwapNeverChangesPublicationMetadata()
    {
        await using var index = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken);
        await index.LocalFiles.UpsertAsync([Row("file") with { FileId = new byte[16], ChangeFileTime = 1 }], TestContext.Current.CancellationToken);
        var before = index.LocalFiles.Find("file")!; before.ChangeFileTime.ShouldBe(1);
        (await index.UpdateNativeChangeTimesAsync([new("file", before.LocalSequence + 1, new byte[16], 2)], TestContext.Current.CancellationToken)).ShouldBe(0);
        (await index.UpdateNativeChangeTimesAsync([new("file", before.LocalSequence, Enumerable.Repeat((byte)1, 16).ToArray(), 2)], TestContext.Current.CancellationToken)).ShouldBe(0);
        (await index.UpdateNativeChangeTimesAsync([new("file", before.LocalSequence, new byte[16], 2)], TestContext.Current.CancellationToken)).ShouldBe(1);
        var after = index.LocalFiles.Find("file")!; after.ChangeFileTime.ShouldBe(2); after.LocalSequence.ShouldBe(before.LocalSequence);
        after.Record!.Body.ToArray().ShouldBe(before.Record!.Body.ToArray()); after.Vector.ShouldBe(before.Vector);
        await Should.ThrowAsync<ArgumentException>(() => index.UpdateNativeChangeTimesAsync([new("file", 1, new byte[16], -1)], TestContext.Current.CancellationToken));
        index.LocalFiles.Find("file")!.ChangeFileTime.ShouldBe(2);
    }

    [Fact]
    public async Task LocalRowsRoundTripExactEnvelopesVectorsAndUnicodeKeysWithAllocatedSequences()
    {
        await using var index = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken);
        var original = Row("Żółć/É.txt") with { Attributes = 3, MtimeTicks = 638000000000000000, FileId = new byte[16], LocalSequence = 88 };
        (await index.LocalFiles.UpsertAsync([original], TestContext.Current.CancellationToken)).ShouldBe(1);
        var saved = index.LocalFiles.Find("żÓŁĆ/é.TXT");
        saved.ShouldNotBeNull();
        saved.Path.ShouldBe(original.Path);
        saved.Attributes.ShouldBe(3u);
        saved.MtimeTicks.ShouldBe(original.MtimeTicks);
        saved.LocalSequence.ShouldBe(1);
        saved.FileId.ShouldBe(original.FileId);
        VersionVector.Compare(saved.Vector, original.Vector).ShouldBe(VvOrder.Equal);
        saved.Record!.Body.ToArray().ShouldBe(original.Record!.Body.ToArray());
        saved.Record.Signature.ToArray().ShouldBe(original.Record.Signature.ToArray());
        saved.Record.LocalSequence.ShouldBe(1ul);
        original.Record.LocalSequence.ShouldBe(999ul);
        await index.LocalFiles.UpsertAsync([original with { Path = "żółć/é.txt" }], TestContext.Current.CancellationToken);
        index.LocalFiles.Count().ShouldBe(1);
        index.LocalFiles.Find("ŻÓŁĆ/É.TXT")!.LocalSequence.ShouldBe(2);
        (await index.LocalFiles.DeleteAsync(["Żółć/é.txt"], TestContext.Current.CancellationToken)).ShouldBe(1);
        index.LocalFiles.Count().ShouldBe(0);
    }

    [Fact]
    public async Task VersionOneDatabaseMigratesWithBackupAndChunkRemovalUsesThePathIndex()
    {
        using (var resource = typeof(FolderIndex).Assembly.GetManifestResourceStream("PeerSync.Storage.FolderDb.Migrations.0001_initial.sql")!)
        using (var reader = new StreamReader(resource))
        {
            string sql = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            await using var original = await Db.OpenAsync(DatabasePath, [new(1, sql)], TestContext.Current.CancellationToken);
        }
        await using (var index = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken))
        {
            await index.LocalFiles.UpsertAsync([Row("file") with { IndexedChunks = [new(0, 1, Hash)] }], TestContext.Current.CancellationToken);
            index.FindChunks(Hash).Single().PathKey.ShouldBe("FILE");
        }
        Directory.GetFiles(root, "*.pre-v1-to-v2-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v2-to-v3-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v3-to-v4-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v4-to-v5-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v5-to-v6-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v6-to-v7-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v7-to-v8-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v8-to-v9-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v9-to-v10-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v10-to-v11-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v11-to-v12-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v12-to-v13-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v13-to-v14-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v14-to-v15-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v15-to-v16-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v16-to-v17-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v17-to-v18-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v18-to-v19-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v19-to-v20-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v20-to-v21-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v21-to-v22-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v22-to-v23-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v23-to-v24-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v24-to-v25-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v25-to-v26-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v26-to-v27-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v27-to-v28-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v28-to-v29-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v29-to-v30-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v30-to-v31-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v31-to-v32-*.bak").Length.ShouldBe(1);
        Directory.GetFiles(root, "*.pre-v32-to-v33-*.bak").Length.ShouldBe(1);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        ((long)command.ExecuteScalar()!).ShouldBe(33);
        command.CommandText = "EXPLAIN QUERY PLAN DELETE FROM chunks WHERE path_key=$path;";
        command.Parameters.AddWithValue("$path", "FILE");
        var details = new List<string>();
        using (var query = command.ExecuteReader()) while (query.Read()) details.Add(query.GetString(3));
        string.Join('\n', details).ShouldContain("ix_chunks_path");
        string.Join('\n', details).ShouldNotContain("SCAN chunks");
        command.CommandText = "EXPLAIN QUERY PLAN SELECT device_id,vv,record_body,signature FROM remote_files WHERE path_key=$path;";
        details.Clear();
        using (var query = command.ExecuteReader()) while (query.Read()) details.Add(query.GetString(3));
        string.Join('\n', details).ShouldContain("ix_remote_path");
        string.Join('\n', details).ShouldNotContain("SCAN remote_files");
        command.Parameters.Clear(); command.CommandText = "EXPLAIN QUERY PLAN SELECT id FROM archive WHERE original_path=$path AND file_hash=$hash AND size=$size;";
        command.Parameters.AddWithValue("$path", "file"); command.Parameters.AddWithValue("$hash", Hash); command.Parameters.AddWithValue("$size", 1);
        details.Clear(); using (var query = command.ExecuteReader()) while (query.Read()) details.Add(query.GetString(3));
        string.Join('\n', details).ShouldContain("ix_archive_version"); string.Join('\n', details).ShouldNotContain("SCAN archive");
    }

    [Fact]
    public async Task EveryOtherTableSupportsParameterizedUpsertReadUpdateAndDelete()
    {
        await using var index = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken);
        var record = Row("dir/file").Record!;
        var vector = new Dictionary<ulong, ulong> { [7] = 9 };
        await index.RemoteFiles.UpsertAsync([new(Peer, "dir/file", vector, record)], TestContext.Current.CancellationToken);
        var remote = index.RemoteFiles.Find(Peer, "DIR/file")!;
        remote.PathKey.ShouldBe("DIR/FILE");
        remote.Record.Body.ToArray().ShouldBe(record.Body.ToArray());
        remote.Record.Signature.ToArray().ShouldBe(record.Signature.ToArray());
        remote.Record.LocalSequence.ShouldBe(0ul);
        VersionVector.Compare(remote.Vector, vector).ShouldBe(VvOrder.Equal);
        index.RemotesForPath("dir/File").Count().ShouldBe(1);

        await index.Needed.UpsertAsync([new("dir/file", Peer, 1), new("other", Peer, 9)], TestContext.Current.CancellationToken);
        index.NeededByPriority().First().PathKey.ShouldBe("OTHER");
        await index.Needed.UpsertAsync([new("dir/file", Peer, 10)], TestContext.Current.CancellationToken);
        index.NeededByPriority().First().PathKey.ShouldBe("DIR/FILE");

        await index.Chunks.UpsertAsync([new(Hash, "dir/file", 0, 1), new(Hash, "dir/file", 4, 3)], TestContext.Current.CancellationToken);
        index.FindChunks(Hash).Select(c => c.Offset).ShouldBe(new long[] { 0, 4 });
        index.Chunks.Find(Hash, "DIR/FILE", 4L)!.Length.ShouldBe(3);

        await index.Devices.UpsertAsync([new(Peer, "Żółć", 638000000000000000, 11, DeviceApproval.Approved, false)], TestContext.Current.CancellationToken);
        var device = index.Devices.Find(Peer)!;
        device.Name.ShouldBe("Żółć"); device.LastSequenceReceived.ShouldBe(11);
        device.Approval.ShouldBe(DeviceApproval.Approved); device.Pinned.ShouldBeFalse();
        await index.Devices.UpsertAsync([device with { Name = "Updated" }], TestContext.Current.CancellationToken);
        index.Devices.Find(Peer)!.Name.ShouldBe("Updated");

        await index.Archive.UpsertAsync([new(0, "old.txt", ".psp/archive/old~20261019.txt", 638000000000000000, 3, Hash),
            new(0, "old.txt", ".psp/archive/old~20261019-2.txt", 638000000000000001, 4, null)], TestContext.Current.CancellationToken);
        var archive = index.Archive.ReadAll().OrderBy(a => a.Id).ToArray();
        archive.Length.ShouldBe(2); archive[0].Id.ShouldBeGreaterThan(0);
        archive[0].FileHash.ShouldBe(Hash); archive[1].FileHash.ShouldBeNull();

        await index.Meta.UpsertAsync([new("scan ' ; --", [1, 2, 3]), new("empty", []), new("null", null)], TestContext.Current.CancellationToken);
        index.Meta.Find("scan ' ; --")!.Value.ShouldBe(new byte[] { 1, 2, 3 });
        index.Meta.Find("empty")!.Value.ShouldBeEmpty(); index.Meta.Find("null")!.Value.ShouldBeNull();

        (await index.RemoteFiles.DeleteAsync([Peer, "dir/file"], TestContext.Current.CancellationToken)).ShouldBe(1);
        (await index.Needed.DeleteAsync(["dir/file"], TestContext.Current.CancellationToken)).ShouldBe(1);
        (await index.Chunks.DeleteAsync([Hash, "dir/file", 4L], TestContext.Current.CancellationToken)).ShouldBe(1);
        (await index.Devices.DeleteAsync([Peer], TestContext.Current.CancellationToken)).ShouldBe(1);
        (await index.Archive.DeleteAsync([archive[0].Id], TestContext.Current.CancellationToken)).ShouldBe(1);
        (await index.Meta.DeleteAsync(["scan ' ; --"], TestContext.Current.CancellationToken)).ShouldBe(1);
        index.RemoteFiles.Count().ShouldBe(0); index.Devices.Count().ShouldBe(0);
    }

    [Fact]
    public async Task LocalChunkReplacementAndDeletionCommitWithTheFileRow()
    {
        await using var index = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken);
        var row = Row("file") with { Size = 3, IndexedChunks = [new(0, 1, Hash), new(1, 2, Peer)] };
        await index.LocalFiles.UpsertAsync([row], TestContext.Current.CancellationToken);
        index.FindChunks(Hash).Single().Length.ShouldBe(1);
        index.FindChunks(Peer).Single().Offset.ShouldBe(1);
        await index.LocalFiles.UpsertAsync([row with { IndexedChunks = [new(0, 3, Peer)] }], TestContext.Current.CancellationToken);
        index.FindChunks(Hash).ShouldBeEmpty(); index.FindChunks(Peer).Single().Length.ShouldBe(3);
        await index.LocalFiles.UpsertAsync([row with { Deleted = true, IndexedChunks = [] }], TestContext.Current.CancellationToken);
        index.Chunks.Count().ShouldBe(0);
        index.LocalFiles.Find("file")!.Deleted.ShouldBeTrue();
        await index.LocalFiles.UpsertAsync([row], TestContext.Current.CancellationToken);
        index.Chunks.Count().ShouldBe(2);
        await index.LocalFiles.DeleteAsync(["file"], TestContext.Current.CancellationToken);
        index.Chunks.Count().ShouldBe(0); index.LocalFiles.Find("file").ShouldBeNull();
    }

    [Fact]
    public async Task ThousandRowBatchesCommitIndependentlyAndFailedBatchDoesNotAdvanceSequence()
    {
        await using var index = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken);
        var rows = Enumerable.Range(0, 1001).Select(i => Row($"file-{i}")).ToArray();
        rows[^1] = rows[^1] with { Path = "../invalid" };
        await Should.ThrowAsync<ArgumentException>(() => index.LocalFiles.UpsertAsync(rows, TestContext.Current.CancellationToken));
        index.LocalFiles.Count().ShouldBe(1000);
        var batch = index.ReadIndexBatch(0);
        batch.Records.Count.ShouldBe(1000); batch.LastSequence.ShouldBe(1000);
        index.ReadIndexBatch(1000).Records.ShouldBeEmpty();

        byte[] nearOverflow = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(nearOverflow, long.MaxValue - 1);
        await index.Meta.UpsertAsync([new("_local-sequence", nearOverflow)], TestContext.Current.CancellationToken);
        await Should.ThrowAsync<OverflowException>(() => index.LocalFiles.UpsertAsync([Row("rollback-first"), Row("overflow-second")], TestContext.Current.CancellationToken));
        index.LocalFiles.Find("rollback-first").ShouldBeNull();
        index.Meta.Find("_local-sequence")!.Value.ShouldBe(nearOverflow);
    }

    [Fact]
    public async Task UnsyncableLocalNamesArePreservedButNeverPublishedAndRemotePathsRemainStrict()
    {
        await using var index = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken);
        string decomposed = "café.txt".Normalize(NormalizationForm.FormD);
        var row = Row(decomposed) with { Record = null, Flags = LocalFileState.Unsyncable };
        await index.LocalFiles.UpsertAsync([row], TestContext.Current.CancellationToken);
        index.LocalFiles.Find(decomposed)!.Path.ShouldBe(decomposed);
        var batch = index.ReadIndexBatch(0);
        batch.Records.ShouldBeEmpty(); batch.LastSequence.ShouldBe(1);
        await Should.ThrowAsync<ArgumentException>(() => index.LocalFiles.UpsertAsync([row with { Record = Row("file").Record }], TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => index.RemoteFiles.UpsertAsync([new(Peer, "../escape", new Dictionary<ulong, ulong>(), Row("file").Record!)], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    public async Task SnapshotCursorDoesNotSkipRecordsDuringConcurrentWrites(int limit)
    {
        await using var index = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken);
        var writing = Task.WhenAll(Enumerable.Range(0, 60).Select(i => index.LocalFiles.UpsertAsync([Row($"file-{i}")], TestContext.Current.CancellationToken)));
        var seen = new HashSet<long>();
        long cursor = 0;
        while (true)
        {
            // If completion is observed only after the read, the last commit
            // can land between an empty snapshot and the exit check.
            bool completedBeforeRead = writing.IsCompleted;
            var batch = index.ReadIndexBatch(cursor, limit);
            foreach (var row in batch.Records)
            {
                row.LocalSequence.ShouldBeGreaterThan(cursor);
                row.LocalSequence.ShouldBeLessThanOrEqualTo(batch.LastSequence);
                seen.Add(row.LocalSequence).ShouldBeTrue();
            }
            cursor = batch.LastSequence;
            if (completedBeforeRead && batch.Records.Count == 0) break;
            await Task.Yield();
        }
        await writing;
        seen.Count.ShouldBe(60); cursor.ShouldBe(60);
    }

    [Fact]
    public async Task UncapturedLiveRecordsAreWithheldUntilDurableReadinessButDirectoriesAndTombstonesNeedNoData()
    {
        await using var index = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken);
        var row = Row("file") with { ContentReady = false };
        await index.LocalFiles.UpsertAsync([row, Row("dir") with { Type = 1, ContentReady = false },
            Row("deleted") with { Deleted = true, ContentReady = false }], TestContext.Current.CancellationToken);
        var batch = index.ReadIndexBatch(0); batch.Records.Select(r => r.Path).ShouldBe(["dir", "deleted"]); batch.LastSequence.ShouldBe(3);
        await index.LocalFiles.UpsertAsync([row with { ContentReady = true }], TestContext.Current.CancellationToken);
        var ready = index.ReadIndexBatch(batch.LastSequence).Records.Single(); ready.Path.ShouldBe("file"); ready.ContentReady.ShouldBeTrue();
        ready.Record!.Body.ToArray().ShouldBe(row.Record!.Body.ToArray());
    }

    [Fact]
    public async Task VersionFourRecordsMigrateWithExactBytesAndAreWithheldUntilContentIsCaptured()
    {
        var migrations = new List<DbMigration>();
        foreach (string file in new[] { "0001_initial.sql", "0002_chunk_path.sql", "0003_remote_path.sql", "0004_apply_intents.sql" })
        {
            using var resource = typeof(FolderIndex).Assembly.GetManifestResourceStream("PeerSync.Storage.FolderDb.Migrations." + file)!;
            using var reader = new StreamReader(resource); migrations.Add(new(migrations.Count + 1, await reader.ReadToEndAsync(TestContext.Current.CancellationToken)));
        }
        var row = Row("legacy");
        await using (var old = await Db.OpenAsync(DatabasePath, migrations, TestContext.Current.CancellationToken))
        {
            await old.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO local_files(path_key,path,type,size,mtime_ticks,attributes,changed_ticks,file_hash,vv,record_body,signature,local_seq) VALUES('LEGACY','legacy',0,1,0,0,0,$hash,$vector,$body,$signature,1);";
                command.Parameters.AddWithValue("$hash", Hash); command.Parameters.AddWithValue("$vector", VersionVector.Serialize(row.Vector));
                command.Parameters.AddWithValue("$body", row.Record!.Body.ToArray()); command.Parameters.AddWithValue("$signature", row.Record.Signature.ToArray());
                int written = command.ExecuteNonQuery();
                command.Parameters.Clear(); command.CommandText = "INSERT INTO meta(key,value) VALUES('_local-sequence',$sequence);";
                byte[] sequence = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(sequence, 1);
                command.Parameters.AddWithValue("$sequence", sequence); command.ExecuteNonQuery(); return written;
            }, TestContext.Current.CancellationToken);
        }
        await using var migrated = await FolderIndex.OpenAsync(DatabasePath, TestContext.Current.CancellationToken);
        var saved = migrated.LocalFiles.Find("legacy")!; saved.ContentReady.ShouldBeFalse(); saved.LocalSequence.ShouldBe(1);
        saved.Record!.Body.ToArray().ShouldBe(row.Record!.Body.ToArray()); saved.Record.Signature.ToArray().ShouldBe(row.Record.Signature.ToArray());
        VersionVector.Compare(saved.Vector, row.Vector).ShouldBe(VvOrder.Equal); migrated.ReadIndexBatch(0).Records.ShouldBeEmpty();
        migrated.ReadIndexBatch(0).LastSequence.ShouldBe(1);
        await migrated.LocalFiles.UpsertAsync([saved with { ContentReady = true }], TestContext.Current.CancellationToken);
        migrated.ReadIndexBatch(1).Records.Single().Record!.Body.ToArray().ShouldBe(row.Record.Body.ToArray());
        Directory.GetFiles(root, "*.pre-v4-to-v5-*.bak").Length.ShouldBe(1);
    }

    private static LocalFileRow Row(string path) => new(path, 0, 1, 638000000000000000, 0, false, 638000000000000000,
        Hash, new Dictionary<ulong, ulong> { [ulong.MaxValue] = 7, [1] = 5 },
        new SignedRecord("opaque storage fixture"u8, new byte[64], 999), null) { ContentReady = true };

    public void Dispose() => Directory.Delete(root, recursive: true);
}
