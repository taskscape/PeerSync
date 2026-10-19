using System.Text;
using PeerSync.Core.Paths;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class StagingRetirementTests : IDisposable
{
    private readonly string fixture=Path.Combine(Path.GetTempPath(),"PeerSync-StagingRetireDb-"+Guid.NewGuid().ToString("N"));
    private static readonly byte[] Root=Enumerable.Repeat((byte)1,24).ToArray(), Id=Enumerable.Repeat((byte)2,16).ToArray();
    private const long Now=638965584000000000;
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    public StagingRetirementTests()=>Directory.CreateDirectory(fixture);
    private string Database=>Path.Combine(fixture,"index.db");

    [Fact]
    public async Task GraceNeededAndAnyActiveApplyAllPreventPermission()
    {
        await using var index=await Open(); var row=await Park(index);
        (await index.ReserveStagingRetirementAsync(row,Now+FolderIndex.StagingGraceTicks-1,Token)).ShouldBeFalse();
        (await index.ReserveStagingRetirementAsync(row,Now-1,Token)).ShouldBeFalse();
        await index.Needed.UpsertAsync([new("file",new byte[32],1)],Token);
        (await index.ReserveStagingRetirementAsync(row,Now+FolderIndex.StagingGraceTicks,Token)).ShouldBeFalse();
        await index.Needed.DeleteAsync(["file"],Token);
        var intent=new ApplyIntent(Guid.NewGuid(),"other",null,[1]); await index.ReserveApplyAsync(intent,Token);
        (await index.ReserveStagingRetirementAsync(row,Now+FolderIndex.StagingGraceTicks,Token)).ShouldBeFalse();
        await index.AbandonApplyAsync(intent,Token);
        (await index.ReserveStagingRetirementAsync(row,Now+FolderIndex.StagingGraceTicks,Token)).ShouldBeTrue();
        index.HasStagingRetirements().ShouldBeTrue(); index.ReadReusableStaging(Root,"file",false).ShouldBeEmpty();
    }

    [Fact]
    public async Task ColdRecoveryRevokesPermissionAndKeepsWitnessEvenWhenNeedsChanged()
    {
        StagedObject row;
        await using(var index=await Open())
        {
            row=await Park(index); (await index.ReserveStagingRetirementAsync(row,Now+FolderIndex.StagingGraceTicks,Token)).ShouldBeTrue();
            await index.Needed.UpsertAsync([new("file",new byte[32],1)],Token);
        }
        await using var reopened=await FolderIndex.OpenAsync(Database,Token);
        (await reopened.RecoverStagingRetirementsAsync(Root,Token)).ShouldBe(1); reopened.HasStagingRetirements().ShouldBeFalse();
        reopened.ReadReusableStaging(Root,"file",false).Single().Id.ShouldBe(row.Id);
        (await reopened.ReserveStagingRetirementAsync(row,Now+FolderIndex.StagingGraceTicks,Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task StaleProofCannotCancelOrRemoveProtectedOrChangedWitness()
    {
        await using var index=await Open(); var row=await Park(index);
        (await index.CancelStagingRetirementAsync(row with { Length=9 },true,Token)).ShouldBeFalse();
        (await index.CancelStagingRetirementAsync(row,true,Token)).ShouldBeTrue();
        (await index.CancelStagingRetirementAsync(row,cancellationToken:Token)).ShouldBeFalse();
        (await index.CompleteMissingStagingAsync(row,Now+FolderIndex.StagingGraceTicks,Token)).ShouldBeFalse();
        index.IsCatalogedStaging(Root,Id,row.TemporaryPath).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCompletionRemovesOnlyMatchingLegacyAlias(bool replaceAlias)
    {
        await using var index=await Open(); var row=await Park(index);
        string key="_apply-staging-"+Convert.ToHexString(Blake3.Hasher.Hash(Encoding.UTF8.GetBytes(PathRules.Key(row.TargetPath))).AsSpan());
        byte[] alias=[..row.RootId,..row.FileId,..Encoding.UTF8.GetBytes(row.TemporaryPath)];
        await index.Meta.UpsertAsync([new(key,replaceAlias?[7]:alias)],Token);
        (await index.ReserveStagingRetirementAsync(row,Now+FolderIndex.StagingGraceTicks,Token)).ShouldBeTrue();
        (await index.CompleteMissingStagingAsync(row,Now+FolderIndex.StagingGraceTicks,Token)).ShouldBeTrue();
        index.HasStagingRetirements().ShouldBeFalse(); index.IsCatalogedStaging(Root,Id,row.TemporaryPath).ShouldBeFalse();
        if(replaceAlias) index.Meta.Find(key)!.Value.ShouldBe(new byte[]{7}); else index.Meta.Find(key).ShouldBeNull();
    }

    [Fact]
    public async Task CursorSurvivesReopenAndCountsFreshAndNeededRows()
    {
        Guid first,second;
        await using(var index=await Open())
        {
            var a=await Park(index); var b=await Park(index,"other");
            await index.Needed.UpsertAsync([new("file",new byte[32],1),new("other",new byte[32],1)],Token);
            var page=index.ReadStagingCleanupPage(Root,1); page.MoreWork.ShouldBeTrue(); first=page.Objects.Single().Id;
            second=first==a.Id?b.Id:a.Id; await index.AdvanceStagingCleanupCursorAsync(Root,null,first,Token);
        }
        await using var reopened=await FolderIndex.OpenAsync(Database,Token);
        var next=reopened.ReadStagingCleanupPage(Root,1); next.Cursor.ShouldBe(first); next.Objects.Single().Id.ShouldBe(second);
        await Should.ThrowAsync<InvalidOperationException>(()=>reopened.AdvanceStagingCleanupCursorAsync(Root,null,second,Token));
        await reopened.AdvanceStagingCleanupCursorAsync(Root,first,second,Token);
        var empty=reopened.ReadStagingCleanupPage(Root,1); empty.Objects.ShouldBeEmpty(); empty.MoreWork.ShouldBeFalse();
        await reopened.AdvanceStagingCleanupCursorAsync(Root,second,null,Token);
        reopened.ReadStagingCleanupPage(Root).Objects.Count.ShouldBe(2);
        Should.Throw<ArgumentOutOfRangeException>(()=>reopened.ReadStagingCleanupPage(Root,1001));
    }

    private async Task<FolderIndex> Open()
    { var index=await FolderIndex.OpenAsync(Database,Token); await index.Meta.UpsertAsync([new("_root-identity",Root)],Token); return index; }
    private static async Task<StagedObject> Park(FolderIndex index,string path="file")
    {
        var intent=new ApplyIntent(Guid.NewGuid(),path,null,[1]); await index.ReserveApplyAsync(intent,Token);
        var row=new StagedObject(Guid.NewGuid(),Root,Id,path,Guid.NewGuid().ToString("N")+".!psp",false,Now,5,new byte[32],123);
        var saved=await index.ParkStagingAsync(intent,row,Token); await index.AbandonApplyAsync(intent,Token); return saved;
    }
    public void Dispose()=>Directory.Delete(fixture,true);
}
