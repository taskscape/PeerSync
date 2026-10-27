using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class DirectoryParentProofTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-ParentProofDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static readonly byte[] Root = new byte[24], Native = new byte[16];
    private const long Now = 638000000000000000;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public DirectoryParentProofTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task OnlyUnprovenAuthenticatedDirectMembersCanAdvanceABlockingParent()
    {
        await using var index = await Open();
        var (queued, draft) = await Prepare(index);
        index.HasUnprovenDirectoryParentMember("topic/0000").ShouldBeFalse();
        await Graph(index, Source(1), 2);
        var proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now,
            cancellationToken: Token)).Proof;
        proof = await Discover(index, queued, draft, proof);
        index.HasUnprovenDirectoryParentMember("topic/0000").ShouldBeTrue();
        index.HasUnprovenDirectoryParentMember("topic/0001").ShouldBeTrue();
        index.HasUnprovenDirectoryParentMember("topic/other").ShouldBeFalse();
        index.HasUnprovenDirectoryParentMember("topic/0000/grandchild").ShouldBeFalse();

        var root = index.ReadNextDirectoryParentRoot(proof)!;
        await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, root,
            index.ReadDirectoryGraphMemberPage(root.Source.SourceId, limit: 1), Token);
        index.HasUnprovenDirectoryParentMember("topic/0000").ShouldBeFalse();
        index.HasUnprovenDirectoryParentMember("topic/0001").ShouldBeTrue();
        await index.DiscardDirectoryPublicationAsync(queued, Token);
        index.HasUnprovenDirectoryParentMember("topic/0001").ShouldBeFalse();
    }

    [Fact]
    public async Task ParentRepresentativesAndVerifiedPrefixesSurviveReopenAndVectorOnlyQueueRestaging()
    {
        DirectoryPublication queued; DirectoryCaptureState draft; DirectoryParentProof proof; DirectoryParentRoot root;
        await using (var index = await Open())
        {
            (queued, draft) = await Prepare(index);
            await Graph(index, Source(1), 2);
            proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: Token)).Proof;
            proof = await Discover(index, queued, draft, proof);
            root = index.ReadNextDirectoryParentRoot(proof)!;
            root = await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, root, index.ReadDirectoryGraphMemberPage(root.Source.SourceId, limit: 1), Token);
            root.Complete.ShouldBeFalse(); root.Cursor.ShouldBe("TOPIC/0000");
        }
        await using var reopened = await Open();
        var saved = reopened.ReadDirectoryParentProof("topic")!;
        reopened.ReadNextDirectoryParentRoot(saved)!.Cursor.ShouldBe(root.Cursor);
        var local = reopened.LocalFiles.Find("topic")!;
        await reopened.LocalFiles.UpsertAsync([local with { Vector = new Dictionary<ulong, ulong> { [1] = 2 }, Record = Source(2, 1).Record }], Token);
        await reopened.DiscardDirectoryPublicationAsync(queued, Token);
        reopened.ReadDirectoryParentProof("topic").ShouldNotBeNull();
        queued = await reopened.StageDirectoryPublicationAsync(queued with { Version = Guid.NewGuid(), ExpectedLocalSequence = reopened.LocalFiles.Find("topic")!.LocalSequence, Template = Source(2, 1).Record }, Token);
        draft = await reopened.BeginDirectoryCaptureAsync("topic", Root, Native, Id(10), 0, new byte[16], Token);
        draft = await reopened.AdvanceDirectoryCaptureAsync(draft, 0, null, null, Id(12), 12, Token);
        (queued, draft) = await Rebind(reopened, queued, draft);
        proof = (await reopened.BeginDirectoryParentProofAsync(queued, draft, Source(2, 1), Id(9), Now + 1, cancellationToken: Token)).Proof;
        proof.ChildEpoch.ShouldBe(0); proof.Generation.ShouldBe(saved.Generation); proof.Clock[1].ShouldBe(2ul);
        var resumed = reopened.ReadNextDirectoryParentRoot(proof)!;
        resumed.Cursor.ShouldBe(root.Cursor); resumed.Source.Record.Body.ToArray().ShouldBe(Source(1).Record.Body.ToArray());
        await reopened.AdvanceDirectoryParentRootAsync(queued, draft, proof, resumed, reopened.ReadDirectoryGraphMemberPage(resumed.Source.SourceId, resumed.Cursor, 1), Token);
        reopened.ReadNextDirectoryParentRoot(proof).ShouldBeNull(); reopened.ReadProvenDirectoryParentClock(queued)[1].ShouldBe(2ul);
        reopened.ReadDirectoryMetadataSource(Source(2, 1).SourceId).ShouldBeNull("vector forwarding must reuse the protected representative");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectChildReadinessAndTombstoneChangesInvalidateEvenAnUnchangedLiveInputHash(bool tombstone)
    {
        await using var index = await Open(); var (queued, draft) = await Prepare(index);
        var child = new LocalFileRow("topic/child", 0, tombstone ? 0 : 1, Now, 0, tombstone, Now,
            tombstone ? null : Id(4), new Dictionary<ulong, ulong> { [1] = 1 }, new([8], new byte[64]), tombstone ? null : Native) { ContentReady = !tombstone };
        await index.LocalFiles.UpsertAsync([child], Token); (queued, draft) = await Rebind(index, queued, draft);
        await Graph(index, Source(1), 2);
        var proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: Token)).Proof;
        proof = await Discover(index, queued, draft, proof); var root = index.ReadNextDirectoryParentRoot(proof)!;
        await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, root, index.ReadDirectoryGraphMemberPage(root.Source.SourceId, limit: 1), Token);
        var saved = index.LocalFiles.Find(child.Path)!;
        await index.LocalFiles.UpsertAsync([tombstone ? saved with { Vector = new Dictionary<ulong, ulong> { [1] = 2 }, Record = new([9], new byte[64]) } : saved with { ContentReady = false }], Token);
        Should.Throw<InvalidOperationException>(() => index.ReadNextDirectoryParentRoot(proof));
        (queued, draft) = await Rebind(index, queued, draft);
        proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now + 1, cancellationToken: Token)).Proof;
        var invalidated = index.ReadNextDirectoryParentRoot(proof)!;
        invalidated.Cursor.ShouldBeNull(); invalidated.Complete.ShouldBeFalse(); proof.ChildEpoch.ShouldBeGreaterThan(0);
        draft.InputHash.ShouldBe(Id(10));
    }

    [Fact]
    public async Task DiscoveryIsPagedAndRemoteRevisionRestartsItWithoutLosingTreeProgress()
    {
        await using var index = await Open(); var (queued, draft) = await Prepare(index);
        await index.RemoteFiles.UpsertAsync(Enumerable.Range(2, 70).Select(n => Remote(n, n)).ToArray(), Token);
        await Graph(index, Source(1), 2);
        var proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: Token)).Proof;
        var first = index.ReadDirectoryParentPage(proof); first.Rows.Count.ShouldBe(32); first.More.ShouldBeTrue();
        proof = await index.AdvanceDirectoryParentDiscoveryAsync(queued, draft, proof, first, first.Rows.Select(r => SourceFor(r, 2)).ToArray(), Now, Token);
        proof.DiscoveryComplete.ShouldBeFalse(); proof.DiscoveryAfter.ShouldBe(Id(33));
        while (!proof.DiscoveryComplete)
        {
            var page = index.ReadDirectoryParentPage(proof); page.Rows.Count.ShouldBeLessThanOrEqualTo(32);
            proof = await index.AdvanceDirectoryParentDiscoveryAsync(queued, draft, proof, page, page.Rows.Select(r => SourceFor(r, 2)).ToArray(), Now, Token);
        }
        var original = index.ReadNextDirectoryParentRoot(proof)!;
        await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, original, index.ReadDirectoryGraphMemberPage(original.Source.SourceId, limit: 1), Token);
        await index.RemoteFiles.UpsertAsync([Remote(2, 999)], Token);
        Should.Throw<InvalidOperationException>(() => index.ReadNextDirectoryParentRoot(proof));
        var changed = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now + 1, cancellationToken: Token)).Proof;
        changed.Generation.ShouldBeGreaterThan(proof.Generation); changed.DiscoveryAfter.ShouldBeNull();
        var pageAfterChange = index.ReadDirectoryParentPage(changed);
        pageAfterChange.Rows[0].Record.Body.ToArray().ShouldBe(Source(999).Record.Body.ToArray());
        await Should.ThrowAsync<InvalidOperationException>(() => index.AdvanceDirectoryParentDiscoveryAsync(queued, draft, proof, first, [], Now, Token));
        changed = await Discover(index, queued, draft, changed);
        index.ReadNextDirectoryParentRoot(changed)!.Cursor.ShouldBe("TOPIC/0000");
        index.ReadDirectoryParentSources(Root).Sources.Single(s => s.Hash.SequenceEqual(Id(2))).Record.Body.ToArray().ShouldBe(Source(2).Record.Body.ToArray());
    }

    [Fact]
    public async Task DiscoveryAdmissionRollsBackRepresentativesMetadataClockAndCursorTogether()
    {
        await using var index = await Open(); var (queued, draft) = await Prepare(index);
        await index.RemoteFiles.UpsertAsync([Remote(2, 2), Remote(3, 3)], Token);
        var proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: Token)).Proof;
        var page = index.ReadDirectoryParentPage(proof);
        Sql("CREATE TRIGGER fail_parent_root BEFORE INSERT ON directory_parent_roots WHEN NEW.node_hash=X'" + Convert.ToHexString(Id(3)) + "' BEGIN SELECT RAISE(ABORT,'parent root rollback'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.AdvanceDirectoryParentDiscoveryAsync(queued, draft, proof, page, [SourceFor(page.Rows[0], 2), SourceFor(page.Rows[1], 3)], Now, Token));
        index.ReadDirectoryParentSources(Root).Sources.Count.ShouldBe(1); index.ReadDirectoryMetadataSource(Source(2).SourceId).ShouldBeNull();
        var unchanged = index.ReadDirectoryParentProof("topic")!; unchanged.DiscoveryAfter.ShouldBeNull(); unchanged.DiscoveryComplete.ShouldBeFalse(); unchanged.Clock.Count.ShouldBe(1);
        Sql("DROP TRIGGER fail_parent_root;");
        proof = await index.AdvanceDirectoryParentDiscoveryAsync(queued, draft, proof, page, [SourceFor(page.Rows[0], 2), SourceFor(page.Rows[1], 3)], Now, Token);
        proof.DiscoveryComplete.ShouldBeTrue(); index.ReadDirectoryParentSources(Root).Sources.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RepresentativesPreventNewGraphRetirementAndStaleProofCleanupIsBounded()
    {
        await using var index = await Open(); var (queued, draft) = await Prepare(index); await Graph(index, Source(1), 0);
        var proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: Token)).Proof;
        (await index.PruneDirectoryGraphsAsync(Root, DateTime.MaxValue.Ticks, true, cancellationToken: Token)).Mutations.ShouldBe(0);
        index.ReadDirectoryGraph(Source(1).SourceId)!.Complete.ShouldBeTrue();
        proof = await Discover(index, queued, draft, proof); var root = index.ReadNextDirectoryParentRoot(proof)!;
        await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, root, [], Token);
        index.ReadProvenDirectoryParentClock(queued)[1].ShouldBe(1ul);
        await index.DiscardDirectoryPublicationAsync(queued, Token);
        (await index.PruneDirectoryParentProofsAsync(Root, Now + TimeSpan.TicksPerDay, limit: 1, cancellationToken: Token)).ShouldBe(1);
        index.ReadDirectoryParentSources(Root).Sources.ShouldBeEmpty(); index.ReadDirectoryParentProof("topic").ShouldNotBeNull();
        (await index.PruneDirectoryParentProofsAsync(Root, Now + TimeSpan.TicksPerDay, limit: 1, cancellationToken: Token)).ShouldBe(1);
        index.ReadDirectoryParentProof("topic").ShouldBeNull();
    }

    [Fact]
    public async Task FinalParentProofAndQueueReleaseRollBackWithThePublishedRow()
    {
        await using var index = await Open(); var (queued, draft) = await Prepare(index); await Graph(index, Source(1), 0);
        var proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: Token)).Proof;
        proof = await Discover(index, queued, draft, proof);
        await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, index.ReadNextDirectoryParentRoot(proof)!, [], Token);
        var original = index.LocalFiles.Find("topic")!; var publication = original with { Record = Source(8).Record, Vector = new Dictionary<ulong, ulong> { [1] = 2 } };
        Sql("CREATE TRIGGER fail_parent_release BEFORE DELETE ON directory_parent_proofs BEGIN SELECT RAISE(ABORT,'parent release rollback'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.CommitDirectoryPublicationAsync(queued, draft, publication, Now, Token));
        index.LocalFiles.Find("topic")!.LocalSequence.ShouldBe(original.LocalSequence); index.ReadDirectoryPublication("topic").ShouldNotBeNull();
        index.ReadProvenDirectoryParentClock(queued)[1].ShouldBe(1ul); index.HasDirectoryParentSources(Root).ShouldBeTrue();
        Sql("DROP TRIGGER fail_parent_release;");
        (await index.CommitDirectoryPublicationAsync(queued, draft, publication, Now, Token)).Changed.ShouldBeTrue();
        index.ReadDirectoryParentProof("topic").ShouldBeNull(); index.ReadDirectoryPublication("topic").ShouldBeNull(); index.HasDirectoryParentSources(Root).ShouldBeFalse();
    }

    [Fact]
    public async Task ANewParentRevisionCannotUseACompleteOldRootSetOrCommitItsClock()
    {
        await using var index = await Open(); var (queued, draft) = await Prepare(index); await Graph(index, Source(1), 0);
        var proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: Token)).Proof;
        proof = await Discover(index, queued, draft, proof); await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, index.ReadNextDirectoryParentRoot(proof)!, [], Token);
        await index.RemoteFiles.UpsertAsync([Remote(2, 2)], Token);
        Should.Throw<InvalidOperationException>(() => index.ReadProvenDirectoryParentClock(queued));
        var publication = index.LocalFiles.Find("topic")! with { Record = Source(8).Record };
        await Should.ThrowAsync<InvalidOperationException>(() => index.CommitDirectoryPublicationAsync(queued, draft, publication, Now, Token));
        index.ReadDirectoryPublication("topic").ShouldNotBeNull(); index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(Source(1).Record.Body.ToArray());
    }

    [Fact]
    public async Task NativeRebindingRetiresOldRepresentativesInBoundedPrefixesBeforeReusingTheSlot()
    {
        await using var index = await Open(); var (queued, draft) = await Prepare(index);
        await index.RemoteFiles.UpsertAsync([Remote(2, 2), Remote(3, 3)], Token); await Graph(index, Source(1), 2);
        var proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: Token)).Proof;
        var page = index.ReadDirectoryParentPage(proof);
        proof = await index.AdvanceDirectoryParentDiscoveryAsync(queued, draft, proof, page, [SourceFor(page.Rows[0], 2), SourceFor(page.Rows[1], 3)], Now, Token);
        var root = index.ReadNextDirectoryParentRoot(proof)!;
        await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, root, index.ReadDirectoryGraphMemberPage(root.Source.SourceId, limit: 1), Token);
        byte[] replacement = Id(44)[..16];
        queued = await index.StageDirectoryPublicationAsync(queued with { Version = Guid.NewGuid(), NativeId = replacement }, Token);
        draft = await index.BeginDirectoryCaptureAsync("topic", Root, replacement, Id(10), 0, new byte[16], Token);
        draft = await index.AdvanceDirectoryCaptureAsync(draft, 0, null, null, Id(12), 12, Token); (queued, draft) = await Rebind(index, queued, draft);
        for (int pass = 0; pass < 3; pass++)
        {
            var prepared = await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, maximumResetWork: 1, cancellationToken: Token);
            prepared.ResetWork.ShouldBe(1); prepared.Proof.Resetting.ShouldBe(pass < 2); proof = prepared.Proof;
            if (proof.Resetting) Should.Throw<InvalidOperationException>(() => index.ReadDirectoryParentPage(proof));
        }
        proof.NativeId.ShouldBe(replacement); proof = await Discover(index, queued, draft, proof);
        index.ReadNextDirectoryParentRoot(proof)!.Cursor.ShouldBeNull();
    }

    [Fact]
    public async Task MoreThanOneThousandMembersRequireEveryExactRemainingPrefix()
    {
        await using var index = await Open(); var (queued, draft) = await Prepare(index); await Graph(index, Source(1), 1005);
        var proof = (await index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: Token)).Proof;
        proof = await Discover(index, queued, draft, proof); var root = index.ReadNextDirectoryParentRoot(proof)!;
        var first = index.ReadDirectoryGraphMemberPage(root.Source.SourceId); first.Count.ShouldBe(1000);
        var prefix = await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, root, first, Token);
        prefix.Complete.ShouldBeFalse(); prefix.Cursor.ShouldBe("TOPIC/0999");
        Should.Throw<InvalidOperationException>(() => index.ReadProvenDirectoryParentClock(queued));
        await Should.ThrowAsync<InvalidOperationException>(() => index.AdvanceDirectoryParentRootAsync(queued, draft, proof, prefix, first.Take(1).ToArray(), Token));
        var remaining = index.ReadDirectoryGraphMemberPage(root.Source.SourceId, prefix.Cursor); remaining.Count.ShouldBe(5);
        (await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, prefix, remaining, Token)).Complete.ShouldBeTrue();
        index.ReadProvenDirectoryParentClock(queued)[1].ShouldBe(1ul);
    }

    [Fact]
    public async Task ParentInputsAreFrozenAndCancellationCannotCreatePartialHolds()
    {
        await using var index = await Open(); var (queued, draft) = await Prepare(index);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.BeginDirectoryParentProofAsync(queued, draft, Source(1), Id(9), Now, cancellationToken: stop.Token));
        index.ReadDirectoryParentProof("topic").ShouldBeNull(); index.HasDirectoryParentSources(Root).ShouldBeFalse();
        var source = Source(1); byte[] metadata = Id(9);
        var begin = index.BeginDirectoryParentProofAsync(queued, draft, source, metadata, Now, cancellationToken: Token);
        source.Hash[0] = 99; source.RouteHint[0] = 99; metadata[0] = 99; var proof = (await begin).Proof;
        proof.MetadataHash.ShouldBe(Id(9)); proof.LocalHash.ShouldBe(Id(1));
        index.ReadDirectoryParentSources(Root).Sources.Single().RouteHint.ShouldBe(Id(1));
        Should.Throw<InvalidOperationException>(() => index.ReadDirectoryParentPage(proof with { Clock = new Dictionary<ulong, ulong> { [1] = 999 } }));
        proof.ToString().ShouldNotContain("TOPIC");
    }

    private static async Task<(DirectoryPublication, DirectoryCaptureState)> Prepare(FolderIndex index)
    {
        var source = Source(1); await index.LocalFiles.UpsertAsync([new("topic", 1, 0, Now, 16, false, Now, null, new Dictionary<ulong, ulong> { [1] = 1 }, source.Record, Native)], Token);
        var queued = await index.StageDirectoryPublicationAsync(new(Guid.NewGuid(), "topic", Root, Native, index.LocalFiles.Find("topic")!.LocalSequence, 16, source.Record, Id(11), 0, Now, Now), Token);
        var draft = await index.BeginDirectoryCaptureAsync("topic", Root, Native, Id(10), 0, new byte[16], Token);
        draft = await index.AdvanceDirectoryCaptureAsync(draft, 0, null, null, Id(12), 12, Token);
        return await Rebind(index, queued, draft);
    }
    private static async Task<(DirectoryPublication, DirectoryCaptureState)> Rebind(FolderIndex index, DirectoryPublication queued, DirectoryCaptureState draft)
    {
        long epoch = index.ReadDirectoryCaptureEpoch();
        return (await index.BindDirectoryPublicationKeysAsync(queued, epoch, Now, Token), await index.BindDirectoryCaptureEpochAsync(draft, epoch, Token));
    }
    private static async Task<DirectoryParentProof> Discover(FolderIndex index, DirectoryPublication queued, DirectoryCaptureState draft, DirectoryParentProof proof)
    {
        while (!proof.DiscoveryComplete)
        {
            var page = index.ReadDirectoryParentPage(proof);
            proof = await index.AdvanceDirectoryParentDiscoveryAsync(queued, draft, proof, page, page.Rows.Select(r => SourceFor(r, 2)).ToArray(), Now, Token);
        }
        return proof;
    }
    private async Task Graph(FolderIndex index, DirectoryMetadataSource source, int members)
    {
        await index.BeginDirectoryGraphAsync(source.SourceId, Root, source.Path, source.Hash, source.Length, Token);
        await index.CompleteDirectoryGraphWorkAsync(source.SourceId, index.ReadDirectoryGraphWork(source.SourceId)!.Id, [], cancellationToken: Token);
        using var connection = Connect(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO directory_graph_members(graph_id,path_key,record_hash,record_length) VALUES($source,$path,$hash,48);";
        command.Parameters.AddWithValue("$source", source.SourceId); command.Parameters.AddWithValue("$hash", Id(20)); command.Parameters.Add("$path", SqliteType.Text);
        for (int n = 0; n < members; n++) { command.Parameters["$path"].Value = "TOPIC/" + n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture); command.ExecuteNonQuery(); }
        transaction.Commit();
    }
    private static DirectoryMetadataSource Source(int value, int hash = -1) => SourceFor(new(Id(value), "TOPIC", new Dictionary<ulong, ulong> { [(ulong)value] = 1 }, new(BitConverter.GetBytes(value), new byte[64])), hash < 0 ? value : hash);
    private static DirectoryMetadataSource SourceFor(RemoteFileRow row, int hash)
    {
        using var digest = Blake3.Hasher.New(); digest.Update("PeerSync case source v1"u8); digest.Update(row.Record.Body); digest.Update(row.Record.Signature);
        return new(digest.Finalize().AsSpan().ToArray(), "topic", row.Record, Id(hash), 12, row.DeviceId);
    }
    private static RemoteFileRow Remote(int device, int body) => new(Id(device), "TOPIC", new Dictionary<ulong, ulong> { [(ulong)device] = 1 }, Source(body).Record);
    private static byte[] Id(int value) { byte[] bytes = new byte[32]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }
    private async Task<FolderIndex> Open() { var index = await FolderIndex.OpenAsync(Database, Token); await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; }
    private SqliteConnection Connect() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); c.Open(); return c; }
    private void Sql(string sql) { using var c = Connect(); using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-ParentProofDb-", StringComparison.Ordinal) || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Parent proof cleanup escaped its generated fixture.");
        Directory.Delete(resolved, true);
    }
}
