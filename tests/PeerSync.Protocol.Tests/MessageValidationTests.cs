using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipelines;
using Google.Protobuf;
using PeerSync.Crypto;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;
using Shouldly;
using Xunit;

namespace PeerSync.Protocol.Tests;

public sealed class MessageValidationTests
{
    [Fact]
    public void ExactRecordLimitAndCompleteReviewedRangesAreAcceptedWithoutResigning()
    {
        using var keys = FolderKeys.CreateNew(); var update = Update(keys);
        var record = SignedRecordFactory.Create(Body(), keys);
        for (ulong sequence = 1; sequence <= 1000; sequence++) update.Records.Add(SignedRecordFactory.ToWire(record.WithLocalSequence(sequence)));
        update.LastSeq = 1200; // Valid unpublished gaps after the last record.
        MessageValidator.Validate(update, keys);
        update.Records[0].Body.Span.SequenceEqual(record.Body).ShouldBeTrue();
        update.Records[0].Signature.Span.SequenceEqual(record.Signature).ShouldBeTrue();
    }

    [Fact]
    public void RepeatedMessageLimitsAreCheckedDuringDecodeBeforeGeneratedCollections()
    {
        var update = new IndexUpdate(); for (int i = 0; i < 1001; i++) update.Records.Add(new Records.SignedRecord());
        var error = Should.Throw<MessageValidationException>(() => WireMessageCodec.Decode(Frame(WireType.IndexUpdate, update)));
        error.Failure.ShouldBe(MessageFailure.TooManyRecords);
        var requests = new BlockRequestBatch(); for (int i = 0; i < 65; i++) requests.Requests.Add(new BlockRequest());
        Should.Throw<MessageValidationException>(() => WireMessageCodec.Decode(Frame(WireType.BlockRequestBatch, requests)))
            .Failure.ShouldBe(MessageFailure.TooManyBlockRequests);
    }

