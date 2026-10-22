using System.Buffers;
using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Quic;
using PeerSync.Net.Security;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;
using Shouldly;
using Xunit;

namespace PeerSync.Net.Tests;

public sealed class PeerHandshakeViolationTests
{
    [Fact]
    public async Task InvalidCertificateAuthenticatedHelloIsStruckAndReceivesProtocolClose()
    {
        byte[] localDevice = Enumerable.Repeat((byte)0x11, 32).ToArray();
        byte[] certificateDevice = Enumerable.Repeat((byte)0x22, 32).ToArray();
        byte[] untrustedHelloDevice = Enumerable.Repeat((byte)0x33, 32).ToArray();
        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        var invalidRemoteHello = new Hello
        {
            ProtocolVersion = 99,
            DeviceId = ByteString.CopyFrom(untrustedHelloDevice),
            DeviceName = "remote-test",
            ClientVersion = "test",
            Nonce = ByteString.CopyFrom(nonce),
            UtcTicks = DateTime.UtcNow.Ticks,
            RequestId = 42,
        };
        (WireType type, byte[] body) = WireMessageCodec.Encode(invalidRemoteHello);
        var inbound = new ArrayBufferWriter<byte>();
        FrameCodec.Write(inbound, (byte)type, body);
        using var stream = new ScriptedStream(inbound.WrittenSpan.ToArray());
        await using var framed = new PeerFrameStream(stream);
        byte[]? struckDevice = null;
        MessageFailure? failure = null;
        var options = new PeerHandshakeOptions
        {
            CreateHello = () => new Hello
            {
                ProtocolVersion = 1,
                DeviceId = ByteString.CopyFrom(localDevice),
                DeviceName = "local-test",
                ClientVersion = "test",
                Nonce = ByteString.CopyFrom(nonce),
                UtcTicks = DateTime.UtcNow.Ticks,
            },
            Folders = [],
            DeviceName = "local-test",
            ClientVersion = "test",
            RequestTimeout = TimeSpan.FromSeconds(2),
            Transport = DiagnosticTransport.Tcp,
            AddStrike = (device, reason) => { struckDevice = device; failure = reason; },
        };

        await Should.ThrowAsync<MessageValidationException>(() => PeerHandshake.AuthenticateAsync(
            framed, options, localDevice, certificateDevice, CancellationToken.None).AsTask());

        struckDevice.ShouldBe(certificateDevice);
        failure.ShouldBe(MessageFailure.UnsupportedVersion);
        List<ProtocolFrame> sent = ReadFrames(stream.Written);
        sent.Count.ShouldBe(2);
        sent[0].Type.ShouldBe((byte)WireType.Hello);
        sent[1].Type.ShouldBe((byte)WireType.Close);
        WireMessageCodec.Decode(sent[1]).ShouldBeOfType<Close>().Reason.ShouldBe(CloseReason.ProtocolViolation);
        struckDevice.ShouldNotBe(untrustedHelloDevice);
    }

    private static List<ProtocolFrame> ReadFrames(byte[] bytes)
    {
        ReadOnlySequence<byte> remaining = new(bytes);
        var frames = new List<ProtocolFrame>();
        while (FrameCodec.TryRead(ref remaining, out ProtocolFrame frame))
            frames.Add(new(frame.Type, new ReadOnlySequence<byte>(frame.Body.ToArray())));
        remaining.IsEmpty.ShouldBeTrue();
        return frames;
    }

    private sealed class ScriptedStream(byte[] input) : Stream
    {
        private readonly MemoryStream source = new(input, writable: false);
        private readonly MemoryStream destination = new();

        internal byte[] Written => destination.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => source.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => destination.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => destination.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { source.Dispose(); destination.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
