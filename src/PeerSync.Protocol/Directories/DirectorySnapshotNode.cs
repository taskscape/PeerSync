using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using PeerSync.Core.Sync;

namespace PeerSync.Protocol.Directories;

public enum DirectorySnapshotNodeKind : byte { Entries = 1, Branch = 2, Record = 3, Blob = 4 }

public sealed class DirectorySnapshotLink
{
    private readonly byte[] hash;
    [JsonIgnore] public byte[] Hash => hash.ToArray();
    public int EncodedLength { get; }
    internal ReadOnlySpan<byte> HashSpan => hash;
    public DirectorySnapshotLink(ReadOnlySpan<byte> hash, int encodedLength)
    {
        if (hash.Length != 32 || encodedLength < DirectorySnapshotNode.HeaderBytes || encodedLength > DirectorySnapshotNode.MaximumNodeBytes)
            throw new ArgumentException("Invalid directory snapshot link.");
        this.hash = hash.ToArray(); EncodedLength = encodedLength;
    }
    public override string ToString() => "Directory snapshot link (redacted)";
}

/// <summary>Bounded canonical metadata block. Parsing alone does not establish
/// directory membership, signatures, graph completeness or native ownership.</summary>
public sealed class DirectorySnapshotNode
{
    public const int HeaderBytes = 12, LinkBytes = 36, MaximumLinks = 128;
    public const int MaximumBlobBytes = 1024 * 1024;
    public const int MaximumNodeBytes = HeaderBytes + MaximumBlobBytes;
    public const int MaximumLinksNodeBytes = HeaderBytes + MaximumLinks * LinkBytes;
    public const int MaximumEnvelopeBytes = SignedRecord.MaxBodyBytes + 68;
    public const int MaximumRecordLinks = (MaximumEnvelopeBytes + MaximumBlobBytes - 1) / MaximumBlobBytes;
    private readonly byte[] payload;
    public DirectorySnapshotNodeKind Kind { get; }
    public int PayloadLength { get; }
    public IReadOnlyList<DirectorySnapshotLink> Links { get; }
    [JsonIgnore] public ReadOnlySpan<byte> BlobPayload => payload;
    public int EncodedLength => HeaderBytes + (Kind == DirectorySnapshotNodeKind.Blob ? payload.Length : Links.Count * LinkBytes);
    private DirectorySnapshotNode(DirectorySnapshotNodeKind kind, DirectorySnapshotLink[] links, int payloadLength, byte[] payload)
    {
        Kind = kind; Links = Array.AsReadOnly(links); PayloadLength = payloadLength; this.payload = payload; Validate();
    }
    public static DirectorySnapshotNode Entries(IReadOnlyList<DirectorySnapshotLink> links) => MakeLinks(DirectorySnapshotNodeKind.Entries, links);
    public static DirectorySnapshotNode Branch(IReadOnlyList<DirectorySnapshotLink> links) => MakeLinks(DirectorySnapshotNodeKind.Branch, links);
    public static DirectorySnapshotNode Record(IReadOnlyList<DirectorySnapshotLink> links, int envelopeLength)
    {
        if (envelopeLength < 69 || envelopeLength > MaximumEnvelopeBytes) throw new ArgumentOutOfRangeException(nameof(envelopeLength));
        return new(DirectorySnapshotNodeKind.Record, Freeze(links, MaximumRecordLinks), envelopeLength, []);
    }
    public static DirectorySnapshotNode Blob(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumBlobBytes) throw new ArgumentOutOfRangeException(nameof(payload));
        return new(DirectorySnapshotNodeKind.Blob, [], payload.Length, payload.ToArray());
    }
    private static DirectorySnapshotNode MakeLinks(DirectorySnapshotNodeKind kind, IReadOnlyList<DirectorySnapshotLink> links)
    {
        var frozen = Freeze(links, MaximumLinks);
        Array.Sort(frozen, static (a, b) => a.HashSpan.SequenceCompareTo(b.HashSpan));
        return new(kind, frozen, 0, []);
    }
    private static DirectorySnapshotLink[] Freeze(IReadOnlyList<DirectorySnapshotLink> links, int maximum)
    {
        ArgumentNullException.ThrowIfNull(links);
        int count = links.Count;
        if (count > maximum) throw new ArgumentException("Directory snapshot link count exceeds its bound.", nameof(links));
        var frozen = new DirectorySnapshotLink[count];
        for (int i = 0; i < count; i++) frozen[i] = links[i] ?? throw new ArgumentException("Missing directory snapshot link.", nameof(links));
        return frozen;
    }
    public byte[] Encode()
    {
        byte[] bytes = new byte[EncodedLength]; "PSDS"u8.CopyTo(bytes); bytes[4] = 1; bytes[5] = (byte)Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), checked((ushort)Links.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), checked((uint)PayloadLength));
        if (Kind == DirectorySnapshotNodeKind.Blob) payload.CopyTo(bytes, HeaderBytes);
        else
            for (int i = 0; i < Links.Count; i++)
            {
                int offset = HeaderBytes + i * LinkBytes; Links[i].HashSpan.CopyTo(bytes.AsSpan(offset));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 32), checked((uint)Links[i].EncodedLength));
            }
        return bytes;
    }
    public byte[] CalculateHash() => Blake3.Hasher.Hash(Encode()).AsSpan().ToArray();
    public static DirectorySnapshotNode Open(DirectorySnapshotLink expected, ReadOnlySpan<byte> encoded)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (encoded.Length != expected.EncodedLength || !CryptographicOperations.FixedTimeEquals(Blake3.Hasher.Hash(encoded).AsSpan(), expected.HashSpan))
            throw new InvalidDataException("Directory snapshot node hash or length mismatch.");
        return Decode(encoded);
    }
    public static DirectorySnapshotNode Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderBytes || encoded.Length > MaximumNodeBytes || !encoded[..4].SequenceEqual("PSDS"u8) || encoded[4] != 1
            || !Enum.IsDefined((DirectorySnapshotNodeKind)encoded[5])) throw new InvalidDataException("Invalid directory snapshot header.");
        var kind = (DirectorySnapshotNodeKind)encoded[5]; int count = BinaryPrimitives.ReadUInt16LittleEndian(encoded[6..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(encoded[8..]);
        if (kind == DirectorySnapshotNodeKind.Blob)
        {
            if (count != 0 || length is < 1 or > MaximumBlobBytes || encoded.Length != HeaderBytes + length)
                throw new InvalidDataException("Invalid directory snapshot blob bounds.");
            return Blob(encoded[HeaderBytes..]);
        }
        int maximum = kind == DirectorySnapshotNodeKind.Record ? MaximumRecordLinks : MaximumLinks;
        if (count > maximum || encoded.Length != HeaderBytes + count * LinkBytes
            || (kind == DirectorySnapshotNodeKind.Record ? length is < 69 or > MaximumEnvelopeBytes : length != 0))
            throw new InvalidDataException("Invalid directory snapshot link bounds.");
        var links = new DirectorySnapshotLink[count];
        for (int i = 0; i < count; i++)
        {
            int offset = HeaderBytes + i * LinkBytes; uint childLength = BinaryPrimitives.ReadUInt32LittleEndian(encoded[(offset + 32)..]);
            if (childLength < HeaderBytes || childLength > MaximumNodeBytes) throw new InvalidDataException("Invalid directory snapshot child length.");
            links[i] = new(encoded.Slice(offset, 32), (int)childLength);
        }
        return new(kind, links, (int)length, []);
    }
    private void Validate()
    {
        if (Kind == DirectorySnapshotNodeKind.Blob)
        {
            if (Links.Count != 0 || payload.Length != PayloadLength || PayloadLength is < 1 or > MaximumBlobBytes)
                throw new InvalidDataException("Invalid directory snapshot blob.");
            return;
        }
        if (Kind == DirectorySnapshotNodeKind.Record)
        {
            if (PayloadLength is < 69 or > MaximumEnvelopeBytes || Links.Count != (PayloadLength + MaximumBlobBytes - 1) / MaximumBlobBytes)
                throw new InvalidDataException("Invalid directory snapshot record fragments.");
            for (int i = 0; i < Links.Count; i++)
                if (Links[i].EncodedLength != HeaderBytes + Math.Min(MaximumBlobBytes, PayloadLength - i * MaximumBlobBytes))
                    throw new InvalidDataException("Invalid directory snapshot fragment length.");
            return;
        }
        if (Kind is not (DirectorySnapshotNodeKind.Entries or DirectorySnapshotNodeKind.Branch) || PayloadLength != 0
            || Links.Count > MaximumLinks || Kind == DirectorySnapshotNodeKind.Branch && Links.Count == 0)
            throw new InvalidDataException("Invalid directory snapshot page.");
        for (int i = 1; i < Links.Count; i++)
            if (Links[i - 1].HashSpan.SequenceCompareTo(Links[i].HashSpan) >= 0) throw new InvalidDataException("Directory snapshot links are not strictly ordered.");
    }
    public override string ToString() => "Directory snapshot node (redacted)";
}
