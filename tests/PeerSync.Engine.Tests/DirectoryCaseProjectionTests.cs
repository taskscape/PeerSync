using Google.Protobuf;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Fact]
    public async Task ProjectionPreservesOriginalChildEnvelopesAndCausalParentCaseAliasesWithoutNativeWrites()
    {
        Directory.CreateDirectory(Path.Combine(Root, "topic", "nested")); File.WriteAllText(Path.Combine(Root, "topic", "nested", "a"), "original bytes");
        await using var index = await FolderIndex.OpenAsync(Database, Token); await PreviewScan(index); await Worker(index).RunAsync(token: Token);
        var child = index.LocalFiles.Find("topic/nested/a")!; var body = VersionView.Open(child.Record!, keys, device).ExportBody(); body.Path = "TOPIC/NeStEd/a";
        body.Vv.Single(entry => entry.Device == 1).Counter++; // A newly signed path spelling is a causal version, not equal-vector different bytes.
        var original = SignedRecordFactory.Create(body, keys); await index.LocalFiles.UpsertAsync([child with { Path = body.Path, Record = original }], Token);
        await PreviewScan(index); await Worker(index).RunAsync(token: Token); var root = index.LocalFiles.Find("topic")!.Record!; await CompleteProjectionGraph(index, root);
        var page = await Project(index, root); page.Members.Count.ShouldBe(2); page.More.ShouldBeFalse();
        var projected = page.Members.Single(member => member.Original.Type == 0);
        projected.Original.Path.ShouldBe("TOPIC/NeStEd/a"); projected.Original.Record.Body.ToArray().ShouldBe(original.Body.ToArray()); projected.Original.Record.Signature.ToArray().ShouldBe(original.Signature.ToArray());
        projected.Destination.ShouldBe(page.DestinationRoot + "/NeStEd/a"); page.Members[0].Original.Type.ShouldBe(1u);
        page.DestinationRoot.ShouldBe(ConflictNames.CaseSourcePath("topic", CaseConflictPolicy.SourceId(VersionView.Open(root, keys, device))));
        Directory.Exists(Path.Combine(Root, page.DestinationRoot)).ShouldBeFalse(); File.ReadAllText(Path.Combine(Root, "topic", "nested", "a")).ShouldBe("original bytes");
        page.ToString().ShouldNotContain("topic"); projected.ToString().ShouldNotContain("TOPIC");
    }
    [Theory]
    [InlineData(1, 8388608)]
    [InlineData(1000, 1)]
    public async Task ProjectionContinuesByExactKeysWithMemberAndEnvelopeBudgets(int limit, int byteBudget)
    {
        WriteFiles("a", "b", "c"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        var root = await PrepareProjection(index); var members = new List<DirectoryCaseMember>(); string? after = null; bool more = true;
        for (int attempt = 0; more && attempt < 5; attempt++)
        {
            var page = await Project(index, root, after, limit, byteBudget); page.Members.Count.ShouldBeLessThanOrEqualTo(1);
            members.AddRange(page.Members); if (page.Members.Count != 0) page.NextKey.ShouldBe(PathRules.Key(page.Members[^1].Original.Path));
            after = page.NextKey; more = page.More;
        }
        more.ShouldBeFalse(); members.Select(member => member.Original.Path).ShouldBe(["topic/a", "topic/b", "topic/c"]);
        members.Select(member => member.Destination).Distinct(StringComparer.Ordinal).Count().ShouldBe(3);
        await Should.ThrowAsync<ArgumentException>(() => Project(index, root, "topical/a"));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("incomplete")]
    [InlineData("retiring")]
    [InlineData("root")]
    public async Task ProjectionRejectsMissingIncompleteRetiringAndRootMismatchedGraphs(string damage)
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); var root = await PrepareProjection(index, buildGraph: damage != "missing");
        if (damage != "missing") Sql(damage switch
        {
            "incomplete" => "UPDATE directory_graphs SET complete=0;",
            "retiring" => "UPDATE directory_graphs SET retiring=1;",
            _ => "UPDATE directory_graphs SET root_id=zeroblob(24);",
        });
        if (damage == "root") await Should.ThrowAsync<InvalidDataException>(() => Project(index, root));
        else await Should.ThrowAsync<InvalidOperationException>(() => Project(index, root));
        Directory.GetDirectories(Root).Select(Path.GetFileName).ShouldNotContain(name => name!.Contains("case-conflict", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionReauthenticatesPersistedMemberKeysAndRecordKinds(bool wrongKind)
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); var root = await PrepareProjection(index);
        if (wrongKind)
        {
            var link = DirectorySnapshotBlocks.SnapshotOf(VersionView.Open(root, keys, device));
            Sql("UPDATE directory_graph_members SET record_hash=X'" + Convert.ToHexString(link.Hash) + "',record_length=" + link.EncodedLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";");
        }
        else Sql("UPDATE directory_graph_members SET path_key='TOPIC/Z';");
        await Should.ThrowAsync<InvalidDataException>(() => Project(index, root));
    }
    [Fact]
    public async Task ProjectionKeepsOldExactSourceAfterLiveReplacementButNeverReusesItsGraphForANewEnvelope()
    {
        WriteFiles("a"); await using var index = await FolderIndex.OpenAsync(Database, Token); var root = await PrepareProjection(index);
        var row = index.LocalFiles.Find("topic")!; var body = VersionView.Open(root, keys, device).ExportBody(); body.ChangedTicks++;
        var changed = SignedRecordFactory.Create(body, keys); await index.LocalFiles.UpsertAsync([row with { Record = changed, ChangedTicks = body.ChangedTicks }], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => Project(index, changed));
        var old = await Project(index, root); old.OriginalRoot.Record.Body.ToArray().ShouldBe(root.Body.ToArray()); old.Members.Single().Original.Path.ShouldBe("topic/a");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        await Should.ThrowAsync<OperationCanceledException>(() => new DirectoryCaseProjection(Root, index, keys, device).ReadUnderGateAsync(root, MissingProjectionBlock, token: cancelled.Token));
    }
    [Fact]
    public async Task EmptyGraphProjectsOnlyItsAuthenticatedRootAndCreatesNothing()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); var root = await PrepareProjection(index);
        var page = await Project(index, root); page.Members.ShouldBeEmpty(); page.NextKey.ShouldBeNull(); page.More.ShouldBeFalse();
        Directory.Exists(Path.Combine(Root, page.DestinationRoot)).ShouldBeFalse();
    }
    private async Task<SignedRecord> PrepareProjection(FolderIndex index, bool buildGraph = true)
    {
        await PreviewScan(index); await Worker(index).RunAsync(token: Token); var root = index.LocalFiles.Find("topic")!.Record!;
        if (buildGraph) await CompleteProjectionGraph(index, root); return root;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionDefersTheNextBlobAtItsByteBudgetAndFlushesFetchedMetadataEvenWhenCancelled(bool cancel)
    {
        WriteFiles("a", "b"); await using var index = await FolderIndex.OpenAsync(Database, Token); var root = await PrepareProjection(index);
        var id = CaseConflictPolicy.SourceId(VersionView.Open(root, keys, device)); var members = index.ReadDirectoryGraphMemberPage(id);
        var metadata = new Dictionary<string, byte[]>(StringComparer.Ordinal); string secondBlob;
        using (var cache = new PublishedBlocks(Root, index))
        {
            foreach (var member in members)
            {
                var link = new PeerSync.Protocol.Directories.DirectorySnapshotLink(member.RecordHash, member.RecordLength); var bytes = cache.TryRead(link.Hash, link.EncodedLength, Token)!;
                metadata.Add(Convert.ToHexString(link.Hash), bytes); var node = PeerSync.Protocol.Directories.DirectorySnapshotNode.Open(link, bytes);
                foreach (var blob in node.Links) metadata.TryAdd(Convert.ToHexString(blob.Hash), cache.TryRead(blob.Hash, blob.EncodedLength, Token)!);
            }
            var second = members[1]; var secondLink = new PeerSync.Protocol.Directories.DirectorySnapshotLink(second.RecordHash, second.RecordLength);
            secondBlob = Convert.ToHexString(PeerSync.Protocol.Directories.DirectorySnapshotNode.Open(secondLink, metadata[Convert.ToHexString(second.RecordHash)]).Links.Single().Hash);
        }
        // Remove only exact known cache bytes from this owned fixture, so reads
        // must exercise the fetch/flush path rather than the warm local cache.
        foreach (var item in metadata)
        {
            string path = Path.GetFullPath(Path.Combine(Root, ".psp", "blocks", item.Key[..2], item.Key));
            path.StartsWith(Path.GetFullPath(fixture) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            (File.GetAttributes(path) & FileAttributes.ReparsePoint).ShouldBe((FileAttributes)0); File.ReadAllBytes(path).ShouldBe(item.Value); File.Delete(path);
        }
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token); var fetched = new List<string>();
        ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> hash, int length, CancellationToken token)
        {
            string key = Convert.ToHexString(hash.Span); fetched.Add(key); var bytes = metadata[key]; bytes.Length.ShouldBe(length);
            if (cancel && key == Convert.ToHexString(members[1].RecordHash)) cancelled.Cancel(); return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);
        }
        using var gate = await FolderOperationGate.AcquireAsync(index, Token); var projection = new DirectoryCaseProjection(Root, index, keys, device);
        if (cancel) await Should.ThrowAsync<OperationCanceledException>(() => projection.ReadUnderGateAsync(root, Fetch, token: cancelled.Token));
        else { var page = await projection.ReadUnderGateAsync(root, Fetch, byteBudget: 1, token: cancelled.Token); page.Members.Single().Original.Path.ShouldBe("topic/a"); page.More.ShouldBeTrue(); }
        fetched.ShouldNotContain(secondBlob); fetched.Count.ShouldBe(3);
        using var retained = new PublishedBlocks(Root, index); var first = members[0];
        retained.TryRead(first.RecordHash, first.RecordLength, Token).ShouldNotBeNull();
        index.ReadBlockObjects().Select(row => Convert.ToHexString(row.Hash)).ShouldContain(Convert.ToHexString(first.RecordHash));
    }
    private async Task CompleteProjectionGraph(FolderIndex index, SignedRecord root)
    {
        using var gate = await FolderOperationGate.AcquireAsync(index, Token); var graph = new DirectorySnapshotGraph(Root, index, keys, device);
        (await graph.AdvanceUnderGateAsync(root, MissingProjectionBlock, 1000, Token)).Complete.ShouldBeTrue();
    }
    private async Task<DirectoryCasePage> Project(FolderIndex index, SignedRecord root, string? after = null, int limit = 1000, int bytes = 8388608)
    {
        using var gate = await FolderOperationGate.AcquireAsync(index, Token);
        return await new DirectoryCaseProjection(Root, index, keys, device).ReadUnderGateAsync(root, MissingProjectionBlock, after, limit: limit, byteBudget: bytes, token: Token);
    }
    private static ValueTask<ReadOnlyMemory<byte>> MissingProjectionBlock(ReadOnlyMemory<byte> hash, int length, CancellationToken token) =>
        throw new IOException("Fixture metadata must be locally retained.");
}
