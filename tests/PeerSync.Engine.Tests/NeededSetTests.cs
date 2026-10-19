using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Core.Settings;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Tests;

public sealed class NeededSetTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Needed-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public NeededSetTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task IncrementalUpdatesAndLocalChangesKeepExactlyTheWinningNeededPaths()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var needed = new NeededSet(index, keys, Device(9));
        var first = Record(1, 1, 10, "old", 1);
        (await needed.ApplyIndexUpdateAsync(Device(1), 0, 1, [first], Token)).Applied.ShouldBeTrue();
        index.Needed.Find("file")!.SourceDevice.ShouldBe(Device(1));
        var newer = Record(2, 1, 20, "new", 1);
        await needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [newer], Token);
        index.Needed.Find("file")!.SourceDevice.ShouldBe(Device(2));
        var decision = needed.Decide(Snapshot(index));
        decision.RequiresContent.ShouldBeTrue(); decision.Vector.Count.ShouldBe(2);
        var local = Local(decision.DesiredRecord!, decision.Vector);
        await index.LocalFiles.UpsertAsync([local], Token);
        await needed.RecomputeAsync(["file"], Token);
        index.Needed.Count().ShouldBe(0);
        var localEdit = Record(9, 1, 30, "local", 1);
        await index.LocalFiles.UpsertAsync([Local(localEdit)], Token);
        await needed.RecomputeAsync(["file"], Token);
        // Winning local contents still require a metadata-only vector merge.
        decision = needed.Decide(Snapshot(index));
        decision.RequiresContent.ShouldBeFalse(); decision.RequiresApply.ShouldBeTrue();
        index.Needed.Find("file")!.SourceDevice.ShouldBe(Device(9));
        decision.LocalLoserFate.ShouldBe(LoserFate.None);
    }

    [Fact]
    public async Task ReplayAndCursorGapsAreSafeAndExistingPeerApprovalMetadataSurvives()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        await index.Devices.UpsertAsync([new(Device(1), "known peer", Approval: DeviceApproval.Approved)], Token);
        var needed = new NeededSet(index, keys, Device(9));
        var record = Record(1, 1, 10, "first", 3);
        await needed.ApplyIndexUpdateAsync(Device(1), 0, 5, [record], Token);
        var replay = await needed.ApplyIndexUpdateAsync(Device(1), 0, 5, [record], Token);
        replay.Applied.ShouldBeFalse(); replay.LastSequence.ShouldBe(5);
        await needed.ApplyIndexUpdateAsync(Device(1), 5, 9, [], Token);
        var peer = index.Devices.Find(Device(1))!;
        peer.LastSequenceReceived.ShouldBe(9); peer.Approval.ShouldBe(DeviceApproval.Approved); peer.Name.ShouldBe("known peer");
        await Should.ThrowAsync<InvalidOperationException>(() => needed.ApplyIndexUpdateAsync(Device(1), 5, 10, [Record(1, 2, 20, "later", 10)], Token));
        index.Devices.Find(Device(1))!.LastSequenceReceived.ShouldBe(9);
        index.RemoteFiles.Count().ShouldBe(1);
    }

    [Fact]
    public async Task InvalidSignaturesAndDuplicatePathsCannotPartiallyAdvanceTheIndex()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var needed = new NeededSet(index, keys, Device(9));
        var valid = Record(1, 1, 10, "valid", 1);
        var corrupt = new Envelope(valid.Body, new byte[64], 2);
        await Should.ThrowAsync<CryptographicException>(() => needed.ApplyIndexUpdateAsync(Device(1), 0, 2, [valid, corrupt], Token));
        index.RemoteFiles.Count().ShouldBe(0); index.Needed.Count().ShouldBe(0); index.Devices.Count().ShouldBe(0);
        await Should.ThrowAsync<ArgumentException>(() => needed.ApplyIndexUpdateAsync(Device(1), 0, 2, [valid, valid.WithLocalSequence(2)], Token));
        index.RemoteFiles.Count().ShouldBe(0); index.Devices.Count().ShouldBe(0);
    }

    [Fact]
    public async Task NeededResolverFailureRollsBackRemoteRowsAndCursorAndWriterRemainsUsable()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var record = Record(1, 1, 10, "data", 1);
        var view = VersionView.Open(record, keys, Device(1));
        RemoteFileRow[] rows = [new(Device(1), "FILE", view.Vector, record)];
        await Should.ThrowAsync<InvalidOperationException>(() => index.ApplyRemoteIndexAsync(Device(1), 0, 1, rows,
            _ => throw new InvalidOperationException("injected selection failure"), Token));
        index.RemoteFiles.Count().ShouldBe(0); index.Needed.Count().ShouldBe(0); index.Devices.Count().ShouldBe(0);
        var needed = new NeededSet(index, keys, Device(9));
        (await needed.ApplyIndexUpdateAsync(Device(1), 0, 1, [record], Token)).Applied.ShouldBeTrue();
        index.Needed.Count().ShouldBe(1);
    }

    [Fact]
    public async Task ReceiveReplayAndFailureDiagnosticsExposeOnlyCorrelatedCountsAndCodes()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "index");
        var needed = new NeededSet(index, keys, Device(9), journal: journal);
        var record = Record(1, 1, 10, "private sync content", 1);
        await needed.ApplyIndexUpdateAsync(Device(1), 0, 1, [record], Token);
        await needed.ApplyIndexUpdateAsync(Device(1), 0, 1, [record], Token);
        await Should.ThrowAsync<CryptographicException>(() => needed.ApplyIndexUpdateAsync(Device(1), 1, 2,
            [new Envelope(record.Body, new byte[64], 2)], Token));
        string log = await File.ReadAllTextAsync(Path.Combine(fixture, "logs", "debug.jsonl"), Token);
        log.ShouldContain("index.receive_complete"); log.ShouldContain("index.replay"); log.ShouldContain("index.receive_failed");
        log.ShouldContain("sessionId"); log.ShouldContain("itemsVisited");
        log.ShouldNotContain("private sync content"); log.ShouldNotContain(keys.ExportText());
    }

    [Fact]
    public async Task CancellationInsideNeededSelectionRollsBackTheEntireReceive()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var record = Record(1, 1, 10, "data", 1);
        var view = VersionView.Open(record, keys, Device(1));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Should.ThrowAsync<OperationCanceledException>(() => index.ApplyRemoteIndexAsync(Device(1), 0, 1,
            [new(Device(1), "FILE", view.Vector, record)], snapshot =>
            {
                cancellation.Cancel(); return new(snapshot.PathKey, Device(1), 0);
            }, cancellation.Token));
        index.RemoteFiles.Count().ShouldBe(0); index.Needed.Count().ShouldBe(0); index.Devices.Count().ShouldBe(0);
    }

    [Fact]
    public async Task IndependentConcurrentReceivesSelectFromTheLatestTransactionalSnapshot()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var needed = new NeededSet(index, keys, Device(9));
        await Task.WhenAll(needed.ApplyIndexUpdateAsync(Device(1), 0, 1, [Record(1, 1, 10, "first", 1)], Token),
            needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record(2, 1, 20, "second", 1)], Token));
        index.RemoteFiles.Count().ShouldBe(2); index.Needed.Count().ShouldBe(1);
        index.Needed.Find("file")!.SourceDevice.ShouldBe(Device(2));
        needed.Decide(Snapshot(index)).Vector.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ReadOnlyMergedMetadataConvergesWithoutChangingTheOriginalSignedBody()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var localRecord = Record(1, 1, 10, "same", 1);
        await index.LocalFiles.UpsertAsync([Local(localRecord)], Token);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        var needed = new NeededSet(index, readOnly, Device(9));
        var remote = Record(2, 1, 20, "same", 1);
        await needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [remote], Token);
        var decision = needed.Decide(Snapshot(index));
        decision.RequiresApply.ShouldBeTrue(); decision.RequiresContent.ShouldBeFalse(); decision.Vector.Count.ShouldBe(2);
        decision.DesiredRecord!.Body.ToArray().ShouldBe(remote.Body.ToArray());
        await index.LocalFiles.UpsertAsync([Local(decision.DesiredRecord, decision.Vector)], Token);
        await needed.RebuildAsync(Token);
        index.Needed.Count().ShouldBe(0);
        index.ReadIndexBatch(0).Records.Single().Record!.Body.ToArray().ShouldBe(remote.Body.ToArray());
    }

    [Fact]
    public async Task ReadOnlyUnsignedLocalChangesRemainBlockedUnlessOverwriteIsEnabled()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var actual = Local(Record(1, 1, 10, "local edit", 1)) with { Record = null };
        await index.LocalFiles.UpsertAsync([actual], Token);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        var needed = new NeededSet(index, readOnly, Device(9));
        await needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record(2, 1, 20, "remote", 1)], Token);
        var decision = needed.Decide(Snapshot(index));
        decision.RequiresApply.ShouldBeTrue(); decision.BlockedReason.ShouldBe("read-only-local-change");
        index.Needed.Count().ShouldBe(1);
        var overwriting = new NeededSet(index, readOnly, Device(9), new FolderSettings { OverwriteLocalChanges = true });
        decision = overwriting.Decide(Snapshot(index));
        decision.BlockedReason.ShouldBeNull(); decision.LocalLoserFate.ShouldBe(LoserFate.Archive);
    }

    [Theory]
    [InlineData(LocalFileState.Ignored)]
    [InlineData(LocalFileState.Unsyncable)]
    [InlineData(LocalFileState.NeedsRescan)]
    public async Task LocalExclusionsAndPendingScansWithholdRemoteApplication(LocalFileState flag)
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var row = Local(Record(1, 1, 10, "local", 1)) with { Flags = flag, Record = flag == LocalFileState.Unsyncable ? null : Record(1, 1, 10, "local", 1) };
        await index.LocalFiles.UpsertAsync([row], Token);
        var needed = new NeededSet(index, keys, Device(9));
        await needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record(2, 1, 20, "remote", 1)], Token);
        index.Needed.Count().ShouldBe(0);
        needed.Decide(Snapshot(index)).BlockedReason.ShouldBe("local-scan-pending-or-excluded");
    }

    [Fact]
    public async Task ReadOnlyCachedKnowledgeCannotMakeOldContentsDefeatTheirSignedCausalDescendant()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        var old = Record(1, 1, 30, "old A contents", 1); await index.LocalFiles.UpsertAsync([Local(old)], Token);
        using var readOnly = FolderKeys.FromText(keys.ExportReadOnlyText());
        var needed = new NeededSet(index, readOnly, Device(9));
        await needed.ApplyIndexUpdateAsync(Device(2), 0, 1, [Record(2, 1, 10, "B contents", 1)], Token);
        var merged = needed.Decide(Snapshot(index));
        await index.LocalFiles.UpsertAsync([Local(merged.DesiredRecord!, merged.Vector)], Token); await needed.RebuildAsync(Token);
        var descendant = Record(1, 2, 20, "restored A contents", 2);
        await needed.ApplyIndexUpdateAsync(Device(1), 0, 2, [descendant], Token);
        var decision = needed.Decide(Snapshot(index));
        decision.Winner!.Record.Body.ToArray().ShouldBe(descendant.Body.ToArray()); decision.RequiresContent.ShouldBeTrue();
        decision.Vector[1].ShouldBe(2ul); decision.Vector[2].ShouldBe(1ul);
    }

    [Fact]
    public async Task UnavailableDiagnosticsCannotChangeCommittedReceiveOrReplaceTheOriginalFailure()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "disposed-logs"), "index"); journal.Dispose();
        var needed = new NeededSet(index, keys, Device(9), journal: journal);
        var record = Record(1, 1, 10, "valid", 1);
        (await needed.ApplyIndexUpdateAsync(Device(1), 0, 1, [record], Token)).Applied.ShouldBeTrue();
        index.Devices.Find(Device(1))!.LastSequenceReceived.ShouldBe(1);
        await Should.ThrowAsync<CryptographicException>(() => needed.ApplyIndexUpdateAsync(Device(1), 1, 2,
            [new Envelope(record.Body, new byte[64], 2)], Token));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token); cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => needed.ApplyIndexUpdateAsync(Device(1), 1, 2, [], cancelled.Token));
        index.Devices.Find(Device(1))!.LastSequenceReceived.ShouldBe(1);
    }

    private Envelope Record(ulong author, ulong counter, long time, string content, ulong sequence) =>
        SignedRecordFactory.Create(ConflictResolverTests.Body(author, counter, time, time, content, false), keys, sequence);
    private LocalFileRow Local(Envelope record, IReadOnlyDictionary<ulong, ulong>? vector = null)
    {
        var view = VersionView.Open(record, keys, Device(9)); var body = view.ExportBody();
        return new(body.Path, body.Type, (long)body.Size, body.MtimeTicks, body.Attributes, body.Deleted, body.ChangedTicks,
            body.FileHash.Length == 0 ? null : body.FileHash.ToByteArray(), vector ?? view.Vector, record, new byte[16]);
    }
    private static FolderPathSnapshot Snapshot(FolderIndex index) => new("FILE", index.LocalFiles.Find("file"), index.RemotesForPath("file").ToArray());
    private static byte[] Device(ulong value) { byte[] bytes = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); return bytes; }
    public void Dispose() { keys.Dispose(); Directory.Delete(fixture, recursive: true); }
}
