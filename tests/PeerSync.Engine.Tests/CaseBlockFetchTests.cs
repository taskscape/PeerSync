using PeerSync.Core.Diagnostics;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Fact]
    public async Task CasePayloadRoutesAllowTransferTimeForLargerBlocksOnSlowerLinks()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await index.Devices.UpsertAsync([new([2, .. new byte[31]])], Token);
        var pending = index.ReadPendingCasePreservations().Single(); var source = VersionView.Open(pending.Source.Record, keys, pending.Source.SourceDevice);
        byte[] bytes = new byte[256 * 1024]; Random.Shared.NextBytes(bytes); byte[] hash = Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
        using var routes = new FileApplier.CaseBlockFetch(new(Root, index, keys, device), pending, source, async (_, _, _, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1250), token); return bytes;
        }, Token);
        (await routes.FetchAsync(hash, bytes.Length, Token)).ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task CasePayloadRoutesSkipCorruptAndStalledPeersWithoutHoldingTheReferenceGate()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await index.Devices.UpsertAsync(Enumerable.Range(2, 3).Select(n => new DeviceRow([(byte)n, .. new byte[31]])).ToArray(), Token);
        var pending = index.ReadPendingCasePreservations().Single(); var source = VersionView.Open(pending.Source.Record, keys, pending.Source.SourceDevice);
        byte[] bytes = "generated route payload"u8.ToArray(), hash = Blake3.Hasher.Hash(bytes).AsSpan().ToArray(); var tried = new List<byte>();
        using var journal = new DiagnosticJournal(Path.Combine(fixture, "route-logs"), "case-route", debugEnabled: true);
        using var routes = new FileApplier.CaseBlockFetch(new(Root, index, keys, device, journal: journal), pending, source, async (peer, _, length, token) =>
        {
            using (await FolderReferenceGate.AcquireAsync(index, token)) { }
            tried.Add(peer.Span[0]);
            if (peer.Span[0] == 2) return new byte[length];
            if (peer.Span[0] == 3) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return bytes;
        }, Token);
        (await routes.FetchAsync(hash, bytes.Length, Token)).ToArray().ShouldBe(bytes); tried.ShouldBe(new byte[] { 2, 3, 4 });
        string log = File.ReadAllText(Path.Combine(fixture, "route-logs", "debug.jsonl"));
        log.ShouldContain("case-route-unavailable"); log.ShouldContain("case-route-timeout"); log.ShouldContain("case-route-failover");
        log.ShouldNotContain("topic"); log.ShouldNotContain(Convert.ToHexString(hash)); log.ShouldNotContain(keys.ExportText());
    }

    [Fact]
    public async Task CasePayloadAttemptUsesOneDevicePageAcrossManyReadsAndPropagatesCancellation()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await index.Devices.UpsertAsync(Enumerable.Range(2, 19).Select(n => new DeviceRow([(byte)n, .. new byte[31]])).ToArray(), Token);
        var pending = index.ReadPendingCasePreservations().Single(); var source = VersionView.Open(pending.Source.Record, keys, pending.Source.SourceDevice);
        byte[] bytes = "generated paged payload"u8.ToArray(), hash = Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
        using var routes = new FileApplier.CaseBlockFetch(new(Root, index, keys, device), pending, source,
            (_, _, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes), Token);
        for (int i = 0; i < 5; i++) (await routes.FetchAsync(hash, bytes.Length, Token)).ToArray().ShouldBe(bytes);
        var next = await index.AdvanceCaseBlockRoutesAsync(pending, Token); next.Devices[0][0].ShouldBe((byte)8); next.Hint![0].ShouldBe((byte)2);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await routes.FetchAsync(hash, bytes.Length, stop.Token));
    }

    [Fact]
    public async Task CasePayloadRoutesReachLaterPagesAcrossAttemptsThenRememberTheWorkingPeer()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); await AdmitDirectoryCase(index);
        await index.Devices.UpsertAsync(Enumerable.Range(2, 19).Select(n => new DeviceRow([(byte)n, .. new byte[31]])).ToArray(), Token);
        var pending = index.ReadPendingCasePreservations().Single(); var source = VersionView.Open(pending.Source.Record, keys, pending.Source.SourceDevice);
        byte[] bytes = "generated late route payload"u8.ToArray(), hash = Blake3.Hasher.Hash(bytes).AsSpan().ToArray(); var tried = new List<byte>();
        ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> peer, ReadOnlyMemory<byte> _, int length, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); tried.Add(peer.Span[0]); length.ShouldBe(bytes.Length);
            if (peer.Span[0] != 20) throw new IOException("Generated unavailable peer.");
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);
        }
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int before = tried.Count;
            using var routes = new FileApplier.CaseBlockFetch(new(Root, index, keys, device), pending, source, Fetch, Token);
            if (attempt < 3) await Should.ThrowAsync<IOException>(async () => await routes.FetchAsync(hash, bytes.Length, Token));
            else (await routes.FetchAsync(hash, bytes.Length, Token)).ToArray().ShouldBe(bytes);
            (tried.Count - before).ShouldBeLessThanOrEqualTo(6);
        }
        tried.ShouldBe(Enumerable.Range(2, 19).Select(n => (byte)n)); tried.Clear();
        using var remembered = new FileApplier.CaseBlockFetch(new(Root, index, keys, device), pending, source, Fetch, Token);
        (await remembered.FetchAsync(hash, bytes.Length, Token)).ToArray().ShouldBe(bytes); tried.ShouldBe(new byte[] { 20 });
        index.ReadPendingCasePreservations().Single().Source.SourceId.ShouldBe(pending.Source.SourceId);
    }
}
