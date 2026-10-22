using System.Buffers;
using Google.Protobuf;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;
using Shouldly;
using Xunit;

namespace PeerSync.Protocol.Tests;

public sealed class HandshakeCorrelationTests
{
    [Fact]
    public void AnnouncedFolderCapIsEnforcedBeforeGeneratedParserAllocations()
    {
        var hello = new Hello(); for (int i = 0; i < 257; i++) hello.FolderIds.Add(ByteString.Empty);
        Should.Throw<MessageValidationException>(() => WireMessageCodec.Decode(new((byte)WireType.Hello, new ReadOnlySequence<byte>(hello.ToByteArray()))))
            .Failure.ShouldBe(MessageFailure.InvalidHello);
    }

    [Fact]
    public void AllowedResultRequiresMatchingResponderProofAndCorrelation()
    {
        var result = new FolderAuthResult { FolderId = ByteString.CopyFrom(new byte[32]), RequestId = 1, Decision = AuthDecision.Allowed };
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(result));
        result.ServerProof = new() { FolderId = result.FolderId, RequestId = 1, Access = FolderAccess.Encrypted, Proof = result.FolderId };
        MessageValidator.Validate(result);
        result.ServerProof.RequestId = 2; Should.Throw<MessageValidationException>(() => MessageValidator.Validate(result));
        result.ServerProof.RequestId = 1; result.ServerProof.FolderId = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 32).ToArray());
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(result));
    }
}
