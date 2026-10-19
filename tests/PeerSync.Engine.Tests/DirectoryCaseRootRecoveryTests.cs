using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Theory]
    [InlineData("case-root-reserved")]
    [InlineData("case-root-prepared")]
    [InlineData("case-root-install-ready")]
    [InlineData("case-root-installed")]
    [InlineData("case-root-accounted")]
    public async Task DirectoryCaseRootRecoversEveryNativeBoundaryAndRetainsTheOriginalBarrier(string stopAt)
    {
        string destination;
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            await AdmitDirectoryCase(index); var pending = index.ReadPendingCasePreservations().Single();
            destination = ConflictNames.CaseSourcePath("topic", pending.Source.SourceId);
            var applier = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == stopAt) throw new Stop(); } };
            await Should.ThrowAsync<Stop>(() => applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token));
            index.HasPendingCasePreservation("topic").ShouldBeTrue(); index.LocalFiles.Find(destination).ShouldBeNull();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var recovered = new FileApplier(Root, reopened, keys, device);
        await recovered.RecoverAsync(Token); await recovered.PrepareCaseCopiesAsync(FetchRootMetadata, Token); await recovered.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        reopened.ReadApplyIntents().ShouldBeEmpty(); var row = reopened.ReadPendingCasePreservations().Single();
        row.DestinationPath.ShouldBe(destination); row.InstalledFileId.ShouldNotBeNull(); Directory.Exists(Path.Combine(Root, destination)).ShouldBeTrue();
        using (var scanner = new Scanner(Root, reopened, keys, device)) await scanner.ScanAsync(Token);
        (await recovered.CompleteCaseCopiesAsync(Token)).ShouldBeEmpty(); reopened.HasPendingCasePreservation("topic").ShouldBeTrue();
        reopened.LocalFiles.Find(destination)!.Record.ShouldNotBeNull();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryCaseRootNeverOverwritesAnUnknownFileOrDirectoryAtItsPortableName(bool directory)
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var pending = index.ReadPendingCasePreservations().Single(); string occupied = ConflictNames.CaseSourcePath("topic", pending.Source.SourceId);
        if (directory) { Directory.CreateDirectory(Path.Combine(Root, occupied)); File.WriteAllText(Path.Combine(Root, occupied, "user"), "unknown child"); }
        else File.WriteAllText(Path.Combine(Root, occupied), "unknown file");
        (await new FileApplier(Root, index, keys, device).PrepareCaseCopiesAsync(FetchRootMetadata, Token)).ShouldBeTrue();
        var installed = index.ReadPendingCasePreservations().Single(); installed.DestinationPath.ShouldBe(ConflictNames.CaseSourcePath("topic", pending.Source.SourceId, 2));
        if (directory) File.ReadAllText(Path.Combine(Root, occupied, "user")).ShouldBe("unknown child");
        else File.ReadAllText(Path.Combine(Root, occupied)).ShouldBe("unknown file");
    }
    [Fact]
    public async Task ChangedPreparedRootTemporaryIsProtectedAndAnotherEmptyRootIsInstalled()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == "case-root-prepared") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token));
        var state = DirectoryCaseRootState.Decode(index.ReadApplyIntents().Single(), keys);
        File.WriteAllText(Path.Combine(Root, state.Temporary, "user"), "prepared user bytes");
        var resumed = new FileApplier(Root, index, keys, device); await resumed.RecoverAsync(Token); index.ReadApplyIntents().ShouldBeEmpty();
        (await resumed.PrepareCaseCopiesAsync(FetchRootMetadata, Token)).ShouldBeTrue();
        File.ReadAllText(Path.Combine(Root, state.Temporary, "user")).ShouldBe("prepared user bytes");
        index.IsCatalogedStaging(state.Root, state.TemporaryId, state.Temporary).ShouldBeTrue();
        index.ReadReusableStaging(state.Root, state.Destination, directory: true).ShouldBeEmpty();
        var installed = index.ReadPendingCasePreservations().Single(); Directory.GetFileSystemEntries(Path.Combine(Root, installed.DestinationPath!)).ShouldBeEmpty();
    }
    [Fact]
    public async Task InstalledRootRecoveryPreservesUserChildrenAndDoesNotPretendTheSourceIsComplete()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == "case-root-installed") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token)); var state = DirectoryCaseRootState.Decode(index.ReadApplyIntents().Single(), keys);
        File.WriteAllText(Path.Combine(Root, state.Destination, "user"), "installed user bytes");
        var resumed = new FileApplier(Root, index, keys, device); await resumed.RecoverAsync(Token);
        File.ReadAllText(Path.Combine(Root, state.Destination, "user")).ShouldBe("installed user bytes");
        index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBe(state.TemporaryId); (await resumed.CompleteCaseCopiesAsync(Token)).ShouldBeEmpty();
        index.HasPendingCasePreservation("topic").ShouldBeTrue();
    }
    [Fact]
    public async Task DirectoryCaseRootDiagnosticsCorrelateNativeStagesWithoutLeakingTheSource()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "case-root", debugEnabled: true);
        var applier = new FileApplier(Root, index, keys, device, journal: journal); await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var pending = index.ReadPendingCasePreservations().Single(); await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        index.ReadCasePreservationRetry(pending.Source.SourceId)!.Value.NextTicks.ShouldBeGreaterThan(DateTime.UtcNow.Ticks);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); journal.DroppedEvents.ShouldBe(0);
        log.ShouldContain("case-root-reserved"); log.ShouldContain("case-root-prepared"); log.ShouldContain("case-root-installed"); log.ShouldContain("case-root-accounted");
        log.ShouldNotContain("topic"); log.ShouldNotContain(Root); log.ShouldNotContain(keys.ExportText());
    }
    private async Task AdmitDirectoryCase(FolderIndex index)
    {
        var source = await PrepareProjection(index, buildGraph: false); var body = VersionView.Open(source, keys, device).ExportBody();
        body.Path = "TOPIC"; body.ModifiedBy = ByteString.CopyFrom([2, .. new byte[31]]); body.MtimeTicks += TimeSpan.TicksPerMinute;
        body.ChangedTicks += TimeSpan.TicksPerMinute; body.Vv.Clear(); body.Vv.Add(new VvEntry { Device = 2, Counter = 1 });
        var winner = SignedRecordFactory.Create(body, keys, 1);
        await new NeededSet(index, keys, device).ApplyIndexUpdateAsync([2, .. new byte[31]], 0, 1, [winner], Token);
        index.ReadPendingCasePreservations().Count.ShouldBe(1);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryCaseRootUsesSourceAttributesAndReadonlyScanningKeepsTheCopyUnsigned(bool readOnly)
    {
        string originalPath = Path.Combine(Root, "topic"); string? copy = null;
        try
        {
            File.SetAttributes(originalPath, FileAttributes.Directory | FileAttributes.ReadOnly | FileAttributes.Hidden);
            await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
            using var access = FolderKeys.FromText(readOnly ? keys.ExportReadOnlyText() : keys.ExportText());
            var applier = new FileApplier(Root, index, access, device); (await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token)).ShouldBeTrue();
            await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token); // Establish empty-member coverage before RW advertisement.
            copy = Path.Combine(Root, index.ReadPendingCasePreservations().Single().DestinationPath!);
            (File.GetAttributes(copy) & (FileAttributes.ReadOnly | FileAttributes.Hidden)).ShouldBe(FileAttributes.ReadOnly | FileAttributes.Hidden);
            using (var scanner = new Scanner(Root, index, access, device)) await scanner.ScanAsync(Token);
            var local = index.LocalFiles.Find(Path.GetFileName(copy))!;
            if (readOnly) { local.Record.ShouldBeNull(); index.ReadIndexBatch(0).Records.ShouldNotContain(row => row.Path == local.Path); }
            else local.Record.ShouldNotBeNull();
            index.HasPendingCasePreservation("topic").ShouldBeTrue();
        }
        finally { File.SetAttributes(originalPath, FileAttributes.Directory); if (copy is not null && Directory.Exists(copy)) File.SetAttributes(copy, FileAttributes.Directory); }
    }
    [Fact]
    public async Task ReplacedAccountedRootIsPreservedAndANewNativeIdentityUsesAnotherSlot()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var applier = new FileApplier(Root, index, keys, device); await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var prior = index.ReadPendingCasePreservations().Single(); string old = Path.Combine(Root, prior.DestinationPath!);
        Directory.Move(old, old + ".user"); Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old, "user"), "foreign replacement bytes");
        await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token); var next = index.ReadPendingCasePreservations().Single();
        next.DestinationPath.ShouldBe(ConflictNames.CaseSourcePath("topic", prior.Source.SourceId, 2)); next.InstalledFileId.ShouldNotBe(prior.InstalledFileId);
        File.ReadAllText(Path.Combine(old, "user")).ShouldBe("foreign replacement bytes"); Directory.Exists(old + ".user").ShouldBeTrue();
    }
    [Fact]
    public async Task NativeRootRenameFollowedByAccountingFailureKeepsItsIntentAndRecoversAtomically()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        Sql("CREATE TRIGGER reject_root_account BEFORE UPDATE OF installed_file_id ON case_preservations WHEN NEW.installed_file_id IS NOT NULL BEGIN SELECT RAISE(ABORT,'root account failure'); END;");
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "case-root-sql", debugEnabled: true);
        var first = new FileApplier(Root, index, keys, device, journal: journal);
        await Should.ThrowAsync<SqliteException>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token));
        var intent = index.ReadApplyIntents().Single(); var state = DirectoryCaseRootState.Decode(intent, keys); state.Prepared.ShouldBeTrue();
        Directory.Exists(Path.Combine(Root, state.Destination)).ShouldBeTrue(); index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBeNull();
        Sql("DROP TRIGGER reject_root_account;"); await new FileApplier(Root, index, keys, device).RecoverAsync(Token);
        index.ReadApplyIntents().ShouldBeEmpty(); index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBe(state.TemporaryId);
        journal.DroppedEvents.ShouldBe(0); string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("case-root-account-failed"); log.ShouldContain("SqliteException"); log.ShouldContain("\"nativeError\":1811"); log.ShouldContain(intent.Id.ToString());
        log.ShouldNotContain("root account failure"); log.ShouldNotContain("topic");
    }
    [Fact]
    public async Task OccupiedRecoveryDestinationPreservesBothObjectsAndUsesAnotherRootSlot()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == "case-root-prepared") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token)); var state = DirectoryCaseRootState.Decode(index.ReadApplyIntents().Single(), keys);
        File.WriteAllText(Path.Combine(Root, state.Destination), "foreign recovery occupant");
        var resumed = new FileApplier(Root, index, keys, device); await resumed.RecoverAsync(Token); await resumed.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        File.ReadAllText(Path.Combine(Root, state.Destination)).ShouldBe("foreign recovery occupant"); Directory.Exists(Path.Combine(Root, state.Temporary)).ShouldBeTrue();
        index.ReadPendingCasePreservations().Single().DestinationPath.ShouldBe(ConflictNames.CaseSourcePath(state.Source.Path, state.SourceId, 2));
    }
    [Fact]
    public async Task RetiringGraphBlocksPreparedRecoveryWithoutMutatingOrDroppingItsWitness()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == "case-root-prepared") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token)); var intent = index.ReadApplyIntents().Single();
        var state = DirectoryCaseRootState.Decode(intent, keys); Sql("UPDATE directory_graphs SET retiring=1;");
        await Should.ThrowAsync<InvalidDataException>(() => new FileApplier(Root, index, keys, device).RecoverAsync(Token));
        index.ReadApplyIntents().Single().Payload.ToArray().ShouldBe(intent.Payload.ToArray()); Directory.Exists(Path.Combine(Root, state.Temporary)).ShouldBeTrue();
        Directory.Exists(Path.Combine(Root, state.Destination)).ShouldBeFalse();
        Sql("UPDATE directory_graphs SET retiring=0;"); await new FileApplier(Root, index, keys, device).RecoverAsync(Token);
        index.ReadApplyIntents().ShouldBeEmpty();
    }
    [Fact]
    public async Task DirectoryRootJournalRejectsNoncanonicalFlagsBoundsBindingsTrailingBytesAndOtherIntentKinds()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var first = new FileApplier(Root, index, keys, device) { Checkpoint = stage => { if (stage == "case-root-prepared") throw new Stop(); } };
        await Should.ThrowAsync<Stop>(() => first.PrepareCaseCopiesAsync(FetchRootMetadata, Token)); var intent = index.ReadApplyIntents().Single();
        var original = intent.Payload.ToArray(); DirectoryCaseRootState.Decode(intent, keys).Encode().ShouldBe(original); var mutations = new List<byte[]>();
        var flag = original.ToArray(); flag[^5] = 2; mutations.Add(flag);
        var length = original.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length.AsSpan(4), int.MaxValue); mutations.Add(length);
        var kind = original.ToArray(); kind[0] = 7; mutations.Add(kind); mutations.Add([.. original, 0]);
        using (var stream = new MemoryStream(original, writable: false)) using (var reader = new BinaryReader(stream))
        {
            reader.ReadInt32(); for (int field = 0; field < 5; field++) reader.ReadBytes(reader.ReadInt32());
            var collision = original.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(collision.AsSpan((int)stream.Position), 1001); mutations.Add(collision);
            var sourceId = original.ToArray(); sourceId[(int)stream.Position - 1] ^= 1; mutations.Add(sourceId);
        }
        foreach (var payload in mutations) Should.Throw<InvalidDataException>(() => DirectoryCaseRootState.Decode(new(intent.Id, intent.PathKey, null, payload), keys));
        Should.Throw<InvalidDataException>(() => DirectoryCaseRootState.Decode(new(intent.Id, "another", null, original), keys));
        Should.Throw<InvalidDataException>(() => DirectoryCaseRootState.Decode(new(intent.Id, intent.PathKey, 1, original), keys));
        DirectoryCaseRootState.Decode(intent, keys).ToString().ShouldNotContain("topic");
    }
    private static ValueTask<ReadOnlyMemory<byte>> FetchRootMetadata(ReadOnlyMemory<byte> peer, ReadOnlyMemory<byte> hash, int length, CancellationToken token) =>
        throw new IOException("Fixture metadata must already be retained.");

    [Fact]
    public async Task BoundedGraphContinuationCreatesNoNativeRootUntilEveryMemberIsValidated()
    {
        WriteFiles("a", "b", "c"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var pending = index.ReadPendingCasePreservations().Single(); string destination = ConflictNames.CaseSourcePath("topic", pending.Source.SourceId);
        var applier = new FileApplier(Root, index, keys, device) { DirectoryCaseGraphWorkLimit = 1 };
        for (int pass = 0; pass < 3; pass++)
        {
            (await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token)).ShouldBeFalse(); Directory.Exists(Path.Combine(Root, destination)).ShouldBeFalse();
            index.ReadCasePreservationRetry(pending.Source.SourceId).ShouldBeNull(); index.ReadDirectoryGraph(pending.Source.SourceId)!.Complete.ShouldBeFalse();
        }
        (await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token)).ShouldBeTrue(); index.ReadDirectoryGraph(pending.Source.SourceId)!.Complete.ShouldBeTrue();
        Directory.Exists(Path.Combine(Root, destination)).ShouldBeTrue(); Directory.GetFileSystemEntries(Path.Combine(Root, destination)).ShouldBeEmpty();
        // Validated graph metadata and an empty root are not copied descendants.
        index.HasPendingCasePreservation("topic").ShouldBeTrue(); index.ReadApplyIntents().ShouldBeEmpty();
    }
    [Fact]
    public async Task FailedGraphAfterCommittedProgressStillConsumesTheSharedBudgetAndOtherSourcesResumeNextPass()
    {
        Directory.CreateDirectory(Path.Combine(Root, "zebra")); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        var body = VersionView.Open(index.LocalFiles.Find("zebra")!.Record!, keys, device).ExportBody(); body.Path = "ZEBRA";
        body.ModifiedBy = ByteString.CopyFrom([2, .. new byte[31]]); body.MtimeTicks += TimeSpan.TicksPerMinute; body.ChangedTicks += TimeSpan.TicksPerMinute;
        body.Vv.Clear(); body.Vv.Add(new VvEntry { Device = 2, Counter = 1 });
        await new NeededSet(index, keys, device).ApplyIndexUpdateAsync([2, .. new byte[31]], 1, 2, [SignedRecordFactory.Create(body, keys, 2)], Token);
        var pending = index.ReadPendingCasePreservations(); pending.Count.ShouldBe(2); bool interrupted = false;
        var applier = new FileApplier(Root, index, keys, device) { DirectoryCaseGraphWorkLimit = 1, Checkpoint = stage =>
        { if (stage == "progress" && !interrupted) { interrupted = true; throw new IOException("Fixture failure after committed graph work."); } } };
        (await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token)).ShouldBeFalse(); interrupted.ShouldBeTrue();
        index.ReadDirectoryGraph(pending[0].Source.SourceId)!.Complete.ShouldBeTrue(); index.ReadDirectoryGraph(pending[1].Source.SourceId)!.Complete.ShouldBeFalse();
        index.ReadCasePreservationRetry(pending[0].Source.SourceId).ShouldNotBeNull(); index.ReadCasePreservationRetry(pending[1].Source.SourceId).ShouldBeNull();
        index.ReadPendingCasePreservations().ShouldAllBe(row => row.DestinationPath == null);
        (await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token)).ShouldBeTrue();
        index.ReadPendingCasePreservations().Single(row => row.Source.SourceId.SequenceEqual(pending[1].Source.SourceId)).InstalledFileId.ShouldNotBeNull();
    }
}
