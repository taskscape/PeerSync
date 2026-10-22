using Google.Protobuf;
using PeerSync.Protocol.Framing;

namespace PeerSync.Protocol.Validation;

// Scan bounded wire bytes before generated parsers allocate repeated message
// collections. Signed bodies remain opaque until signature verification.
internal static class ProtobufIngressLimits
{
    internal struct FieldBudget
    {
        private int count;

        internal void Consume()
        {
            if (++count > MessageValidator.MaximumFieldOccurrences)
                throw new MessageValidationException(MessageFailure.InvalidMessage);
        }
    }

    internal static void Validate(WireType type, byte[] bytes)
    {
        using var input = new CodedInputStream(bytes);
        var budget = new FieldBudget();
        int count = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            budget.Consume();
            RejectGroups(tag);
            bool records = type == WireType.IndexUpdate && tag == 18;
            bool requests = type == WireType.BlockRequestBatch && tag == 18;
            if (type == WireType.Hello && tag == 82)
            {
                if (++count > MessageValidator.MaximumAnnouncedFolders) throw new MessageValidationException(MessageFailure.InvalidHello);
                input.SkipLastField();
            }
            else if (type == WireType.FolderAuthResult && tag == 34) ValidateFlat(input.ReadBytes().ToByteArray(), ref budget);
            else if (records || requests)
            {
                int maximum = records ? MessageValidator.MaximumRecords : MessageValidator.MaximumBlockRequests;
                if (++count > maximum) throw new MessageValidationException(records ? MessageFailure.TooManyRecords : MessageFailure.TooManyBlockRequests);
                ValidateFlat(input.ReadBytes().ToByteArray(), ref budget);
            }
            else input.SkipLastField();
        }
    }

    internal static void ValidateFlat(byte[] bytes)
    {
        var budget = new FieldBudget();
        ValidateFlat(bytes, ref budget);
    }

    internal static void ValidateFlat(byte[] bytes, ref FieldBudget budget)
    {
        using var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0) { budget.Consume(); RejectGroups(tag); input.SkipLastField(); }
    }

    internal static void RejectGroups(uint tag)
    {
        var type = WireFormat.GetTagWireType(tag);
        if (type is WireFormat.WireType.StartGroup or WireFormat.WireType.EndGroup)
            throw new MessageValidationException(MessageFailure.InvalidMessage);
    }
}
