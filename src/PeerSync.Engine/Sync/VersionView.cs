using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Crypto.Chunking;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Sync;

/// <summary>A verified immutable view. Explicit body access returns a clone.</summary>
public sealed class VersionView
{
    private readonly FileRecordBody value;
    private readonly byte[] source;
    private VersionView(Envelope record, FileRecordBody value, byte[] source)
    {
        Record = record; this.value = value; this.source = source.ToArray();
        Vector = new ReadOnlyDictionary<ulong, ulong>(value.Vv.ToDictionary(v => v.Device, v => v.Counter));
    }
    [JsonIgnore] public Envelope Record { get; }
    [JsonIgnore] public IReadOnlyDictionary<ulong, ulong> Vector { get; }
    [JsonIgnore] public ReadOnlySpan<byte> SourceDevice => source;
    [JsonIgnore] public ReadOnlySpan<byte> ModifiedBy => value.ModifiedBy.Span;
    [JsonIgnore] public ReadOnlySpan<byte> FileHash => value.FileHash.Span;
    [JsonIgnore] public string Path => value.Path;
    public uint Type => value.Type;
    public bool Deleted => value.Deleted;
    public long Size => checked((long)value.Size);
    public long MtimeTicks => value.MtimeTicks;
    internal uint Attributes => value.Attributes;
    public long ChangedTicks => value.ChangedTicks;
    public long EffectiveChangeTicks => Deleted ? ChangedTicks : MtimeTicks;
    public FileRecordBody ExportBody() => value.Clone();

    public static VersionView Open(Envelope record, FolderKeys keys, byte[] sourceDevice)
    {
        ArgumentNullException.ThrowIfNull(sourceDevice);
        if (sourceDevice.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(sourceDevice));
        if (!SignedRecordFactory.TryOpen(record, keys, out var body)) throw new CryptographicException("Invalid signed file record.");
        if (body.Type > 2 || (body.Attributes & ~(uint)(FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0
            || body.Size > long.MaxValue || body.MtimeTicks < 0 || body.MtimeTicks > DateTime.MaxValue.Ticks
            || body.ChangedTicks < 0 || body.ChangedTicks > DateTime.MaxValue.Ticks || body.ModifiedBy.Length != 32
            || body.Vv.Count == 0 || !body.Vv.Any(v => v.Device == BinaryPrimitives.ReadUInt64LittleEndian(body.ModifiedBy.Span)))
            throw new InvalidDataException("Invalid signed version metadata.");
        if (body.Deleted || body.Type != 0)
        {
            if (body.Size != 0 || body.FileHash.Length != 0 || body.Chunks.Count != 0)
                throw new InvalidDataException("Non-file version contains file data.");
        }
        else
        {
            if (body.FileHash.Length != 32 || body.ChunkClass != ChunkParameters.ClassFor((long)body.Size))
                throw new InvalidDataException("Invalid file hash or chunk class.");
            long size = 0;
            int maximum = ChunkParameters.ForClass(body.ChunkClass).Max;
            foreach (var chunk in body.Chunks)
            {
                if (chunk.Length == 0 || chunk.Length > maximum || chunk.Hash.Length != 32)
                    throw new InvalidDataException("Invalid chunk metadata.");
                size = checked(size + chunk.Length);
            }
            if (size != (long)body.Size) throw new InvalidDataException("Chunk lengths do not match the file.");
        }
        return new(record, body, sourceDevice);
    }
    public override string ToString() => "Verified file version (path and payload redacted)";
}
