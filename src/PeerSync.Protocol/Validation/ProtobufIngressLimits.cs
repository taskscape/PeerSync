using Google.Protobuf;
using PeerSync.Protocol.Framing;

namespace PeerSync.Protocol.Validation;

// Scan bounded wire bytes before generated parsers allocate repeated message
// collections. Signed bodies remain opaque until signature verification.
internal static class ProtobufIngressLimits
{
    internal static void Validate(WireType type, byte[] bytes)
    {
        using var input = new CodedInputStream(bytes);
        int count = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            RejectGroups(tag);
            bool records = type == WireType.IndexUpdate && tag == 18;
            bool requests = type == WireType.BlockRequestBatch && tag == 18;
            if (records || requests)
            {
                int maximum = records ? MessageValidator.MaximumRecords : MessageValidator.MaximumBlockRequests;
                if (++count > maximum) throw new MessageValidationException(records ? MessageFailure.TooManyRecords : MessageFailure.TooManyBlockRequests);
                ValidateFlat(input.ReadBytes().ToByteArray());
            }
            else input.SkipLastField();
        }
    }

    internal static void ValidateFlat(byte[] bytes)
    {
        using var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0) { RejectGroups(tag); input.SkipLastField(); }
    }

    internal static void RejectGroups(uint tag)
    {
        var type = WireFormat.GetTagWireType(tag);
        if (type is WireFormat.WireType.StartGroup or WireFormat.WireType.EndGroup)
            throw new MessageValidationException(MessageFailure.InvalidMessage);
    }
}
