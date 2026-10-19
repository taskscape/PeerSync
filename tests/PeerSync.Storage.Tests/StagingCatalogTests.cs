using Microsoft.Data.Sqlite;
using PeerSync.Storage;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class StagingCatalogTests : IDisposable
{
    private readonly string fixture=Path.Combine(Path.GetTempPath(),"PeerSync-StagingDb-"+Guid.NewGuid().ToString("N"));
    private static readonly byte[] Root=Enumerable.Repeat((byte)1,24).ToArray(), FileId=Enumerable.Repeat((byte)2,16).ToArray(), Hash=Enumerable.Repeat((byte)3,32).ToArray();
    private const long Now=638965584000000000;
    private string Database=>Path.Combine(fixture,"index.db");
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    public StagingCatalogTests()=>Directory.CreateDirectory(fixture);

    [Fact]
    public async Task CatalogIsDurableAfterIntentAbandonAndReturnedBuffersAreIndependent()
    {
        var row=Row();
        await using(var index=await Open())
        {
            var intent=Intent(); await index.ReserveApplyAsync(intent,Token); var saved=await index.ParkStagingAsync(intent,row,Token);
            saved.ContentHash![0]=99; row.FileId[0]=99; await index.AbandonApplyAsync(intent,Token);
        }
        await using var reopened=await FolderIndex.OpenAsync(Database,Token); var actual=reopened.ReadReusableStaging(Root,"folder/file",false).Single();
        actual.FileId.ShouldBe(FileId); actual.ContentHash.ShouldBe(Hash); actual.ParkedTicks.ShouldBe(Now);
        actual.RootId[0]=99; reopened.ReadReusableStaging(Root,"FOLDER/FILE",false).Single().RootId.ShouldBe(Root);
        actual.ToString().ShouldNotContain("folder");
    }

    [Fact]
    public async Task ExactActiveIntentAndBoundRootAreMandatory()
    {
        await using var index=await Open(); var intent=Intent();
        await Should.ThrowAsync<InvalidOperationException>(()=>index.ParkStagingAsync(intent,Row(),Token));
        await index.ReserveApplyAsync(intent,Token);
        await Should.ThrowAsync<InvalidOperationException>(()=>index.ParkStagingAsync(new(intent.Id,"folder/file",null,[2]),Row(),Token));
        await Should.ThrowAsync<InvalidOperationException>(()=>index.ParkStagingAsync(intent,Row() with { RootId=new byte[24] },Token));
        await Should.ThrowAsync<ArgumentException>(()=>index.ParkStagingAsync(intent,Row() with { TargetPath="folder/other" },Token));
        index.ReadReusableStaging(Root,"folder/file",false).ShouldBeEmpty();
        await index.ParkStagingAsync(intent,Row(),Token); await index.AbandonApplyAsync(intent,Token);
        await Should.ThrowAsync<InvalidOperationException>(()=>index.ParkStagingAsync(intent,Row(),Token));
    }

    [Fact]
    public async Task UnchangedReparkAndCaseAliasesPreserveAgeIncludingClockRollback()
    {
        await using var index=await Open(); var intent=Intent(); await index.ReserveApplyAsync(intent,Token); var row=Row();
        var first=await index.ParkStagingAsync(intent,row,Token);
        var later=await index.ParkStagingAsync(intent,row with { Id=Guid.NewGuid(),ParkedTicks=Now+100,TargetPath="FOLDER/FILE",TemporaryPath=row.TemporaryPath.ToUpperInvariant().Replace(".!PSP",".!psp",StringComparison.Ordinal) },Token);
        later.Id.ShouldBe(first.Id); later.ParkedTicks.ShouldBe(Now); later.TemporaryPath.ShouldBe(first.TemporaryPath);
        var earlier=await index.ParkStagingAsync(intent,row with { ParkedTicks=Now-100 },Token); earlier.ParkedTicks.ShouldBe(Now);
        index.ReadReusableStaging(Root,"folder/file",false).Count.ShouldBe(1);
        index.IsCatalogedStaging(Root,FileId,row.TemporaryPath.ToUpperInvariant().Replace(".!PSP",".!psp",StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task NewSettledContentOrChangeStampResetsGraceAndProtectionExcludesResume()
    {
        await using var index=await Open(); var intent=Intent(); await index.ReserveApplyAsync(intent,Token); var row=Row();
        await index.ParkStagingAsync(intent,row,Token);
        var changed=await index.ParkStagingAsync(intent,row with { ParkedTicks=Now+10,ContentHash=new byte[32] },Token); changed.ParkedTicks.ShouldBe(Now+10);
        var touched=await index.ParkStagingAsync(intent,changed with { ParkedTicks=Now+20,ChangeFileTime=124 },Token); touched.ParkedTicks.ShouldBe(Now+20);
        await index.ParkStagingAsync(intent,touched with { State=StagingObjectState.Protected },Token);
        index.ReadReusableStaging(Root,"folder/file",false).ShouldBeEmpty(); index.IsCatalogedStaging(Root,FileId,row.TemporaryPath).ShouldBeTrue();
    }

    [Fact]
    public async Task IdentityCannotChangeTargetOrKindAndReadLimitIsBounded()
    {
        await using var index=await Open(); var intent=Intent(); await index.ReserveApplyAsync(intent,Token); var row=Row(); await index.ParkStagingAsync(intent,row,Token);
        var other=new ApplyIntent(Guid.NewGuid(),"folder/other",null,[1]); await index.ReserveApplyAsync(other,Token);
        await Should.ThrowAsync<InvalidOperationException>(()=>index.ParkStagingAsync(other,row with { TargetPath="folder/other" },Token));
        await Should.ThrowAsync<InvalidOperationException>(()=>index.ParkStagingAsync(intent,row with { IsDirectory=true,Length=0,ContentHash=null },Token));
        Should.Throw<ArgumentOutOfRangeException>(()=>index.ReadReusableStaging(Root,"folder/file",false,1001));
        Should.Throw<InvalidOperationException>(()=>index.ReadReusableStaging(new byte[24],"folder/file",false));
    }

    [Fact]
    public async Task QueuedRegistrationFreezesInputBuffersAndCancellationDoesNotCreateWitness()
    {
        await using var index=await Open(); var intent=Intent(); await index.ReserveApplyAsync(intent,Token);
        using var release=new ManualResetEventSlim(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker=index.RecomputeNeededAsync(["blocker"],_=>{ entered.SetResult(); if(!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); return null; },Token);
        var row=Row(); Task<StagedObject>? pending=null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10),Token); pending=index.ParkStagingAsync(intent,row,Token); row.RootId[0]=99; row.FileId[0]=99; row.ContentHash![0]=99; }
        finally { release.Set(); }
        await blocker; await pending!; var saved=index.ReadReusableStaging(Root,"folder/file",false).Single(); saved.FileId.ShouldBe(FileId); saved.ContentHash.ShouldBe(Hash);
        using var canceled=new CancellationTokenSource(); canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(()=>index.ParkStagingAsync(intent,Row() with { FileId=new byte[16] },canceled.Token));
        index.ReadReusableStaging(Root,"folder/file",false).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("../file",false)]
    [InlineData("other/00000000000000000000000000000000.!psp",false)]
    [InlineData("folder/not-owned.!psp",false)]
    [InlineData("folder/00000000000000000000000000000000.!psp",true)]
    public async Task InvalidSiblingOrDirectoryFingerprintIsRejected(string temporary,bool badDirectory)
    {
        await using var index=await Open(); var intent=Intent(); await index.ReserveApplyAsync(intent,Token);
        await Should.ThrowAsync<ArgumentException>(()=>index.ParkStagingAsync(intent,Row() with { TemporaryPath=temporary,IsDirectory=badDirectory },Token));
        index.ReadReusableStaging(Root,"folder/file",false).ShouldBeEmpty();
    }

    [Fact]
    public async Task MigrationPreservesUnagedLegacyPointersAndBacksUpVersion19()
    {
        var assembly=typeof(FolderIndex).Assembly;
        var migrations=Enumerable.Range(1,19).Select(version=>
        {
            using var stream=assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name=>name.StartsWith($"PeerSync.Storage.FolderDb.Migrations.{version:D4}_",StringComparison.Ordinal)))!;
            using var reader=new StreamReader(stream); return new DbMigration(version,reader.ReadToEnd());
        }).ToArray();
        byte[] legacy=[1,2,3];
        await using(var old=await Db.OpenAsync(Database,migrations,Token)) await old.WriteAsync((connection,transaction)=>
        {
            using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText="INSERT INTO meta VALUES('_root-identity',$root),('_apply-staging-legacy',$legacy);";
            command.Parameters.AddWithValue("$root",Root); command.Parameters.AddWithValue("$legacy",legacy); return command.ExecuteNonQuery();
        },Token);
        await using var upgraded=await FolderIndex.OpenAsync(Database,Token); upgraded.Meta.Find("_apply-staging-legacy")!.Value.ShouldBe(legacy);
        upgraded.ReadReusableStaging(Root,"folder/file",false).ShouldBeEmpty();
        string backup=Directory.GetFiles(fixture,"*.pre-v19-to-v20-*.bak").Single();
        using var saved=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=backup,Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()); saved.Open();
        using var query=saved.CreateCommand(); query.CommandText="PRAGMA user_version;"; ((long)query.ExecuteScalar()!).ShouldBe(19);
    }

    [Fact]
    public async Task GuidSiblingMayExceedWirePathLengthWhileTargetAndParentRemainValid()
    {
        await using var index=await Open(); string parent=string.Join('/',Enumerable.Repeat(new string('d',254),16));
        string target=parent+"/x"; string temporary=parent+"/00000000000000000000000000000000.!psp"; temporary.Length.ShouldBeGreaterThan(4096);
        var intent=new ApplyIntent(Guid.NewGuid(),target,null,[1]); await index.ReserveApplyAsync(intent,Token);
        await index.ParkStagingAsync(intent,Row() with { TargetPath=target,TemporaryPath=temporary },Token);
        index.ReadReusableStaging(Root,target,false).Single().TemporaryPath.ShouldBe(temporary);
    }

    private async Task<FolderIndex> Open() { var index=await FolderIndex.OpenAsync(Database,Token); await index.Meta.UpsertAsync([new("_root-identity",Root)],Token); return index; }
    private static ApplyIntent Intent()=>new(Guid.NewGuid(),"folder/file",null,[1]);
    private static StagedObject Row()=>new(Guid.NewGuid(),Root.ToArray(),FileId.ToArray(),"folder/file","folder/00000000000000000000000000000000.!psp",false,Now,5,Hash.ToArray(),123);
    public void Dispose()=>Directory.Delete(fixture,true);
}
