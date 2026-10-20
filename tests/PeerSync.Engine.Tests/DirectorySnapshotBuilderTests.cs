using System.Buffers.Binary;
using PeerSync.Engine.Hashing;
using PeerSync.Protocol.Directories;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class DirectorySnapshotBuilderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(16384)]
    [InlineData(16385)]
    public async Task RestoredCheckpointsProduceTheExactUninterruptedRootAndNodeSequence(int count)
    {
        var uninterrupted = new List<string>(); var resumed = new List<string>();
        async Task<DirectorySnapshotLink> Build(bool restore, List<string> emitted)
        {
            Task<DirectorySnapshotLink> Write(DirectorySnapshotNode node, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); byte[] hash = node.CalculateHash(); emitted.Add(Convert.ToHexString(hash));
                return Task.FromResult(new DirectorySnapshotLink(hash, node.EncodedLength));
            }
            var builder = new DirectorySnapshotBuilder(Write);
            for (int value = 0; value < count; value++)
            {
                await builder.AddAsync(Record(value), Token);
                if (restore && value % 127 == 0)
                {
                    byte[] checkpoint = builder.ExportCheckpoint(); checkpoint.Length.ShouldBeLessThanOrEqualTo(DirectorySnapshotBuilder.MaximumCheckpointBytes);
                    builder = DirectorySnapshotBuilder.RestoreCheckpoint(Write, checkpoint); Array.Clear(checkpoint);
                }
            }
            if (restore) builder = DirectorySnapshotBuilder.RestoreCheckpoint(Write, builder.ExportCheckpoint());
            return await builder.CompleteAsync(Token);
        }
        var expected = await Build(false, uninterrupted); var actual = await Build(true, resumed);
        actual.Hash.ShouldBe(expected.Hash); actual.EncodedLength.ShouldBe(expected.EncodedLength); resumed.ShouldBe(uninterrupted);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("count")]
    [InlineData("carry")]
    [InlineData("record-length")]
    [InlineData("page-length")]
    [InlineData("trailing")]
    [InlineData("depth")]
    [InlineData("truncated")]
    public async Task CorruptCheckpointBoundsCountsAndLinksAreRejectedBeforeAnyWrites(string failure)
    {
        var builder = new DirectorySnapshotBuilder(Write);
        for (int value = 0; value < 129; value++) await builder.AddAsync(Record(value), Token);
        byte[] state = builder.ExportCheckpoint();
        switch (failure)
        {
            case "header": state[0] = 0; break;
            case "count": BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(8), ulong.MaxValue); break;
            case "carry": BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(16), 0); break;
            case "record-length": BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(18 + 32), 12); break;
            case "page-length": BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(18 + 36 + 32), 48); break;
            case "trailing": state = [.. state, 0]; break;
            case "depth": state[5] = 11; break;
            case "truncated": state = state[..^1]; break;
        }
        Should.Throw<InvalidDataException>(() => DirectorySnapshotBuilder.RestoreCheckpoint(
            static (_, _) => throw new InvalidOperationException("Restore must perform no writes."), state));
    }

    [Fact]
    public async Task FailedAdditionCannotBeCheckpointedAndLastSuccessfulStateResumes()
    {
        bool fail = false;
        var builder = new DirectorySnapshotBuilder((node, token) => fail ? throw new IOException("interrupted node write") : Write(node, token));
        for (int value = 0; value < 127; value++) await builder.AddAsync(Record(value), Token);
        byte[] durable = builder.ExportCheckpoint(); fail = true;
        await Should.ThrowAsync<IOException>(() => builder.AddAsync(Record(127), Token));
        Should.Throw<InvalidDataException>(() => builder.ExportCheckpoint());
        var recovered = DirectorySnapshotBuilder.RestoreCheckpoint(Write, durable);
        await recovered.AddAsync(Record(127), Token); (await recovered.CompleteAsync(Token)).EncodedLength.ShouldBe(DirectorySnapshotNode.MaximumLinksNodeBytes);
    }

    [Fact]
    public void BoundedRandomCheckpointsNeverLeakParserExceptionsOrAllocateFromUntrustedCounts()
    {
        var random = new Random(7329);
        for (int attempt = 0; attempt < 5000; attempt++)
        {
            byte[] bytes = new byte[random.Next(0, 300)]; random.NextBytes(bytes);
            try { _ = DirectorySnapshotBuilder.RestoreCheckpoint(Write, bytes); }
            catch (Exception error) { error.ShouldBeOfType<InvalidDataException>(); }
        }
        Should.Throw<InvalidDataException>(() => DirectorySnapshotBuilder.RestoreCheckpoint(Write, new byte[DirectorySnapshotBuilder.MaximumCheckpointBytes + 1]));
    }
    private static DirectorySnapshotLink Record(int value)
    {
        byte[] seed = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(seed, value);
        return new(Blake3.Hasher.Hash(seed).AsSpan(), 48);
    }
    private static Task<DirectorySnapshotLink> Write(DirectorySnapshotNode node, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(new DirectorySnapshotLink(node.CalculateHash(), node.EncodedLength)); }
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(128, 1)]
    [InlineData(129, 2)]
    [InlineData(16384, 2)]
    [InlineData(16385, 3)]
    public async Task StreamingPagesRetainEveryLeafExactlyOnceWithoutRedundantRoots(int count, int expectedDepth)
    {
        var pages = new Dictionary<string, DirectorySnapshotNode>(StringComparer.Ordinal); int supplied = 0; int firstWriteAt = -1;
        Task<DirectorySnapshotLink> Write(DirectorySnapshotNode node, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (firstWriteAt < 0) firstWriteAt = supplied;
            byte[] hash = node.CalculateHash(); pages.Add(Convert.ToHexString(hash), node); return Task.FromResult(new DirectorySnapshotLink(hash, node.EncodedLength));
        }
        var builder = new DirectorySnapshotBuilder(Write); var expected = new HashSet<string>(StringComparer.Ordinal);
        for (int value = 0; value < count; value++)
        {
            byte[] seed = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(seed, value);
            byte[] hash = Blake3.Hasher.Hash(seed).AsSpan().ToArray(); expected.Add(Convert.ToHexString(hash)); supplied++;
            await builder.AddAsync(new(hash, 48), Token);
        }
        var root = await builder.CompleteAsync(Token); firstWriteAt.ShouldBe(Math.Min(count, 128));
        var found = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        var work = new Stack<(DirectorySnapshotLink Link, int Depth)>(); work.Push((root, 1)); int depth = 0;
        while (work.TryPop(out var next))
        {
            string id = Convert.ToHexString(next.Link.Hash); visited.Add(id).ShouldBeTrue();
            var node = DirectorySnapshotNode.Open(next.Link, pages[id].Encode()); depth = Math.Max(depth, next.Depth);
            if (node.Kind == DirectorySnapshotNodeKind.Entries)
                foreach (var link in node.Links) found.Add(Convert.ToHexString(link.Hash)).ShouldBeTrue();
            else
            {
                node.Kind.ShouldBe(DirectorySnapshotNodeKind.Branch);
                foreach (var link in node.Links) work.Push((link, next.Depth + 1));
            }
        }
        found.SetEquals(expected).ShouldBeTrue(); visited.Count.ShouldBe(pages.Count); depth.ShouldBe(expectedDepth);
        await Should.ThrowAsync<ObjectDisposedException>(() => builder.CompleteAsync(Token));
        await Should.ThrowAsync<ObjectDisposedException>(() => builder.AddAsync(root, Token));
    }
    [Fact]
    public async Task CancellationBeforePartialTailPublicationDoesNotReturnACompleteRoot()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); int written = 0;
        var builder = new DirectorySnapshotBuilder((node, token) =>
        { token.ThrowIfCancellationRequested(); written++; return Task.FromResult(new DirectorySnapshotLink(node.CalculateHash(), node.EncodedLength)); });
        await builder.AddAsync(new(new byte[32], 48), Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => builder.CompleteAsync(stop.Token)); written.ShouldBe(0);
    }
}
