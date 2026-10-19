using System.Buffers.Binary;
using System.Text;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Ignore;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Scanner-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static readonly byte[] Device = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ScannerTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task NewRecordsAreSignedAndUnchangedRescansDoNotHashOrAdvanceSequences()
    {
        Directory.CreateDirectory(Path.Combine(Root, "dir"));
        await Write("dir/é.txt", "content"); await Write("empty", "");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device);
        var first = await scanner.ScanAsync(Token);
        first.Problems.ShouldBeEmpty(); first.Hashed.ShouldBe(2); first.Updated.ShouldBe(3);
        foreach (var row in index.LocalFiles.ReadAll())
        {
            SignedRecordFactory.TryOpen(row.Record!, keys, out var body).ShouldBeTrue();
            body!.Path.ShouldBe(row.Path); body.ModifiedBy.ToByteArray().ShouldBe(Device);
            row.Vector[BinaryPrimitives.ReadUInt64LittleEndian(Device)].ShouldBe(1ul);
            if (row.Type == 0) index.FindChunks(row.FileHash!).Count().ShouldBe(row.Size == 0 ? 0 : 1);
        }
        long sequence = index.ReadIndexBatch(0).LastSequence;
        var second = await scanner.ScanAsync(Token);
        second.Unchanged.ShouldBe(3); second.Hashed.ShouldBe(0); second.Updated.ShouldBe(0);
        index.ReadIndexBatch(0).LastSequence.ShouldBe(sequence);
        // Child changes touch the directory's timestamp but must not create a directory version.
        var directory = index.LocalFiles.Find("dir")!;
        await Write("dir/new", "new child");
        var third = await scanner.ScanAsync(Token);
        third.Hashed.ShouldBe(1); third.Updated.ShouldBe(1);
        index.LocalFiles.Find("dir")!.LocalSequence.ShouldBe(directory.LocalSequence);
    }

    [Fact]
    public async Task RenameReusesVerifiedChunksAndMissingPathsBecomeSignedTombstones()
    {
        await Write("before", "rename fixture");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device);
        await scanner.ScanAsync(Token);
        var original = index.LocalFiles.Find("before")!;
        File.Move(Path.Combine(Root, "before"), Path.Combine(Root, "after"));
        var rename = await scanner.ScanAsync(Token);
        rename.Hashed.ShouldBe(0); rename.Renamed.ShouldBe(1); rename.Deleted.ShouldBe(1);
        var saved = index.LocalFiles.Find("after")!;
        saved.FileHash.ShouldBe(original.FileHash); saved.FileId.ShouldBe(original.FileId);
        saved.Vector.Values.Single().ShouldBe(2ul);
        var removed = index.LocalFiles.Find("before")!;
        removed.Deleted.ShouldBeTrue();
        SignedRecordFactory.TryOpen(removed.Record!, keys, out var body).ShouldBeTrue();
        body!.Deleted.ShouldBeTrue(); body.Chunks.ShouldBeEmpty();
        index.FindChunks(saved.FileHash!).Single().PathKey.ShouldBe("AFTER");
        File.Delete(Path.Combine(Root, "after"));
        (await scanner.ScanAsync(Token)).Deleted.ShouldBe(1);
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    [Fact]
    public async Task SameMetadataReplacementIsDetectedByFileIdAndChangedBytesAreRehashed()
    {
        await Write("file", "first");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device);
        await scanner.ScanAsync(Token);
        var before = index.LocalFiles.Find("file")!;
        File.Move(Path.Combine(Root, "file"), Path.Combine(fixture, "old"));
        await Write("file", "other");
        File.SetLastWriteTimeUtc(Path.Combine(Root, "file"), new DateTime(before.MtimeTicks, DateTimeKind.Utc));
        var result = await scanner.ScanAsync(Token);
        result.Hashed.ShouldBe(1); result.Updated.ShouldBe(1);
        var after = index.LocalFiles.Find("file")!;
        after.FileId.ShouldNotBe(before.FileId); after.FileHash.ShouldNotBe(before.FileHash);
        after.Vector.Values.Single().ShouldBe(2ul);
    }

    [Fact]
    public async Task SameIdentityAndLengthEditWithRestoredMtimeStillPublishesChangedBytesAndRetainsPriorVersion()
    {
        await Write("file", "first"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token);
        var before = index.LocalFiles.Find("file")!; string path = Path.Combine(Root, "file");
        File.WriteAllText(path, "other"); File.SetLastWriteTimeUtc(path, new(before.MtimeTicks, DateTimeKind.Utc));
        File.SetAttributes(path, (FileAttributes)before.Attributes);
        var result = await scanner.ScanAsync(Token); result.Hashed.ShouldBe(1); result.Updated.ShouldBe(1);
        var after = index.LocalFiles.Find("file")!; after.FileId.ShouldBe(before.FileId); after.FileHash.ShouldNotBe(before.FileHash);
        after.MtimeTicks.ShouldBe(before.MtimeTicks); after.Vector.Values.Single().ShouldBe(2UL);
        var archive = index.Archive.ReadAll().Single(); File.ReadAllText(Path.Combine(Root, archive.ArchivePath)).ShouldBe("first");
        (await scanner.ScanAsync(Token)).Hashed.ShouldBe(0);
    }

    [Fact]
    public async Task RenameAfterAnEditWithRestoredMtimeCannotReuseStalePublishedChunks()
    {
        await Write("before", "first"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token); var before = index.LocalFiles.Find("before")!;
        string source = Path.Combine(Root, "before"), target = Path.Combine(Root, "after");
        File.WriteAllText(source, "other"); File.SetLastWriteTimeUtc(source, new(before.MtimeTicks, DateTimeKind.Utc));
        File.SetAttributes(source, (FileAttributes)before.Attributes); File.Move(source, target);
        var result = await scanner.ScanAsync(Token); result.Hashed.ShouldBe(1); result.Renamed.ShouldBe(0); result.Deleted.ShouldBe(1);
        index.LocalFiles.Find("after")!.FileHash.ShouldBe(Blake3.Hasher.Hash("other"u8).AsSpan().ToArray());
        File.ReadAllText(Path.Combine(Root, index.Archive.ReadAll().Single().ArchivePath)).ShouldBe("first");
    }

    [Fact]
    public async Task UnsynchronizedCreationTimeChangeRefreshesCacheWithoutPublishingAnotherVersion()
    {
        await Write("file", "same bytes"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token); var before = index.LocalFiles.Find("file")!;
        string path = Path.Combine(Root, "file"); File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-1));
        var result = await scanner.ScanAsync(Token); result.Hashed.ShouldBe(1); result.Updated.ShouldBe(0);
        result.ChangedPaths.ShouldContain("file");
        var after = index.LocalFiles.Find("file")!; after.ChangeFileTime.ShouldNotBe(before.ChangeFileTime); after.LocalSequence.ShouldBe(before.LocalSequence);
        after.Record!.Body.ToArray().ShouldBe(before.Record!.Body.ToArray()); after.Record.Signature.ToArray().ShouldBe(before.Record.Signature.ToArray());
        after.Vector.ShouldBe(before.Vector); index.Archive.Count().ShouldBe(0); (await scanner.ScanAsync(Token)).Hashed.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionEightUpgradeRefreshesSignedRwAndRoCachesWithoutNewPublication(bool readOnly)
    {
        await Write("file", "upgrade cache bytes"); LocalFileRow before;
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            using var scanner = new Scanner(Root, index, keys, Device); await scanner.ScanAsync(Token); before = index.LocalFiles.Find("file")!;
        }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE directory_case_members; DROP TABLE directory_case_progress; DROP TABLE directory_publication_cache; DROP TABLE directory_publications; DROP TRIGGER directory_capture_insert; DROP TRIGGER directory_capture_update; DROP TRIGGER directory_capture_delete; DROP TABLE directory_capture_clock; DROP INDEX ix_local_directory_children; DROP INDEX ix_local_directory_sort_backfill; ALTER TABLE local_files DROP COLUMN parent_key; ALTER TABLE local_files DROP COLUMN path_sort_key; DROP TABLE directory_captures; DROP TABLE directory_graph_references; DROP TABLE directory_graph_members; DROP TABLE directory_graph_work; DROP TABLE directory_graphs; DROP TABLE case_preservation_retries; DROP TABLE case_preservations; DROP TABLE staging_retirements; DROP TABLE staging_cleanup_state; DROP TABLE staged_objects; DROP TRIGGER needed_retry_invalidate; DROP TABLE pull_retries; DROP TABLE remote_index_ranges; DROP INDEX ix_needed_work; ALTER TABLE needed DROP COLUMN work_hash; ALTER TABLE needed DROP COLUMN work_mtime; ALTER TABLE needed DROP COLUMN work_size; ALTER TABLE needed DROP COLUMN work_stage; DROP INDEX ix_archive_contents_retiring; DROP INDEX ix_archive_contents_active; CREATE UNIQUE INDEX ix_archive_contents_active ON archive_contents(source_id) WHERE active=1; ALTER TABLE archive_contents DROP COLUMN retiring; DROP TABLE capture_retirements; DROP TABLE block_deletions; DROP TRIGGER block_orphan_registration; DROP TABLE block_orphans; DROP TABLE block_retention_state; ALTER TABLE block_objects DROP COLUMN legacy_retention; DROP TABLE archive_policy_releases; DROP INDEX ix_archive_path; ALTER TABLE archive DROP COLUMN archive_path_key; DROP TRIGGER history_archive_link_invalidate; DROP TABLE history_archive_links; DROP TABLE archive_retirement_sources; DROP TABLE block_reference_sources; DROP TABLE archive_content_chunks; DROP TABLE archive_contents; DROP TRIGGER archive_provenance_insert; DROP TRIGGER archive_provenance_delete; DROP TRIGGER archive_provenance_before_update; DROP TRIGGER archive_provenance_after_update; DROP TABLE archive_provenance; DROP TABLE block_reference_archives; DROP TABLE block_references; DROP TABLE block_reference_snapshots; ALTER TABLE local_files DROP COLUMN change_filetime; PRAGMA user_version=8;"; command.ExecuteNonQuery();
        }
        await using var migrated = await FolderIndex.OpenAsync(Database, Token); migrated.LocalFiles.Find("file")!.ChangeFileTime.ShouldBeNull();
        migrated.LocalFiles.Find("file")!.ContentReady.ShouldBeTrue(); using var ro = FolderKeys.FromText(keys.ExportReadOnlyText());
        using var upgradedScanner = new Scanner(Root, migrated, readOnly ? ro : keys, Device); var result = await upgradedScanner.ScanAsync(Token);
        result.Hashed.ShouldBe(1); result.Updated.ShouldBe(0); result.Problems.ShouldBeEmpty();
        var after = migrated.LocalFiles.Find("file")!; after.ChangeFileTime.ShouldNotBeNull(); after.LocalSequence.ShouldBe(before.LocalSequence);
        after.Record!.Body.ToArray().ShouldBe(before.Record!.Body.ToArray()); after.Record.Signature.ToArray().ShouldBe(before.Record.Signature.ToArray());
        after.Vector.ShouldBe(before.Vector); (await upgradedScanner.ScanAsync(Token)).Hashed.ShouldBe(0);
    }

    [Fact]
    public async Task InternalAndIgnoredTreesArePrunedAndReparsePointsAreRecordedWithoutFollowing()
    {
        await Write(".psp/private", "private"); await Write("file.!psp", "temporary");
        await Write("skip/secret", "ignored"); await Write("Thumbs.db", "default ignored");
        string outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret"), "outside", Token);
        Directory.CreateSymbolicLink(Path.Combine(Root, "link"), outside);
        await Write("e\u0301", "non-NFC name");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device, new IgnoreMatcher("skip/"));
        var result = await scanner.ScanAsync(Token);
        result.Hashed.ShouldBe(0); result.Unsyncable.ShouldBe(2);
        index.LocalFiles.Count().ShouldBe(2);
        foreach (var row in index.LocalFiles.ReadAll())
        { row.Flags.ShouldBe(LocalFileState.Unsyncable); row.Record.ShouldBeNull(); }
        index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
        Directory.Delete(Path.Combine(Root, "link")); File.Delete(Path.Combine(Root, "e\u0301"));
        (await scanner.ScanAsync(Token)).Deleted.ShouldBe(2);
        index.LocalFiles.ReadAll().ShouldAllBe(r => r.Deleted && r.Record == null);
    }

    [Fact]
    public async Task IgnoringPreviouslyPublishedPathsSuppressesThemWithoutPublishingDeletions()
    {
        await Write("skip/child", "keep locally");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
        using (var scanner = new Scanner(Root, index, keys, Device, new IgnoreMatcher("skip/")))
        {
            var result = await scanner.ScanAsync(Token);
            result.Deleted.ShouldBe(0); result.Hashed.ShouldBe(0);
            index.LocalFiles.ReadAll().ShouldAllBe(r => !r.Deleted && r.Flags == LocalFileState.Ignored);
            index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        }
        using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
        index.ReadIndexBatch(0).Records.Count.ShouldBe(2);
    }

    [Fact]
    public async Task IncompleteSubtreePreservesOldChildrenAndNeverInventsDeletions()
    {
        await Write("dir/child", "keep");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device);
        await scanner.ScanAsync(Token);
        var child = index.LocalFiles.Find("dir/child")!;
        using (var exclusive = NativeFiles.CreateFile(Path.Combine(Root, "dir"), 0x81, 0, 0, 3, 0x02200000, 0))
        {
            exclusive.IsInvalid.ShouldBeFalse();
            var result = await scanner.ScanAsync(Token);
            result.Problems.ShouldContain(p => p.RelativePath == "dir" && p.Code == "enumeration-failed");
            result.Deleted.ShouldBe(0);
            index.LocalFiles.Find("dir/child")!.LocalSequence.ShouldBe(child.LocalSequence);
        }
        (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReparseReplacementPreservesPreviouslyIndexedChildren()
    {
        await Write("dir/child", "keep");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device);
        await scanner.ScanAsync(Token);
        var child = index.LocalFiles.Find("dir/child")!;
        Directory.Move(Path.Combine(Root, "dir"), Path.Combine(fixture, "moved"));
        Directory.CreateSymbolicLink(Path.Combine(Root, "dir"), Path.Combine(fixture, "moved"));
        var result = await scanner.ScanAsync(Token);
        result.Unsyncable.ShouldBe(1); result.Deleted.ShouldBe(0);
        index.LocalFiles.Find("dir/child")!.LocalSequence.ShouldBe(child.LocalSequence);
        index.LocalFiles.Find("dir")!.Flags.ShouldBe(LocalFileState.Unsyncable);
    }

    [Fact]
    public async Task LockedChangedFilesRetryAndDebugJournalDoesNotContainPathsOrContent()
    {
        await Write("private-name", "original private content");
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "scanner", debugEnabled: true);
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device, journal: journal);
        await scanner.ScanAsync(Token);
        await Write("private-name", "changed private content longer");
        using (var exclusive = File.Open(Path.Combine(Root, "private-name"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await scanner.ScanAsync(Token);
            result.Problems.Single().Code.ShouldBe("read-failed");
            index.LocalFiles.Find("private-name")!.Flags.ShouldBe(LocalFileState.NeedsRescan);
        }
        (await scanner.ScanAsync(Token)).Hashed.ShouldBe(1);
        index.LocalFiles.Find("private-name")!.Flags.ShouldBe(LocalFileState.None);
        string log = await File.ReadAllTextAsync(Path.Combine(fixture, "logs", "debug.jsonl"), Token);
        log.ShouldContain("scan.defer"); log.ShouldContain("scan.complete");
        log.ShouldNotContain("private-name"); log.ShouldNotContain("private content"); log.ShouldNotContain(keys.ExportText());
    }

    [Fact]
    public async Task ReplacedRootIsRejectedWithoutChangingTheIndex()
    {
        await Write("keep", "keep");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device);
        await scanner.ScanAsync(Token);
        long sequence = index.ReadIndexBatch(0).LastSequence;
        Directory.Move(Root, Path.Combine(fixture, "original-root")); Directory.CreateDirectory(Root);
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        index.ReadIndexBatch(0).LastSequence.ShouldBe(sequence);
        index.LocalFiles.Find("keep")!.Deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadOnlyScansTrackLocalEditsAndDeletesWithoutSigningOrAdvancingVectors()
    {
        await Write("edit", "before"); await Write("delete", "remove");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using (var writer = new Scanner(Root, index, keys, Device)) await writer.ScanAsync(Token);
        var vector = index.LocalFiles.Find("edit")!.Vector;
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        using var scanner = new Scanner(Root, index, readOnly, Device);
        (await scanner.ScanAsync(Token)).Unchanged.ShouldBe(2);
        await Write("edit", "local edit longer"); await Write("new", "new local file");
        File.Delete(Path.Combine(Root, "delete"));
        var result = await scanner.ScanAsync(Token);
        result.Hashed.ShouldBe(2); result.Deleted.ShouldBe(1);
        index.LocalFiles.ReadAll().ShouldAllBe(r => r.Record == null);
        VersionVector.Compare(index.LocalFiles.Find("edit")!.Vector, vector).ShouldBe(VvOrder.Equal);
        index.LocalFiles.Find("new")!.Vector.ShouldBeEmpty();
        index.ReadIndexBatch(0).Records.ShouldBeEmpty();
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    private Task Write(string relative, string content)
    {
        string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, content, new UTF8Encoding(false), Token);
    }
    public void Dispose() { keys.Dispose(); Directory.Delete(fixture, recursive: true); }

    [Fact]
    public async Task RootReplacementDuringTheWalkPreventsPublishingTheCollectedChanges()
    {
        await Write("new", "new data");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        bool moved = false;
        var clock = new CallbackClock(() =>
        {
            if (moved) return;
            moved = true;
            Directory.Move(Root, Path.Combine(fixture, "moved-during-scan")); Directory.CreateDirectory(Root);
        });
        using var scanner = new Scanner(Root, index, keys, Device, timeProvider: clock);
        await Should.ThrowAsync<IOException>(() => scanner.ScanAsync(Token));
        moved.ShouldBeTrue(); index.LocalFiles.Count().ShouldBe(0);
    }

    [Fact]
    public async Task CancellationBeforeScanDoesNotChangePreviouslyCommittedRecords()
    {
        await Write("file", "data");
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var scanner = new Scanner(Root, index, keys, Device);
        await scanner.ScanAsync(Token);
        long sequence = index.ReadIndexBatch(0).LastSequence;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => scanner.ScanAsync(cancellation.Token));
        index.ReadIndexBatch(0).LastSequence.ShouldBe(sequence);
        (await scanner.ScanAsync(Token)).Updated.ShouldBe(0);
    }

    private sealed class CallbackClock(Action callback) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() { callback(); return DateTimeOffset.UtcNow; }
    }
}
