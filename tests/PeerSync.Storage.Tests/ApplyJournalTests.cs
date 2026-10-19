using System.Text.Json;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class ApplyJournalTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ApplyJournal-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] OldHash => Enumerable.Repeat((byte)1, 32).ToArray();
    private static byte[] NewHash => Enumerable.Repeat((byte)2, 32).ToArray();
    public ApplyJournalTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public void IntentsOwnBoundedRecoveryBytesValidateIdentifiersAndRedactSensitiveData()
    {
        byte[] bytes = "private recovery payload"u8.ToArray();
        var intent = new ApplyIntent(Guid.NewGuid(), "private-name", null, bytes); bytes[0] = 0;
        intent.Payload[0].ShouldBe((byte)'p');
        string json = JsonSerializer.Serialize(intent); json.ShouldNotContain("private-name"); json.ShouldNotContain("recovery payload");
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<ApplyIntent>(json));
        intent.ToString().ShouldNotContain("private-name");
        Should.Throw<ArgumentException>(() => new ApplyIntent(Guid.Empty, "file", null, [1]));
        Should.Throw<ArgumentException>(() => new ApplyIntent(Guid.NewGuid(), "../escape", null, [1]));
        Should.Throw<ArgumentOutOfRangeException>(() => new ApplyIntent(Guid.NewGuid(), "file", -1, [1]));
        Should.Throw<ArgumentOutOfRangeException>(() => new ApplyIntent(Guid.NewGuid(), "file", null, []));
        Should.Throw<ArgumentOutOfRangeException>(() => new ApplyIntent(Guid.NewGuid(), "file", null, new byte[ApplyIntent.MaxPayloadBytes + 1]));
    }

    [Fact]
    public async Task ReservationIsIdempotentAndPerPathExclusiveAndRecoveryStateUpdatesCompareExactBytes()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [1, 2]);
        (await index.ReserveApplyAsync(intent, Token)).ShouldBeTrue(); (await index.ReserveApplyAsync(intent, Token)).ShouldBeFalse();
        await Should.ThrowAsync<SqliteException>(() => index.ReserveApplyAsync(new(Guid.NewGuid(), "FILE", null, [3]), Token));
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveApplyAsync(new(intent.Id, "file", null, [3]), Token));
        var changed = await index.RewriteApplyAsync(intent, new byte[] { 3, 4 }, Token);
        index.ReadApplyIntents().Single().Payload.ToArray().ShouldBe(new byte[] { 3, 4 });
        await Should.ThrowAsync<InvalidOperationException>(() => index.RewriteApplyAsync(intent, new byte[] { 5 }, Token));
        await Should.ThrowAsync<InvalidOperationException>(() => index.AbandonApplyAsync(intent, Token));
        (await index.AbandonApplyAsync(changed, Token)).ShouldBeTrue(); (await index.AbandonApplyAsync(changed, Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task PendingIntentSurvivesReopeningAndReservationRejectsAStaleLocalSequence()
    {
        var intent = new ApplyIntent(Guid.NewGuid(), "file", 1, [1, 2, 3]);
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            await index.LocalFiles.UpsertAsync([Row(OldHash)], Token);
            await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveApplyAsync(new(Guid.NewGuid(), "file", null, [1]), Token));
            await index.ReserveApplyAsync(intent, Token);
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var actual = reopened.ReadApplyIntents().Single(); actual.Id.ShouldBe(intent.Id);
        actual.ExpectedLocalSequence.ShouldBe(1); actual.PathKey.ShouldBe("FILE"); actual.Payload.ToArray().ShouldBe(intent.Payload.ToArray());
    }

    [Fact]
    public async Task CompletionAtomicallyWritesLocalChunksSequenceArchiveAndCurrentNeededStateAndIsIdempotent()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await PrepareAsync(index);
        var intent = new ApplyIntent(Guid.NewGuid(), "file", 1, [1]); await index.ReserveApplyAsync(intent, Token);
        bool called = false;
        (await index.CommitApplyAsync(intent, Row(NewHash), [Archive()], snapshot =>
        {
            called = true; snapshot.Local!.FileHash.ShouldBe(NewHash); snapshot.Local.LocalSequence.ShouldBe(2);
            snapshot.Local.Record!.LocalSequence.ShouldBe(2ul); snapshot.Remote.Count.ShouldBe(1);
            return new("FILE", NewHash, 17); // A newer remote may remain needed; never blindly clear the row.
        }, Token)).ShouldBeTrue();
        called.ShouldBeTrue(); index.ReadApplyIntents().ShouldBeEmpty();
        index.LocalFiles.Find("file")!.FileHash.ShouldBe(NewHash); index.FindChunks(OldHash).ShouldBeEmpty(); index.FindChunks(NewHash).Count().ShouldBe(1);
        index.Archive.Count().ShouldBe(1); index.Archive.ReadAll().Single().Id.ShouldBeGreaterThan(0);
        index.Needed.Find("file")!.Priority.ShouldBe(17); index.Needed.Find("file")!.SourceDevice.ShouldBe(NewHash);
        (await index.CommitApplyAsync(intent, Row(NewHash), [Archive()], _ => throw new InvalidOperationException(), Token)).ShouldBeFalse();
        index.Archive.Count().ShouldBe(1); index.LocalFiles.Find("file")!.LocalSequence.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledCompletionRollsBackAllTablesAndRetainsIntentForASuccessfulRetry(bool cancel)
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await PrepareAsync(index);
        var intent = new ApplyIntent(Guid.NewGuid(), "file", 1, [1]); await index.ReserveApplyAsync(intent, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var error = await Record.ExceptionAsync(() => index.CommitApplyAsync(intent, Row(NewHash), [Archive()], snapshot =>
        {
            snapshot.Local!.LocalSequence.ShouldBe(2);
            if (!cancel) throw new InvalidOperationException("injected resolver failure");
            cancellation.Cancel(); return null;
        }, cancellation.Token));
        if (cancel)
        {
            error.ShouldNotBeNull(); error.ShouldBeAssignableTo<OperationCanceledException>();
            ((OperationCanceledException)error).CancellationToken.ShouldBe(cancellation.Token);
        }
        else error.ShouldBeOfType<InvalidOperationException>();
        index.ReadApplyIntents().Single().Id.ShouldBe(intent.Id);
        index.LocalFiles.Find("file")!.LocalSequence.ShouldBe(1); index.LocalFiles.Find("file")!.FileHash.ShouldBe(OldHash);
        index.FindChunks(OldHash).Count().ShouldBe(1); index.FindChunks(NewHash).ShouldBeEmpty(); index.Archive.Count().ShouldBe(0);
        index.Needed.Find("file")!.Priority.ShouldBe(5);
        (await index.CommitApplyAsync(intent, Row(NewHash), [Archive()], _ => null, Token)).ShouldBeTrue();
        index.LocalFiles.Find("file")!.LocalSequence.ShouldBe(2); index.Needed.Count().ShouldBe(0);
    }

    [Fact]
    public async Task ChangedLocalOrRecoveryStateCannotBeOverwrittenByAStaleCompletion()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await PrepareAsync(index);
        var intent = new ApplyIntent(Guid.NewGuid(), "file", 1, [1]); await index.ReserveApplyAsync(intent, Token);
        var rewritten = await index.RewriteApplyAsync(intent, new byte[] { 2 }, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.CommitApplyAsync(intent, Row(NewHash), [Archive()], _ => null, Token));
        await index.LocalFiles.UpsertAsync([Row(OldHash)], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.CommitApplyAsync(rewritten, Row(NewHash), [Archive()], _ => null, Token));
        index.ReadApplyIntents().Single().Payload.ToArray().ShouldBe(new byte[] { 2 });
        index.LocalFiles.Find("file")!.LocalSequence.ShouldBe(2); index.LocalFiles.Find("file")!.FileHash.ShouldBe(OldHash); index.Archive.Count().ShouldBe(0);
    }

    [Fact]
    public async Task QueuedCompletionOwnsMutableCallerDataBeforeWaitingForTheWriter()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await PrepareAsync(index);
        var intent = new ApplyIntent(Guid.NewGuid(), "file", 1, [1]); await index.ReserveApplyAsync(intent, Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task<int> blocker = index.RecomputeNeededAsync(["file"], _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), Token)) throw new TimeoutException("Writer test barrier timed out.");
            return new("FILE", OldHash, 5);
        }, Token);
        try
        {
            await entered.Task.WaitAsync(Token);
            byte[] hash = NewHash, chunkHash = NewHash, archiveHash = OldHash, id = new byte[16];
            var vector = new Dictionary<ulong, ulong> { [1] = 1 };
            var row = Row(hash) with { Vector = vector, FileId = id, IndexedChunks = [new(0, 3, chunkHash)] };
            Task<bool> completion = index.CommitApplyAsync(intent, row, [Archive() with { FileHash = archiveHash }], _ => null, Token);
            hash[0] = 99; chunkHash[0] = 99; archiveHash[0] = 99; id[0] = 99; vector[1] = 99;
            release.Set(); await blocker; (await completion).ShouldBeTrue();
            var saved = index.LocalFiles.Find("file")!; saved.FileHash.ShouldBe(NewHash); saved.FileId![0].ShouldBe((byte)0); saved.Vector[1].ShouldBe(1ul);
            index.FindChunks(NewHash).Count().ShouldBe(1); index.Archive.ReadAll().Single().FileHash.ShouldBe(OldHash);
        }
        finally { release.Set(); await blocker; }
    }

    [Fact]
    public async Task UnrelatedOrInvalidArchiveMetadataFailsBeforeAnyCompletionWrite()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await PrepareAsync(index);
        var intent = new ApplyIntent(Guid.NewGuid(), "file", 1, [1]); await index.ReserveApplyAsync(intent, Token);
        await Should.ThrowAsync<ArgumentException>(() => index.CommitApplyAsync(intent, Row(NewHash),
            [Archive() with { OriginalPath = "other" }], _ => null, Token));
        await Should.ThrowAsync<ArgumentException>(() => index.CommitApplyAsync(intent, Row(NewHash),
            [Archive() with { ArchivePath = "../outside" }], _ => null, Token));
        index.LocalFiles.Find("file")!.LocalSequence.ShouldBe(1); index.ReadApplyIntents().Count().ShouldBe(1); index.Archive.Count().ShouldBe(0);
    }

    private static LocalFileRow Row(byte[] hash) => new("file", 0, 3, 0, 0, false, 0, hash, new Dictionary<ulong, ulong> { [1] = 1 },
        new(hash, new byte[64]), new byte[16]) { IndexedChunks = [new(0, 3, hash)] };
    private static ArchiveRow Archive() => new(0, "file", ".psp/archive/file~20261019-120000", DateTime.UtcNow.Ticks, 3, OldHash);
    private static async Task PrepareAsync(FolderIndex index)
    {
        await index.LocalFiles.UpsertAsync([Row(OldHash)], Token);
        await index.RemoteFiles.UpsertAsync([new(NewHash, "FILE", new Dictionary<ulong, ulong> { [2] = 1 }, new([1], new byte[64]))], Token);
        await index.Needed.UpsertAsync([new("FILE", OldHash, 5)], Token);
    }
    public void Dispose() => Directory.Delete(fixture, recursive: true);
}
