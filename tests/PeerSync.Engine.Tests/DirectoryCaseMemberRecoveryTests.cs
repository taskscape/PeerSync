using Microsoft.Data.Sqlite;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryMembersCopyNestedBytesInBoundedPassesAndReadonlyCopiesStayUnsigned(bool readOnly)
    {
        Directory.CreateDirectory(Path.Combine(Root, "topic", "nested")); File.WriteAllText(Path.Combine(Root, "topic", "nested", "a"), "nested original bytes"); WriteFiles("b");
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        using var access = FolderKeys.FromText(readOnly ? keys.ExportReadOnlyText() : keys.ExportText());
        var applier = new FileApplier(Root, index, access, device) { DirectoryCaseMemberWorkLimit = 1 };
        (await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token)).ShouldBeTrue(); var pending = index.ReadPendingCasePreservations().Single();
        for (int pass = 0; pass < 5 && !index.ReadDirectoryCaseProgress(pending.Source.SourceId, pending.InstalledFileId!).Exhausted; pass++)
            await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var progress = index.ReadDirectoryCaseProgress(pending.Source.SourceId, pending.InstalledFileId!); progress.Exhausted.ShouldBeTrue();
        File.ReadAllText(Path.Combine(Root, pending.DestinationPath!, "nested", "a")).ShouldBe("nested original bytes");
        File.ReadAllText(Path.Combine(Root, pending.DestinationPath!, "b")).ShouldBe(File.ReadAllText(Path.Combine(Root, "topic", "b")));
        foreach (string path in new[] { "topic/b", "topic/nested", "topic/nested/a" })
        {
            var proof = index.ReadDirectoryCaseMember(pending.Source.SourceId, pending.InstalledFileId!, path)!; proof.InstalledFileId.ShouldNotBeNull();
            proof.MemberSourceId.ShouldBe(CaseConflictPolicy.SourceId(VersionView.Open(index.LocalFiles.Find(path)!.Record!, keys, device)));
        }
        using (var scanner = new Scanner(Root, index, access, device)) await scanner.ScanAsync(Token);
        var copied = index.LocalFiles.Find(pending.DestinationPath + "/nested/a")!;
        if (readOnly) copied.Record.ShouldBeNull(); else { copied.Record.ShouldNotBeNull(); copied.ContentReady.ShouldBeTrue(); }
        index.HasPendingCasePreservation("topic").ShouldBeTrue(); var completed = await applier.CompleteCaseCopiesAsync(Token);
        if (readOnly) { completed.ShouldBe(["TOPIC"]); index.HasPendingCasePreservation("topic").ShouldBeFalse(); }
        else completed.ShouldBeEmpty(); // Legacy RW scan still lacks signed directory snapshots.
    }
    [Theory]
    [InlineData(false, "case-member-reserved")]
    [InlineData(false, "case-member-prepared")]
    [InlineData(false, "case-member-install-ready")]
    [InlineData(false, "case-member-installed")]
    [InlineData(false, "case-member-accounted")]
    [InlineData(true, "case-member-reserved")]
    [InlineData(true, "case-member-prepared")]
    [InlineData(true, "case-member-install-ready")]
    [InlineData(true, "case-member-installed")]
    [InlineData(true, "case-member-accounted")]
    public async Task DirectoryMemberRestartsAtEveryNativeAndDurableBoundary(bool directory, string stopAt)
    {
        if (directory) Directory.CreateDirectory(Path.Combine(Root, "topic", "a")); else WriteFiles("a");
        string destination;
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            await AdmitDirectoryCase(index); await new FileApplier(Root, index, keys, device).PrepareCaseCopiesAsync(FetchRootMetadata, Token);
            destination = index.ReadPendingCasePreservations().Single().DestinationPath!;
            var applier = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == stopAt) throw new Stop(); } };
            await Should.ThrowAsync<Stop>(() => applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token));
            index.HasPendingCasePreservation("topic").ShouldBeTrue();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var recovered = new FileApplier(Root, reopened, keys, device);
        await recovered.RecoverAsync(Token); await recovered.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        reopened.ReadApplyIntents().ShouldBeEmpty(); var pending = reopened.ReadPendingCasePreservations().Single();
        var proof = reopened.ReadDirectoryCaseMember(pending.Source.SourceId, pending.InstalledFileId!, "topic/a")!; proof.InstalledFileId.ShouldNotBeNull();
        if (directory) Directory.Exists(Path.Combine(Root, destination, "a")).ShouldBeTrue();
        else File.ReadAllText(Path.Combine(Root, destination, "a")).ShouldBe(File.ReadAllText(Path.Combine(Root, "topic", "a")));
        (File.GetAttributes(Path.Combine(Root, destination, "a")) & FileAttributes.Hidden).ShouldBe((FileAttributes)0);
    }
    [Fact]
    public async Task MemberAccountingFailureAfterRenameRetainsTheIntentAndEmitsNativeSqlDetails()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await new FileApplier(Root, index, keys, device).PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        Sql("CREATE TRIGGER fail_member_account BEFORE UPDATE OF installed_file_id ON directory_case_members BEGIN SELECT RAISE(ABORT,'private member SQL'); END;");
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "member-sql", debugEnabled: true);
        var applier = new FileApplier(Root, index, keys, device, journal: journal);
        await Should.ThrowAsync<SqliteException>(() => applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token));
        var intent = index.ReadApplyIntents().Single(); var state = DirectoryCaseMemberState.Decode(intent);
        File.Exists(Path.Combine(Root, state.Destination)).ShouldBeTrue(); index.ReadDirectoryCaseProgress(state.SourceId, state.RootFileId).LastKey.ShouldBeNull();
        index.ReadDirectoryCaseMember(state.SourceId, state.RootFileId, state.MemberKey)!.InstalledFileId.ShouldBeNull();
        Sql("DROP TRIGGER fail_member_account;"); await new FileApplier(Root, index, keys, device).RecoverAsync(Token);
        index.ReadApplyIntents().ShouldBeEmpty(); index.ReadDirectoryCaseProgress(state.SourceId, state.RootFileId).LastKey.ShouldBe(state.MemberKey);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); journal.DroppedEvents.ShouldBe(0);
        log.ShouldContain("case-member-account-failed"); log.ShouldContain("\"nativeError\":1811"); log.ShouldContain(intent.Id.ToString());
        log.ShouldNotContain("private member SQL"); log.ShouldNotContain("topic");
    }
    [Fact]
    public async Task ForeignMemberOccupantPreservesThePartialTreeAndStartsANewRootIdentity()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var applier = new FileApplier(Root, index, keys, device); await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var prior = index.ReadPendingCasePreservations().Single(); string foreign = Path.Combine(Root, prior.DestinationPath!, "a"); File.WriteAllText(foreign, "foreign child");
        await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token); index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBeNull();
        await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token); await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var next = index.ReadPendingCasePreservations().Single(); next.DestinationPath.ShouldBe(ConflictNames.CaseSourcePath("topic", prior.Source.SourceId, 2));
        next.InstalledFileId.ShouldNotBe(prior.InstalledFileId); File.ReadAllText(foreign).ShouldBe("foreign child");
        File.ReadAllText(Path.Combine(Root, next.DestinationPath!, "a")).ShouldBe(File.ReadAllText(Path.Combine(Root, "topic", "a")));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EditedMemberStagingIsProtectedAcrossRecoveryAndNeverUsedAsSourceData(bool directory, bool prepared)
    {
        if (directory) Directory.CreateDirectory(Path.Combine(Root, "topic", "a")); else WriteFiles("a");
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index); await new FileApplier(Root, index, keys, device).PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == (prepared ? "case-member-prepared" : "case-member-reserved")) throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token)); var state = DirectoryCaseMemberState.Decode(index.ReadApplyIntents().Single());
        string foreign = directory ? Path.Combine(Root, state.Temporary, "user") : Path.Combine(Root, state.Temporary);
        if (!directory && !prepared)
        {
            using var edit = new FileStream(foreign, FileMode.Open, FileAccess.Write, FileShare.None);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("edited staging bytes"); edit.Write(bytes); edit.SetLength(bytes.Length);
        }
        else File.WriteAllText(foreign, "edited staging bytes");
        var resumed = new FileApplier(Root, index, keys, device); await resumed.RecoverAsync(Token); await resumed.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        File.ReadAllText(foreign).ShouldBe("edited staging bytes"); index.IsCatalogedStaging(state.Root, state.TemporaryId, state.Temporary).ShouldBeTrue();
        index.ReadReusableStaging(state.Root, state.Destination, directory).ShouldBeEmpty(); index.ReadApplyIntents().ShouldBeEmpty();
        if (directory) Directory.GetFileSystemEntries(Path.Combine(Root, state.Destination)).ShouldBeEmpty();
        else File.ReadAllText(Path.Combine(Root, state.Destination)).ShouldBe(File.ReadAllText(Path.Combine(Root, "topic", "a")));
    }
    [Fact]
    public async Task ReplacedIntermediateDirectoryCannotAcquireChildrenThroughAnOldParentWitness()
    {
        Directory.CreateDirectory(Path.Combine(Root, "topic", "nested")); File.WriteAllText(Path.Combine(Root, "topic", "nested", "a"), "authenticated bytes");
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var applier = new FileApplier(Root, index, keys, device) { DirectoryCaseMemberWorkLimit = 1 };
        await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token); await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var pending = index.ReadPendingCasePreservations().Single(); string parent = Path.Combine(Root, pending.DestinationPath!, "nested");
        Directory.Move(parent, parent + ".saved"); Directory.CreateDirectory(parent); File.WriteAllText(Path.Combine(parent, "user"), "replacement parent bytes");
        await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        File.ReadAllText(Path.Combine(parent, "user")).ShouldBe("replacement parent bytes"); File.Exists(Path.Combine(parent, "a")).ShouldBeFalse();
        Directory.Exists(parent + ".saved").ShouldBeTrue(); index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBeNull();
    }
    [Fact]
    public async Task CollisionDuringMemberRecoveryPreservesBothThePreparedBytesAndForeignDestination()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await new FileApplier(Root, index, keys, device).PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == "case-member-prepared") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token)); var state = DirectoryCaseMemberState.Decode(index.ReadApplyIntents().Single());
        File.WriteAllText(Path.Combine(Root, state.Destination), "recovery collision");
        await new FileApplier(Root, index, keys, device).RecoverAsync(Token); index.ReadApplyIntents().ShouldBeEmpty();
        File.ReadAllText(Path.Combine(Root, state.Destination)).ShouldBe("recovery collision");
        File.ReadAllText(Path.Combine(Root, state.Temporary)).ShouldBe(File.ReadAllText(Path.Combine(Root, "topic", "a")));
        index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBeNull();
    }
    [Fact]
    public async Task EmptyReceiverFetchesOriginalSubtreeFromExactRootAdvertisersWithoutUsingForwarderIdentityAsOwnership()
    {
        File.WriteAllText(Path.Combine(Root, "topic", "a"), "remote original bytes");
        await using var sender = await FolderIndex.OpenAsync(Database, Token); var source = await PrepareProjection(sender);
        byte[] sourceId = CaseConflictPolicy.SourceId(VersionView.Open(source, keys, device)); var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var cache = new PeerSync.Engine.Hashing.PublishedBlocks(Root, sender))
            foreach (var block in sender.ReadDirectoryGraphReferences(sourceId)) blobs.Add(Convert.ToHexString(block.Hash), cache.TryRead(block.Hash, block.Length, Token)!);
        string receiverRoot = Path.Combine(fixture, "receiver"); Directory.CreateDirectory(receiverRoot);
        await using var receiver = await FolderIndex.OpenAsync(Path.Combine(fixture, "receiver.db"), Token); byte[] own = [8, .. new byte[31]], first = [2, .. new byte[31]], second = [4, .. new byte[31]], winnerId = [3, .. new byte[31]];
        using (var scanner = new Scanner(receiverRoot, receiver, keys, own)) await scanner.ScanAsync(Token);
        var needed = new NeededSet(receiver, keys, own);
        await needed.ApplyIndexUpdateAsync(first, 0, 1, [source.WithLocalSequence(1)], Token); await needed.ApplyIndexUpdateAsync(second, 0, 1, [source.WithLocalSequence(1)], Token);
        var winner = VersionView.Open(source, keys, device).ExportBody(); winner.Path = "TOPIC"; winner.MtimeTicks += TimeSpan.TicksPerMinute; winner.ChangedTicks += TimeSpan.TicksPerMinute;
        winner.ModifiedBy = ByteString.CopyFrom(winnerId); winner.Vv.Clear(); winner.Vv.Add(new VvEntry { Device = 3, Counter = 1 });
        await needed.ApplyIndexUpdateAsync(winnerId, 0, 1, [SignedRecordFactory.Create(winner, keys, 1)], Token);
        var pending = receiver.ReadPendingCasePreservations().Single(); byte[] unavailable = pending.Source.SourceDevice; var successful = new List<string>(); int failures = 0;
        ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> peer, ReadOnlyMemory<byte> hash, int length, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (peer.Span.SequenceEqual(unavailable)) { failures++; throw new IOException("Fixture unavailable forwarding route."); }
            peer.Span.SequenceEqual(winnerId).ShouldBeFalse(); successful.Add(Convert.ToHexString(peer.Span));
            var bytes = blobs[Convert.ToHexString(hash.Span)]; bytes.Length.ShouldBe(length); return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);
        }
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "remote-members", debugEnabled: true);
        var applier = new FileApplier(receiverRoot, receiver, keys, own, journal: journal);
        await applier.PrepareCaseCopiesAsync(Fetch, Token); await applier.PrepareCaseCopiesAsync(Fetch, Token);
        var installed = receiver.ReadPendingCasePreservations().Single(); File.ReadAllText(Path.Combine(receiverRoot, installed.DestinationPath!, "a")).ShouldBe("remote original bytes");
        failures.ShouldBeGreaterThan(0); successful.ShouldNotBeEmpty(); successful.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        receiver.ReadDirectoryCaseProgress(sourceId, installed.InstalledFileId!).Exhausted.ShouldBeTrue(); receiver.HasPendingCasePreservation("topic").ShouldBeTrue();
        var proof = receiver.ReadDirectoryCaseMember(sourceId, installed.InstalledFileId!, "topic/a")!;
        proof.MemberSourceId.ShouldBe(CaseConflictPolicy.SourceId(VersionView.Open(sender.LocalFiles.Find("topic/a")!.Record!, keys, device)));
        File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")).ShouldContain("case-route-failover");
    }
    [Fact]
    public async Task MemberRecoveryCannotHashThroughAnExistingWriterAndPreservesTheGuardedFile()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await new FileApplier(Root, index, keys, device).PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == "case-member-installed") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token)); var state = DirectoryCaseMemberState.Decode(index.ReadApplyIntents().Single());
        using (var writer = new FileStream(Path.Combine(Root, state.Destination), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            await new FileApplier(Root, index, keys, device).RecoverAsync(Token);
        index.ReadApplyIntents().ShouldBeEmpty(); index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBeNull();
        File.ReadAllText(Path.Combine(Root, state.Destination)).ShouldBe(File.ReadAllText(Path.Combine(Root, "topic", "a")));
    }
    [Fact]
    public async Task MemberRecoveryRequiresTheSameCompleteGraphAndRetainsTheIntentIfItsBindingChanges()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await new FileApplier(Root, index, keys, device).PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == "case-member-prepared") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token)); var intent = index.ReadApplyIntents().Single(); var state = DirectoryCaseMemberState.Decode(intent);
        Sql("UPDATE directory_graphs SET retiring=1;"); await Should.ThrowAsync<InvalidDataException>(() => new FileApplier(Root, index, keys, device).RecoverAsync(Token));
        index.ReadApplyIntents().Single().Payload.ToArray().ShouldBe(intent.Payload.ToArray()); File.Exists(Path.Combine(Root, state.Destination)).ShouldBeFalse();
        File.Exists(Path.Combine(Root, state.Temporary)).ShouldBeTrue(); Sql("UPDATE directory_graphs SET retiring=0;");
        await new FileApplier(Root, index, keys, device).RecoverAsync(Token); index.ReadApplyIntents().ShouldBeEmpty();
    }
    [Fact]
    public async Task MemberJournalRejectsNoncanonicalFlagsBoundsDestinationAndTrailingData()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await new FileApplier(Root, index, keys, device).PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == "case-member-prepared") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token)); var intent = index.ReadApplyIntents().Single(); byte[] original = intent.Payload.ToArray();
        DirectoryCaseMemberState.Decode(intent).Encode().ShouldBe(original); var mutations = new List<byte[]>();
        var flag = original.ToArray(); flag[^5] = 2; mutations.Add(flag); var length = original.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length.AsSpan(4), int.MaxValue); mutations.Add(length);
        var kind = original.ToArray(); kind[0] = 8; mutations.Add(kind); mutations.Add([.. original, 0]);
        foreach (var payload in mutations) Should.Throw<InvalidDataException>(() => DirectoryCaseMemberState.Decode(new(intent.Id, intent.PathKey, null, payload)));
        Should.Throw<InvalidDataException>(() => DirectoryCaseMemberState.Decode(new(intent.Id, "another", null, original)));
        DirectoryCaseMemberState.Decode(intent).ToString().ShouldNotContain("topic");
    }
}
