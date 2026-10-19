using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Storage.FolderDb;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Engine.Sync;
using PeerSync.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class BlockOwnershipTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-BlockOwnership-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private readonly byte[] device = Enumerable.Repeat((byte)1, 32).ToArray();
    private readonly List<string> links = [];
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public BlockOwnershipTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task RegistrationPersistsExactIndependentObjectAndRemovesProofOnlyAfterCommit()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(123456), hash = Hash(bytes), identity;
        await using (var index = await BindAsync())
        {
            using var tree = new SafeFileTree(Root); using var blocks = new PublishedBlocks(tree);
            blocks.Put(bytes, hash, Token); index.ReadBlockObjects().ShouldBeEmpty();
            Directory.GetFiles(ProofDirectory).Length.ShouldBe(1);
            using (var parent = tree.Directory(Parent(hash), privatePath: true))
            using (var file = parent.OpenReadGuard(Convert.ToHexString(hash))) identity = BlockCaptureProof.Identity(file);
            (await blocks.FlushAsync(index, Token)).ShouldBe(1); Directory.GetFiles(ProofDirectory).ShouldBeEmpty();
            var row = index.ReadBlockObjects().Single(); row.RootId.ShouldBe(tree.Identity.ToArray()); row.FileId.ShouldBe(identity);
            row.Hash.ShouldBe(hash); row.Length.ShouldBe(bytes.Length); row.LastUsedTicks.ShouldBeGreaterThanOrEqualTo(row.CapturedTicks);
            index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        reopened.ReadBlockObjects().Single().FileId.ShouldBe(identity);
    }

    [Fact]
    public async Task InstalledCaptureRecoversAfterOwnerAndDatabaseRestartWithoutPublishingAFile()
    {
        byte[] bytes = "installed but unregistered"u8.ToArray(), hash = Hash(bytes);
        await using (var index = await BindAsync())
        {
            using var tree = new SafeFileTree(Root);
            using var blocks = new PublishedBlocks(tree) { Checkpoint = stage => { if (stage == "installed") throw new IOException("fault"); } };
            Should.Throw<IOException>(() => blocks.Put(bytes, hash, Token)); index.ReadBlockObjects().ShouldBeEmpty();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); using var recoveryTree = new SafeFileTree(Root);
        var result = await PublishedBlocks.RecoverCapturesAsync(recoveryTree, reopened, cancellationToken: Token);
        result.Registered.ShouldBe(1); result.Installed.ShouldBe(0); result.ProofsRemoved.ShouldBe(1); result.Deferred.ShouldBe(0);
        reopened.ReadBlockObjects().Single().Hash.ShouldBe(hash); reopened.ReadIndexBatch(0).Records.ShouldBeEmpty();
        using var reader = new PublishedBlocks(Root, reopened); reader.TryRead(hash, bytes.Length, Token).ShouldBe(bytes);
        (await PublishedBlocks.RecoverCapturesAsync(recoveryTree, reopened, cancellationToken: Token)).ShouldBe(default);
    }

    [Fact]
    public async Task CompleteOwnedStagingIsInstalledAndRegisteredBeforeScannerWalks()
    {
        await using var index = await BindAsync(); byte[] bytes = "complete staging fixture"u8.ToArray();
        var proof = Stage(bytes, bytes);
        using var scanner = new Scanner(Root, index, keys, device); await scanner.ScanAsync(Token);
        index.ReadBlockObjects().Single().FileId.ShouldBe(proof.DataId); File.Exists(Disk(proof.Parent + "/" + proof.Final)).ShouldBeTrue();
        File.Exists(Disk(proof.Parent + "/" + proof.Temporary)).ShouldBeFalse(); Directory.GetFiles(ProofDirectory).ShouldBeEmpty();
        index.ReadIndexBatch(0).Records.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("corrupt")]
    [InlineData("replaced-temp")]
    [InlineData("occupied-final")]
    [InlineData("changed-final")]
    public async Task IncompleteChangedOrUnknownDataIsPreservedAndCannotGainOwnership(string scenario)
    {
        await using var index = await BindAsync(); byte[] bytes = "expected immutable bytes"u8.ToArray();
        byte[] actual = scenario == "partial" ? bytes[..3] : scenario == "corrupt" ? new byte[bytes.Length] : bytes;
        var proof = Stage(bytes, actual); string temporary = Disk(proof.Parent + "/" + proof.Temporary), final = Disk(proof.Parent + "/" + proof.Final);
        if (scenario == "replaced-temp") { File.Move(temporary, temporary + ".unknown"); File.WriteAllBytes(temporary, actual); }
        if (scenario == "occupied-final") File.WriteAllBytes(final, bytes);
        if (scenario == "changed-final")
        {
            File.Move(temporary, final);
            // Preserve identity and hidden attributes; CREATE_ALWAYS rejects a
            // preexisting hidden object even when its DACL permits writing.
            using var file = File.OpenHandle(final, FileMode.Open, FileAccess.Write, FileShare.Read);
            RandomAccess.Write(file, new byte[bytes.Length], 0); RandomAccess.FlushToDisk(file);
        }
        string[] files = Directory.GetFiles(Root, "*", SearchOption.AllDirectories);
        var snapshots = files.ToDictionary(p => p, File.ReadAllBytes);
        using var tree = new SafeFileTree(Root); var result = await PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token);
        result.Deferred.ShouldBe(1); result.Registered.ShouldBe(0); result.ProofsRemoved.ShouldBe(0); index.ReadBlockObjects().ShouldBeEmpty();
        foreach (var pair in snapshots) File.ReadAllBytes(pair.Key).ShouldBe(pair.Value);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("root")]
    [InlineData("marker")]
    [InlineData("name")]
    [InlineData("length")]
    [InlineData("truncated")]
    public async Task InvalidOwnershipProofsNeverAuthorizeDataMutation(string invalid)
    {
        await using var index = await BindAsync(); byte[] bytes = "unchanged staging"u8.ToArray(); var proof = Stage(bytes, bytes);
        string path = Disk(BlockCaptureProof.Directory + "/" + proof.Name); byte[] encoded;
        if (invalid == "root") encoded = (proof with { RootId = new byte[24] }).Encode();
        else if (invalid == "marker") encoded = (proof with { MarkerId = new byte[16] }).Encode();
        else if (invalid == "length") encoded = (proof with { Length = 0 }).Encode();
        else { encoded = proof.Encode(); if (invalid == "checksum") encoded[^1] ^= 1; if (invalid == "truncated") encoded = encoded[..80]; }
        File.WriteAllBytes(path, encoded);
        if (invalid == "name") { string next = Disk(BlockCaptureProof.Directory + "/" + Guid.NewGuid().ToString("N") + ".capture"); File.Move(path, next); path = next; }
        using var tree = new SafeFileTree(Root); var result = await PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token);
        result.Deferred.ShouldBe(1); result.Registered.ShouldBe(0); index.ReadBlockObjects().ShouldBeEmpty();
        File.ReadAllBytes(path).ShouldBe(encoded); File.ReadAllBytes(Disk(proof.Parent + "/" + proof.Temporary)).ShouldBe(bytes);
    }

    [Fact]
    public async Task AbsentDataPermitsOnlyVerifiedProofCleanup()
    {
        await using var index = await BindAsync(); var proof = Stage("missing data"u8.ToArray(), []);
        File.Delete(Disk(proof.Parent + "/" + proof.Temporary));
        using var tree = new SafeFileTree(Root); var result = await PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token);
        result.ProofsRemoved.ShouldBe(1); result.Registered.ShouldBe(0); result.Deferred.ShouldBe(0); index.ReadBlockObjects().ShouldBeEmpty();
    }

    [Fact]
    public async Task DatabaseFailureWithholdsPublicationAndLeavesProofForNextRecovery()
    {
        await using var index = await BindAsync(); byte[] bytes = "database failure bytes"u8.ToArray(); File.WriteAllBytes(Disk("file"), bytes);
        Sql("CREATE TRIGGER reject_block BEFORE INSERT ON block_objects BEGIN SELECT RAISE(ABORT,'test registration failure'); END;");
        using var scanner = new Scanner(Root, index, keys, device);
        await Should.ThrowAsync<SqliteException>(() => scanner.ScanAsync(Token));
        index.ReadIndexBatch(0).Records.ShouldBeEmpty(); index.LocalFiles.Find("file").ShouldBeNull(); index.ReadBlockObjects().ShouldBeEmpty();
        Directory.GetFiles(ProofDirectory).Length.ShouldBe(1);
        Sql("DROP TRIGGER reject_block;"); await scanner.ScanAsync(Token);
        index.ReadIndexBatch(0).Records.Single().ContentReady.ShouldBeTrue(); index.ReadBlockObjects().Single().Hash.ShouldBe(Hash(bytes));
        Directory.GetFiles(ProofDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task VerifiedLegacyDataIsAdoptedWithoutRewritingAndDuplicatesKeepOneOwner()
    {
        await using var index = await BindAsync(); byte[] bytes = "legacy independently verified"u8.ToArray(), hash = Hash(bytes);
        string path = Disk(Parent(hash) + "/" + Convert.ToHexString(hash)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        using var tree = new SafeFileTree(Root); byte[] id;
        using (var parent = tree.Directory(Parent(hash), privatePath: true))
        using (var file = parent.OpenReadGuard(Convert.ToHexString(hash))) id = BlockCaptureProof.Identity(file);
        using var blocks = new PublishedBlocks(tree);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => blocks.Put(bytes, hash, Token), Token)));
        (await blocks.FlushAsync(index, Token)).ShouldBe(1); index.ReadBlockObjects().Single().FileId.ShouldBe(id);
        Directory.Exists(ProofDirectory).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeRegistersOriginalSignedPartitionWithoutResigningOrLosingReadOnlyPublication(bool readOnly)
    {
        byte[] bytes = "legacy partition differs from current FastCDC boundaries"u8.ToArray();
        File.WriteAllBytes(Disk("file"), bytes); LocalFileRow before;
        await using (var index = await BindAsync())
        {
            before = index.LocalFiles.Find("file")!;
            var body = VersionView.Open(before.Record!, keys, device).ExportBody(); body.Chunks.Clear();
            byte[] first = bytes[..17], second = bytes[17..];
            body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash(first)), Length = (uint)first.Length });
            body.Chunks.Add(new Chunk { Hash = ByteString.CopyFrom(Hash(second)), Length = (uint)second.Length });
            await index.LocalFiles.UpsertAsync([before with { Record = SignedRecordFactory.Create(body, keys, 0),
                IndexedChunks = [new(0, first.Length, Hash(first)), new(first.Length, second.Length, Hash(second))] }], Token);
            before = index.LocalFiles.Find("file")!;
        }
        Sql("DROP TABLE directory_case_members; DROP TABLE directory_case_progress; DROP TABLE directory_publication_cache; DROP TABLE directory_publications; DROP TRIGGER directory_capture_insert; DROP TRIGGER directory_capture_update; DROP TRIGGER directory_capture_delete; DROP TABLE directory_capture_clock; DROP INDEX ix_local_directory_children; DROP INDEX ix_local_directory_sort_backfill; ALTER TABLE local_files DROP COLUMN parent_key; ALTER TABLE local_files DROP COLUMN path_sort_key; DROP TABLE directory_captures; DROP TABLE directory_graph_references; DROP TABLE directory_graph_members; DROP TABLE directory_graph_work; DROP TABLE directory_graphs; DROP TABLE case_preservation_retries; DROP TABLE case_preservations; DROP TABLE staging_retirements; DROP TABLE staging_cleanup_state; DROP TABLE staged_objects; DROP TRIGGER needed_retry_invalidate; DROP TABLE pull_retries; DROP TABLE remote_index_ranges; DROP INDEX ix_needed_work; ALTER TABLE needed DROP COLUMN work_hash; ALTER TABLE needed DROP COLUMN work_mtime; ALTER TABLE needed DROP COLUMN work_size; ALTER TABLE needed DROP COLUMN work_stage; DROP INDEX ix_archive_contents_retiring; DROP INDEX ix_archive_contents_active; CREATE UNIQUE INDEX ix_archive_contents_active ON archive_contents(source_id) WHERE active=1; ALTER TABLE archive_contents DROP COLUMN retiring; DROP TABLE capture_retirements; DROP TABLE block_deletions; DROP TRIGGER block_orphan_registration; DROP TABLE block_orphans; DROP TABLE block_retention_state; ALTER TABLE block_objects DROP COLUMN legacy_retention; DROP TABLE archive_policy_releases; DROP INDEX ix_archive_path; ALTER TABLE archive DROP COLUMN archive_path_key; DROP TRIGGER history_archive_link_invalidate; DROP TABLE history_archive_links; DROP TABLE archive_retirement_sources; DROP TABLE block_reference_sources; DROP TABLE archive_content_chunks; DROP TABLE archive_contents; DROP TRIGGER archive_provenance_insert; DROP TRIGGER archive_provenance_delete; DROP TRIGGER archive_provenance_before_update; DROP TRIGGER archive_provenance_after_update; DROP TABLE archive_provenance; DROP TABLE block_reference_archives; DROP TABLE block_references; DROP TABLE block_reference_snapshots; ALTER TABLE local_files DROP COLUMN change_filetime; DROP TABLE archive_retirements; DROP TABLE block_objects; PRAGMA user_version=6;");
        await using var migrated = await FolderIndex.OpenAsync(Database, Token);
        migrated.ReadIndexBatch(0).Records.ShouldBeEmpty(); migrated.LocalFiles.Find("file")!.ContentReady.ShouldBeFalse();
        using var ro = FolderKeys.FromText(keys.ExportReadOnlyText());
        using var scanner = new Scanner(Root, migrated, readOnly ? ro : keys, device); var result = await scanner.ScanAsync(Token);
        result.Hashed.ShouldBe(1); result.Problems.ShouldBeEmpty(); var after = migrated.ReadIndexBatch(0).Records.Single();
        after.ContentReady.ShouldBeTrue(); after.LocalSequence.ShouldBeGreaterThan(before.LocalSequence);
        after.Record!.Body.ToArray().ShouldBe(before.Record!.Body.ToArray()); after.Record.Signature.ToArray().ShouldBe(before.Record.Signature.ToArray());
        after.Vector.ShouldBe(before.Vector); using var blocks = new PublishedBlocks(Root, migrated);
        foreach (var chunk in bodyChunks())
        {
            blocks.TryRead(chunk.Hash.Span, (int)chunk.Length, Token).ShouldNotBeNull();
            migrated.ReadBlockObjects().Any(r => r.Hash.AsSpan().SequenceEqual(chunk.Hash.Span)).ShouldBeTrue();
        }
        (await scanner.ScanAsync(Token)).Hashed.ShouldBe(0);
        IEnumerable<Chunk> bodyChunks() => VersionView.Open(before.Record!, keys, device).ExportBody().Chunks;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedOrLockedCaptureIsDeferredUntilItCanBeVerifiedIndependently(bool hardlink)
    {
        await using var index = await BindAsync(); byte[] bytes = "held capture"u8.ToArray(); var proof = Stage(bytes, bytes);
        string source = Disk(proof.Parent + "/" + proof.Temporary); using var tree = new SafeFileTree(Root);
        if (hardlink)
        {
            using var parent = tree.Directory(proof.Parent, privatePath: true); using var root = tree.Directory("");
            using var file = parent.OpenFile(proof.Temporary, mutation: true, temporary: true); root.LinkHere(file, "unknown");
        }
        using (var held = hardlink ? null : File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token);
            result.Deferred.ShouldBe(1); result.Registered.ShouldBe(0); File.Exists(Disk(BlockCaptureProof.Directory + "/" + proof.Name)).ShouldBeTrue();
        }
        if (hardlink) File.Delete(Disk("unknown"));
        var retry = await PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token);
        retry.Registered.ShouldBe(1); retry.Installed.ShouldBe(1); retry.ProofsRemoved.ShouldBe(1);
    }

    [Fact]
    public async Task ReparseDataParentCannotRedirectRecoveryOutsideTheBoundRoot()
    {
        await using var index = await BindAsync(); var proof = Stage("reparse protected"u8.ToArray(), "reparse protected"u8.ToArray());
        string parent = Disk(proof.Parent), outside = Path.Combine(fixture, "outside"); Directory.Move(parent, outside);
        Directory.CreateSymbolicLink(parent, outside); links.Add(parent);
        using var tree = new SafeFileTree(Root); var result = await PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token);
        result.Deferred.ShouldBe(1); result.Registered.ShouldBe(0); Directory.GetFiles(outside).Select(Path.GetFileName).ShouldBe([proof.Temporary]);
        File.Exists(Disk(BlockCaptureProof.Directory + "/" + proof.Name)).ShouldBeTrue(); index.ReadBlockObjects().ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationBeforeAndAfterProofFlushCannotPublishOrDeleteUnknownData()
    {
        await using var index = await BindAsync(); byte[] bytes = "cancel proof boundary"u8.ToArray();
        using var tree = new SafeFileTree(Root); using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var blocks = new PublishedBlocks(tree) { Checkpoint = stage => { if (stage == "proof") cancelled.Cancel(); } };
        Should.Throw<OperationCanceledException>(() => blocks.Put(bytes, Hash(bytes), cancelled.Token));
        Directory.GetFiles(ProofDirectory).Length.ShouldBe(1); index.ReadBlockObjects().ShouldBeEmpty();
        await Should.ThrowAsync<OperationCanceledException>(() => PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: cancelled.Token));
        Directory.GetFiles(ProofDirectory).Length.ShouldBe(1);
        var recovered = await PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token);
        recovered.ProofsRemoved.ShouldBe(1); recovered.Registered.ShouldBe(0); index.ReadIndexBatch(0).Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task FailedRecoveryRegistrationRetainsInstalledDataAndProofUntilDatabaseCanCommit()
    {
        await using var index = await BindAsync(); byte[] bytes = "recovery transaction failure"u8.ToArray(); var proof = Stage(bytes, bytes);
        Sql("CREATE TRIGGER reject_block BEFORE INSERT ON block_objects BEGIN SELECT RAISE(ABORT,'test recovery failure'); END;");
        using var tree = new SafeFileTree(Root);
        await Should.ThrowAsync<SqliteException>(() => PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token));
        index.ReadBlockObjects().ShouldBeEmpty(); File.ReadAllBytes(Disk(proof.Parent + "/" + proof.Final)).ShouldBe(bytes);
        File.Exists(Disk(BlockCaptureProof.Directory + "/" + proof.Name)).ShouldBeTrue();
        Sql("DROP TRIGGER reject_block;"); var recovered = await PublishedBlocks.RecoverCapturesAsync(tree, index, cancellationToken: Token);
        recovered.Registered.ShouldBe(1); recovered.Installed.ShouldBe(0); recovered.ProofsRemoved.ShouldBe(1);
    }

    [Fact]
    public async Task DiagnosticsDistinguishCatalogFailureRecoveryAndCommitWithoutPrivateData()
    {
        await using var index = await BindAsync(); byte[] bytes = "private block bytes"u8.ToArray(); File.WriteAllBytes(Disk("private-name"), bytes);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "blocks", debugEnabled: true);
        using var scanner = new Scanner(Root, index, keys, device, journal: journal);
        Sql("CREATE TRIGGER reject_block BEFORE INSERT ON block_objects BEGIN SELECT RAISE(ABORT,'secret database text'); END;");
        await Should.ThrowAsync<SqliteException>(() => scanner.ScanAsync(Token)); Sql("DROP TRIGGER reject_block;");
        await scanner.ScanAsync(Token);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("block.catalog_failed"); log.ShouldContain("block.recovery_complete"); log.ShouldContain("block.catalog_committed");
        log.ShouldNotContain(Root); log.ShouldNotContain("private-name"); log.ShouldNotContain("private block bytes");
        log.ShouldNotContain("secret database text"); log.ShouldNotContain(Convert.ToHexString(Hash(bytes)));
    }

    // Durable on-disk boundary fixture, not a process/power-cut simulation. The
    // real codec and native identities are used, and proof flush precedes data.
    private BlockCaptureProof Stage(byte[] expected, byte[] actual)
    {
        byte[] hash = Hash(expected); Guid id = Guid.NewGuid(); using var tree = new SafeFileTree(Root);
        using var parent = tree.Directory(Parent(hash), create: true, privatePath: true);
        using var data = parent.CreateFile(id.ToString("N") + ".!psp", temporary: true);
        using var home = tree.Directory(BlockCaptureProof.Directory, create: true, privatePath: true);
        using var marker = home.CreateFile(id.ToString("N") + ".capture");
        var proof = new BlockCaptureProof(id, tree.Identity.ToArray(), BlockCaptureProof.Identity(marker), BlockCaptureProof.Identity(data), hash, expected.Length, DateTime.UtcNow.Ticks);
        RandomAccess.Write(marker, proof.Encode(), 0); RandomAccess.FlushToDisk(marker);
        RandomAccess.Write(data, actual, 0); RandomAccess.FlushToDisk(data); return proof;
    }
    private async Task<FolderIndex> BindAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { using var scanner = new Scanner(Root, index, keys, device); await scanner.ScanAsync(Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static byte[] Hash(byte[] bytes) => Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    private static string Parent(byte[] hash) => ".psp/blocks/" + Convert.ToHexString(hash)[..2];
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private string ProofDirectory => Disk(BlockCaptureProof.Directory);
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-BlockOwnership-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        foreach (string link in links) Directory.Delete(link);
        Directory.Delete(resolved, recursive: true);
    }
}
