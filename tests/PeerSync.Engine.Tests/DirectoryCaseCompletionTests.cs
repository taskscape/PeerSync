using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryCompletionResumesVerifiedPrefixesAfterReopenAndRetainsReadonlyGraphs(bool readOnly)
    {
        WriteFiles("a", "b", "c"); File.WriteAllText(Path.Combine(Root, "topic", "a"), "retained original bytes"); string destination; byte[] source;
        using var access = FolderKeys.FromText(readOnly ? keys.ExportReadOnlyText() : keys.ExportText());
        await using (var index = await FolderIndex.OpenAsync(Database, Token))
        {
            var pending = await PrepareCompletion(index, access); destination = pending.DestinationPath!; source = pending.Source.SourceId;
            (await CompletionApplier(index, access, limit: 1).CompleteCaseCopiesAsync(Token)).ShouldBeEmpty();
            var proof = await index.BeginDirectoryCaseVerificationAsync(pending, index.LocalFiles.Find(destination)!.LocalSequence, !readOnly, Token);
            proof.LastKey.ShouldBe("TOPIC/A"); proof.Complete.ShouldBeFalse();
            index.HasPendingCasePreservation("topic").ShouldBeTrue(); index.HasPendingCasePreservation(destination + "/a").ShouldBeTrue();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        var resumed = CompletionApplier(reopened, access, limit: 1);
        (await resumed.CompleteCaseCopiesAsync(Token)).ShouldBeEmpty();
        (await resumed.CompleteCaseCopiesAsync(Token)).ShouldBeEmpty();
        (await resumed.CompleteCaseCopiesAsync(Token)).ShouldBe(["TOPIC"]);
        reopened.ReadPendingCasePreservations().ShouldBeEmpty(); reopened.HasPendingCasePreservation(destination).ShouldBeFalse();
        File.ReadAllText(Path.Combine(Root, destination, "a")).ShouldBe("retained original bytes");
        var copied = reopened.LocalFiles.Find(destination + "/a")!;
        if (readOnly)
        {
            copied.Record.ShouldBeNull();
            (await reopened.RetireDirectoryCaseWitnessesAsync(reopened.Meta.Find("_root-identity")!.Value!, DateTime.UtcNow.AddDays(2).Ticks, cancellationToken: Token)).ShouldBeGreaterThan(0);
            File.ReadAllText(Path.Combine(Root, destination, "a")).ShouldBe("retained original bytes");
            reopened.ReadRetainedDirectoryCases(reopened.Meta.Find("_root-identity")!.Value!, DateTime.UtcNow.Ticks).Single().SourceId.ShouldBe(source);
            // Isolate the original retained graph as the sole source of references.
            Sql("DELETE FROM local_files; DELETE FROM remote_files; DELETE FROM needed; DELETE FROM directory_publications;");
            var refresh = await new BlockReferences(Root, reopened, access, device).RefreshAsync(Token);
            refresh.SnapshotId.ShouldNotBeNull(); reopened.ReadBlockReferences().ShouldNotBeEmpty();
            reopened.ReadBlockReferences().All(row => (row.Kinds & BlockReferenceKind.History) != 0).ShouldBeTrue();
            reopened.ReadBlockReferences().Any(row => row.Hash.AsSpan().SequenceEqual(copied.FileHash)).ShouldBeTrue();
        }
        else
        {
            copied.Record.ShouldNotBeNull(); copied.ContentReady.ShouldBeTrue();
            reopened.ReadRetainedDirectoryCases(reopened.Meta.Find("_root-identity")!.Value!, DateTime.UtcNow.Ticks).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task ReplacedCopiedRootPreservesBothTreesAndCannotCompleteWithTheOldIdentity()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var access = FolderKeys.FromText(keys.ExportReadOnlyText()); var pending = await PrepareCompletion(index, access);
        string path = Path.Combine(Root, pending.DestinationPath!); Directory.Move(path, path + ".saved");
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "user"), "replacement directory");
        (await CompletionApplier(index, access).CompleteCaseCopiesAsync(Token)).ShouldBeEmpty();
        index.ReadPendingCasePreservations().Single().InstalledFileId.ShouldBeNull();
        File.ReadAllText(Path.Combine(path, "user")).ShouldBe("replacement directory"); File.Exists(Path.Combine(path + ".saved", "a")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrMissingCopiedMemberReselectsTheRootWithoutRemovingUserBytes(bool missing)
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var access = FolderKeys.FromText(keys.ExportReadOnlyText()); var pending = await PrepareCompletion(index, access);
        string path = Path.Combine(Root, pending.DestinationPath!, "a");
        if (missing) File.Delete(path); else File.WriteAllText(path, "edited copied member");
        (await CompletionApplier(index, access).CompleteCaseCopiesAsync(Token)).ShouldBeEmpty();
        var next = index.ReadPendingCasePreservations().Single(); next.InstalledFileId.ShouldBeNull(); next.DestinationPath.ShouldBeNull();
        Directory.Exists(Path.Combine(Root, pending.DestinationPath!)).ShouldBeTrue();
        if (!missing) File.ReadAllText(path).ShouldBe("edited copied member");
    }

    [Fact]
    public async Task DirectoryCompletionEpochChangeAtFinalCommitRetainsTheSourceAndRestartsVerification()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var access = FolderKeys.FromText(keys.ExportReadOnlyText()); var pending = await PrepareCompletion(index, access);
        var first = CompletionApplier(index, access, stage => { if (stage == "case-tree-before-completion") Sql("UPDATE local_files SET flags=flags;"); });
        (await first.CompleteCaseCopiesAsync(Token)).ShouldBeEmpty(); index.ReadPendingCasePreservations().Count.ShouldBe(1);
        var proof = await index.BeginDirectoryCaseVerificationAsync(pending, index.LocalFiles.Find(pending.DestinationPath!)!.LocalSequence, false, Token);
        proof.Complete.ShouldBeFalse(); proof.LastKey.ShouldBeNull();
        // The retry uses the injected clock; advance beyond its next-attempt time.
        (await CompletionApplier(index, access, hours: 2).CompleteCaseCopiesAsync(Token)).ShouldBe(["TOPIC"]);
    }

    [Fact]
    public async Task FailedDirectoryCompletionTransactionKeepsNativeBytesAndTheOriginalSource()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var access = FolderKeys.FromText(keys.ExportReadOnlyText()); var pending = await PrepareCompletion(index, access);
        Sql("CREATE TRIGGER fail_case_completion BEFORE UPDATE OF completed ON case_preservations BEGIN SELECT RAISE(ABORT,'private completion failure'); END;");
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "completion-sql", debugEnabled: true);
        var applier = new FileApplier(Root, index, access, device, journal: journal, clock: new CompletionClock(DateTimeOffset.UtcNow.AddHours(1)));
        await Should.ThrowAsync<SqliteException>(() => applier.CompleteCaseCopiesAsync(Token));
        index.ReadPendingCasePreservations().Single().Source.SourceId.ShouldBe(pending.Source.SourceId);
        File.Exists(Path.Combine(Root, pending.DestinationPath!, "a")).ShouldBeTrue();
        Sql("DROP TRIGGER fail_case_completion;");
        (await CompletionApplier(index, access).CompleteCaseCopiesAsync(Token)).ShouldBe(["TOPIC"]);
        string log = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl"));
        log.ShouldContain("case-tree-transaction-failed"); log.ShouldContain("\"nativeError\":1811");
        log.ShouldNotContain("private completion failure"); log.ShouldNotContain("topic"); journal.DroppedEvents.ShouldBe(0);
    }

    private async Task<PendingCasePreservation> PrepareCompletion(FolderIndex index, FolderKeys access)
    {
        await AdmitDirectoryCase(index); var applier = new FileApplier(Root, index, access, device);
        for (int pass = 0; pass < 8; pass++)
        {
            await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
            var pending = index.ReadPendingCasePreservations().Single();
            if (pending.InstalledFileId is not null && index.ReadDirectoryCaseProgress(pending.Source.SourceId, pending.InstalledFileId).Exhausted) break;
        }
        var installed = index.ReadPendingCasePreservations().Single();
        index.ReadDirectoryCaseProgress(installed.Source.SourceId, installed.InstalledFileId!).Exhausted.ShouldBeTrue();
        if (access.CanWrite)
        {
            await PreviewScan(index, access); await Worker(index, access).RunAsync(token: Token);
            index.LocalFiles.Find(installed.DestinationPath!)!.Record.ShouldNotBeNull();
        }
        else { using var scanner = new Scanner(Root, index, access, device); await scanner.ScanAsync(Token); }
        return installed;
    }

    private FileApplier CompletionApplier(FolderIndex index, FolderKeys access, Action<string>? checkpoint = null, int hours = 1, int limit = 32) =>
        new(Root, index, access, device, clock: new CompletionClock(DateTimeOffset.UtcNow.AddHours(hours)))
        { DirectoryCaseVerificationWorkLimit = limit, Checkpoint = checkpoint };

    private sealed class CompletionClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        internal void AdvanceTo(long ticks) => now = new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    [Fact]
    public async Task ExhaustedPreparationCannotContinuouslyPostponeCompletionAtItsRetryDeadline()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        using var access = FolderKeys.FromText(keys.ExportReadOnlyText()); var clock = new CompletionClock(DateTimeOffset.UtcNow);
        var applier = new FileApplier(Root, index, access, device, clock: clock);
        await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token); await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        var pending = index.ReadPendingCasePreservations().Single();
        (await applier.CompleteCaseCopiesAsync(Token)).ShouldBeEmpty(); // The copy has not been scanned.
        var retry = index.ReadCasePreservationRetry(pending.Source.SourceId)!.Value; clock.AdvanceTo(retry.NextTicks);
        await applier.PrepareCaseCopiesAsync(FetchRootMetadata, Token);
        index.ReadCasePreservationRetry(pending.Source.SourceId).ShouldBe(retry);
        using (var scanner = new Scanner(Root, index, access, device)) await scanner.ScanAsync(Token);
        (await applier.CompleteCaseCopiesAsync(Token)).ShouldBe(["TOPIC"]); index.ReadPendingCasePreservations().ShouldBeEmpty();
    }
}