    [Fact]
    public void AuthenticatedFolderContextAndStrictSequenceOrderAreRequired()
    {
        using var keys = FolderKeys.CreateNew(); using var other = FolderKeys.CreateNew(); var update = Update(keys);
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update)).Failure.ShouldBe(MessageFailure.FolderNotAuthorized);
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, other)).Failure.ShouldBe(MessageFailure.FolderNotAuthorized);
        var record = SignedRecordFactory.Create(Body(), keys, 2); update.Records.Add(SignedRecordFactory.ToWire(record)); update.Records.Add(SignedRecordFactory.ToWire(record)); update.LastSeq = 3;
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, keys)).Failure.ShouldBe(MessageFailure.InvalidSequence);
        update.Records.Clear(); update.AfterSeq = 5; update.LastSeq = 4;
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, keys)).Failure.ShouldBe(MessageFailure.InvalidSequence);
        update.AfterSeq = 0; update.LastSeq = ulong.MaxValue;
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, keys)).Failure.ShouldBe(MessageFailure.InvalidSequence);
    }

    [Theory]
    [InlineData("../escape")] [InlineData(".psp/private")] [InlineData("CON")] [InlineData("file.txt.!psp")]
    public void SignedInvalidPathsAndAvailabilityPathsAreRejected(string path)
    {
        using var keys = FolderKeys.CreateNew(); var body = Body(); body.Path = path; var update = Update(keys);
        update.Records.Add(SignRaw(body.ToByteArray(), keys)); update.LastSeq = 1;
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, keys));
        var availability = new Availability { FolderId = update.FolderId, Path = path };
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(availability, keys)).Failure.ShouldBe(MessageFailure.InvalidPath);
    }

    [Fact]
    public void InvalidSignatureIsRejectedBeforeOpeningMalformedSignedBody()
    {
        using var keys = FolderKeys.CreateNew(); var update = Update(keys); update.LastSeq = 1;
        update.Records.Add(new Records.SignedRecord { Body = ByteString.CopyFrom([255]), Signature = ByteString.CopyFrom(new byte[64]), LocalSeq = 1 });
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, keys)).Failure.ShouldBe(MessageFailure.InvalidSignedRecord);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ValidSignatureCannotAuthorizeInvalidFileMetadata(int mutation)
    {
        using var keys = FolderKeys.CreateNew(); var body = Body();
        switch (mutation)
        {
            case 0: body.Type = 99; break;
            case 1: body.Attributes = (uint)FileAttributes.ReparsePoint; break;
            case 2: body.ModifiedBy = ByteString.CopyFrom(new byte[31]); break;
            case 3: body.MtimeTicks = -1; break;
            case 4: body.Size = ulong.MaxValue; break;
            case 5: body.Vv.Clear(); break;
            case 6: body.FileHash = ByteString.CopyFrom(new byte[32]); break; // Empty file digest must be correct.
            case 7: body.ChunkClass = 2; break;
        }
        var update = Update(keys); update.LastSeq = 1; update.Records.Add(SignRaw(body.ToByteArray(), keys));
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, keys));
    }

    [Fact]
    public void EmptyNestedMessagesAndDeprecatedGroupsCannotAmplifySignedParsing()
    {
        using var keys = FolderKeys.CreateNew(); var update = Update(keys); update.LastSeq = 1;
        foreach (byte[] bytes in new byte[][] { [66, 0], [82, 0], [163, 6, 164, 6] })
        {
            update.Records.Clear(); update.Records.Add(SignRaw(bytes, keys));
            Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, keys));
        }
        // Groups are invalid even in unknown outer fields, before allocation.
        Should.Throw<MessageValidationException>(() => WireMessageCodec.Decode(new((byte)WireType.Ping, new(new byte[] { 163, 6, 164, 6 }))));
    }

    [Fact]
    public void BlockLimitsDigestsAndErrorOneofAreEnforced()
    {
        using var keys = FolderKeys.CreateNew(); byte[] bytes = [1, 2, 3]; var hash = ByteString.CopyFrom(Blake3.Hasher.Hash(bytes).AsSpan());
        var batch = new BlockRequestBatch { FolderId = ByteString.CopyFrom(keys.FolderId()) };
        for (int i = 0; i < 64; i++) batch.Requests.Add(new BlockRequest { ChunkHash = hash, Length = 3 });
        MessageValidator.Validate(batch, keys); batch.Requests[0].Length = 0;
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(batch, keys)).Failure.ShouldBe(MessageFailure.InvalidBlockRequest);
        var response = new BlockResponse { ChunkHash = hash, Data = ByteString.CopyFrom(bytes) }; MessageValidator.Validate(response);
        response.Data = ByteString.CopyFrom([9]);
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(response)).Failure.ShouldBe(MessageFailure.InvalidBlockResponse);
        response.ErrorCode = BlockError.NotFound; MessageValidator.Validate(response);
        response.ErrorCode = BlockError.None;
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(response));
    }

    [Fact]
    public void HelloNamesClocksAndRoleProofShapesAreBounded()
    {
        var hello = new Hello { ProtocolVersion = 1, DeviceId = ByteString.CopyFrom(new byte[32]), Nonce = ByteString.CopyFrom(new byte[32]), DeviceName = "peer", ClientVersion = "1", UtcTicks = 1 };
        MessageValidator.Validate(hello); hello.UtcTicks = 0; Should.Throw<MessageValidationException>(() => MessageValidator.Validate(hello));
        hello.UtcTicks = 1; hello.DeviceName = new string('é', 65); Should.Throw<MessageValidationException>(() => MessageValidator.Validate(hello));
        hello.DeviceName = "peer\nspoof"; Should.Throw<MessageValidationException>(() => MessageValidator.Validate(hello));
        var auth = new FolderAuth { FolderId = ByteString.CopyFrom(new byte[32]), Proof = ByteString.CopyFrom(new byte[32]), ReadOnlyProof = ByteString.CopyFrom(new byte[32]), Access = FolderAccess.ReadOnly };
        MessageValidator.Validate(auth); auth.Access = FolderAccess.ReadWrite;
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(auth));
        auth.Signature = ByteString.CopyFrom(new byte[64]); MessageValidator.Validate(auth);
    }

    [Fact]
    public async Task InvalidMessageClosesARealConnectionWithAReasonAndStrikesCertificateIdentity()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        Task<TcpClient> accepting = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        using var server = await accepting; var sink = new Sink(server, deadline.Token); byte[] certificateId = Enumerable.Repeat((byte)7, 32).ToArray();
        var guard = new PeerMessageGuard(certificateId, sink); certificateId[0] = 9;
        var spoof = new Hello { DeviceId = ByteString.CopyFrom(new byte[32]) };
        await Should.ThrowAsync<MessageValidationException>(async () => await guard.DecodeAsync(Frame(WireType.Hello, spoof)));
        sink.Struck.ShouldBe(Enumerable.Repeat((byte)7, 32).ToArray()); sink.Closed.ShouldBeTrue();
        var reader = PipeReader.Create(client.GetStream());
        try
        {
            var received = new List<Close>();
            await foreach (var frame in FrameCodec.ReadFramesAsync(reader, deadline.Token)) received.Add((Close)WireMessageCodec.Decode(frame));
            received.Count.ShouldBe(1); received[0].Reason.ShouldBe(CloseReason.ProtocolViolation);
        }
        finally { await reader.CompleteAsync(); }
    }

    [Fact]
    public async Task RejectedConnectionsCannotAdmitMoreMessagesOrRepeatTheirStrike()
    {
        var sink = new Sink(null, TestContext.Current.CancellationToken);
        var guard = new PeerMessageGuard(new byte[32], sink);
        await guard.RejectAsync(MessageFailure.InvalidMessage); await guard.RejectAsync(MessageFailure.InvalidFraming);
        sink.StrikeCount.ShouldBe(1);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await guard.DecodeAsync(Frame(WireType.Ping, new Ping())));
    }

    [Fact]
    public void NonemptyChunksDirectoriesTombstonesAndNormalUnknownFieldsKeepTheirSemantics()
    {
        using var keys = FolderKeys.CreateNew(); byte[] payload = [1, 2, 3]; var body = Body();
        body.Size = 3; body.FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash(payload).AsSpan());
        body.Chunks.Add(new Chunk { Length = 3, Hash = body.FileHash });
        var update = Update(keys); update.LastSeq = 1; update.Records.Add(SignRaw([.. body.ToByteArray(), 160, 6, 7], keys));
        MessageValidator.Validate(update, keys);
        body.Chunks[0].Length = 2; update.Records[0] = SignRaw(body.ToByteArray(), keys);
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, keys)).Failure.ShouldBe(MessageFailure.InvalidRecord);
        body.Size = 0; body.FileHash = ByteString.Empty; body.Chunks.Clear(); body.Type = 1;
        update.Records[0] = SignRaw(body.ToByteArray(), keys); MessageValidator.Validate(update, keys);
        body.Deleted = true; update.Records[0] = SignRaw(body.ToByteArray(), keys); MessageValidator.Validate(update, keys);
        body.Size = 1; update.Records[0] = SignRaw(body.ToByteArray(), keys);
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(update, keys));
    }

    [Fact]
    public void BitmapRequestAndEnumBoundaryViolationsAreRejected()
    {
        using var keys = FolderKeys.CreateNew(); var folder = ByteString.CopyFrom(keys.FolderId());
        var availability = new Availability { FolderId = folder, Path = "file.txt", ChunkBitmap = ByteString.CopyFrom(new byte[MessageValidator.MaximumBitmapBytes]) };
        MessageValidator.Validate(availability, keys); availability.ChunkBitmap = ByteString.CopyFrom(new byte[MessageValidator.MaximumBitmapBytes + 1]);
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(availability, keys));
        var batch = new BlockRequestBatch { FolderId = folder, Requests = { new BlockRequest { ChunkHash = ByteString.CopyFrom(new byte[32]), Length = MessageValidator.MaximumBlockBytes } } };
        MessageValidator.Validate(batch, keys); batch.Requests[0].Length++;
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(batch, keys));
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(new Close { Reason = (CloseReason)99 }));
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(new FolderAuthResult { FolderId = folder, Decision = (AuthDecision)99 }));
        Should.Throw<MessageValidationException>(() => MessageValidator.Validate(new IndexRequest { FolderId = folder, Limit = 1001 }, keys));
    }

    [Fact]
    public async Task StrikeStorageFailureStillClosesTheOwnedConnection()
    {
        var sink = new Sink(null, TestContext.Current.CancellationToken) { FailStrike = true };
        var guard = new PeerMessageGuard(new byte[32], sink);
        await Should.ThrowAsync<IOException>(async () => await guard.RejectAsync(MessageFailure.InvalidMessage));
        sink.Closed.ShouldBeTrue();
    }

    private static ProtocolFrame Frame(WireType type, IMessage message) => new((byte)type, new ReadOnlySequence<byte>(message.ToByteArray()));
    private static IndexUpdate Update(FolderKeys keys) => new() { FolderId = ByteString.CopyFrom(keys.FolderId()) };
    private static FileRecordBody Body()
    {
        byte[] device = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(device, 1);
        return new() { Path = "nested/file.txt", ModifiedBy = ByteString.CopyFrom(device), FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash(ReadOnlySpan<byte>.Empty).AsSpan()), Vv = { new VvEntry { Device = 1, Counter = 1 } } };
    }
    private static Records.SignedRecord SignRaw(byte[] bytes, FolderKeys keys) => new()
    { Body = ByteString.CopyFrom(bytes), Signature = ByteString.CopyFrom(keys.Sign([.. "psp1 record"u8, .. keys.FolderId(), .. bytes])), LocalSeq = 1 };

    private sealed class Sink(TcpClient? connection, CancellationToken cancellationToken) : IProtocolViolationSink
    {
        public byte[]? Struck { get; private set; }
        public bool Closed { get; private set; }
        public bool FailStrike { get; init; }
        public int StrikeCount { get; private set; }
        public void AddStrike(ReadOnlySpan<byte> device, MessageFailure failure)
        { StrikeCount++; Struck = device.ToArray(); if (FailStrike) throw new IOException("Synthetic strike storage failure."); }
        public async ValueTask CloseAsync(MessageFailure failure)
        {
            Closed = true;
            if (connection is null) return;
            try
            {
                var writer = PipeWriter.Create(connection.GetStream(), new(leaveOpen: true));
                try
                {
                    var (type, body) = WireMessageCodec.Encode(new Close { Reason = CloseReason.ProtocolViolation });
                    await FrameCodec.WriteAsync(writer, (byte)type, body, cancellationToken);
                }
                finally { await writer.CompleteAsync(); }
            }
            finally { connection.Dispose(); }
        }
    }
}
