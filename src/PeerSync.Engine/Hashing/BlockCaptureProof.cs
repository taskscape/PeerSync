using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace PeerSync.Engine.Hashing;

// Fixed local format; no network parsing, filenames or key material in payload.
internal sealed record BlockCaptureProof(Guid Id, byte[] RootId, byte[] MarkerId, byte[] DataId, byte[] Hash, int Length, long CapturedTicks)
{
    internal const int Size = 156;
    internal const string Directory = ".psp/block-captures";
    internal string Name => Id.ToString("N") + ".capture";
    internal string Temporary => Id.ToString("N") + ".!psp";
    internal string Final => Convert.ToHexString(Hash);
    internal string Parent => ".psp/blocks/" + Final[..2];
    internal byte[] Encode()
    {
        byte[] bytes = new byte[Size]; "PSPBLK01"u8.CopyTo(bytes); RootId.CopyTo(bytes, 8); MarkerId.CopyTo(bytes, 32); DataId.CopyTo(bytes, 48);
        Hash.CopyTo(bytes, 64); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(96), Length); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(100), CapturedTicks);
        Id.TryWriteBytes(bytes.AsSpan(108, 16)); Blake3.Hasher.Hash(bytes.AsSpan(0, 124)).AsSpan().CopyTo(bytes.AsSpan(124)); return bytes;
    }
    internal static BlockCaptureProof Decode(ReadOnlySpan<byte> bytes, string name, ReadOnlySpan<byte> rootId, ReadOnlySpan<byte> markerId)
    {
        if (bytes.Length != Size || !bytes[..8].SequenceEqual("PSPBLK01"u8) || !bytes.Slice(8, 24).SequenceEqual(rootId)
            || !bytes.Slice(32, 16).SequenceEqual(markerId) || !Blake3.Hasher.Hash(bytes[..124]).AsSpan().SequenceEqual(bytes[124..]))
            throw new InvalidDataException("Unverified capture proof.");
        var proof = new BlockCaptureProof(new(bytes.Slice(108, 16)), bytes.Slice(8, 24).ToArray(), bytes.Slice(32, 16).ToArray(),
            bytes.Slice(48, 16).ToArray(), bytes.Slice(64, 32).ToArray(), BinaryPrimitives.ReadInt32LittleEndian(bytes[96..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[100..]));
        if (proof.Id == Guid.Empty || proof.Name != name || proof.Length is <= 0 or > PublishedBlocks.MaximumBlockBytes
            || proof.CapturedTicks <= 0 || proof.CapturedTicks > DateTime.MaxValue.Ticks) throw new InvalidDataException("Invalid capture ownership metadata.");
        return proof;
    }
    internal static byte[] Identity(SafeFileHandle file)
    {
        var snapshot = FileSnapshot.Read(file); byte[] bytes = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, snapshot.IdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), snapshot.IdHigh); return bytes;
    }
    public override string ToString() => "Block capture ownership (redacted)";
}
