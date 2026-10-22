using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Google.Protobuf;
using PeerSync.Core.Sync;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Crypto.Chunking;
using PeerSync.Net;
using PeerSync.Net.Security;
using PeerSync.Net.Tcp;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;
using Shouldly;
using Xunit;
using CoreSignedRecord = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Net.Tests;

public sealed class TcpPeerProtocolSessionTests
{
    [Fact]
    public async Task InvalidAuthenticatedControlMessageClosesWithProtocolReasonAndStrikesCertificateIdentityOnce()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var clientCertificate = CreateCertificate();
        using var serverCertificate = CreateCertificate();
        using var keys = FolderKeys.CreateNew();
        byte[] clientId = PeerTls.DeviceId(clientCertificate), serverId = PeerTls.DeviceId(serverCertificate);
        string diagnosticRoot = Path.Combine(Path.GetTempPath(), "PeerSync-ProtocolStrikeTests", Guid.NewGuid().ToString("N"));
        using var diagnostics = new DiagnosticJournal(diagnosticRoot, "tcp_protocol_strike", debugEnabled: true);
        byte[]? struckDevice = null;
        MessageFailure? failure = null;
        int strikes = 0;

        await using var clientListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0),
            new TcpPeerOptions { Certificate = clientCertificate, IsDevicePinned = id => CryptographicOperations.FixedTimeEquals(id, serverId) });
        await using var serverListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0),
            new TcpPeerOptions { Certificate = serverCertificate, IsDevicePinned = id => CryptographicOperations.FixedTimeEquals(id, clientId) });
        Task<TcpPeerSession> accepting = serverListener.AcceptAsync(stop.Token).AsTask();
        TcpPeerSession clientRaw = await clientListener.ConnectAsync(serverListener.LocalEndPoint, stop.Token);
        Task<TcpPeerProtocolSession> clientStart = TcpPeerProtocolSession.StartAsync(clientRaw,
            ProtocolOptions(clientId, serverId, [keys], [[]], diagnostics), initiator: true, stop.Token).AsTask();
        TcpPeerSession serverRaw = await accepting;
        Task<TcpPeerProtocolSession> serverStart = TcpPeerProtocolSession.StartAsync(serverRaw,
            ProtocolOptions(serverId, clientId, [keys], [[]], diagnostics,
                (device, reason) => { struckDevice = device; failure = reason; Interlocked.Increment(ref strikes); }),
            initiator: false, stop.Token).AsTask();
        await using TcpPeerProtocolSession client = await clientStart.WaitAsync(stop.Token);
        await using TcpPeerProtocolSession server = await serverStart.WaitAsync(stop.Token);

        var frame = new ArrayBufferWriter<byte>();
        (WireType type, byte[] body) = WireMessageCodec.Encode(new Close { Reason = (CloseReason)99 });
        FrameCodec.Write(frame, (byte)type, body);
        await clientRaw.ControlStream.WriteAsync(frame.WrittenMemory, stop.Token);
        await server.Closed.WaitAsync(stop.Token);
        await client.Closed.WaitAsync(stop.Token);

        strikes.ShouldBe(1);
        struckDevice.ShouldBe(clientId);
        failure.ShouldBe(MessageFailure.InvalidMessage);
        bool receivedProtocolClose = false;
        foreach (string line in File.ReadLines(Path.Combine(diagnosticRoot, "debug.jsonl")))
        {
            using JsonDocument item = JsonDocument.Parse(line);
            if (item.RootElement.GetProperty("eventName").GetString() == "transport.remote_close"
                && item.RootElement.GetProperty("itemsVisited").GetInt64() == (long)CloseReason.ProtocolViolation)
            { receivedProtocolClose = true; break; }
        }
        receivedProtocolClose.ShouldBeTrue();
        diagnostics.Dispose();
        string fullTempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PeerSync-ProtocolStrikeTests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(diagnosticRoot).StartsWith(fullTempRoot, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(diagnosticRoot, recursive: true);
    }

    [Fact]
    public async Task MutualTlsTcpSessionAuthenticatesMultipleFoldersAndCarriesIndexPushAndBlocksBothWays()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(20));
        using var clientCertificate = CreateCertificate();
        using var serverCertificate = CreateCertificate();
        using var firstKeys = FolderKeys.CreateNew();
        using var secondKeys = FolderKeys.CreateNew();
        byte[] clientId = PeerTls.DeviceId(clientCertificate), serverId = PeerTls.DeviceId(serverCertificate);
        byte[] clientBlock = [1, 2, 3, 4, 5];
        byte[] serverBlock = [9, 8, 7, 6, 5, 4];

        await using var clientListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0),
            new TcpPeerOptions { Certificate = clientCertificate, IsDevicePinned = id => CryptographicOperations.FixedTimeEquals(id, serverId) });
        await using var serverListener = TcpPeerListener.Listen(new IPEndPoint(IPAddress.Loopback, 0),
            new TcpPeerOptions { Certificate = serverCertificate, IsDevicePinned = id => CryptographicOperations.FixedTimeEquals(id, clientId) });

        Task<TcpPeerSession> acceptedRaw = serverListener.AcceptAsync(stop.Token).AsTask();
        TcpPeerSession clientRaw = await clientListener.ConnectAsync(serverListener.LocalEndPoint, stop.Token);
        Task<TcpPeerProtocolSession> clientStart = TcpPeerProtocolSession.StartAsync(clientRaw,
            ProtocolOptions(clientId, serverId, [firstKeys, secondKeys], [clientBlock, clientBlock]),
            initiator: true, stop.Token).AsTask();
        TcpPeerSession serverRaw = await acceptedRaw;
        Task<TcpPeerProtocolSession> serverStart = TcpPeerProtocolSession.StartAsync(serverRaw,
            ProtocolOptions(serverId, clientId, [firstKeys, secondKeys], [serverBlock, serverBlock]),
            initiator: false, stop.Token).AsTask();
        await using TcpPeerProtocolSession client = await clientStart.WaitAsync(stop.Token);
        await using TcpPeerProtocolSession server = await serverStart.WaitAsync(stop.Token);

        client.RemoteDeviceId.ShouldBe(serverId);
        server.RemoteDeviceId.ShouldBe(clientId);
        client.RemoteEndPoint.Port.ShouldBe(serverListener.LocalEndPoint.Port);
        server.RemoteEndPoint.Port.ShouldBe(clientListener.LocalEndPoint.Port);
        client.SessionId.ShouldBe(server.SessionId);
        client.AuthenticatedFolders.Count.ShouldBe(2);
        server.AuthenticatedFolders.Count.ShouldBe(2);
        client.RemoteHello.DeviceId.Span.ToArray().ShouldBe(serverId);
        server.RemoteHello.DeviceId.Span.ToArray().ShouldBe(clientId);

        IPeerTransport FolderTransport(TcpPeerProtocolSession session, FolderKeys keys)
            => session.AuthenticatedFolders.Single(folder => folder.FolderId.AsSpan().SequenceEqual(keys.FolderId())).Transport!;
        IPeerTransport clientFirst = FolderTransport(client, firstKeys);
        IPeerTransport clientSecond = FolderTransport(client, secondKeys);
        IPeerTransport serverFirst = FolderTransport(server, firstKeys);
        IPeerTransport serverSecond = FolderTransport(server, secondKeys);

        long[] targets = await Task.WhenAll(clientFirst.ReadIndexTargetAsync(stop.Token).AsTask(),
            clientSecond.ReadIndexTargetAsync(stop.Token).AsTask());
        targets.ShouldBe([20L, 21L]);
        PeerIndexPage page = await clientSecond.ReadIndexAsync(0, cancellationToken: stop.Token);
        page.LastSequence.ShouldBe(21L);
        FileRecordBody.Parser.ParseFrom(page.Records.Single().Body).Path.ShouldBe("tcp-remote.txt");

        byte[] serverHash = Blake3.Hasher.Hash(serverBlock).AsSpan().ToArray();
        byte[] clientHash = Blake3.Hasher.Hash(clientBlock).AsSpan().ToArray();
        (await clientFirst.ReadBlockAsync(serverHash, serverBlock.Length, stop.Token)).ToArray().ShouldBe(serverBlock);
        (await serverSecond.ReadBlockAsync(clientHash, clientBlock.Length, stop.Token)).ToArray().ShouldBe(clientBlock);

        var toClientPush = (IPeerIndexPushTransport)clientFirst;
        var toServerPush = (IPeerIndexPushTransport)serverFirst;
        await toServerPush.PublishIndexTargetAsync(31, stop.Token);
        (await toClientPush.IndexTargets.ReadAsync(stop.Token).AsTask().WaitAsync(stop.Token)).ShouldBe(31L);
        await toClientPush.PublishIndexTargetAsync(32, stop.Token);
        (await toServerPush.IndexTargets.ReadAsync(stop.Token).AsTask().WaitAsync(stop.Token)).ShouldBe(32L);
    }

    private static TcpPeerProtocolOptions ProtocolOptions(byte[] localId,
        byte[] expectedPeerId, IReadOnlyList<FolderKeys> folders, IReadOnlyList<byte[]> localBlocks,
        DiagnosticJournal? diagnostics = null, Action<byte[], MessageFailure>? addStrike = null) => new()
    {
        CreateHello = () => new Hello
        {
            ProtocolVersion = 1, DeviceId = ByteString.CopyFrom(localId), DeviceName = "tcp-test",
            ClientVersion = "test", Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            UtcTicks = DateTime.UtcNow.Ticks,
        },
        Folders = folders.Select((keys, index) => new PeerFolderRegistration(keys,
            _ => PeerSync.Protocol.Wire.AuthDecision.Allowed,
            peer =>
            {
                peer.ShouldBe(expectedPeerId);
                return new TestHandler(localId, keys, index + 20, localBlocks[index]);
            })).ToArray(),
        DeviceName = "tcp-test",
        ClientVersion = "test",
        Diagnostics = diagnostics,
        AddStrike = addStrike,
    };

    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PeerSync TCP Protocol Test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "test-only"), "test-only",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private sealed class TestHandler(byte[] localDeviceId, FolderKeys keys, long sequence, byte[] block) : IPeerRequestHandler
    {
        private readonly CoreSignedRecord record = CreateRecord(keys, sequence);

        public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken)
            => ValueTask.FromResult(new Hello
            {
                ProtocolVersion = 1, DeviceId = ByteString.CopyFrom(localDeviceId), DeviceName = "tcp-test",
                ClientVersion = "test", Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                UtcTicks = DateTime.UtcNow.Ticks, RequestId = hello.RequestId, Response = true,
            });

        public ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(sequence);
        public ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit, CancellationToken cancellationToken)
            => ValueTask.FromResult(new PeerIndexPage(afterSequence, sequence, afterSequence < sequence ? [record] : []));

        public ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken)
        {
            if (length != block.Length || !CryptographicOperations.FixedTimeEquals(Blake3.Hasher.Hash(block).AsSpan(), hash.Span))
                throw new FileNotFoundException();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(block);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static CoreSignedRecord CreateRecord(FolderKeys keys, long sequence)
        {
            byte[] device = new byte[32]; System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(device, 1);
            var body = new FileRecordBody
            {
                Path = "tcp-remote.txt", Size = 0, MtimeTicks = DateTime.UtcNow.Ticks, ChangedTicks = DateTime.UtcNow.Ticks,
                FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash(ReadOnlySpan<byte>.Empty).AsSpan()),
                ModifiedBy = ByteString.CopyFrom(device), ChunkClass = ChunkParameters.ClassFor(0),
                Vv = { new VvEntry { Device = 1, Counter = 1 } },
            };
            return SignedRecordFactory.Create(body, keys, checked((ulong)sequence));
        }
    }
}
