using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class IndexAdmissionTests : IDisposable
{
    private readonly string fixture=Path.Combine(Path.GetTempPath(),"PeerSync-AdmissionDb-"+Guid.NewGuid().ToString("N"));
    private static readonly byte[] Peer=new byte[32];
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    public IndexAdmissionTests()=>Directory.CreateDirectory(fixture);

    [Theory]
    [InlineData(false,1)]
    [InlineData(false,2)]
    [InlineData(true,1)]
    [InlineData(true,2)]
    public async Task RevokedAdmissionRollsBackRecordsNeededCursorOrRangeAndWriterRemainsUsable(bool announce,int denyAt)
    {
        await using var index=await FolderIndex.OpenAsync(Path.Combine(fixture,"index.db"),Token);
        await index.Devices.UpsertAsync([new(Peer,"approved peer",Approval:DeviceApproval.Approved)],Token);
        if(announce) await index.AnnounceRemoteIndexRangeAsync(Peer,1,Token);
        int checks=0; void Admission() { if(++checks==denyAt) throw new InvalidOperationException("admission revoked"); }
        if(announce) await Should.ThrowAsync<InvalidOperationException>(()=>index.AnnounceRemoteIndexRangeWithAdmissionAsync(Peer,2,Admission,Token));
        else await Should.ThrowAsync<InvalidOperationException>(()=>index.ApplyRemoteIndexWithAdmissionAsync(Peer,0,1,
            [new(Peer,"FILE",new Dictionary<ulong,ulong>{{1,1}},new SignedRecord([7],new byte[64],1))],
            snapshot=>new(snapshot.PathKey,Peer,0) { Work=new(1,0,1,new byte[32]) },Admission,Token));
        checks.ShouldBe(denyAt); index.RemoteFiles.Count().ShouldBe(0); index.Needed.Count().ShouldBe(0);
        var peer=index.Devices.Find(Peer)!; peer.LastSequenceReceived.ShouldBe(0); peer.Name.ShouldBe("approved peer"); peer.Approval.ShouldBe(DeviceApproval.Approved);
        if(announce) index.ReadRemoteIndexRange(Peer)!.TargetSequence.ShouldBe(1); else index.ReadRemoteIndexRange(Peer).ShouldBeNull();
        (await index.ApplyRemoteIndexWithAdmissionAsync(Peer,0,1,[],_=>null,()=>{},Token)).Applied.ShouldBeTrue();
        index.Devices.Find(Peer)!.LastSequenceReceived.ShouldBe(1);
    }
    public void Dispose()=>Directory.Delete(fixture,recursive:true);
}
