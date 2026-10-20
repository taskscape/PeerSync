using System.Buffers.Binary;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DirectoryCaseClashPreservesTheLosingSignedSubtreeOnAnEmptyReceiver(bool reverseArrival, bool nested)
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2); await using var c = await NodeAsync(3);
        string relative = nested ? "nested/shared.txt" : "shared.txt";
        await PublishDirectory(a, "topic", relative, First, early); await PublishDirectory(b, "TOPIC", relative, Second, early.AddMinutes(1));
        var network = new InMemoryNetwork(); network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        var endpoint = network.Register(c.Id, c.Engine.CreateHandler);
        await using var ca = await c.Engine.ConnectAsync(endpoint.Connect(a.Id), Token); await using var cb = await c.Engine.ConnectAsync(endpoint.Connect(b.Id), Token);
        if (reverseArrival) { await cb.SynchronizeAsync(cancellationToken: Token); await ca.SynchronizeAsync(cancellationToken: Token); }
        else { await ca.SynchronizeAsync(cancellationToken: Token); await cb.SynchronizeAsync(cancellationToken: Token); }
        for (int pass = 0; pass < 8; pass++) { var result = await c.Engine.RunPassAsync(Token); c.Clock.AdvanceTo(result.NextAttemptTicks); }
        var copy = Directory.GetDirectories(c.Root).SingleOrDefault(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal));
        copy.ShouldNotBeNull("a directory source needs its own visible subtree, not only a pending metadata proof");
        File.ReadAllText(Path.Combine(copy!, relative)).ShouldBe(First);
        File.Exists(Path.Combine(c.Root, "TOPIC", relative)).ShouldBeTrue(CaseState(c));
        File.ReadAllText(Path.Combine(c.Root, "TOPIC", relative)).ShouldBe(Second);
        c.Index.ReadPendingCasePreservations().ShouldBeEmpty(CaseState(c)); c.Index.Needed.Count().ShouldBe(0, CaseState(c));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryCaseClashWithAFilePreservesBothKinds(bool fileWins)
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2); await using var c = await NodeAsync(3);
        await PublishDirectory(a, "topic", "shared.txt", First, early.AddMinutes(fileWins ? 0 : 1));
        await Publish(b, "TOPIC", Second, early.AddMinutes(fileWins ? 1 : 0));
        var network = new InMemoryNetwork(); network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        var endpoint = network.Register(c.Id, c.Engine.CreateHandler);
        await using var ca = await c.Engine.ConnectAsync(endpoint.Connect(a.Id), Token); await using var cb = await c.Engine.ConnectAsync(endpoint.Connect(b.Id), Token);
        await ca.SynchronizeAsync(cancellationToken: Token); await cb.SynchronizeAsync(cancellationToken: Token);
        for (int pass = 0; pass < 8; pass++) { var result = await c.Engine.RunPassAsync(Token); c.Clock.AdvanceTo(result.NextAttemptTicks); }
        if (fileWins)
        {
            File.Exists(Path.Combine(c.Root, "TOPIC")).ShouldBeTrue(); File.ReadAllText(Path.Combine(c.Root, "TOPIC")).ShouldBe(Second);
            string copy = Directory.GetDirectories(c.Root).Single(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal));
            File.ReadAllText(Path.Combine(copy, "shared.txt")).ShouldBe(First);
        }
        else
        {
            File.ReadAllText(Path.Combine(c.Root, "topic", "shared.txt")).ShouldBe(First);
            File.ReadAllText(Directory.GetFiles(c.Root).Single(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal))).ShouldBe(Second);
        }
        c.Index.ReadPendingCasePreservations().ShouldBeEmpty(CaseState(c)); c.Index.Needed.Count().ShouldBe(0, CaseState(c));
    }

    [Fact]
    public async Task DirectoryCaseRenameIsCausalEvenWhenTheSignedChildRetainsItsOldParentSpelling()
    {
        await using var source = await NodeAsync(1); await using var receiver = await NodeAsync(2);
        await PublishDirectory(source, "topic", "shared.txt", First, early);
        var network = new InMemoryNetwork(); network.Register(source.Id, source.Engine.CreateHandler); var endpoint = network.Register(receiver.Id, receiver.Engine.CreateHandler);
        await using var connection = await receiver.Engine.ConnectAsync(endpoint.Connect(source.Id), Token);
        await connection.SynchronizeAsync(cancellationToken: Token); await receiver.Engine.RunPassAsync(Token);
        Directory.Move(Path.Combine(source.Root, "topic"), Path.Combine(source.Root, "TOPIC")); source.Engine.RequestScan(); await source.Engine.RunPassAsync(Token);
        VersionView.Open(source.Index.LocalFiles.Find("topic/shared.txt")!.Record!, keys, source.Id).Path.ShouldBe("topic/shared.txt");
        await connection.SynchronizeAsync(cancellationToken: Token); for (int pass = 0; pass < 3; pass++) await receiver.Engine.RunPassAsync(Token);
        File.ReadAllText(Path.Combine(receiver.Root, "TOPIC", "shared.txt")).ShouldBe(First);
        Directory.GetDirectories(receiver.Root).Where(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private static async Task PublishDirectory(Node node, string directory, string relative, string content, DateTime at)
    {
        string path = Path.Combine(node.Root, directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, at); Directory.SetLastWriteTimeUtc(Path.Combine(node.Root, directory), at);
        node.Engine.RequestScan(); await node.Engine.RunPassAsync(Token);
    }

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

    [Fact]
    public async Task SettledReadOnlyCaseWorkAndDeadlinesDrainAcrossRepeatedPasses()
    {
        await using var a=await NodeAsync(1); await using var b=await NodeAsync(2); await using var c=await NodeAsync(3,readOnly:true);
        await Publish(a,"file.txt",First,early); await Publish(b,"FILE.txt",Second,early.AddMinutes(1));
        var network=new InMemoryNetwork(); network.Register(a.Id,a.Engine.CreateHandler); network.Register(b.Id,b.Engine.CreateHandler);
        var receiver=network.Register(c.Id,c.Engine.CreateHandler);
        await using var ca=await c.Engine.ConnectAsync(receiver.Connect(a.Id),Token); await using var cb=await c.Engine.ConnectAsync(receiver.Connect(b.Id),Token);
        await ca.SynchronizeAsync(cancellationToken:Token); await cb.SynchronizeAsync(cancellationToken:Token);
        await c.Engine.RunPassAsync(Token); RequireVisibleVersions(c); c.Index.ReadPendingCasePreservations().ShouldBeEmpty();
        c.Index.Needed.Count().ShouldBe(0);
        for(int pass=0;pass<4;pass++)
        {
            var result=await c.Engine.RunPassAsync(Token); result.Visited.ShouldBe(0); result.MoreWork.ShouldBeFalse(); result.NextAttemptTicks.ShouldBeNull();
        }
    }

    [Fact]
    public async Task AnotherAuthenticatedForwarderServesTheExactSourceAfterFirstHintDisconnects()
    {
        await using var a=await NodeAsync(1); await using var forwarder=await NodeAsync(2); await using var c=await NodeAsync(3); await using var winner=await NodeAsync(4);
        await Publish(a,"file.txt",First,early); await Publish(winner,"FILE.txt",Second,early.AddMinutes(1));
        var network=new InMemoryNetwork(); network.Register(a.Id,a.Engine.CreateHandler); network.Register(winner.Id,winner.Engine.CreateHandler);
        var forwarding=network.Register(forwarder.Id,forwarder.Engine.CreateHandler); var receiver=network.Register(c.Id,c.Engine.CreateHandler);
        await using var fa=await forwarder.Engine.ConnectAsync(forwarding.Connect(a.Id),Token);
        await fa.SynchronizeAsync(cancellationToken:Token); await forwarder.Engine.RunPassAsync(Token);
        await using var ca=await c.Engine.ConnectAsync(receiver.Connect(a.Id),Token); await using var cw=await c.Engine.ConnectAsync(receiver.Connect(winner.Id),Token);
        await ca.SynchronizeAsync(cancellationToken:Token); await cw.SynchronizeAsync(cancellationToken:Token);
        c.Index.ReadPendingCasePreservations().Single().Source.SourceDevice.ShouldBe(a.Id); await ca.DisposeAsync();
        await using var cb=await c.Engine.ConnectAsync(receiver.Connect(forwarder.Id),Token); await cb.SynchronizeAsync(cancellationToken:Token);
        await c.Engine.RunPassAsync(Token); RequireVisibleVersions(c); c.Index.ReadPendingCasePreservations().ShouldBeEmpty(); c.Index.Needed.Count().ShouldBe(0);
    }

    private static void RequireVisibleVersions(Node node)
    {
        string[] paths=Directory.GetFiles(node.Root).Where(path=>PathRules.IsValid(Path.GetFileName(path))).ToArray();
        paths.Length.ShouldBe(2,"case clashes require two visible versions; private blocks or an archive alone are insufficient");
        paths.Select(File.ReadAllText).Order(StringComparer.Ordinal).ToArray().ShouldBe(new[]{First,Second}.Order(StringComparer.Ordinal).ToArray());
        paths.Count(path=>Path.GetFileName(path).Contains(".case-conflict",StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    [Fact]
    public async Task LateChildrenWaitBehindAFileButTombstonesAndLaterDirectoryReactivationStillProgress()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2); await using var c = await NodeAsync(3);
        await PublishDirectory(a, "node", "child", First, early); await Publish(a, "node/survivor", First, early);
        await Publish(b, "node", Second, early.AddMinutes(1));
        var network = new InMemoryNetwork(); network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        var endpoint = network.Register(c.Id, c.Engine.CreateHandler);
        await using var cb = await c.Engine.ConnectAsync(endpoint.Connect(b.Id), Token); await cb.SynchronizeAsync(cancellationToken: Token);
        await c.Engine.RunPassAsync(Token); File.ReadAllText(Path.Combine(c.Root, "node")).ShouldBe(Second);
        await using var ca = await c.Engine.ConnectAsync(endpoint.Connect(a.Id), Token); await ca.SynchronizeAsync(cancellationToken: Token);
        for (int pass = 0; pass < 4; pass++) { var result = await c.Engine.RunPassAsync(Token); c.Clock.AdvanceTo(result.NextAttemptTicks); }
        c.Index.Needed.Find("node/child").ShouldBeNull(); c.Index.Needed.Find("node/survivor").ShouldBeNull();
        c.Index.RemotesForPath("node/survivor").Count().ShouldBeGreaterThan(0); // Suspension retains original signed versions.
        File.ReadAllText(Path.Combine(c.Root, "node")).ShouldBe(Second);
        File.Delete(Path.Combine(a.Root, "node", "child")); a.Engine.RequestScan(); await a.Engine.RunPassAsync(Token);
        await ca.SynchronizeAsync(cancellationToken: Token);
        for (int pass = 0; pass < 4; pass++) { var result = await c.Engine.RunPassAsync(Token); c.Clock.AdvanceTo(result.NextAttemptTicks); }
        c.Index.LocalFiles.Find("node/child")!.Deleted.ShouldBeTrue(); File.ReadAllText(Path.Combine(c.Root, "node")).ShouldBe(Second);
        File.Delete(Path.Combine(b.Root, "node")); await PublishDirectory(b, "node", "fresh", Second, early.AddMinutes(2));
        await cb.SynchronizeAsync(cancellationToken: Token);
        for (int pass = 0; pass < 8; pass++) { var result = await c.Engine.RunPassAsync(Token); c.Clock.AdvanceTo(result.NextAttemptTicks); }
        File.ReadAllText(Path.Combine(c.Root, "node", "survivor")).ShouldBe(First);
        File.ReadAllText(Path.Combine(c.Root, "node", "fresh")).ShouldBe(Second);
        File.Exists(Path.Combine(c.Root, "node", "child")).ShouldBeFalse(); c.Index.Needed.Count().ShouldBe(0, CaseState(c));
    }
    private static string CaseState(Node node) => string.Join(Environment.NewLine,
        node.Index.ReadPendingCasePreservations().Select(row => "case " + row.Source.PathKey + " -> " + row.DestinationPath + " retry=" + node.Index.ReadCasePreservationRetry(row.Source.SourceId)
            + " progress=" + (row.InstalledFileId is null ? null : node.Index.ReadDirectoryCaseProgress(row.Source.SourceId, row.InstalledFileId)))
        .Concat(node.Index.LocalFiles.ReadAll().Select(row => "local " + row.Path + " flags=" + row.Flags + " ready=" + row.ContentReady + " seq=" + row.LocalSequence))
        .Concat(node.Index.Needed.ReadAll().Select(row => "needed " + row.PathKey + " work=" + row.Work))
        .Concat(node.Index.ReadDirectoryPublicationPaths().Select(path => "publication " + path + " mode=" + node.Index.ReadDirectoryPublication(path)!.ReconstructionOnly)));

    [Fact]
    public async Task LocalDirectoryReplacementReactivatesSuspendedRemoteChildren()
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2); await using var c = await NodeAsync(3);
        await PublishDirectory(a, "node", "child", First, early); await Publish(b, "node", Second, early.AddMinutes(1));
        var network = new InMemoryNetwork(); network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        var endpoint = network.Register(c.Id, c.Engine.CreateHandler);
        await using var cb = await c.Engine.ConnectAsync(endpoint.Connect(b.Id), Token); await cb.SynchronizeAsync(cancellationToken: Token); await c.Engine.RunPassAsync(Token);
        await using var ca = await c.Engine.ConnectAsync(endpoint.Connect(a.Id), Token); await ca.SynchronizeAsync(cancellationToken: Token);
        for (int pass = 0; pass < 4; pass++) { var result = await c.Engine.RunPassAsync(Token); c.Clock.AdvanceTo(result.NextAttemptTicks); }
        c.Index.Needed.Find("node/child").ShouldBeNull(); File.ReadAllText(Path.Combine(c.Root, "node")).ShouldBe(Second);
        File.Delete(Path.Combine(c.Root, "node")); Directory.CreateDirectory(Path.Combine(c.Root, "node")); c.Engine.RequestScan();
        for (int pass = 0; pass < 8; pass++) { var result = await c.Engine.RunPassAsync(Token); c.Clock.AdvanceTo(result.NextAttemptTicks); }
        File.ReadAllText(Path.Combine(c.Root, "node", "child")).ShouldBe(First);
        c.Index.Needed.Count().ShouldBe(0, CaseState(c)); c.Index.HasPendingDirectoryPublications().ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentlyPreservedDirectoryCopiesConvergeThroughFourNodeGossip(bool readonlyReceiver)
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        await using var c = await NodeAsync(3); await using var d = await NodeAsync(4, readOnly: readonlyReceiver);
        await PublishDirectory(a, "topic", "nested/shared.txt", First, early);
        await PublishDirectory(b, "TOPIC", "nested/shared.txt", Second, early.AddMinutes(1));
        Node[] nodes = [a, b, c, d]; var network = new InMemoryNetwork();
        var endpoints = nodes.ToDictionary(node => node, node => network.Register(node.Id, node.Engine.CreateHandler));
        var links = new List<(Node Owner, Node Sender, PeerFolderConnection Connection)>();
        try
        {
            foreach (var receiver in nodes)
                foreach (var sender in nodes.Where(node => !ReferenceEquals(node, receiver)))
                    links.Add((receiver, sender, await receiver.Engine.ConnectAsync(endpoints[receiver].Connect(sender.Id), Token)));
            // First create both receiver copies independently from the originals.
            foreach (var link in links.Where(link => (link.Owner == c || link.Owner == d) && (link.Sender == a || link.Sender == b)))
                await link.Connection.SynchronizeAsync(maxPages: 1, cancellationToken: Token);
            for (int pass = 0; pass < 8; pass++)
            {
                var first = await c.Engine.RunPassAsync(Token); var second = await d.Engine.RunPassAsync(Token);
                long now = nodes.Max(node => node.Clock.GetUtcNow().UtcTicks);
                long? next = new[] { first.NextAttemptTicks, second.NextAttemptTicks }.Where(ticks => ticks > now).Min();
                foreach (var node in nodes) node.Clock.AdvanceTo(next);
            }
            foreach (var receiver in new[] { c, d })
                File.ReadAllText(Path.Combine(Directory.GetDirectories(receiver.Root).Single(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal)), "nested", "shared.txt")).ShouldBe(First);
            for (int cycle = 0; cycle < 16; cycle++)
            {
                var deadlines = new List<long>();
                foreach (var link in links)
                {
                    await link.Connection.SynchronizeAsync(maxPages: 1, cancellationToken: Token);
                    var result = await link.Owner.Engine.RunPassAsync(Token);
                    if (result.NextAttemptTicks is long deadline) deadlines.Add(deadline);
                }
                long now = nodes.Max(node => node.Clock.GetUtcNow().UtcTicks);
                long? next = deadlines.Where(ticks => ticks > now).Select(ticks => (long?)ticks).Min();
                foreach (var node in nodes) node.Clock.AdvanceTo(next);
            }
            string? stableCopy = null;
            foreach (var node in nodes)
            {
                string copy = Directory.GetDirectories(node.Root).Single(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal));
                stableCopy ??= Path.GetFileName(copy); Path.GetFileName(copy).ShouldBe(stableCopy);
                File.ReadAllText(Path.Combine(copy, "nested", "shared.txt")).ShouldBe(First);
                File.ReadAllText(Path.Combine(node.Root, "TOPIC", "nested", "shared.txt")).ShouldBe(Second);
                node.Index.ReadPendingCasePreservations().ShouldBeEmpty(CaseState(node)); node.Index.Needed.Count().ShouldBe(0, CaseState(node));
                node.Index.HasPendingDirectoryPublications().ShouldBeFalse(CaseState(node));
            }
            var uncovered = new List<string>();
            foreach (var node in nodes)
            {
                var references = new BlockReferences(node.Root, node.Index, node.Access ?? keys, node.Id);
                var coverage = await references.RefreshAsync(Token);
                for (int pass = 1; pass < 4 && !coverage.AllKnownSourcesCovered; pass++) coverage = await references.RefreshAsync(Token);
                if (!coverage.AllKnownSourcesCovered)
                {
                    var graphs = node.Index.LocalFiles.ReadAll().Where(row => row is { Type: 1, Deleted: false, Record: not null }).Select(row =>
                    {
                        var graph = node.Index.ReadDirectoryGraph(CaseConflictPolicy.SourceId(VersionView.Open(row.Record!, node.Access ?? keys, node.Id)));
                        return row.Path + ":" + (graph is null ? "absent" : graph.Complete ? "complete" : "pending");
                    });
                    uncovered.Add("node=" + node.Id[0] + " captures=" + node.Index.HasDirectoryCaptures() + " graphs=" + string.Join(',', graphs) + " " + coverage);
                }
            }
            uncovered.ShouldBeEmpty("Gossip reference coverage after bounded continuation: " + string.Join("; ", uncovered));
        }
        finally { foreach (var link in links) await link.Connection.DisposeAsync(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryCaseCopiesReconstructThroughAForwarderAfterOriginalOwnerDisconnects(bool readOnly)
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        await using var forwarder = await NodeAsync(3); await using var receiver = await NodeAsync(4, readOnly);
        await PublishDirectory(a, "topic", "nested/shared.txt", First, early);
        await PublishDirectory(b, "TOPIC", "nested/shared.txt", Second, early.AddMinutes(1));
        var network = new InMemoryNetwork(); network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        var forwardingEndpoint = network.Register(forwarder.Id, forwarder.Engine.CreateHandler);
        await using (var source = await forwarder.Engine.ConnectAsync(forwardingEndpoint.Connect(a.Id), Token))
        {
            await source.SynchronizeAsync(maxPages: 1, cancellationToken: Token);
            for (int pass = 0; pass < 8; pass++) { var result = await forwarder.Engine.RunPassAsync(Token); forwarder.Clock.AdvanceTo(result.NextAttemptTicks); }
            File.ReadAllText(Path.Combine(forwarder.Root, "topic", "nested", "shared.txt")).ShouldBe(First);
            (await new BlockReferences(forwarder.Root, forwarder.Index, keys, forwarder.Id).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
        }
        // No connection to the original owner remains. Only the forwarding PC
        // and the independent winning author are reachable from this receiver.
        var endpoint = network.Register(receiver.Id, receiver.Engine.CreateHandler);
        await using var forwarded = await receiver.Engine.ConnectAsync(endpoint.Connect(forwarder.Id), Token);
        await using var winning = await receiver.Engine.ConnectAsync(endpoint.Connect(b.Id), Token);
        await forwarded.SynchronizeAsync(maxPages: 1, cancellationToken: Token);
        await winning.SynchronizeAsync(maxPages: 1, cancellationToken: Token);
        for (int pass = 0; pass < 12; pass++) { var result = await receiver.Engine.RunPassAsync(Token); receiver.Clock.AdvanceTo(result.NextAttemptTicks); }
        string copy = Directory.GetDirectories(receiver.Root).Single(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(copy, "nested", "shared.txt")).ShouldBe(First);
        File.ReadAllText(Path.Combine(receiver.Root, "TOPIC", "nested", "shared.txt")).ShouldBe(Second);
        receiver.Index.ReadPendingCasePreservations().ShouldBeEmpty(CaseState(receiver)); receiver.Index.Needed.Count().ShouldBe(0, CaseState(receiver));
        receiver.Index.HasPendingDirectoryPublications().ShouldBeFalse(CaseState(receiver));
        (await new BlockReferences(receiver.Root, receiver.Index, receiver.Access ?? keys, receiver.Id).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditedPreservedSubtreeForwardsToAFreshReceiverWithBothOriginalAuthorsOffline(bool readOnly)
    {
        await using var a = await NodeAsync(1); await using var b = await NodeAsync(2);
        await using var forwarder = await NodeAsync(3); await using var receiver = await NodeAsync(4, readOnly);
        await PublishDirectory(a, "topic", "nested/shared.txt", First, early);
        await PublishDirectory(b, "TOPIC", "nested/shared.txt", Second, early.AddMinutes(1));
        var network = new InMemoryNetwork(); network.Register(a.Id, a.Engine.CreateHandler); network.Register(b.Id, b.Engine.CreateHandler);
        var endpoint = network.Register(forwarder.Id, forwarder.Engine.CreateHandler);
        await using (var first = await forwarder.Engine.ConnectAsync(endpoint.Connect(a.Id), Token))
        await using (var second = await forwarder.Engine.ConnectAsync(endpoint.Connect(b.Id), Token))
        {
            await first.SynchronizeAsync(maxPages: 1, cancellationToken: Token); await second.SynchronizeAsync(maxPages: 1, cancellationToken: Token);
            for (int pass = 0; pass < 12; pass++) { var result = await forwarder.Engine.RunPassAsync(Token); forwarder.Clock.AdvanceTo(result.NextAttemptTicks); }
        }
        string copy = Directory.GetDirectories(forwarder.Root).Single(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal));
        string edited = Path.Combine(copy, "nested", "shared.txt"); File.WriteAllText(edited, "edited preserved subtree");
        File.SetLastWriteTimeUtc(edited, early.AddMinutes(2)); forwarder.Engine.RequestScan();
        for (int pass = 0; pass < 8; pass++) { var result = await forwarder.Engine.RunPassAsync(Token); forwarder.Clock.AdvanceTo(result.NextAttemptTicks); }
        forwarder.Index.HasPendingDirectoryPublications().ShouldBeFalse(CaseState(forwarder));
        var receiving = network.Register(receiver.Id, receiver.Engine.CreateHandler);
        await using var forwarded = await receiver.Engine.ConnectAsync(receiving.Connect(forwarder.Id), Token);
        await forwarded.SynchronizeAsync(maxPages: 1, cancellationToken: Token);
        for (int pass = 0; pass < 12; pass++) { var result = await receiver.Engine.RunPassAsync(Token); receiver.Clock.AdvanceTo(result.NextAttemptTicks); }
        var copies = Directory.GetDirectories(receiver.Root).Where(path => Path.GetFileName(path).Contains(".case-conflict", StringComparison.Ordinal)).ToArray();
        copies.Length.ShouldBe(1); Path.GetFileName(copies[0]).ShouldBe(Path.GetFileName(copy));
        File.ReadAllText(Path.Combine(copies[0], "nested", "shared.txt")).ShouldBe("edited preserved subtree");
        File.ReadAllText(Path.Combine(receiver.Root, "TOPIC", "nested", "shared.txt")).ShouldBe(Second);
        receiver.Index.ReadPendingCasePreservations().ShouldBeEmpty(CaseState(receiver)); receiver.Index.Needed.Count().ShouldBe(0, CaseState(receiver));
        receiver.Index.HasPendingDirectoryPublications().ShouldBeFalse(CaseState(receiver));
        (await new BlockReferences(receiver.Root, receiver.Index, receiver.Access ?? keys, receiver.Id).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
    }

    private static async Task Publish(Node node,string path,string text,DateTime at)
    { string disk=Path.Combine(node.Root,path); File.WriteAllText(disk,text); File.SetLastWriteTimeUtc(disk,at); node.Engine.RequestScan(); await node.Engine.RunPassAsync(Token); }
    private async Task<Node> NodeAsync(ulong identity,bool readOnly=false)
    {
        string directory=Path.Combine(fixture,identity.ToString(System.Globalization.CultureInfo.InvariantCulture)); string root=Path.Combine(directory,"tree"); Directory.CreateDirectory(root);
        var index=await FolderIndex.OpenAsync(Path.Combine(directory,"index.db"),Token); byte[] id=new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id,identity);
        var access=readOnly?FolderKeys.FromText(keys.ExportReadOnlyText()):null;
        var clock = new CaseClock(); var engine=new TransportFolder(root,index,access??keys,id,settings:null,timeProvider:clock,journal:null); await engine.RunPassAsync(Token); return new(root,index,engine,id,access,clock);
    }
    private sealed record Node(string Root,FolderIndex Index,TransportFolder Engine,byte[] Id,FolderKeys? Access,CaseClock Clock) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); await Index.DisposeAsync(); Access?.Dispose(); } }
    private sealed class CaseClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void AdvanceTo(long? ticks) { if (ticks > now.UtcTicks) now = new DateTimeOffset(ticks.Value, TimeSpan.Zero); }
    }
    public void Dispose() { keys.Dispose(); if(Directory.Exists(fixture)) Directory.Delete(fixture,true); }
}
