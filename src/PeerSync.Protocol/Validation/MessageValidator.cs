using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Crypto.Chunking;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Protocol.Wire;

namespace PeerSync.Protocol.Validation;

public static class MessageValidator
{
    public const int MaximumRecords = 1000, MaximumBlockRequests = 64;
    public const int MaximumBlockBytes = 8 * 1024 * 1024, MaximumBitmapBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Validate(IMessage message, FolderKeys? authenticatedFolder = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case Hello hello:
                Require(hello.ProtocolVersion == 1, MessageFailure.UnsupportedVersion);
                Require(hello.DeviceId.Length == 32 && hello.Nonce.Length == 32 && hello.UtcTicks > 0 && hello.UtcTicks <= DateTime.MaxValue.Ticks
                    && TextValid(hello.DeviceName, 128) && TextValid(hello.ClientVersion, 64), MessageFailure.InvalidHello);
                break;
            case FolderAuth auth:
                Require(auth.FolderId.Length == 32 && auth.Proof.Length == 32 && auth.Access switch
                {
                    FolderAccess.ReadWrite => auth.ReadOnlyProof.Length == 32 && auth.Signature.Length == 64,
                    FolderAccess.ReadOnly => auth.ReadOnlyProof.Length == 32 && auth.Signature.IsEmpty,
                    FolderAccess.Encrypted => auth.ReadOnlyProof.IsEmpty && auth.Signature.IsEmpty,
                    _ => false,
                }, MessageFailure.InvalidAuthentication);
                break;
            case FolderAuthResult result:
                Require(result.FolderId.Length == 32 && result.Decision is >= AuthDecision.Allowed and <= AuthDecision.Denied
                    && KnownReason(result.Reason, allowUnspecified: true), MessageFailure.InvalidAuthentication);
                break;
            case IndexSummary summary:
                RequireFolder(summary.FolderId, authenticatedFolder);
                Require(summary.HighestSeq <= long.MaxValue, MessageFailure.InvalidSequence);
                break;
            case IndexRequest request:
                RequireFolder(request.FolderId, authenticatedFolder);
                Require(request.AfterSeq <= long.MaxValue && request.Limit <= MaximumRecords, MessageFailure.InvalidSequence);
                break;
            case IndexUpdate update:
                RequireFolder(update.FolderId, authenticatedFolder);
                Require(update.Records.Count <= MaximumRecords, MessageFailure.TooManyRecords);
                Require(update.LastSeq <= long.MaxValue && update.AfterSeq <= update.LastSeq, MessageFailure.InvalidSequence);
                ulong previous = update.AfterSeq;
                foreach (var record in update.Records)
                {
                    Require(record.LocalSeq > previous && record.LocalSeq <= update.LastSeq, MessageFailure.InvalidSequence);
                    ValidateRecord(record, authenticatedFolder!); previous = record.LocalSeq;
                }
                break;
            case Availability availability:
                RequireFolder(availability.FolderId, authenticatedFolder);
                Require(PathRules.IsValid(availability.Path), MessageFailure.InvalidPath);
                Require(availability.ChunkBitmap.Length <= MaximumBitmapBytes, MessageFailure.InvalidMessage);
                break;
            case BlockRequestBatch batch:
                RequireFolder(batch.FolderId, authenticatedFolder);
                Require(batch.Requests.Count is > 0 and <= MaximumBlockRequests, MessageFailure.TooManyBlockRequests);
                foreach (var item in batch.Requests)
                    Require(item.ChunkHash.Length == 32 && item.Length is > 0 and <= MaximumBlockBytes, MessageFailure.InvalidBlockRequest);
                break;
            case BlockResponse response:
                Require(response.ChunkHash.Length == 32, MessageFailure.InvalidBlockResponse);
                Require(response.PayloadCase switch
                {
                    BlockResponse.PayloadOneofCase.Data => response.Data.Length is > 0 and <= MaximumBlockBytes
                        && CryptographicOperations.FixedTimeEquals(Blake3.Hasher.Hash(response.Data.Span).AsSpan(), response.ChunkHash.Span),
                    BlockResponse.PayloadOneofCase.ErrorCode => response.ErrorCode is > BlockError.None and <= BlockError.Unavailable,
                    _ => false,
                }, MessageFailure.InvalidBlockResponse);
                break;
            case Ping or Pong: break;
            case Close close: Require(KnownReason(close.Reason, allowUnspecified: false), MessageFailure.InvalidMessage); break;
            default: throw new MessageValidationException(MessageFailure.InvalidMessage);
        }
        Require(message.CalculateSize() <= FrameCodec.MaximumBodyBytes, MessageFailure.InvalidMessage);
    }

    private static void RequireFolder(ByteString folder, FolderKeys? keys) => Require(keys is not null && folder.Length == 32
        && CryptographicOperations.FixedTimeEquals(folder.Span, keys.FolderId()), MessageFailure.FolderNotAuthorized);

    private static void ValidateRecord(Records.SignedRecord wire, FolderKeys keys)
    {
        Require(wire.Signature.Length == 64 && wire.Body.Length <= Core.Sync.SignedRecord.MaxBodyBytes, MessageFailure.InvalidSignedRecord);
        // Verify before any FileRecordBody parsing, including allocation guards.
        byte[] signed = [.. "psp1 record"u8, .. keys.FolderId(), .. wire.Body.Span];
        Require(keys.Verify(signed, wire.Signature.Span), MessageFailure.InvalidSignedRecord);
        try { CheckRecordAllocations(wire.Body.ToByteArray()); }
        catch (InvalidProtocolBufferException) { throw new MessageValidationException(MessageFailure.InvalidRecord); }
        var record = SignedRecordFactory.FromWire(wire);
        Require(SignedRecordFactory.TryOpen(record, keys, out var body), MessageFailure.InvalidSignedRecord);
        Require(ValidMetadata(body!), MessageFailure.InvalidRecord);
    }

    private static void CheckRecordAllocations(byte[] bytes)
    {
        using var input = new CodedInputStream(bytes);
        bool hasDevice = false; ulong previous = 0; uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            ProtobufIngressLimits.RejectGroups(tag);
            if (tag is 66 or 82)
            {
                byte[] part = input.ReadBytes().ToByteArray(); ProtobufIngressLimits.ValidateFlat(part);
                if (tag == 66)
                {
                    var entry = VvEntry.Parser.ParseFrom(part);
                    Require(entry.Counter != 0 && (!hasDevice || entry.Device > previous), MessageFailure.InvalidRecord);
                    hasDevice = true; previous = entry.Device;
                }
                else
                {
                    var chunk = Chunk.Parser.ParseFrom(part);
                    Require(chunk.Hash.Length == 32 && chunk.Length is > 0 and <= MaximumBlockBytes, MessageFailure.InvalidRecord);
                }
            }
            else input.SkipLastField();
        }
    }

    private static bool ValidMetadata(FileRecordBody body)
    {
        if (!PathRules.IsValid(body.Path) || body.Type > 2 || body.Size > long.MaxValue
            || (body.Attributes & ~(uint)(FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0
            || body.MtimeTicks < 0 || body.MtimeTicks > DateTime.MaxValue.Ticks
            || body.ChangedTicks < 0 || body.ChangedTicks > DateTime.MaxValue.Ticks || body.ModifiedBy.Length != 32
            || body.Vv.Count == 0 || !body.Vv.Any(entry => entry.Device == BinaryPrimitives.ReadUInt64LittleEndian(body.ModifiedBy.Span))) return false;
        if (body.Deleted || body.Type != 0) return body.Size == 0 && body.FileHash.IsEmpty && body.Chunks.Count == 0;
        if (body.FileHash.Length != 32 || body.ChunkClass != ChunkParameters.ClassFor((long)body.Size)) return false;
        ulong size = 0; int maximum = ChunkParameters.ForClass(body.ChunkClass).Max;
        foreach (var chunk in body.Chunks)
        {
            if (chunk.Hash.Length != 32 || chunk.Length == 0 || chunk.Length > maximum) return false;
            size = checked(size + chunk.Length);
        }
        return size == body.Size && (size != 0 || CryptographicOperations.FixedTimeEquals(body.FileHash.Span, Blake3.Hasher.Hash(ReadOnlySpan<byte>.Empty).AsSpan()));
    }

    private static bool KnownReason(CloseReason reason, bool allowUnspecified) => reason >= (allowUnspecified ? CloseReason.Unspecified : CloseReason.PeerShutdown) && reason <= CloseReason.InternalError;
    private static bool TextValid(string text, int maximum)
    {
        if (text.Length == 0 || text.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(text) <= maximum; }
        catch (EncoderFallbackException) { return false; }
    }
    private static void Require(bool valid, MessageFailure failure) { if (!valid) throw new MessageValidationException(failure); }
}
