using System.Buffers.Binary;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class MetadataCleanupTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-MetadataCleanup-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static byte[] Device => Enumerable.Repeat((byte)1, 32).ToArray();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static byte[] Bytes => Enumerable.Range(0, 1001).Select(i => (byte)(i % 251)).ToArray();
    public MetadataCleanupTests() => Directory.CreateDirectory(Root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSweepResumesBoundedMetadataAfterRestartAndPreservesCurrentAndNeededBytes(bool crash)
    {
        await using (var index = await PrepareAsync("current bytes"u8.ToArray()))
        {
            await AddArchiveAsync(index); await AddNeededAsync(index); BlockSweepResult? result = null;
            using (var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "metadata-cleanup", debugEnabled: true))
            {
                var retention = new BlockRetention(Root, index, keys, Device, journal: journal) { Checkpoint = stage => { if (crash && stage == "metadata-pruned") throw new InvalidOperationException("process boundary"); } };
                if (crash) await Should.ThrowAsync<InvalidOperationException>(() => retention.SweepAsync(Token));
                else result = await retention.SweepAsync(Token);
            }
            if (!crash) { result!.MetadataMutations.ShouldBe(1000); result.MetadataCleanupPending.ShouldBeTrue(); }
            Count("archive_content_chunks").ShouldBe(2); Count("archive_contents").ShouldBe(1); index.ReadArchiveProvenance().Single().Attached.ShouldBeFalse();
            string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); log.ShouldNotContain(Root); log.ShouldNotContain("private-history"); log.ShouldNotContain(Convert.ToHexString(Hash(Bytes)));
            if (!crash) { log.ShouldContain("block.metadata_pruned"); log.ShouldContain("batch-limit"); }
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var finished = await new BlockRetention(Root, reopened, keys, Device).SweepAsync(Token);
        finished.MetadataCleanupPending.ShouldBeFalse(); Count("archive_content_chunks").ShouldBe(0); Count("archive_contents").ShouldBe(0); reopened.ReadArchiveProvenance().ShouldBeEmpty();
        File.ReadAllText(Disk("current")).ShouldBe("current bytes"); reopened.Needed.Find("incoming").ShouldNotBeNull();
        reopened.ReadBlockReferences().Single(r => r.Hash.SequenceEqual(Hash([0]))).Kinds.ShouldBe(BlockReferenceKind.Needed);
        using var blocks = new PublishedBlocks(Root, reopened); blocks.TryRead(Hash([0]), 1, Token)!.ShouldBe(new byte[] { 0 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusingAnArchiveRepopulatesExactSignedChunksBeforeRenewingHistoryAndFinishingOldMetadata(bool failCapture)
    {
        await using var index = await PrepareAsync(Bytes); var previous = index.LocalFiles.Find("current")!; var source = await AddArchiveAsync(index);
        await index.ReleaseArchiveSourceAsync(source.SourceId, source.RootId!, DateTime.UtcNow.AddDays(-30).Ticks, DateTime.UtcNow.Ticks, Token);
        var fresh = await new BlockReferences(Root, index, keys, Device).RefreshAsync(Token);
        using (await FolderOperationGate.AcquireAsync(index, Token)) using (await FolderReferenceGate.AcquireAsync(index, Token))
            await index.PruneRetiredMetadataAsync(fresh.SnapshotId!.Value, source.RootId!, DateTime.UtcNow.Ticks, 30, 3, Token);
        Count("archive_content_chunks").ShouldBe(999); index.FindActiveArchiveContent(source.SourceId).ShouldBeNull();
        // Simulate a legacy/missing cache while the verified native archive
        // still contains the exact signed current version.
        File.Delete(BlockPath(Hash(Bytes))); File.WriteAllText(Disk("current"), "new user bytes");
        if (failCapture)
            Sql("CREATE TRIGGER reject_recapture BEFORE INSERT ON block_objects WHEN NEW.hash=X'" + Convert.ToHexString(Hash(Bytes)) + "' BEGIN SELECT RAISE(ABORT,'private recapture failure'); END;");
        using (var scanner = new Scanner(Root, index, keys, Device))
        {
            if (failCapture) await Should.ThrowAsync<SqliteException>(() => scanner.ScanAsync(Token)); else await scanner.ScanAsync(Token);
        }
        if (failCapture)
        {
            index.ReadApplyIntents().Count().ShouldBe(1); index.IsArchiveSourceReleased(source.SourceId, source.RootId!).ShouldBeTrue();
            File.ReadAllBytes(Disk(source.Archive.ArchivePath)).ShouldBe(Bytes); File.ReadAllText(Disk("current")).ShouldBe("new user bytes");
            Sql("DROP TRIGGER reject_recapture;"); using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token);
        }
        index.ReadApplyIntents().ShouldBeEmpty(); index.IsArchiveSourceReleased(source.SourceId, source.RootId!).ShouldBeFalse();
        var marker = index.Meta.ReadAll().Single(m => m.Key.StartsWith("_history-", StringComparison.Ordinal)); var state = HistoryState.DecodeMarker(marker, keys);
        state.Source.Record.Body.ToArray().ShouldBe(previous.Record!.Body.ToArray());
        using (var blocks = new PublishedBlocks(Root, index)) blocks.TryRead(Hash(Bytes), Bytes.Length, Token)!.ShouldBe(Bytes);
        var cleanup = await new BlockRetention(Root, index, keys, Device).SweepAsync(Token); cleanup.MetadataCleanupPending.ShouldBeFalse(); Count("archive_contents").ShouldBe(0);
        index.ReadArchiveProvenance().Single().SourceId.ShouldBe(source.SourceId); index.Meta.Find(marker.Key).ShouldNotBeNull();
        File.Delete(Disk(source.Archive.ArchivePath)); using var independent = new PublishedBlocks(Root, index); independent.TryRead(Hash(Bytes), Bytes.Length, Token)!.ShouldBe(Bytes);
        File.ReadAllText(Disk("current")).ShouldBe("new user bytes");
    }

    private async Task<FolderIndex> PrepareAsync(byte[] current)
    {
        File.WriteAllBytes(Disk("current"), current); var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    private async Task<ArchiveProvenance> AddArchiveAsync(FolderIndex index)
    {
        Directory.CreateDirectory(Disk(".psp/archive")); File.WriteAllBytes(Disk(".psp/archive/private-history"), Bytes);
        using var operation = await FolderOperationGate.AcquireAsync(index, Token); using var references = await FolderReferenceGate.AcquireAsync(index, Token);
        await index.Archive.UpsertAsync([new(0, "current", ".psp/archive/private-history", DateTime.UtcNow.AddDays(-40).Ticks, Bytes.Length, Hash(Bytes))], Token);
        var source = index.ReadArchiveProvenance().Single(); using var tree = new SafeFileTree(Root); using var archive = tree.Directory(".psp/archive", privatePath: true);
        using var file = archive.OpenReadGuard("private-history"); GuardedFile.Hash(file, Token).ShouldBe(Hash(Bytes));
        using var blocks = new PublishedBlocks(tree); var chunks = new List<LocalChunk>(); byte[] bytes = Bytes;
        for (int i = 0; i < bytes.Length; i++) { byte[] hash = Hash(bytes.AsSpan(i, 1)); blocks.Put(bytes.AsSpan(i, 1), hash, Token); chunks.Add(new(i, 1, hash)); }
        await blocks.FlushAsync(index, Token);
        var manifest = await index.BeginArchiveContentAsync(source, tree.Identity.ToArray(), BlockCaptureProof.Identity(file), DateTime.UtcNow.Ticks, Token);
        foreach (var batch in chunks.Chunk(1000)) await index.AppendArchiveContentChunksAsync(manifest.Id, batch, Token);
        await index.ActivateArchiveContentAsync(manifest.Id, Token); return source;
    }
    private async Task AddNeededAsync(FolderIndex index)
    {
        byte[] peer = Enumerable.Repeat((byte)2, 32).ToArray(), hash = Hash([0]);
        var body = new FileRecordBody { Path = "incoming", Size = 1, MtimeTicks = DateTime.UtcNow.Ticks, ChangedTicks = DateTime.UtcNow.Ticks,
            ModifiedBy = ByteString.CopyFrom(peer), FileHash = ByteString.CopyFrom(hash) };
        body.Vv.Add(new VvEntry { Device = BinaryPrimitives.ReadUInt64LittleEndian(peer), Counter = 1 }); body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(hash), Length = 1 });
        await new NeededSet(index, keys, Device).ApplyIndexUpdateAsync(peer, 0, 1, [SignedRecordFactory.Create(body, keys, 1)], Token);
    }
    private static byte[] Hash(ReadOnlySpan<byte> bytes) => Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    private string BlockPath(byte[] hash) => Disk(".psp/blocks/" + Convert.ToHexString(hash)[..2] + "/" + Convert.ToHexString(hash));
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private long Count(string table)
    { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table; return (long)command.ExecuteScalar()!; }
    private SqliteConnection Open()
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open(); return connection; }
    private void Sql(string sql)
    { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-MetadataCleanup-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
