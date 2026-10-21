using System.Buffers;
using Google.Protobuf;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Wire;
using PeerSync.Protocol.Validation;

namespace PeerSync.Protocol.Framing;

public enum WireType : byte
{
    Hello = 1, FolderAuth = 2, FolderAuthResult = 3, IndexSummary = 4,
    IndexRequest = 5, IndexUpdate = 6, Availability = 7, BlockRequestBatch = 8,
    BlockResponse = 9, Ping = 10, Pong = 11, Close = 12,
}

public static class WireMessageCodec
{
    public static (WireType Type, byte[] Body) Encode(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        WireType type = message switch
        {
            Hello => WireType.Hello, FolderAuth => WireType.FolderAuth,
            FolderAuthResult => WireType.FolderAuthResult, IndexSummary => WireType.IndexSummary,
            IndexRequest => WireType.IndexRequest, IndexUpdate => WireType.IndexUpdate,
            Availability => WireType.Availability, BlockRequestBatch => WireType.BlockRequestBatch,
            BlockResponse => WireType.BlockResponse, Ping => WireType.Ping, Pong => WireType.Pong,
            Close => WireType.Close, _ => throw new ProtocolException(ProtocolError.UnknownMessageType),
        };
        if (message.CalculateSize() > FrameCodec.MaximumBodyBytes) throw new ProtocolException(ProtocolError.FrameTooLarge);
        return (type, message.ToByteArray());
    }

    // Structural parsing only. Authentication and semantic validation are
    // mandatory at the session boundary before a message reaches the engine.
    public static IMessage Decode(ProtocolFrame frame)
    {
        if (frame.Body.Length > FrameCodec.MaximumBodyBytes) throw new ProtocolException(ProtocolError.FrameTooLarge);
        MessageParser parser = (WireType)frame.Type switch
        {
            WireType.Hello => Hello.Parser, WireType.FolderAuth => FolderAuth.Parser,
            WireType.FolderAuthResult => FolderAuthResult.Parser, WireType.IndexSummary => IndexSummary.Parser,
            WireType.IndexRequest => IndexRequest.Parser, WireType.IndexUpdate => IndexUpdate.Parser,
            WireType.Availability => Availability.Parser, WireType.BlockRequestBatch => BlockRequestBatch.Parser,
            WireType.BlockResponse => BlockResponse.Parser, WireType.Ping => Ping.Parser,
            WireType.Pong => Pong.Parser, WireType.Close => Close.Parser,
            _ => throw new ProtocolException(ProtocolError.UnknownMessageType),
        };
        try
        {
            byte[] bytes = frame.Body.ToArray();
            ProtobufIngressLimits.Validate((WireType)frame.Type, bytes);
            return parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException) { throw new ProtocolException(ProtocolError.MalformedMessage); }
    }
}
