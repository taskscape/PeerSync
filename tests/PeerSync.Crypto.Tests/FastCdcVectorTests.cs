using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using PeerSync.Crypto.Chunking;
using Shouldly;
using Xunit;

namespace PeerSync.Crypto.Tests;

public sealed class FastCdcVectorTests
{
    [Fact]
    public async Task AllSizeClassesMatchIndependentGoldenBoundariesAcrossReadSizes()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fastcdc-vectors.json")));
        var root = fixture.RootElement;
        byte[] data = new byte[root.GetProperty("length").GetInt32()];
        Span<byte> input = stackalloc byte[26];
        "psp1 chunk fixture"u8.CopyTo(input);
        Span<byte> digest = stackalloc byte[32];
        for (int offset = 0; offset < data.Length; offset += 32)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(input[18..], (ulong)(offset / 32));
            SHA256.HashData(input, digest);
            digest[..Math.Min(32, data.Length - offset)].CopyTo(data.AsSpan(offset));
        }
        Convert.ToHexString(SHA256.HashData(data)).ShouldBe(root.GetProperty("sha256").GetString()!.ToUpperInvariant());
        foreach (var vector in root.GetProperty("vectors").EnumerateArray())
        foreach (int readSize in new[] { 4093, 65536, 1000000 })
        {
            uint chunkClass = vector.GetProperty("chunkClass").GetUInt32();
            int[] expected = vector.GetProperty("lengths").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var actual = new List<int>();
            await using var stream = new MemoryStream(data, writable: false);
            long total = await ChunkReader.ReadAsync(stream, chunkClass, chunk => actual.Add(chunk.Length), readSize, TestContext.Current.CancellationToken);
            total.ShouldBe(data.Length);
            actual.ShouldBe(expected);
        }
    }
}
