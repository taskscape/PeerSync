using System.Buffers;
using Google.Protobuf;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Wire;
using Shouldly;
using Xunit;

namespace PeerSync.Protocol.Tests;

public sealed class WireMessageTests
{
    [Fact]
    public void EveryDesignMessageHasAStableTypeAndRoundTripsThroughTheWire()
    {
        IMessage[] messages = [new Hello { ProtocolVersion = 1 }, new FolderAuth { Proof = ByteString.CopyFrom([1, 2, 3]) },
            new FolderAuthResult { Decision = AuthDecision.Pending }, new IndexSummary { HighestSeq = 123 },
            new IndexRequest { AfterSeq = 4, Limit = 1000 }, new IndexUpdate { AfterSeq = 4, LastSeq = 100 },
            new Availability { Path = "nested/file.txt" }, new BlockRequestBatch { Requests = { new BlockRequest { Length = 65536 } } },
            new BlockResponse { ErrorCode = BlockError.NotFound }, new Ping { Id = 123 }, new Pong { Id = 123 },
            new Close { Reason = CloseReason.PeerShutdown }];
        for (int index = 0; index < messages.Length; index++)
        {
            var (type, body) = WireMessageCodec.Encode(messages[index]); ((int)type).ShouldBe(index + 1);
            var bytes = new ArrayBufferWriter<byte>(); FrameCodec.Write(bytes, (byte)type, body);
            var input = new ReadOnlySequence<byte>(bytes.WrittenMemory); FrameCodec.TryRead(ref input, out var frame).ShouldBeTrue();
            WireMessageCodec.Decode(frame).ShouldBe(messages[index]); input.IsEmpty.ShouldBeTrue();
        }
    }

    [Fact]
    public void SignedBodiesAndSignaturesAreForwardedExactlyIncludingUnknownFields()
    {
        byte[] original = [10, 1, 65, 160, 6, 7]; byte[] signature = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var update = new IndexUpdate { AfterSeq = 3, LastSeq = 99, Records = { new Records.SignedRecord
        { Body = ByteString.CopyFrom(original), Signature = ByteString.CopyFrom(signature), LocalSeq = 5 } } };
        var (type, body) = WireMessageCodec.Encode(update);
        var copy = (IndexUpdate)WireMessageCodec.Decode(new((byte)type, new(body)));
        copy.Records[0].Body.ToByteArray().ShouldBe(original); copy.Records[0].Signature.ToByteArray().ShouldBe(signature);
        copy.AfterSeq.ShouldBe(3UL); copy.LastSeq.ShouldBe(99UL); copy.Records[0].LocalSeq.ShouldBe(5UL);
    }

    [Fact]
    public void MessageEncodingRejectsBodyOverTheFrameLimit()
    {
        var message = new BlockResponse { Data = ByteString.CopyFrom(new byte[FrameCodec.MaximumBodyBytes]) };
        Should.Throw<ProtocolException>(() => WireMessageCodec.Encode(message)).Code.ShouldBe(ProtocolError.FrameTooLarge);
    }

    [Fact]
    public void UnknownTypesAndMalformedProtobufHaveSafeReasonCodes()
    {
        Should.Throw<ProtocolException>(() => WireMessageCodec.Decode(new(255, ReadOnlySequence<byte>.Empty)))
            .Code.ShouldBe(ProtocolError.UnknownMessageType);
        var error = Should.Throw<ProtocolException>(() => WireMessageCodec.Decode(new((byte)WireType.Hello, new(new byte[] { 15 }))));
        error.Code.ShouldBe(ProtocolError.MalformedMessage);
        error.InnerException.ShouldBeNull();
    }
}
