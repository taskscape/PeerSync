using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Fact]
    public async Task QueueCommitPublishesCacheAndReleasesBothRowsWhileNoOpKeepsVectorAndSequence()
    {
        WriteFiles("a"); await using var index = await Open(); var draft = await AdvanceIndex(index, 1000);
        var publication = Publication(index, draft); var queued = await StageQueue(index, draft, publication);
        var first = await CommitQueue(index, queued, draft, publication); first.Changed.ShouldBeTrue();
        index.HasPendingDirectoryPublications().ShouldBeFalse(); index.HasDirectoryCaptures().ShouldBeFalse();
        var cache = index.ReadDirectoryPublicationCache("topic")!;
        cache.InputHash.ShouldBe(draft.InputHash); cache.InputEpoch.ShouldBe(index.ReadDirectoryCaptureEpoch()); cache.ExpectedKeysHash.ShouldBe(queued.ExpectedKeysHash);
        cache.RecordSourceId.ShouldBe(CaseConflictPolicy.SourceId(VersionView.Open(first.Row.Record!, keys, device)));
        long sequence = index.ReadIndexHighWater(), epoch = index.ReadDirectoryCaptureEpoch(); var body = first.Row.Record!.Body.ToArray(); var signature = first.Row.Record.Signature.ToArray();
        var nextDraft = await AdvanceIndex(index, 1000); var next = await StageQueue(index, nextDraft, Publication(index, nextDraft));
        var unchanged = await CommitQueue(index, next, nextDraft, null); unchanged.Changed.ShouldBeFalse();
        unchanged.Row.Record!.Body.ToArray().ShouldBe(body); unchanged.Row.Record.Signature.ToArray().ShouldBe(signature); unchanged.Row.Vector.ShouldBe(first.Row.Vector);
        index.ReadIndexHighWater().ShouldBe(sequence); index.ReadDirectoryCaptureEpoch().ShouldBe(epoch);
        index.HasDirectoryCaptures().ShouldBeFalse(); index.HasPendingDirectoryPublications().ShouldBeFalse();
    }

    [Fact]
    public async Task ReadonlyReconstructionCanReleaseOnlyTheIdenticalAuthenticatedCurrentRoot()
    {
        WriteFiles("a"); await using var index = await Open(); var draft = await AdvanceIndex(index, 1000); var publication = Publication(index, draft);
        var queued = await StageQueue(index, draft, publication); var first = await CommitQueue(index, queued, draft, publication);
        var receivedDraft = await AdvanceIndex(index, 1000);
        var received = await StageQueue(index, receivedDraft, first.Row, receivedTemplate: true);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText()); long sequence = index.ReadIndexHighWater();
        var result = await CommitQueue(index, received, receivedDraft, null, readOnly); result.Changed.ShouldBeFalse();
        result.Row.Record!.Body.ToArray().ShouldBe(first.Row.Record!.Body.ToArray()); index.ReadIndexHighWater().ShouldBe(sequence);
        index.HasPendingDirectoryPublications().ShouldBeFalse(); index.HasDirectoryCaptures().ShouldBeFalse();
    }

    [Theory]
    [InlineData("row")]
    [InlineData("cache")]
    [InlineData("queue")]
    [InlineData("draft")]
    public async Task QueueCommitSqlFailureRollsBackRowCacheEpochQueueAndDraft(string boundary)
    {
        WriteFiles("a"); await using var index = await Open(); var draft = await AdvanceIndex(index, 1000); var publication = Publication(index, draft);
        var queued = await StageQueue(index, draft, publication); var prior = index.LocalFiles.Find("topic")!;
        long sequence = index.ReadIndexHighWater(), epoch = index.ReadDirectoryCaptureEpoch();
        string trigger = boundary switch
        {
            "row" => "BEFORE UPDATE ON local_files WHEN NEW.path_key='TOPIC'",
            "cache" => "BEFORE INSERT ON directory_publication_cache",
            "queue" => "BEFORE DELETE ON directory_publications",
            _ => "BEFORE DELETE ON directory_captures",
        };
        Sql("CREATE TRIGGER reject_queue_commit " + trigger + " BEGIN SELECT RAISE(ABORT,'private queue SQL'); END;");
        await Should.ThrowAsync<SqliteException>(() => CommitQueue(index, queued, draft, publication));
        index.HasPendingDirectoryPublications().ShouldBeTrue(); index.HasDirectoryCaptures().ShouldBeTrue(); index.ReadDirectoryPublicationCache("topic").ShouldBeNull();
        index.ReadIndexHighWater().ShouldBe(sequence); index.ReadDirectoryCaptureEpoch().ShouldBe(epoch);
        index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(prior.Record!.Body.ToArray());
        Sql("DROP TRIGGER reject_queue_commit;"); (await CommitQueue(index, queued, draft, publication)).Changed.ShouldBeTrue();
        index.HasPendingDirectoryPublications().ShouldBeFalse(); index.HasDirectoryCaptures().ShouldBeFalse();
    }

    [Fact]
    public async Task ReplacedQueueVersionCannotReleaseANewTemplateOrInstallItsOldRoot()
    {
        await using var index = await Open(); var draft = await AdvanceIndex(index, 1000); var publication = Publication(index, draft);
        var prior = await StageQueue(index, draft, publication);
        var replaced = await index.StageDirectoryPublicationAsync(prior with { Version = Guid.NewGuid() }, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => CommitQueue(index, prior, draft, publication));
        index.ReadDirectoryPublication("topic")!.Version.ShouldBe(replaced.Version); index.HasDirectoryCaptures().ShouldBeTrue();
        index.ReadDirectoryPublicationCache("topic").ShouldBeNull();
    }

    private async Task<DirectoryPublication> StageQueue(FolderIndex index, DirectoryCaptureState draft, LocalFileRow publication, bool receivedTemplate = false)
    {
        SignedRecordFactory.TryOpen(publication.Record!, keys, out var body).ShouldBeTrue();
        if (!receivedTemplate) { body.DirectorySnapshotHash = ByteString.Empty; body.DirectorySnapshotLength = 0; }
        var template = receivedTemplate ? publication.Record! : SignedRecordFactory.Create(body, keys);
        var current = index.LocalFiles.Find("topic")!; long now = DateTime.UtcNow.Ticks;
        var queued = await index.StageDirectoryPublicationAsync(new(Guid.NewGuid(), "topic", draft.RootId, draft.NativeId, current.LocalSequence,
            publication.Attributes, template, new byte[32], draft.MemberCount, now, now), Token);
        return await index.BindDirectoryPublicationKeysAsync(queued, draft.InputEpoch!.Value, now, Token);
    }
    private async Task<DirectoryPublicationCommit> CommitQueue(FolderIndex index, DirectoryPublication queued, DirectoryCaptureState draft, LocalFileRow? publication, FolderKeys? access = null)
    {
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        return await new DirectorySnapshotCapture(Root, index, access ?? keys, device).CommitQueuedUnderGateAsync(queued, draft, publication, DateTime.UtcNow.Ticks, Token);
    }
}
