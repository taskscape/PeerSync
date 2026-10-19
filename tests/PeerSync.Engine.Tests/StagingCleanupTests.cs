using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class StagingCleanupTests : IDisposable
{
    private readonly string fixture=Path.Combine(Path.GetTempPath(),"PeerSync-StagingCleanup-"+Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys=FolderKeys.CreateNew();
    private static readonly byte[] Own=new byte[32],Data="owned incomplete download"u8.ToArray();
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    private string Root=>Path.Combine(fixture,"tree");
    private string Database=>Path.Combine(fixture,"index.db");
    private readonly StageClock clock=new();
    public StagingCleanupTests()=>Directory.CreateDirectory(Root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactOwnedFileOrEmptyDirectoryExpiresAtGraceBoundary(bool directory)
    {
        await using var index=await Prepare(); var row=await Park(index,directory:directory); var cleaner=Cleaner(index);
        clock.Advance(TimeSpan.FromDays(1)-TimeSpan.FromTicks(1)); await cleaner.RunAsync(row.RootId,Token); Exists(row).ShouldBeTrue();
        clock.Advance(TimeSpan.FromTicks(1)); await cleaner.RunAsync(row.RootId,Token); Exists(row).ShouldBeFalse();
        index.IsCatalogedStaging(row.RootId,row.FileId,row.TemporaryPath).ShouldBeFalse(); index.HasStagingRetirements().ShouldBeFalse();
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("nonempty-directory")]
    [InlineData("hardlink")]
    [InlineData("locked")]
    [InlineData("needed")]
    [InlineData("active-apply")]
    public async Task UnsafeOrNeededObjectIsPreserved(string reason)
    {
        await using var index=await Prepare(); var row=await Park(index,directory:reason=="nonempty-directory");
        if(reason=="changed")
        { using var changed=new FileStream(Disk(row),FileMode.Open,FileAccess.Write); changed.Write("changed bytes must survive"u8); }
        if(reason=="nonempty-directory") File.WriteAllText(Path.Combine(Disk(row),"child"),"keep");
        if(reason=="hardlink")
        {
            using var tree=new SafeFileTree(Root); using var parent=tree.Directory(""); using var file=parent.OpenFile(row.TemporaryPath,mutation:true,temporary:true);
            parent.LinkHere(file,"other-name");
        }
        if(reason=="needed") await index.Needed.UpsertAsync([new(row.TargetPath,new byte[32],1)],Token);
        if(reason=="active-apply") await index.ReserveApplyAsync(new(Guid.NewGuid(),"other",null,[1]),Token);
        using var locked=reason=="locked"?File.Open(Disk(row),FileMode.Open,FileAccess.ReadWrite,FileShare.None):null;
        clock.Advance(TimeSpan.FromDays(2)); var cleaner=Cleaner(index); await cleaner.RunAsync(row.RootId,Token); Exists(row).ShouldBeTrue();
        cleaner.RetryNeeded.ShouldBe(reason=="locked");
        index.IsCatalogedStaging(row.RootId,row.FileId,row.TemporaryPath).ShouldBeTrue(); index.HasStagingRetirements().ShouldBeFalse();
    }

    [Fact]
    public async Task MissingOrForeignReplacementNeverDeletesTheReplacement()
    {
        await using var index=await Prepare(); var absent=await Park(index); var foreign=await Park(index,"other");
        File.Delete(Disk(absent)); File.Move(Disk(foreign),Path.Combine(Root,"moved-owned")); File.WriteAllText(Disk(foreign),"foreign");
        clock.Advance(TimeSpan.FromDays(2)); await Cleaner(index).RunAsync(absent.RootId,Token);
        File.ReadAllText(Disk(foreign)).ShouldBe("foreign"); File.ReadAllBytes(Path.Combine(Root,"moved-owned")).ShouldBe(Data);
        index.IsCatalogedStaging(absent.RootId,absent.FileId,absent.TemporaryPath).ShouldBeFalse();
        index.IsCatalogedStaging(foreign.RootId,foreign.FileId,foreign.TemporaryPath).ShouldBeTrue();
    }

    [Theory]
    [InlineData("staging-cleanup-prepared",true)]
    [InlineData("staging-cleanup-deleted",false)]
    public async Task CrashBeforeOrAfterNativeRemovalIsRecoveredWithoutReplayingPermission(string stage,bool survives)
    {
        StagedObject row;
        await using(var index=await Prepare())
        {
            row=await Park(index); clock.Advance(TimeSpan.FromDays(2)); var cleaner=Cleaner(index);
            cleaner.Checkpoint=point=>{ if(point==stage) throw new Crash(); };
            await Should.ThrowAsync<Crash>(()=>cleaner.RunAsync(row.RootId,Token)); Exists(row).ShouldBe(survives); index.HasStagingRetirements().ShouldBeTrue();
        }
        await using var reopened=await FolderIndex.OpenAsync(Database,Token); var recovery=Cleaner(reopened);
        (await recovery.RecoverAsync(row.RootId,Token)).ShouldBe(1); Exists(row).ShouldBe(survives); reopened.HasStagingRetirements().ShouldBeFalse();
        // Recovery only revokes. A separately fresh needed state still blocks expiry.
        await reopened.Needed.UpsertAsync([new(row.TargetPath,new byte[32],1)],Token); await recovery.RunAsync(row.RootId,Token); Exists(row).ShouldBe(survives);
        await reopened.Needed.DeleteAsync([row.TargetPath],Token); await recovery.RunAsync(row.RootId,Token); Exists(row).ShouldBeFalse();
        reopened.IsCatalogedStaging(row.RootId,row.FileId,row.TemporaryPath).ShouldBeFalse();
    }

    [Fact]
    public async Task NewNeededReceiptBetweenHashAndPermissionPreventsDeletion()
    {
        await using var index=await Prepare(); var row=await Park(index); clock.Advance(TimeSpan.FromDays(2));
        var cleaner=Cleaner(index); cleaner.Checkpoint=point=>
        {
            if(point=="staging-cleanup-verified")
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var references=FolderReferenceGate.AcquireAsync(index,timeout.Token).AsTask().GetAwaiter().GetResult();
                index.Needed.UpsertAsync([new(row.TargetPath,new byte[32],1)],timeout.Token).GetAwaiter().GetResult();
            }
        };
        await cleaner.RunAsync(row.RootId,Token); Exists(row).ShouldBeTrue(); index.HasStagingRetirements().ShouldBeFalse();
    }

    [Fact]
    public async Task CancellationAfterReservationKeepsRecoverableWitnessAndReleasesNativeGuard()
    {
        await using var index=await Prepare(); var row=await Park(index); clock.Advance(TimeSpan.FromDays(2));
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(Token); var cleaner=Cleaner(index);
        cleaner.Checkpoint=point=>{ if(point=="staging-cleanup-prepared") cancel.Cancel(); };
        await Should.ThrowAsync<OperationCanceledException>(()=>cleaner.RunAsync(row.RootId,cancel.Token));
        using(var file=File.Open(Disk(row),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) file.Length.ShouldBe(Data.Length);
        index.HasStagingRetirements().ShouldBeTrue(); await cleaner.RecoverAsync(row.RootId,Token); Exists(row).ShouldBeTrue();
    }

    [Fact]
    public async Task PullerRecoversThenRebuildsNeedsBeforeSweepingAndHonorsHourlyMaintenanceDeadline()
    {
        await using var index=await Prepare(); var row=await Park(index); clock.Advance(TimeSpan.FromDays(2));
        (await index.ReserveStagingRetirementAsync(row,clock.GetUtcNow().UtcTicks,Token)).ShouldBeTrue();
        using var puller=new Puller(Root,index,keys,Own,(_,_,_,_)=>throw new InvalidOperationException("No network"),timeProvider:clock);
        var pass=await puller.RunPassAsync(Token); pass.Recovered.ShouldBe(1); pass.Scans.ShouldBe(1); Exists(row).ShouldBeFalse();
        var next=await Park(index,"next"); clock.Advance(TimeSpan.FromDays(2));
        await puller.RunPassAsync(Token); Exists(next).ShouldBeFalse();
    }

    [Fact]
    public async Task FullPageOfNeededRowsDoesNotStarveLaterExpiredObject()
    {
        await using var index=await Prepare(); byte[] root=index.Meta.Find("_root-identity")!.Value!;
        for(int i=1;i<=1000;i++) await Park(index,"blocked",id:Guid.ParseExact(i.ToString("x32",System.Globalization.CultureInfo.InvariantCulture),"N"));
        var last=await Park(index,"last",id:Guid.ParseExact(new string('f',32),"N"));
        await index.Needed.UpsertAsync([new("blocked",new byte[32],1)],Token); clock.Advance(TimeSpan.FromDays(2));
        var cleaner=Cleaner(index); (await cleaner.RunAsync(root,Token)).ShouldBeTrue(); Exists(last).ShouldBeTrue();
        (await cleaner.RunAsync(root,Token)).ShouldBeFalse(); Exists(last).ShouldBeFalse();
        index.ReadStagingCleanupPage(root).Objects.Count.ShouldBe(1000);
        index.ReadReusableStaging(root,"blocked",false,1000).Count.ShouldBe(1000);
    }

    [Fact]
    public async Task UnknownAndUnagedLegacyTempsRemainEvenWithAncientFilesystemDates()
    {
        await using var index=await Prepare(); var known=await Park(index); string unknown=Guid.NewGuid().ToString("N")+".!psp";
        File.WriteAllBytes(Path.Combine(Root,unknown),Data); File.SetLastWriteTimeUtc(Path.Combine(Root,unknown),DateTime.UtcNow.AddYears(-1));
        await index.Meta.UpsertAsync([new("_apply-staging-unaged",new byte[]{1,2,3})],Token); clock.Advance(TimeSpan.FromDays(2));
        await Cleaner(index).RunAsync(known.RootId,Token); Exists(known).ShouldBeFalse();
        File.ReadAllBytes(Path.Combine(Root,unknown)).ShouldBe(Data); index.Meta.Find("_apply-staging-unaged")!.Value.ShouldBe(new byte[]{1,2,3});
    }

    [Fact]
    public async Task ReparseParentCannotRedirectCleanupIntoAnotherDirectory()
    {
        await using var index=await Prepare(); Directory.CreateDirectory(Path.Combine(Root,"nested"));
        var row=await Park(index,"nested/file"); string outside=Path.Combine(fixture,"outside");
        Directory.Move(Path.Combine(Root,"nested"),outside); Directory.CreateSymbolicLink(Path.Combine(Root,"nested"),outside);
        try
        {
            clock.Advance(TimeSpan.FromDays(2)); await Cleaner(index).RunAsync(row.RootId,Token);
            File.ReadAllBytes(Path.Combine(outside,ApplyState.Name(row.TemporaryPath))).ShouldBe(Data);
            index.IsCatalogedStaging(row.RootId,row.FileId,row.TemporaryPath).ShouldBeTrue();
        }
        finally { Directory.Delete(Path.Combine(Root,"nested")); }
    }

    private StagingCleaner Cleaner(FolderIndex index)=>new(Root,index,clock);
    private async Task<FolderIndex> Prepare()
    { var index=await FolderIndex.OpenAsync(Database,Token); using var scanner=new Scanner(Root,index,keys,Own); await scanner.ScanAsync(Token); return index; }
    private async Task<StagedObject> Park(FolderIndex index,string target="file",bool directory=false,Guid? id=null)
    {
        var intent=new ApplyIntent(Guid.NewGuid(),target,null,[1]); await index.ReserveApplyAsync(intent,Token);
        using var tree=new SafeFileTree(Root); using var parent=tree.Directory(ApplyState.Parent(target)); string name=Guid.NewGuid().ToString("N")+".!psp";
        using var file=directory?parent.OwnedTemporaryDirectory(name,true):parent.CreateFile(name,true);
        if(!directory) RandomAccess.Write(file,Data,0);
        var snapshot=FileSnapshot.Read(file); var row=new StagedObject(id??Guid.NewGuid(),tree.Identity.ToArray(),BlockCaptureProof.Identity(file),target,ApplyState.Join(ApplyState.Parent(target),name),directory,
            clock.GetUtcNow().UtcTicks,snapshot.Length,directory?null:GuardedFile.Hash(file,Token),snapshot.Changed);
        var saved=await index.ParkStagingAsync(intent,row,Token); await index.AbandonApplyAsync(intent,Token); return saved;
    }
    private string Disk(StagedObject row)=>Path.Combine(Root,row.TemporaryPath);
    private bool Exists(StagedObject row)=>row.IsDirectory?Directory.Exists(Disk(row)):File.Exists(Disk(row));
    private sealed class StageClock : TimeProvider
    { private DateTimeOffset now=DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>now; public void Advance(TimeSpan value)=>now+=value; }
    private sealed class Crash : Exception;
    public void Dispose()
    {
        keys.Dispose(); foreach(string file in Directory.EnumerateFiles(fixture,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal);
        Directory.Delete(fixture,true);
    }
}
