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
