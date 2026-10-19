using CsCheck;
using PeerSync.Core.Sync;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class VersionVectorTests
{
    private static readonly Gen<Dictionary<ulong, ulong>> Vectors =
        Gen.Dictionary(Gen.ULong[0, 31], Gen.ULong)[0, 20];

    [Fact]
    public void CompareIsAntisymmetricAndMatchesComponentwiseOrder()
    {
        Gen.Select(Vectors, Vectors).Sample(pair =>
        {
            var (a, b) = pair;
            var ab = VersionVector.Compare(a, b);
            VersionVector.Compare(b, a).ShouldBe(ab switch
            {
                VvOrder.Dominates => VvOrder.DominatedBy,
                VvOrder.DominatedBy => VvOrder.Dominates,
                _ => ab,
            });
            var devices = a.Keys.Union(b.Keys).ToArray();
            bool ge = devices.All(d => a.GetValueOrDefault(d) >= b.GetValueOrDefault(d));
            bool le = devices.All(d => a.GetValueOrDefault(d) <= b.GetValueOrDefault(d));
            ab.ShouldBe(ge ? (le ? VvOrder.Equal : VvOrder.Dominates) : (le ? VvOrder.DominatedBy : VvOrder.Concurrent));
        }, iter: 10000);
    }

    [Fact]
    public void MergeIsCommutativeIdempotentAssociativeAndAnUpperBound()
    {
        Gen.Select(Vectors, Vectors, Vectors).Sample(tuple =>
        {
            var (a, b, c) = tuple;
            var merged = VersionVector.Merge(a, b);
            VersionVector.Serialize(merged).ShouldBe(VersionVector.Serialize(VersionVector.Merge(b, a)));
            VersionVector.Compare(a, VersionVector.Merge(a, a)).ShouldBe(VvOrder.Equal);
            VersionVector.Serialize(VersionVector.Merge(merged, c))
                .ShouldBe(VersionVector.Serialize(VersionVector.Merge(a, VersionVector.Merge(b, c))));
            VersionVector.Compare(merged, a).ShouldBeOneOf(VvOrder.Equal, VvOrder.Dominates);
            VersionVector.Compare(merged, b).ShouldBeOneOf(VvOrder.Equal, VvOrder.Dominates);
            foreach (ulong device in a.Keys.Union(b.Keys))
                merged.GetValueOrDefault(device).ShouldBe(Math.Max(a.GetValueOrDefault(device), b.GetValueOrDefault(device)));
        }, iter: 10000);
    }

    [Fact]
    public void IncrementChangesOnlySelfDominatesAndLeavesInputUnchanged()
    {
        Gen.Select(Vectors, Gen.ULong[0, 63]).Sample(tuple =>
        {
            var (vector, self) = tuple;
            byte[] before = VersionVector.Serialize(vector);
            if (vector.GetValueOrDefault(self) == ulong.MaxValue)
                Should.Throw<OverflowException>(() => VersionVector.Increment(vector, self));
            else
            {
                var next = VersionVector.Increment(vector, self);
                next[self].ShouldBe(vector.GetValueOrDefault(self) + 1);
                foreach (var (device, counter) in vector)
                    if (device != self) next.GetValueOrDefault(device).ShouldBe(counter);
                VersionVector.Compare(next, vector).ShouldBe(VvOrder.Dominates);
            }
            VersionVector.Serialize(vector).ShouldBe(before);
        }, iter: 10000);
    }

    [Fact]
    public void SerializationIsCanonicalAndRoundTrips()
    {
        Vectors.Sample(vector =>
        {
            byte[] serialized = VersionVector.Serialize(vector);
            VersionVector.Compare(vector, VersionVector.Deserialize(serialized)).ShouldBe(VvOrder.Equal);
            var reversed = vector.Reverse().ToDictionary(e => e.Key, e => e.Value);
            VersionVector.Serialize(reversed).ShouldBe(serialized);
            VersionVector.Serialize(VersionVector.Deserialize(serialized)).ShouldBe(serialized);
        }, iter: 10000);
    }

    [Fact]
    public void FixedEncodingIsSortedByUnsignedDeviceAndUsesMinimalVarints()
    {
        var input = new Dictionary<ulong, ulong> { [ulong.MaxValue] = 128, [1] = 1, [4] = 0 };
        Convert.ToHexString(VersionVector.Serialize(input)).ShouldBe("02010000000000000001FFFFFFFFFFFFFFFF8001");
        VersionVector.Serialize(new Dictionary<ulong, ulong>()).ShouldBe(new byte[] { 0 });
        var overflow = new Dictionary<ulong, ulong> { [0] = ulong.MaxValue };
        Should.Throw<OverflowException>(() => VersionVector.Increment(overflow, 0));
        VersionVector.Deserialize(VersionVector.Serialize(overflow))[0].ShouldBe(ulong.MaxValue);
        VersionVector.Compare(new Dictionary<ulong, ulong> { [7] = 0 }, new Dictionary<ulong, ulong>()).ShouldBe(VvOrder.Equal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("8000")]
    [InlineData("0000")]
    [InlineData("01000000000000000000")]
    [InlineData("0100000000000000008000")]
    [InlineData("010000000000000000FFFFFFFFFFFFFFFFFF02")]
    [InlineData("02010000000000000001010000000000000001")]
    [InlineData("02020000000000000001010000000000000001")]
    [InlineData("FFFFFFFFFFFFFFFFFF01")]
    public void MalformedAndNoncanonicalEncodingsAreRejected(string hex)
    {
        byte[] input = Convert.FromHexString(hex);
        VersionVector.TryDeserialize(input, out var result).ShouldBeFalse();
        result.ShouldBeNull();
        Should.Throw<FormatException>(() => VersionVector.Deserialize(input));
    }

    [Fact]
    public void ArbitraryBytesEitherRejectOrHaveAnIdenticalCanonicalReencoding()
    {
        Gen.Byte.Array[0, 100].Sample(bytes =>
        {
            if (VersionVector.TryDeserialize(bytes, out var vector))
                VersionVector.Serialize(vector!).ShouldBe(bytes);
        }, iter: 10000);
    }
}
