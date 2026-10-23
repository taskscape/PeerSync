using System.Buffers.Binary;
using System.Threading.Channels;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Crypto.Chunking;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Tests;

public sealed class PullerTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Puller-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static readonly byte[] Own = Device(1), Peer = Device(2), Old = "published original"u8.ToArray(), New = "incoming replacement"u8.ToArray();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    public PullerTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task RealPassCreatesParentsThenFilesByPriorityThenFileAndDeepDirectoryDeletes()
    {
        Directory.CreateDirectory(Disk("gone")); File.WriteAllBytes(Disk("gone/child"), Old); File.WriteAllBytes(Disk("old"), Old);
        await using var index = await FolderIndex.OpenAsync(Database, Token); var fetched = new List<int>();
        using var puller = Coordinator(index, (peer, hash, length, token) =>
        {
            peer.ToArray().ShouldBe(Peer); Directory.Exists(Disk("dir/sub")).ShouldBeTrue();
            File.Exists(Disk("old")).ShouldBeTrue(); File.Exists(Disk("gone/child")).ShouldBeTrue(); fetched.Add(length);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Enumerable.Repeat((byte)length, length).ToArray());
        }, new FolderSettings { Priority = TransferPriority.SmallerFirst });
        await puller.RunPassAsync(Token);
        Envelope[] records = [Record(index,"dir",1,type:1), Record(index,"dir/sub",2,type:1),
            Record(index,"dir/sub/big",3,[4,4,4,4]), Record(index,"dir/sub/small",4,[2,2]),
            Record(index,"old",5,deleted:true), Record(index,"gone",6,type:1,deleted:true), Record(index,"gone/child",7,deleted:true)];
        await puller.AnnounceIndexRangeAsync(Peer, 7, Token); await puller.ReceiveIndexAsync(Peer, 0, 7, records, Token);
        var result = await puller.RunPassAsync(Token); result.Applied.ShouldBe(7); result.Failed.ShouldBe(0); result.Scans.ShouldBe(1); fetched.ShouldBe([2,4]);
        long Sequence(string path) => index.LocalFiles.Find(path)!.LocalSequence;
        Sequence("dir").ShouldBeLessThan(Sequence("dir/sub")); Sequence("dir/sub").ShouldBeLessThan(Sequence("dir/sub/small"));
        Sequence("dir/sub/small").ShouldBeLessThan(Sequence("dir/sub/big")); Sequence("dir/sub/big").ShouldBeLessThan(Sequence("old"));
        Sequence("gone/child").ShouldBeLessThan(Sequence("gone"));
        File.Exists(Disk("old")).ShouldBeFalse(); Directory.Exists(Disk("gone")).ShouldBeFalse(); index.Needed.Count().ShouldBe(0);
        index.ReadApplyIntents().ShouldBeEmpty(); index.HasPendingDirectoryPublications().ShouldBeTrue(); result.MoreWork.ShouldBeTrue();
        var settled = await puller.RunPassAsync(Token); settled.Applied.ShouldBe(0); index.HasPendingDirectoryPublications().ShouldBeFalse(); settled.NextAttemptTicks.ShouldBeNull();
    }

    [Fact]
    public async Task FileIndexPageBeforeParentDirectoryWaitsWithoutNativeFailureThenApplies()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token);
        using var puller = Coordinator(index);
        await puller.RunPassAsync(Token);
        await puller.AnnounceIndexRangeAsync(Peer, 2, Token);
        await puller.ReceiveIndexAsync(Peer, 0, 1, [Record(index, "folder/child", 1, New)], Token);

        var waiting = await puller.RunPassAsync(Token);
        waiting.Visited.ShouldBe(0);
        waiting.Failed.ShouldBe(0);
        index.Needed.Find("folder/child").ShouldNotBeNull();
        index.ReadPullRetry("folder/child").ShouldBeNull();
        Directory.Exists(Disk("folder")).ShouldBeFalse();

        await puller.ReceiveIndexAsync(Peer, 1, 2, [Record(index, "folder", 2, type: 1)], Token);
        var applied = await puller.RunPassAsync(Token);
        applied.Applied.ShouldBe(2);
        applied.Failed.ShouldBe(0);
        File.ReadAllBytes(Disk("folder/child")).ShouldBe(New);
        index.Needed.Count().ShouldBe(0);
    }

    [Fact]
    public async Task LockedTargetBacksOffAcrossCoordinatorRestartWhileIndependentPathProgresses()
    {
        File.WriteAllBytes(Disk("locked"), Old); File.WriteAllBytes(Disk("good"), Old);
        await using var index = await FolderIndex.OpenAsync(Database, Token); var clock = new MutableClock();
        using (var first = Coordinator(index, clock: clock))
        {
            await first.RunPassAsync(Token);
            await first.ReceiveIndexAsync(Peer, 0, 2, [Record(index,"locked",1,New),Record(index,"good",2,New)], Token);
            using (File.Open(Disk("locked"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await first.RunPassAsync(Token); result.Failed.ShouldBe(1); result.Applied.ShouldBe(1); result.Recovered.ShouldBe(1);
                index.ReadApplyIntents().ShouldBeEmpty(); File.ReadAllBytes(Disk("good")).ShouldBe(New);
                index.ReadPullRetry("locked")!.Attempts.ShouldBe(1); result.MoreWork.ShouldBeFalse();
            }
        }
        using var second = Coordinator(index, clock: clock);
        (await second.RunPassAsync(Token)).Visited.ShouldBe(0); File.ReadAllBytes(Disk("locked")).ShouldBe(Old);
        clock.Advance(TimeSpan.FromSeconds(10)); (await second.RunPassAsync(Token)).Applied.ShouldBe(1);
        File.ReadAllBytes(Disk("locked")).ShouldBe(New); index.ReadPullRetry("locked").ShouldBeNull(); index.Needed.Count().ShouldBe(0);
    }

    [Fact]
    public async Task NewRemoteWorkInvalidatesBackoffAndWrongChunkCannotStopIndependentWorkOrLeakPrivateData()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); using var journal = new DiagnosticJournal(Path.Combine(fixture,"logs"),"puller",debugEnabled:true);
        bool corrupt = true; byte[] bad = "sensitive payload"u8.ToArray();
        using var puller = Coordinator(index, (_, hash, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(
            hash.Span.SequenceEqual(Hash(bad)) && corrupt ? new byte[bad.Length] : hash.Span.SequenceEqual(Hash(New)) ? New : bad), journal: journal);
        await puller.RunPassAsync(Token);
        await puller.ReceiveIndexAsync(Peer, 0, 2, [Record(index,"private-path",1,bad),Record(index,"independent",2,New)], Token);
        var result = await puller.RunPassAsync(Token); result.Failed.ShouldBe(1); result.Applied.ShouldBe(1); index.ReadApplyIntents().ShouldBeEmpty();
        index.ReadPullRetry("private-path").ShouldNotBeNull(); File.ReadAllBytes(Disk("independent")).ShouldBe(New);
        corrupt = false;
        var newer = Record(index,"private-path",3,bad); var body = VersionView.Open(newer, keys, Peer).ExportBody(); body.Vv.Single(v=>v.Device==2).Counter=2;
        await puller.ReceiveIndexAsync(Peer, 2, 3, [SignedRecordFactory.Create(body,keys,3)], Token);
        (await puller.RunPassAsync(Token)).Applied.ShouldBe(1); File.ReadAllBytes(Disk("private-path")).ShouldBe(bad);
        string log = File.ReadAllText(Path.Combine(fixture,"logs","debug.jsonl")); log.ShouldContain("puller.work_retry"); log.ShouldContain("retryInMs");
        log.ShouldNotContain("private-path"); log.ShouldNotContain("sensitive payload"); log.ShouldNotContain(keys.ExportText()); log.ShouldNotContain(Convert.ToHexString(Hash(bad)));
    }

    [Fact]
    public async Task StartupRecoversPreparedMoveBeforeScanningAndDoesNotCreateAFalseLocalEdit()
    {
        File.WriteAllBytes(Disk("file"),Old); await using var index = await FolderIndex.OpenAsync(Database,Token);
        using (var scanner = new Scanner(Root,index,keys,Own)) await scanner.ScanAsync(Token);
        long ownCounter = (long)index.LocalFiles.Find("file")!.Vector[1]; var needed = new NeededSet(index,keys,Own);
        await needed.ApplyIndexUpdateAsync(Peer,0,1,[Record(index,"file",1,New)],Token);
        var applier = new FileApplier(Root,index,keys,Own) { Checkpoint = stage => { if(stage=="archived") throw new IOException("injected"); } };
        await Should.ThrowAsync<IOException>(()=>applier.ApplyAsync("file",(_,_,_)=>ValueTask.FromResult<ReadOnlyMemory<byte>>(New),Token));
        File.Exists(Disk("file")).ShouldBeFalse(); index.ReadApplyIntents().Count().ShouldBe(1);
        using var puller = Coordinator(index,(_,_,_,_)=>throw new InvalidOperationException("Recovery needs no network"));
        var result = await puller.RunPassAsync(Token); result.Recovered.ShouldBe(1); result.Scans.ShouldBe(1);
        File.ReadAllBytes(Disk("file")).ShouldBe(New); index.LocalFiles.Find("file")!.Vector[1].ShouldBe((ulong)ownCounter);
        File.ReadAllBytes(Disk(index.Archive.ReadAll().Single().ArchivePath)).ShouldBe(Old); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task PreservedPublicCopiesAreScannedOncePerBatchAndBecomeSignedLocalFiles()
    {
        for(int i=0;i<10;i++) File.WriteAllBytes(Disk("file"+i),Old);
        await using var index=await FolderIndex.OpenAsync(Database,Token); using var puller=Coordinator(index,settings:new FolderSettings { KeepBothCopies=true });
        await puller.RunPassAsync(Token);
        await puller.ReceiveIndexAsync(Peer,0,10,Enumerable.Range(0,10).Select(i=>Record(index,"file"+i,(ulong)i+1,New)).ToArray(),Token);
        var result=await puller.RunPassAsync(Token); result.Applied.ShouldBe(10); result.Scans.ShouldBe(1);
        string[] copies=Directory.GetFiles(Root,"*.sync-conflict-*"); copies.Length.ShouldBe(10);
        foreach(string copy in copies) { File.ReadAllBytes(copy).ShouldBe(Old); index.LocalFiles.Find(Path.GetFileName(copy))!.Record.ShouldNotBeNull(); }
        index.Needed.Count().ShouldBe(0); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task IncompleteIndexDoesNotConsumeRetriesAndCompletionImmediatelyMakesDeletionEligible()
    {
        File.WriteAllBytes(Disk("file"),Old); await using var index=await FolderIndex.OpenAsync(Database,Token); using var puller=Coordinator(index);
        await puller.RunPassAsync(Token); await puller.AnnounceIndexRangeAsync(Peer,2,Token);
        await puller.ReceiveIndexAsync(Peer,0,1,[Record(index,"file",1,deleted:true)],Token);
        var waiting=await puller.RunPassAsync(Token); waiting.Visited.ShouldBe(0); waiting.MoreWork.ShouldBeFalse(); waiting.NextAttemptTicks.ShouldBeNull();
        index.ReadPullRetry("file").ShouldBeNull(); File.Exists(Disk("file")).ShouldBeTrue();
        await puller.ReceiveIndexAsync(Peer,1,2,[],Token); (await puller.RunPassAsync(Token)).Applied.ShouldBe(1); File.Exists(Disk("file")).ShouldBeFalse();
    }

    [Fact]
    public async Task CancellationDuringDownloadRecoversBeforeNextScanAndRetry()
    {
        File.WriteAllBytes(Disk("file"),Old); await using var index=await FolderIndex.OpenAsync(Database,Token);
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(Token); bool cancel=true;
        using var puller=Coordinator(index,(_,_,_,_)=>{ if(cancel) cancellation.Cancel(); return ValueTask.FromResult<ReadOnlyMemory<byte>>(New); });
        await puller.RunPassAsync(Token); await puller.ReceiveIndexAsync(Peer,0,1,[Record(index,"file",1,New)],Token);
        await Should.ThrowAsync<OperationCanceledException>(()=>puller.RunPassAsync(cancellation.Token));
        File.ReadAllBytes(Disk("file")).ShouldBe(Old); index.ReadApplyIntents().Count().ShouldBe(1);
        cancel=false; var retry=await puller.RunPassAsync(Token); retry.Recovered.ShouldBe(1); retry.Applied.ShouldBe(1);
        File.ReadAllBytes(Disk("file")).ShouldBe(New); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task HostedLoopWakesForIndexAndLocalChangesAndReleasesIdleGatesOnCancellation()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); using var puller=Coordinator(index);
        using var stopping=CancellationTokenSource.CreateLinkedTokenSource(Token); Task running=puller.RunAsync(stopping.Token);
        try
        {
            (await Next(puller)).Scans.ShouldBe(1);
            await Should.ThrowAsync<InvalidOperationException>(()=>puller.RunAsync(Token));
            using(await FolderOperationGate.AcquireAsync(index,Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5),Token))
                await puller.ReceiveIndexAsync(Peer,0,1,[Record(index,"file",1,New)],Token).WaitAsync(TimeSpan.FromSeconds(5),Token);
            PullPassResult applied; do { applied=await Next(puller); } while(applied.Applied==0);
            File.ReadAllBytes(Disk("file")).ShouldBe(New);
            File.WriteAllBytes(Disk("local"),Old); puller.RequestScan();
            PullPassResult scanned; do { scanned=await Next(puller); } while(scanned.Scans==0);
            index.LocalFiles.Find("local")!.Record.ShouldNotBeNull();
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5),Token); }
        using var again=CancellationTokenSource.CreateLinkedTokenSource(Token); Task restarted=puller.RunAsync(again.Token);
        try { await Next(puller); } finally { again.Cancel(); await restarted.WaitAsync(TimeSpan.FromSeconds(5),Token); }
    }

    [Fact]
    public async Task PullerSharesScanBudgetAndCanCancelWithoutBindingOrLeakingItsPassGate()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); using var puller=Coordinator(index);
        using var cancelled=CancellationTokenSource.CreateLinkedTokenSource(Token);
        using(await FolderScanBudget.AcquireAsync(Token))
        {
            var waiting=puller.RunPassAsync(cancelled.Token); waiting.IsCompleted.ShouldBeFalse();
            await puller.AnnounceIndexRangeAsync(Peer,0,Token).WaitAsync(TimeSpan.FromSeconds(5),Token);
            cancelled.Cancel(); await Should.ThrowAsync<OperationCanceledException>(()=>waiting); index.Meta.Find("_root-identity").ShouldBeNull();
        }
        (await puller.RunPassAsync(Token).WaitAsync(TimeSpan.FromSeconds(5),Token)).Scans.ShouldBe(1);
    }

    [Fact]
    public async Task HostedDurableRetryWakesAtItsPersistedDeadlineWithoutAnotherNotification()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new TimerClock(); bool corrupt=true;
        using var puller=Coordinator(index,(_,_,length,_)=>ValueTask.FromResult<ReadOnlyMemory<byte>>(corrupt?new byte[length]:New),clock:clock);
        using var stopping=CancellationTokenSource.CreateLinkedTokenSource(Token); Task running=puller.RunAsync(stopping.Token);
        try
        {
            await Next(puller); (await clock.Next()).Delay.ShouldBe(TimeSpan.FromSeconds(600));
            await puller.ReceiveIndexAsync(Peer,0,1,[Record(index,"file",1,New)],Token);
            (await Next(puller)).Failed.ShouldBe(1); var retry=await clock.Next(); retry.Delay.ShouldBe(TimeSpan.FromSeconds(10));
            index.ReadPullRetry("file")!.NextTicks.ShouldBe(clock.GetUtcNow().UtcTicks+10*TimeSpan.TicksPerSecond);
            corrupt=false; clock.Advance(TimeSpan.FromSeconds(10)); retry.Fire();
            (await Next(puller)).Applied.ShouldBe(1); File.ReadAllBytes(Disk("file")).ShouldBe(New); index.ReadPullRetry("file").ShouldBeNull();
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5),Token); }
    }

    [Fact]
    public async Task HostedPeriodicScanWakesAtItsDeadlineWithoutAWatcherNotification()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new TimerClock();
        using var puller=Coordinator(index,settings:new FolderSettings { RescanIntervalSeconds=2 },clock:clock);
        using var stopping=CancellationTokenSource.CreateLinkedTokenSource(Token); Task running=puller.RunAsync(stopping.Token);
        try
        {
            await Next(puller); var timer=await clock.Next(); timer.Delay.ShouldBe(TimeSpan.FromSeconds(2));
            File.WriteAllBytes(Disk("periodic"),Old); clock.Advance(TimeSpan.FromSeconds(2)); timer.Fire();
            (await Next(puller)).Scans.ShouldBe(1); index.LocalFiles.Find("periodic")!.Record.ShouldNotBeNull();
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5),Token); }
    }

    [Fact]
    public async Task MissingRootRetriesFolderWithoutHoldingTheScanBudgetAndNotificationsCannotBypassDelay()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new TimerClock(); using var puller=Coordinator(index,clock:clock);
        using var stopping=CancellationTokenSource.CreateLinkedTokenSource(Token); Task running=puller.RunAsync(stopping.Token);
        try
        {
            await Next(puller); await clock.Next(); string moved=Path.Combine(fixture,"moved"); Directory.Move(Root,moved); puller.RequestScan();
            var retry=await clock.Next(); retry.Delay.ShouldBe(TimeSpan.FromSeconds(10));
            for(int i=0;i<20;i++) puller.NotifyWorkAvailable();
            using(await FolderScanBudget.AcquireAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5),Token)) { }
            Directory.Move(moved,Root); clock.Advance(TimeSpan.FromSeconds(10)); retry.Fire();
            (await Next(puller)).Scans.ShouldBe(1);
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5),Token); }
    }

    [Fact]
    public async Task UnboundRecoveryStateFailsBeforeScanningOrFetchingAndUnavailableDiagnosticsDoNotReplaceTheFailure()
    {
        File.WriteAllBytes(Disk("file"),Old); await using var index=await FolderIndex.OpenAsync(Database,Token);
        await index.ReserveApplyAsync(new(Guid.NewGuid(),"file",null,[1]),Token);
        using var journal=new DiagnosticJournal(Path.Combine(fixture,"logs"),"puller"); journal.Dispose();
        using var puller=Coordinator(index,(_,_,_,_)=>throw new InvalidOperationException("No network"),journal:journal);
        await Should.ThrowAsync<InvalidDataException>(()=>puller.RunPassAsync(Token));
        index.Meta.Find("_root-identity").ShouldBeNull(); index.ReadApplyIntents().Count().ShouldBe(1); File.ReadAllBytes(Disk("file")).ShouldBe(Old);
        journal.DroppedEvents.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task UnknownChildSurvivesDirectoryTombstoneAndPostBatchScanSignsItsActualContents()
    {
        Directory.CreateDirectory(Disk("dir")); await using var index=await FolderIndex.OpenAsync(Database,Token); using var puller=Coordinator(index);
        await puller.RunPassAsync(Token); File.WriteAllBytes(Disk("dir/unknown"),Old); index.LocalFiles.Find("dir/unknown").ShouldBeNull();
        await puller.AnnounceIndexRangeAsync(Peer,1,Token); await puller.ReceiveIndexAsync(Peer,0,1,[Record(index,"dir",1,type:1,deleted:true)],Token);
        var result=await puller.RunPassAsync(Token); result.Applied.ShouldBe(1); result.Scans.ShouldBe(1);
        File.ReadAllBytes(Disk("dir/unknown")).ShouldBe(Old); index.LocalFiles.Find("dir/unknown")!.Record.ShouldNotBeNull();
        index.LocalFiles.Find("dir")!.Deleted.ShouldBeTrue(); index.HasPendingDirectoryPublications().ShouldBeTrue();
        var published = await puller.RunPassAsync(Token); published.Failed.ShouldBe(0); index.HasPendingDirectoryPublications().ShouldBeFalse();
        index.LocalFiles.Find("dir")!.Deleted.ShouldBeFalse(); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task TargetChangedDuringFetchIsScannedOutsideMutationGatesAndPreservedByTheNextApplication()
    {
        File.WriteAllBytes(Disk("file"),Old); await using var index=await FolderIndex.OpenAsync(Database,Token); bool change=true; byte[] user="concurrent user bytes"u8.ToArray();
        using var puller=Coordinator(index,(_,_,_,_)=>
        {
            if(change) { change=false; File.WriteAllBytes(Disk("file"),user); } return ValueTask.FromResult<ReadOnlyMemory<byte>>(New);
        });
        await puller.RunPassAsync(Token); await puller.ReceiveIndexAsync(Peer,0,1,[Record(index,"file",1,New)],Token);
        var deferred=await puller.RunPassAsync(Token); deferred.Deferred.ShouldBe(1); deferred.Scans.ShouldBe(1);
        File.ReadAllBytes(Disk("file")).ShouldBe(user); index.LocalFiles.Find("file")!.FileHash.ShouldBe(Hash(user)); index.ReadApplyIntents().ShouldBeEmpty();
        (await puller.RunPassAsync(Token)).Applied.ShouldBe(1); File.ReadAllBytes(Disk("file")).ShouldBe(New);
        index.Archive.ReadAll().Select(row=>File.ReadAllBytes(Disk(row.ArchivePath))).ShouldContain(bytes=>bytes.SequenceEqual(user));
    }

    [Theory]
    [InlineData(0u,0L)]
    [InlineData(1u,0L)]
    [InlineData(0u,504911232000000000L)]
    [InlineData(1u,504911232000000000L)]
    public async Task UnrepresentableNativeTimestampBlocksOnlyItsItem(uint type,long mtime)
    {
        File.WriteAllBytes(Disk("bad"),Old); await using var index=await FolderIndex.OpenAsync(Database,Token); using var puller=Coordinator(index);
        await puller.RunPassAsync(Token); var body=VersionView.Open(Record(index,"bad",1,type==0?New:[],type),keys,Peer).ExportBody(); body.MtimeTicks=mtime;
        await puller.ReceiveIndexAsync(Peer,0,2,[SignedRecordFactory.Create(body,keys,1),Record(index,"good",2,New)],Token);
        var result=await puller.RunPassAsync(Token); result.Applied.ShouldBe(1); result.Deferred.ShouldBe(1); result.Failed.ShouldBe(0);
        File.ReadAllBytes(Disk("bad")).ShouldBe(Old); File.ReadAllBytes(Disk("good")).ShouldBe(New);
        index.ReadPullRetry("bad")!.Attempts.ShouldBe(1); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task IncrementalScanExcludesNonNfcWireNamesWithoutStoppingValidPulls()
    {
        await using var index=await FolderIndex.OpenAsync(Database,Token); using var puller=Coordinator(index); await puller.RunPassAsync(Token);
        const string invalid="e\u0301"; File.WriteAllBytes(Disk(invalid),Old); puller.RequestScan();
        await puller.ReceiveIndexAsync(Peer,0,1,[Record(index,"good",1,New)],Token);
        var result=await puller.RunPassAsync(Token); result.Applied.ShouldBe(1); result.Scans.ShouldBe(2);
        index.LocalFiles.ReadAll().Single(r=>r.Path==invalid).Flags.HasFlag(LocalFileState.Unsyncable).ShouldBeTrue();
        File.ReadAllBytes(Disk(invalid)).ShouldBe(Old); File.ReadAllBytes(Disk("good")).ShouldBe(New);
    }

    [Fact]
    public async Task NativeCacheOnlyChangeInvalidatesWorkBackoffWithoutWaitingForTheOldDeadline()
    {
        File.WriteAllBytes(Disk("file"),Old); await using var index=await FolderIndex.OpenAsync(Database,Token); using var puller=Coordinator(index);
        await puller.RunPassAsync(Token); await puller.ReceiveIndexAsync(Peer,0,1,[Record(index,"file",1,New)],Token);
        var row=index.Needed.Find("file")!; byte[] root=index.Meta.Find("_root-identity")!.Value!;
        await index.RecordPullFailureAsync("file",root,row.Work!.Fingerprint,DateTime.UtcNow.Ticks,Token);
        File.SetCreationTimeUtc(Disk("file"),DateTime.UtcNow.AddDays(-2)); puller.RequestScan();
        (await puller.RunPassAsync(Token)).Applied.ShouldBe(1); File.ReadAllBytes(Disk("file")).ShouldBe(New); index.ReadPullRetry("file").ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterLocalCommitRepairsNeededEvenWhenTheNextScanIsUnchanged(bool cancel)
    {
        File.WriteAllBytes(Disk("file"),Old); await using var index=await FolderIndex.OpenAsync(Database,Token); var clock=new MutableClock();
        using var puller=Coordinator(index,clock:clock); await puller.RunPassAsync(Token);
        await puller.ReceiveIndexAsync(Peer,0,1,[Record(index,"file",1,New)],Token);
        using(var locked=File.Open(Disk("file"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            locked.SetLength(New.Length); locked.Write(New); locked.Flush(flushToDisk:true); puller.RequestScan();
            (await puller.RunPassAsync(Token)).Visited.ShouldBe(0);
            index.LocalFiles.Find("file")!.Flags.HasFlag(LocalFileState.NeedsRescan).ShouldBeTrue(); index.Needed.Count().ShouldBe(0);
        }
        clock.Advance(TimeSpan.FromSeconds(11)); puller.RequestScan(); using var cancelled=CancellationTokenSource.CreateLinkedTokenSource(Token);
        if(cancel) puller.Checkpoint=stage=>{ if(stage=="after-local-scan") cancelled.Cancel(); };
        else Sql("CREATE TRIGGER reject_needed BEFORE INSERT ON needed BEGIN SELECT RAISE(ABORT,'test'); END;");
        if(cancel) await Should.ThrowAsync<OperationCanceledException>(()=>puller.RunPassAsync(cancelled.Token));
        else await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(()=>puller.RunPassAsync(Token));
        index.LocalFiles.Find("file")!.Flags.ShouldBe(LocalFileState.None); index.LocalFiles.Find("file")!.FileHash.ShouldBe(Hash(New)); index.Needed.Count().ShouldBe(0);
        puller.Checkpoint=null; if(!cancel) Sql("DROP TRIGGER reject_needed;");
        (await puller.RunPassAsync(Token)).Applied.ShouldBe(1); index.Needed.Count().ShouldBe(0); index.ReadApplyIntents().ShouldBeEmpty();
        void Sql(string sql)
        {
            using var connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource=Database,Pooling=false }.ToString()); connection.Open();
            using var command=connection.CreateCommand(); command.CommandText=sql; command.ExecuteNonQuery();
        }
    }

    private Puller Coordinator(FolderIndex index, Func<ReadOnlyMemory<byte>,ReadOnlyMemory<byte>,int,CancellationToken,ValueTask<ReadOnlyMemory<byte>>>? fetch=null,
        FolderSettings? settings=null, TimeProvider? clock=null, DiagnosticJournal? journal=null) => new(Root,index,keys,Own,
            fetch??((_,_,_,_)=>ValueTask.FromResult<ReadOnlyMemory<byte>>(New)),settings,clock,journal);
    [Theory]
    [InlineData("recreate")]
    [InlineData("absent")]
    [InlineData("incoming-delete")]
    [InlineData("cache-invalidated")]
    public async Task ReviewSuspendedChildReactivatesAfterAFileAncestorStopsBlockingIt(string transition)
    {
        File.WriteAllBytes(Disk("parent"), Old); await using var index = await FolderIndex.OpenAsync(Database, Token); var clock = new MutableClock(); using var puller = Coordinator(index, clock: clock);
        await puller.RunPassAsync(Token); await puller.ReceiveIndexAsync(Peer, 0, 1, [Record(index, "parent/child", 1, New)], Token);
        index.Needed.Find("parent/child").ShouldBeNull(); index.RemotesForPath("parent/child").Single().ShouldNotBeNull();
        if (transition == "cache-invalidated") await index.LocalFiles.UpsertAsync([index.LocalFiles.Find("parent")! with { Flags = LocalFileState.NeedsRescan }], Token);
        if (transition == "incoming-delete")
        {
            await puller.AnnounceIndexRangeAsync(Peer, 2, Token);
            await puller.ReceiveIndexAsync(Peer, 1, 2, [Record(index, "parent", 2, deleted: true)], Token);
        }
        else { File.Delete(Disk("parent")); puller.RequestScan(); }
        var result = await puller.RunPassAsync(Token); index.Needed.Find("parent/child").ShouldNotBeNull(
            $"transition={transition}; pass={result}; parent={index.LocalFiles.Find("parent")?.Type}/{index.LocalFiles.Find("parent")?.Deleted}; range={index.ReadRemoteIndexRange(Peer)}; needs={string.Join(',', index.Needed.ReadAll().Select(row => row.PathKey))}");
        if (transition == "absent")
        {
            Directory.Exists(Disk("parent")).ShouldBeFalse();
            await puller.ReceiveIndexAsync(Peer, 1, 2, [Record(index, "parent", 2, type: 1)], Token);
        }
        else { Directory.CreateDirectory(Disk("parent")); puller.RequestScan(); }
        for (int pass = 0; pass < 4; pass++)
        {
            if (result.NextAttemptTicks is long next && next > clock.GetUtcNow().UtcTicks) clock.Advance(TimeSpan.FromTicks(next - clock.GetUtcNow().UtcTicks));
            result = await puller.RunPassAsync(Token);
        }
        File.ReadAllBytes(Disk("parent/child")).ShouldBe(New); index.Needed.Count().ShouldBe(0); index.HasPendingDirectoryPublications().ShouldBeFalse();
    }

    private Envelope Record(FolderIndex index,string path,ulong sequence,byte[]? data=null,uint type=0,bool deleted=false)
    {
        data??=[]; long now=DateTime.UtcNow.AddMinutes(1).Ticks;
        var body=new FileRecordBody { Path=path,Type=type,Deleted=deleted,MtimeTicks=now,ChangedTicks=now,ModifiedBy=ByteString.CopyFrom(Peer),
            Size=(ulong)data.Length,ChunkClass=ChunkParameters.ClassFor(data.Length) };
        if(!deleted&&type==0) { body.FileHash=ByteString.CopyFrom(Hash(data)); if(data.Length!=0) body.Chunks.Add(new Chunk { Hash=body.FileHash,Length=(uint)data.Length }); }
        body.Vv.AddRange(VersionVector.Increment(index.LocalFiles.Find(path)?.Vector??new Dictionary<ulong,ulong>(),2).Select(v=>new VvEntry { Device=v.Key,Counter=v.Value }));
        return SignedRecordFactory.Create(body,keys,sequence);
    }
    private static async Task<PullPassResult> Next(Puller puller)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await puller.Outcomes.ReadAsync(timeout.Token);
    }
    private string Disk(string path)=>Path.Combine(Root,path.Replace('/',Path.DirectorySeparatorChar));
    private static byte[] Device(byte value) { byte[] bytes=new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(bytes,value); return bytes; }
    private static byte[] Hash(byte[] bytes)=>Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    public void Dispose()
    {
        keys.Dispose(); foreach(string file in Directory.EnumerateFiles(fixture,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal);
        Directory.Delete(fixture,recursive:true);
    }
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset current=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>current;
        public void Advance(TimeSpan delta)=>current+=delta;
    }
    private sealed class TimerClock : TimeProvider
    {
        private long ticks=DateTimeOffset.UtcNow.UtcTicks;
        private readonly Channel<ManualTimer> timers=Channel.CreateUnbounded<ManualTimer>();
        public override DateTimeOffset GetUtcNow()=>new(Interlocked.Read(ref ticks),TimeSpan.Zero);
        public void Advance(TimeSpan delta)=>Interlocked.Add(ref ticks,delta.Ticks);
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {
            var timer=new ManualTimer(callback,state,dueTime); timers.Writer.TryWrite(timer); return timer;
        }
        public async Task<ManualTimer> Next()
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await timers.Reader.ReadAsync(timeout.Token);
        }
    }
    private sealed class ManualTimer(TimerCallback callback,object? state,TimeSpan delay) : ITimer
    {
        private int disposed;
        public TimeSpan Delay { get; }=delay;
        public bool Change(TimeSpan dueTime,TimeSpan period)=>Volatile.Read(ref disposed)==0;
        public void Fire() { if(Interlocked.Exchange(ref disposed,1)==0) callback(state); }
        public void Dispose()=>Interlocked.Exchange(ref disposed,1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
