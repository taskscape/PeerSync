using System.Security.Cryptography;
using PeerSync.Crypto.Chunking;
using Shouldly;
using Xunit;

namespace PeerSync.Crypto.Tests;

public sealed class ChunkingTests
{
    [Fact]
    public async Task BoundariesAreIndependentOfReadSize()
    {
        byte[] corpus = new byte[3 * 1024 * 1024 + 17];
        new Random(20261018).NextBytes(corpus);
        List<int>? expected = null;
        foreach (int size in new[] { 1, 7, 4093, 65536, 1000000 })
        {
            var lengths = new List<int>();
            using var whole = Blake3.Hasher.New();
            await using var input = new MemoryStream(corpus, writable: false);
            long total = await ChunkReader.ReadAsync(input, 0, chunk => { lengths.Add(chunk.Length); whole.Update(chunk); }, size, TestContext.Current.CancellationToken);
            total.ShouldBe(corpus.Length);
            if (expected is not null) lengths.ShouldBe(expected);
            expected = lengths;
            var actual = whole.Finalize();
            var reference = Blake3.Hasher.Hash(corpus);
            actual.AsSpan().ToArray().ShouldBe(reference.AsSpan().ToArray());
            lengths.Take(lengths.Count - 1).ShouldAllBe(n => n >= 16384 && n <= 262144);
        }
    }

    [Theory]
    [InlineData("", "AF1349B9F5F9A1A6A0404DEA36DCC9499BCB25C9ADC112B7CC9A93CAE41F3262")]
    [InlineData("abc", "6437B3AC38465133FFB63B75273A8DB548C558465D79DB03FD359C6CD5BD9D85")]
    public void Blake3MatchesPublishedVectors(string input, string expected)
    {
        var hash = Blake3.Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(input));
        Convert.ToHexString(hash.AsSpan()).ShouldBe(expected);
    }

    [Fact]
    public void SizeClassesHaveProtocolBoundaries()
    {
        ChunkParameters.ClassFor((256L << 20) - 1).ShouldBe(0u);
        ChunkParameters.ClassFor(256L << 20).ShouldBe(1u);
        ChunkParameters.ClassFor(8L << 30).ShouldBe(2u);
        Should.Throw<ArgumentOutOfRangeException>(() => ChunkParameters.ClassFor(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => ChunkParameters.ForClass(3));
    }

    [Fact]
    public async Task EmptyAndExactMaxSizedStreamsLoseNoData()
    {
        foreach (int length in new[] { 0, 1, 16384, 262144, 524288 })
        {
            byte[] data = RandomNumberGenerator.GetBytes(length);
            using var output = new MemoryStream();
            await using var input = new MemoryStream(data, writable: false);
            await ChunkReader.ReadAsync(input, 0, chunk => output.Write(chunk), cancellationToken: TestContext.Current.CancellationToken);
            output.ToArray().ShouldBe(data);
        }
    }
}
