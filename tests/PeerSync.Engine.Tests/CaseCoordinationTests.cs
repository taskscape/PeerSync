using System.Buffers.Binary;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Net.InMemory;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class CaseCoordinationTests : IDisposable
{
    private readonly string fixture=Path.Combine(Path.GetTempPath(),"PeerSync-CaseCoordination-"+Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys=FolderKeys.CreateNew();
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    private const string First="first spelling payload",Second="second spelling payload";
    private readonly DateTime early=DateTime.UtcNow.AddHours(-1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyReceiverPreservesBothConcurrentRemoteCaseVersionsVisibly(bool reverseArrival)
    {
        await using var a=await NodeAsync(1); await using var b=await NodeAsync(2); await using var c=await NodeAsync(3);
        await Publish(a,"file.txt",First,early); await Publish(b,"FILE.txt",Second,early.AddMinutes(1));
        var network=new InMemoryNetwork(); network.Register(a.Id,a.Engine.CreateHandler); network.Register(b.Id,b.Engine.CreateHandler);
        var receiver=network.Register(c.Id,c.Engine.CreateHandler);
        await using var ca=await c.Engine.ConnectAsync(receiver.Connect(a.Id),Token);
        await using var cb=await c.Engine.ConnectAsync(receiver.Connect(b.Id),Token);
        if(reverseArrival) { await cb.SynchronizeAsync(cancellationToken:Token); await ca.SynchronizeAsync(cancellationToken:Token); }
        else { await ca.SynchronizeAsync(cancellationToken:Token); await cb.SynchronizeAsync(cancellationToken:Token); }
        for(int pass=0;pass<4;pass++) await c.Engine.RunPassAsync(Token);
        RequireVisibleVersions(c);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingReceiverPreservesBothCaseVersionsWhetherNormalWinnerIsLocalOrRemote(bool remoteWins)
    {
        await using var local=await NodeAsync(1); await using var remote=await NodeAsync(2);
        await Publish(local,"file.txt",First,early.AddMinutes(remoteWins?0:1));
        await Publish(remote,"FILE.txt",Second,early.AddMinutes(remoteWins?1:0));
        var network=new InMemoryNetwork(); var endpoint=network.Register(local.Id,local.Engine.CreateHandler); network.Register(remote.Id,remote.Engine.CreateHandler);
        await using var connection=await local.Engine.ConnectAsync(endpoint.Connect(remote.Id),Token);
        await connection.SynchronizeAsync(cancellationToken:Token);
        for(int pass=0;pass<4;pass++) await local.Engine.RunPassAsync(Token);
        RequireVisibleVersions(local);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CausalCaseRenameWithOrWithoutEditDoesNotCreateCaseConflict(bool edit)
    {
        await using var source=await NodeAsync(1); await using var receiver=await NodeAsync(2);
        await Publish(source,"file.txt",First,early);
        var network=new InMemoryNetwork(); network.Register(source.Id,source.Engine.CreateHandler); var endpoint=network.Register(receiver.Id,receiver.Engine.CreateHandler);
        await using var connection=await receiver.Engine.ConnectAsync(endpoint.Connect(source.Id),Token);
        await connection.SynchronizeAsync(cancellationToken:Token); await receiver.Engine.RunPassAsync(Token);
        File.Move(Path.Combine(source.Root,"file.txt"),Path.Combine(source.Root,"FILE.txt"));
        if(edit) File.WriteAllText(Path.Combine(source.Root,"FILE.txt"),Second);
        File.SetLastWriteTimeUtc(Path.Combine(source.Root,"FILE.txt"),early.AddMinutes(1));
        source.Engine.RequestScan(); await source.Engine.RunPassAsync(Token);
        await connection.SynchronizeAsync(cancellationToken:Token); await receiver.Engine.RunPassAsync(Token);
        File.ReadAllText(Path.Combine(receiver.Root,"FILE.txt")).ShouldBe(edit?Second:First);
        Directory.EnumerateFiles(receiver.Root).Where(path=>Path.GetFileName(path).Contains(".case-conflict",StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty();
    }

    private static void RequireVisibleVersions(Node node)
    {
        string[] paths=Directory.GetFiles(node.Root).Where(path=>PathRules.IsValid(Path.GetFileName(path))).ToArray();
        paths.Length.ShouldBe(2,"case clashes require two visible versions; private blocks or an archive alone are insufficient");
        paths.Select(File.ReadAllText).Order(StringComparer.Ordinal).ToArray().ShouldBe(new[]{First,Second}.Order(StringComparer.Ordinal).ToArray());
        paths.Count(path=>Path.GetFileName(path).Contains(".case-conflict",StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }
    private static async Task Publish(Node node,string path,string text,DateTime at)
    { string disk=Path.Combine(node.Root,path); File.WriteAllText(disk,text); File.SetLastWriteTimeUtc(disk,at); node.Engine.RequestScan(); await node.Engine.RunPassAsync(Token); }
    private async Task<Node> NodeAsync(ulong identity)
    {
        string directory=Path.Combine(fixture,identity.ToString(System.Globalization.CultureInfo.InvariantCulture)); string root=Path.Combine(directory,"tree"); Directory.CreateDirectory(root);
        var index=await FolderIndex.OpenAsync(Path.Combine(directory,"index.db"),Token); byte[] id=new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id,identity);
        var engine=new TransportFolder(root,index,keys,id); await engine.RunPassAsync(Token); return new(root,index,engine,id);
    }
    private sealed record Node(string Root,FolderIndex Index,TransportFolder Engine,byte[] Id) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Index.DisposeAsync(); } }
    public void Dispose() { keys.Dispose(); if(Directory.Exists(fixture)) Directory.Delete(fixture,true); }
}
