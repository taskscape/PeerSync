using System.Buffers.Binary;
using Google.Protobuf;
using PeerSync.Crypto;
using PeerSync.Protocol.Directories;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using Shouldly;
using Xunit;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Protocol.Tests;

public sealed class DirectorySnapshotTests
{
    [Fact]
    public void EmptyDirectoryHasOneCanonicalGoldenNodeButLegacyAbsenceIsDifferent()
    {
        byte[] expected = [0x50, 0x53, 0x44, 0x53, 1, 1, 0, 0, 0, 0, 0, 0];
        var node = DirectorySnapshotNode.Entries([]); node.Encode().ShouldBe(expected);
        node.CalculateHash().ShouldBe(Blake3.Hasher.Hash(expected).AsSpan().ToArray());
        var decoded = DirectorySnapshotNode.Decode(expected); decoded.Kind.ShouldBe(DirectorySnapshotNodeKind.Entries); decoded.Links.ShouldBeEmpty();
        using var keys = FolderKeys.CreateNew(); var body = Directory(); SignedRecordFactory.TryOpen(SignedRecordFactory.Create(body, keys), keys, out var legacy).ShouldBeTrue();
        legacy!.DirectorySnapshotHash.ShouldBe(ByteString.Empty); legacy.DirectorySnapshotLength.ShouldBe(0u);
    }
    [Fact]
    public void PageFactoriesSortAndFreezeCallerBuffersWhileWireDecoderRejectsNoncanonicalOrder()
    {
        byte[] hash = Hash(2); var second = new DirectorySnapshotLink(hash, 12); hash[0] = 99;
        DirectorySnapshotLink[] input = [second, Link(1, 48)]; var node = DirectorySnapshotNode.Entries(input); input[0] = Link(3, 12);
        node.Links[0].Hash.ShouldBe(Hash(1)); node.Links[1].Hash.ShouldBe(Hash(2));
        node.Links[0].Hash[0] = 99; node.Links[0].Hash.ShouldBe(Hash(1));
        byte[] bytes = node.Encode(); byte[] first = bytes.AsSpan(12, 36).ToArray(); bytes.AsSpan(48, 36).CopyTo(bytes.AsSpan(12)); first.CopyTo(bytes, 48);
        Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Decode(bytes));
        Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Entries([Link(1, 12), Link(1, 12)]));
        Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Branch([]));
    }
    [Fact]
    public void PageBoundsAreCheckedBeforeEnumerationAndMaximalBranchRoundTrips()
    {
        var links = Enumerable.Range(1, 128).Select(value => Link((byte)value, 12)).ToArray();
        var node = DirectorySnapshotNode.Branch(links); node.EncodedLength.ShouldBe(DirectorySnapshotNode.MaximumLinksNodeBytes);
        DirectorySnapshotNode.Decode(node.Encode()).Encode().ShouldBe(node.Encode());
        Should.Throw<ArgumentException>(() => DirectorySnapshotNode.Entries([.. links, Link(129, 12)]));
        Should.Throw<ArgumentException>(() => new DirectorySnapshotLink(new byte[31], 12));
        Should.Throw<ArgumentException>(() => new DirectorySnapshotLink(Hash(1), 11));
        Should.Throw<ArgumentException>(() => new DirectorySnapshotLink(Hash(1), DirectorySnapshotNode.MaximumNodeBytes + 1));
    }
    [Fact]
    public void MaximumBlobIsOwnedAndImmutableAcrossEncodeDecode()
    {
        byte[] original = new byte[DirectorySnapshotNode.MaximumBlobBytes]; original[0] = 17; original[^1] = 29;
        var node = DirectorySnapshotNode.Blob(original); original[0] = 99; var bytes = node.Encode(); var decoded = DirectorySnapshotNode.Decode(bytes); bytes[^1] = 98;
        decoded.BlobPayload[0].ShouldBe((byte)17); decoded.BlobPayload[^1].ShouldBe((byte)29); decoded.EncodedLength.ShouldBe(DirectorySnapshotNode.MaximumNodeBytes);
        Should.Throw<ArgumentOutOfRangeException>(() => DirectorySnapshotNode.Blob([]));
        Should.Throw<ArgumentOutOfRangeException>(() => DirectorySnapshotNode.Blob(new byte[DirectorySnapshotNode.MaximumBlobBytes + 1]));
        node.ToString().ShouldBe("Directory snapshot node (redacted)"); node.Links.ShouldBeEmpty();
    }
    [Fact]
    public void RecordFragmentsKeepOrderAllowRepeatedHashesAndUseExactCanonicalLengths()
    {
        int total = DirectorySnapshotNode.MaximumEnvelopeBytes;
        var fragments = Enumerable.Range(0, DirectorySnapshotNode.MaximumRecordLinks).Select(i => Link(7,
            12 + Math.Min(DirectorySnapshotNode.MaximumBlobBytes, total - i * DirectorySnapshotNode.MaximumBlobBytes))).ToArray();
        var node = DirectorySnapshotNode.Record(fragments, total); node.Links.Count.ShouldBe(17);
        DirectorySnapshotNode.Decode(node.Encode()).Encode().ShouldBe(node.Encode());
        Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Record([Link(1, 81)], 70));
        Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Record([], 69));
        Should.Throw<ArgumentOutOfRangeException>(() => DirectorySnapshotNode.Record([], total + 1));
        Should.Throw<ArgumentOutOfRangeException>(() => DirectorySnapshotNode.Record([], 68));
        var ordered = DirectorySnapshotNode.Record([Link(9, 12 + DirectorySnapshotNode.MaximumBlobBytes), Link(1, 12 + 1)], DirectorySnapshotNode.MaximumBlobBytes + 1);
        DirectorySnapshotNode.Decode(ordered.Encode()).Links[0].Hash.ShouldBe(Hash(9));
    }
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 2)]
    [InlineData(5, 0)]
    [InlineData(5, 255)]
    [InlineData(6, 255)]
    [InlineData(8, 1)]
    [InlineData(11, 255)]
    public void MalformedHeaderFieldsCannotAllocateOrNormalizeIntoValidPages(int offset, byte value)
    {
        byte[] bytes = DirectorySnapshotNode.Entries([]).Encode(); bytes[offset] = value;
        Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Decode(bytes));
    }
    [Fact]
    public void EveryTruncationAndTrailingByteIsRejectedBeforeOpeningAnyNode()
    {
        byte[] bytes = DirectorySnapshotNode.Branch([Link(1, 12)]).Encode();
        for (int length = 0; length < bytes.Length; length++) { byte[] truncated = bytes[..length]; Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Decode(truncated)); }
        Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Decode([.. bytes, 0]));
        byte[] blob = DirectorySnapshotNode.Blob([1]).Encode(); blob[6] = 1; Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Decode(blob));
        byte[] record = DirectorySnapshotNode.Record([Link(1, 81)], 69).Encode(); record[48 - 4]++; Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Decode(record));
    }
    [Fact]
    public void OpenVerifiesFullEncodedHashAndExactLengthBeforeDecoding()
    {
        var node = DirectorySnapshotNode.Blob([1, 2, 3]); byte[] bytes = node.Encode(); var link = new DirectorySnapshotLink(node.CalculateHash(), bytes.Length);
        DirectorySnapshotNode.Open(link, bytes).Encode().ShouldBe(bytes);
        for (int i = 0; i < bytes.Length; i++) { bytes[i] ^= 1; Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Open(link, bytes)); bytes[i] ^= 1; }
        Should.Throw<InvalidDataException>(() => DirectorySnapshotNode.Open(new(link.Hash, bytes.Length + 1), bytes));
    }
    [Fact]
    public void EnvelopePreservesUnknownFieldOrderOriginalSignatureAndDropsOnlyLocalSequence()
    {
        using var keys = FolderKeys.CreateNew(); byte[] body = [0x98, 0x06, 1, .. Directory().ToByteArray()];
        var original = new Envelope(body, keys.Sign([.. "psp1 record"u8, .. keys.FolderId(), .. body]), 999);
        var encoded = DirectorySnapshotEnvelope.Encode(original); var decoded = DirectorySnapshotEnvelope.Decode(encoded); encoded[4] ^= 1;
        decoded.Body.ToArray().ShouldBe(original.Body.ToArray()); decoded.Signature.ToArray().ShouldBe(original.Signature.ToArray()); decoded.LocalSequence.ShouldBe(0ul);
        SignedRecordFactory.TryOpen(decoded, keys, out var parsed).ShouldBeTrue(); parsed!.ToByteArray().SequenceEqual(decoded.Body).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => DirectorySnapshotEnvelope.Encode(new([], new byte[64])));
        byte[] invalid = DirectorySnapshotEnvelope.Encode(original); BinaryPrimitives.WriteUInt32LittleEndian(invalid, uint.MaxValue);
        Should.Throw<InvalidDataException>(() => DirectorySnapshotEnvelope.Decode(invalid));
        Should.Throw<InvalidDataException>(() => DirectorySnapshotEnvelope.Decode(invalid.AsSpan(0, 68)));
    }
    [Fact]
    public void DirectoryRootHashAndLengthAreSignedTogetherAndInvalidPairsFailAfterAuthentication()
    {
        using var keys = FolderKeys.CreateNew(); var root = DirectorySnapshotNode.Entries([]); var body = Directory();
        body.DirectorySnapshotHash = ByteString.CopyFrom(root.CalculateHash()); body.DirectorySnapshotLength = (uint)root.EncodedLength;
        var record = SignedRecordFactory.Create(body, keys); SignedRecordFactory.TryOpen(record, keys, out var parsed).ShouldBeTrue();
        parsed!.DirectorySnapshotHash.ShouldBe(body.DirectorySnapshotHash); parsed.DirectorySnapshotLength.ShouldBe(12u);
        var bad = body.Clone(); bad.DirectorySnapshotLength = 0; Reject(bad);
        bad = body.Clone(); bad.DirectorySnapshotHash = ByteString.Empty; Reject(bad);
        bad = body.Clone(); bad.DirectorySnapshotLength = 13; Reject(bad);
        bad = body.Clone(); bad.DirectorySnapshotHash = ByteString.CopyFrom(new byte[31]); Reject(bad);
        bad = body.Clone(); bad.Type = 0; Reject(bad); bad = body.Clone(); bad.Deleted = true; Reject(bad);
        bad = body.Clone(); bad.DirectorySnapshotLength = uint.MaxValue; Reject(bad);
        byte[] changed = record.Body.ToArray(); changed[^1] ^= 1; SignedRecordFactory.TryOpen(new(changed, record.Signature), keys, out _).ShouldBeFalse();
        var changedRoot = body.Clone(); byte[] hash = changedRoot.DirectorySnapshotHash.ToByteArray(); hash[0] ^= 1;
        changedRoot.DirectorySnapshotHash = ByteString.CopyFrom(hash); SignedRecordFactory.TryOpen(new(changedRoot.ToByteArray(), record.Signature), keys, out _).ShouldBeFalse();
        void Reject(FileRecordBody candidate)
        {
            Should.Throw<ArgumentException>(() => SignedRecordFactory.Create(candidate, keys)); byte[] bytes = candidate.ToByteArray();
            SignedRecordFactory.TryOpen(new(bytes, keys.Sign([.. "psp1 record"u8, .. keys.FolderId(), .. bytes])), keys, out _).ShouldBeFalse();
        }
    }
    [Fact]
    public void ArbitraryUntrustedBytesHaveOnlyBoundedFormatFailures()
    {
        var random = new Random(472901);
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested(); byte[] bytes = new byte[random.Next(2048)]; random.NextBytes(bytes);
            try { var node = DirectorySnapshotNode.Decode(bytes); node.Encode().ShouldBe(bytes); } catch (InvalidDataException) { }
            try { var record = DirectorySnapshotEnvelope.Decode(bytes); DirectorySnapshotEnvelope.Encode(record).ShouldBe(bytes); } catch (InvalidDataException) { }
        }
    }
    private static DirectorySnapshotLink Link(byte value, int length) => new(Hash(value), length);
    private static byte[] Hash(byte value) { byte[] hash = new byte[32]; hash[0] = value; return hash; }
    private static FileRecordBody Directory() => new() { Path = "topic", Type = 1, Vv = { new VvEntry { Device = 1, Counter = 1 } } };
}
