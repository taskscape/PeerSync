using System.Text;
using Google.Protobuf;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Crypto.Chunking;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class StagingCatalogTests : IDisposable
{
    private readonly string fixture=Path.Combine(Path.GetTempPath(),"PeerSync-StagingNative-"+Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys=FolderKeys.CreateNew();
    private static readonly byte[] Own=Device(1),Peer=Device(2),New="verified incoming bytes"u8.ToArray(),Old="preserve this other name"u8.ToArray();
    private string Root=>Path.Combine(fixture,"tree");
    private string Database=>Path.Combine(fixture,"index.db");
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    public StagingCatalogTests()=>Directory.CreateDirectory(Root);

    [Fact]
    public async Task ParkedProofSurvivesReopenAndUnchangedReparkKeepsAgeThenReusesWithoutNetwork()
    {
        var clock=new StageClock(); byte[] root; StagedObject proof;
        await using(var index=await Prepare())
        {
            var pending=await Pending(index); WriteTemporary(pending.State.Temporary,New);
            await new FileApplier(Root,index,keys,Own,clock:clock).RecoverAsync(Token);
            root=index.Meta.Find("_root-identity")!.Value!; proof=index.ReadReusableStaging(root,"file",false).Single();
            proof.ContentHash.ShouldBe(Hash(New)); proof.Length.ShouldBe(New.Length);
        }
        clock.Advance(TimeSpan.FromHours(2));
        await using var reopened=await FolderIndex.OpenAsync(Database,Token);
        var interrupted=new FileApplier(Root,reopened,keys,Own,clock:clock) { Checkpoint=stage=>{ if(stage=="staging") throw new IOException("injected"); } };
        await Should.ThrowAsync<IOException>(()=>interrupted.ApplyAsync("file",(_,_,_)=>throw new InvalidOperationException("No network"),Token));
        await new FileApplier(Root,reopened,keys,Own,clock:clock).RecoverAsync(Token);
        var current=reopened.ReadReusableStaging(root,"file",false).Single(); current.Id.ShouldBe(proof.Id); current.ParkedTicks.ShouldBe(proof.ParkedTicks);
        (await new FileApplier(Root,reopened,keys,Own).ApplyAsync("file",(_,_,_)=>throw new InvalidOperationException("No network"),Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Path.Combine(Root,"file")).ShouldBe(New);
    }

    [Fact]
    public async Task ChangedCatalogedBytesArePreservedAndNeverUsedAsAnUnagedLegacyBypass()
    {
        await using var index=await Prepare(); var pending=await Pending(index); WriteTemporary(pending.State.Temporary,New);
        await new FileApplier(Root,index,keys,Own).RecoverAsync(Token); var proof=index.ReadReusableStaging(pending.State.Root,"file",false).Single();
        await LegacyPointer(index,pending.State); WriteTemporary(proof.TemporaryPath,Old);
        (await new FileApplier(Root,index,keys,Own).ApplyAsync("file",Fetch,Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Disk(proof.TemporaryPath)).ShouldBe(Old); File.ReadAllBytes(Disk("file")).ShouldBe(New);
        using var tree=new SafeFileTree(Root); using var parent=tree.Directory(""); using var installed=parent.OpenReadGuard("file");
        BlockCaptureProof.Identity(installed).ShouldNotBe(proof.FileId);
    }

    [Fact]
    public async Task AlteredPreparedStagingIsProtectedDuringRecoveryAndVerifiedBlocksStillSupplyFreshTarget()
    {
        await using var index=await Prepare(); await Receive(index);
        var interrupted=new FileApplier(Root,index,keys,Own) { Checkpoint=stage=>{ if(stage=="prepared") throw new IOException("injected"); } };
        await Should.ThrowAsync<IOException>(()=>interrupted.ApplyAsync("file",Fetch,Token));
        var state=ApplyState.Decode(index.ReadApplyIntents().Single(),keys); WriteTemporary(state.Temporary,Old);
        await new FileApplier(Root,index,keys,Own).RecoverAsync(Token);
        index.ReadReusableStaging(state.Root,"file",false).ShouldBeEmpty(); index.IsCatalogedStaging(state.Root,state.TemporaryId,state.Temporary).ShouldBeTrue();
        (await new FileApplier(Root,index,keys,Own).ApplyAsync("file",(_,_,_)=>throw new InvalidOperationException("Use verified blocks"),Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Disk(state.Temporary)).ShouldBe(Old); File.ReadAllBytes(Disk("file")).ShouldBe(New);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardlinkedLegacyAndCatalogedTempsNeverOverwriteTheirOtherName(bool cataloged)
    {
        await using var index=await Prepare(); var pending=await Pending(index); WriteTemporary(pending.State.Temporary,Old);
        if(cataloged) await new FileApplier(Root,index,keys,Own).RecoverAsync(Token);
        else { await LegacyPointer(index,pending.State); await index.AbandonApplyAsync(pending.Intent,Token); }
        using(var tree=new SafeFileTree(Root)) using(var parent=tree.Directory("")) using(var temporary=parent.OpenFile(ApplyState.Name(pending.State.Temporary),mutation:true,temporary:true))
            parent.LinkHere(temporary,"other-name");
        (await new FileApplier(Root,index,keys,Own).ApplyAsync("file",Fetch,Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Disk("other-name")).ShouldBe(Old); File.ReadAllBytes(Disk(pending.State.Temporary)).ShouldBe(Old); File.ReadAllBytes(Disk("file")).ShouldBe(New);
    }

    [Fact]
    public async Task UnagedLegacyPointerStillReusesVerifiedOwnedBytes()
    {
        await using var index=await Prepare(); var pending=await Pending(index); WriteTemporary(pending.State.Temporary,New);
        await LegacyPointer(index,pending.State); await index.AbandonApplyAsync(pending.Intent,Token);
        (await new FileApplier(Root,index,keys,Own).ApplyAsync("file",(_,_,_)=>throw new InvalidOperationException("No network"),Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Disk("file")).ShouldBe(New); index.ReadReusableStaging(pending.State.Root,"file",false).ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationDuringCatalogResumeClosesNativeGuardAndDoesNotReserveNewIntent()
    {
        await using var index=await Prepare(); var pending=await Pending(index); WriteTemporary(pending.State.Temporary,New);
        await new FileApplier(Root,index,keys,Own).RecoverAsync(Token);
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(Token);
        var applier=new FileApplier(Root,index,keys,Own) { Checkpoint=stage=>{ if(stage=="staging-reuse-verifying") cancel.Cancel(); } };
        await Should.ThrowAsync<OperationCanceledException>(()=>applier.ApplyAsync("file",Fetch,cancel.Token));
        index.ReadApplyIntents().ShouldBeEmpty(); using var reopened=File.Open(Disk(pending.State.Temporary),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        reopened.Length.ShouldBe(New.Length);
    }

    [Fact]
    public async Task MalformedUnagedPointerDoesNotBlockFreshTransferOrClaimUnknownTemp()
    {
        await using var index=await Prepare(); var pending=await Pending(index);
        string key="_apply-staging-"+Convert.ToHexString(Hash(Encoding.UTF8.GetBytes(PathRules.Key("file"))));
        await index.Meta.UpsertAsync([new(key,[..pending.State.Root,..pending.State.TemporaryId,0xC3,0x28])],Token);
        await index.AbandonApplyAsync(pending.Intent,Token);
        (await new FileApplier(Root,index,keys,Own).ApplyAsync("file",Fetch,Token)).ShouldBe(FileApplyOutcome.Applied);
        File.ReadAllBytes(Disk("file")).ShouldBe(New); File.Exists(Disk(pending.State.Temporary)).ShouldBeTrue();
        index.ReadReusableStaging(pending.State.Root,"file",false).ShouldBeEmpty();
    }

    [Fact]
    public async Task CanceledCatalogCommitLeavesOwningIntentRecoverableAndReleasesNativeGuard()
    {
        await using var index=await Prepare(); var pending=await Pending(index); var clock=new StageClock();
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(Token); clock.OnRead=()=>cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(()=>new FileApplier(Root,index,keys,Own,clock:clock).RecoverAsync(cancel.Token));
        index.ReadApplyIntents().Count().ShouldBe(1); index.ReadReusableStaging(pending.State.Root,"file",false).ShouldBeEmpty();
        using(var reopened=File.Open(Disk(pending.State.Temporary),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) reopened.Length.ShouldBe(0);
        clock.OnRead=null; await new FileApplier(Root,index,keys,Own,clock:clock).RecoverAsync(Token); index.ReadApplyIntents().ShouldBeEmpty();
    }

    [Fact]
    public async Task DirectoryTransitionParksTypedEmptyContainerAndReusesItAfterConcurrentTargetScan()
    {
        File.WriteAllBytes(Disk("file"),Old); await using var index=await Prepare();
        long now=DateTime.UtcNow.AddMinutes(1).Ticks; var body=new FileRecordBody { Path="file",Type=1,MtimeTicks=now,ChangedTicks=now,ModifiedBy=ByteString.CopyFrom(Peer) };
        foreach(var pair in index.LocalFiles.Find("file")!.Vector) body.Vv.Add(new VvEntry { Device=pair.Key,Counter=pair.Value });
        body.Vv.Add(new VvEntry { Device=2,Counter=1 });
        await new NeededSet(index,keys,Own).ApplyIndexUpdateAsync(Peer,0,1,[SignedRecordFactory.Create(body,keys,1)],Token);
        var applier=new FileApplier(Root,index,keys,Own) { Checkpoint=stage=>{ if(stage=="staging") File.WriteAllText(Disk("file"),"concurrent target"); } };
        (await applier.ApplyAsync("file",Fetch,Token)).ShouldBe(FileApplyOutcome.Deferred);
        byte[] root=index.Meta.Find("_root-identity")!.Value!; var proof=index.ReadReusableStaging(root,"file",true).Single(); proof.IsDirectory.ShouldBeTrue(); proof.ContentHash.ShouldBeNull(); proof.Length.ShouldBe(0);
        using(var scanner=new Scanner(Root,index,keys,Own)) await scanner.ScanAsync(Token);
        (await new FileApplier(Root,index,keys,Own).ApplyAsync("file",(_,_,_)=>throw new InvalidOperationException("Directory needs no bytes"),Token)).ShouldBe(FileApplyOutcome.Applied);
        Directory.Exists(Disk("file")).ShouldBeTrue(); Directory.Exists(Disk(proof.TemporaryPath)).ShouldBeFalse();
        index.Archive.ReadAll().Any(row=>File.ReadAllText(Disk(row.ArchivePath))=="concurrent target").ShouldBeTrue();
    }

    private async Task<FolderIndex> Prepare()
    {
        var index=await FolderIndex.OpenAsync(Database,Token); using var scanner=new Scanner(Root,index,keys,Own); await scanner.ScanAsync(Token); return index;
    }
    private async Task Receive(FolderIndex index)
    {
        long now=DateTime.UtcNow.AddMinutes(1).Ticks; var body=new FileRecordBody { Path="file",Size=(ulong)New.Length,MtimeTicks=now,ChangedTicks=now,
            ModifiedBy=ByteString.CopyFrom(Peer),FileHash=ByteString.CopyFrom(Hash(New)),ChunkClass=ChunkParameters.ClassFor(New.Length) };
        body.Vv.Add(new VvEntry { Device=2,Counter=1 }); body.Chunks.Add(new Chunk { Hash=body.FileHash,Length=(uint)New.Length });
        await new NeededSet(index,keys,Own).ApplyIndexUpdateAsync(Peer,0,1,[SignedRecordFactory.Create(body,keys,1)],Token);
    }
    private async Task<(ApplyIntent Intent,ApplyState State)> Pending(FolderIndex index)
    {
        await Receive(index); using var cancel=CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Should.ThrowAsync<OperationCanceledException>(()=>new FileApplier(Root,index,keys,Own).ApplyAsync("file",(_,_,_)=>
        { cancel.Cancel(); return ValueTask.FromResult<ReadOnlyMemory<byte>>(New); },cancel.Token));
        var intent=index.ReadApplyIntents().Single(); return (intent,ApplyState.Decode(intent,keys));
    }
    private static ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> hash,int length,CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<ReadOnlyMemory<byte>>(New); }
    private static Task<int> LegacyPointer(FolderIndex index,ApplyState state)
    {
        string key="_apply-staging-"+Convert.ToHexString(Hash(Encoding.UTF8.GetBytes(PathRules.Key(state.Path))));
        return index.Meta.UpsertAsync([new(key,[..state.Root,..state.TemporaryId,..Encoding.UTF8.GetBytes(state.Temporary)])],Token);
    }
    private void WriteTemporary(string path,byte[] data) { using var stream=new FileStream(Disk(path),FileMode.Open,FileAccess.Write); stream.Write(data); stream.SetLength(data.Length); }
    private string Disk(string path)=>Path.Combine(Root,path.Replace('/',Path.DirectorySeparatorChar));
    private static byte[] Hash(byte[] bytes)=>Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    private static byte[] Device(byte value) { byte[] bytes=new byte[32]; bytes[0]=value; return bytes; }
    private sealed class StageClock : TimeProvider
    {
        private long utc=DateTime.UtcNow.Ticks; public Action? OnRead { get; set; }
        public override DateTimeOffset GetUtcNow() { OnRead?.Invoke(); return new(utc,TimeSpan.Zero); }
        public void Advance(TimeSpan duration)=>utc+=duration.Ticks;
    }
    public void Dispose() { keys.Dispose(); foreach(string path in Directory.EnumerateFiles(fixture,"*",SearchOption.AllDirectories)) File.SetAttributes(path,FileAttributes.Normal); Directory.Delete(fixture,true); }
}
