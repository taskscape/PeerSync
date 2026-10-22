using System.Net;
using System.Net.Quic;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Crypto.Chunking;
using PeerSync.Net;
using PeerSync.Net.Quic;
using PeerSync.Net.Security;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Protocol.Wire;
using Shouldly;
using Xunit;

namespace PeerSync.Net.Tests;

public sealed class QuicPeerSessionTests
{
    [Fact]
    public async Task ProtocolViolationRecordsOneStrikeAgainstTheCertificateIdentityAndClosesWithAReason()
    {
        if (!QuicListener.IsSupported) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var serverCertificate = CreateCertificate();
        using var clientCertificate = CreateCertificate();
        using var keys = FolderKeys.CreateNew();
        byte[] serverId = PeerTls.DeviceId(serverCertificate), clientId = PeerTls.DeviceId(clientCertificate);
        int strikes = 0;
        byte[]? struckDevice = null;
        MessageFailure? failure = null;
        string diagnosticRoot = Path.Combine(Path.GetTempPath(), "PeerSync-QuicStrikeTests", Guid.NewGuid().ToString("N"));
        using var diagnostics = new DiagnosticJournal(diagnosticRoot, "quic_protocol_strike", debugEnabled: true);
        var serverOptions = Options(serverCertificate, keys, clientId, serverId, _ => AuthDecision.Allowed,
            diagnostics, (device, reason) => { struckDevice = device; failure = reason; Interlocked.Increment(ref strikes); });
        var clientOptions = Options(clientCertificate, keys, serverId, clientId, _ => AuthDecision.Allowed, diagnostics);

        await using var listener = await QuicPeerListener.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), serverOptions, stop.Token);
        Task<QuicPeerSession> accepting = listener.AcceptAsync(stop.Token).AsTask();
        await using QuicPeerSession client = await QuicPeerSession.ConnectAsync((IPEndPoint)listener.LocalEndPoint,
            clientOptions, cancellationToken: stop.Token);
        await using QuicPeerSession server = await accepting;

        server.ReportTransportFailure(new MessageValidationException(MessageFailure.InvalidPath));
        server.ReportTransportFailure(new MessageValidationException(MessageFailure.InvalidPath));
        await server.Closed.WaitAsync(stop.Token);
        await client.Closed.WaitAsync(stop.Token);

        strikes.ShouldBe(1);
        struckDevice.ShouldBe(clientId);
        failure.ShouldBe(MessageFailure.InvalidPath);
        bool receivedProtocolClose = false;
        foreach (string line in File.ReadLines(Path.Combine(diagnosticRoot, "debug.jsonl")))
        {
            using System.Text.Json.JsonDocument item = System.Text.Json.JsonDocument.Parse(line);
            if (item.RootElement.GetProperty("eventName").GetString() == "transport.remote_close"
                && item.RootElement.GetProperty("itemsVisited").GetInt64() == (long)CloseReason.ProtocolViolation)
            { receivedProtocolClose = true; break; }
        }
        receivedProtocolClose.ShouldBeTrue();
        diagnostics.Dispose();
        string fullTempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PeerSync-QuicStrikeTests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(diagnosticRoot).StartsWith(fullTempRoot, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(diagnosticRoot, recursive: true);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException), BlockError.NotFound)]
    [InlineData(typeof(DirectoryNotFoundException), BlockError.NotFound)]
    [InlineData(typeof(UnauthorizedAccessException), BlockError.Forbidden)]
    [InlineData(typeof(TimeoutException), BlockError.Busy)]
    [InlineData(typeof(IOException), BlockError.Unavailable)]
    public void BlockServerMapsReadFailuresToStableProtocolErrors(Type exceptionType, BlockError expected)
    {
        Exception error = (Exception)Activator.CreateInstance(exceptionType)!;
        QuicFolderTransport.MapBlockError(error).ShouldBe(expected);
    }

    [Fact]
    public async Task MutualTlsHelloAndFolderProofsUseCertificateIdentityAndSharedTranscript()
    {
        if (!QuicListener.IsSupported) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var serverCertificate = CreateCertificate(); using var clientCertificate = CreateCertificate();
        using var keys = FolderKeys.CreateNew();
        byte[] serverId = PeerTls.DeviceId(serverCertificate), clientId = PeerTls.DeviceId(clientCertificate);
        byte[] serverObserved = []; byte[] clientObserved = [];
        var serverOptions = Options(serverCertificate, keys, clientId, serverId, id =>
        { serverObserved = id; return AuthDecision.Allowed; });
        var clientOptions = Options(clientCertificate, keys, serverId, clientId, id =>
        { clientObserved = id; return AuthDecision.Allowed; });

        await using var listener = await QuicPeerListener.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), serverOptions, stop.Token);
        Task<QuicPeerSession> accepting = listener.AcceptAsync(stop.Token).AsTask();
        await using QuicPeerSession client = await QuicPeerSession.ConnectAsync((IPEndPoint)listener.LocalEndPoint, clientOptions,
            cancellationToken: stop.Token);
        await using QuicPeerSession server = await accepting;

        client.RemoteDeviceId.ShouldBe(serverId); server.RemoteDeviceId.ShouldBe(clientId);
        client.SessionId.ShouldBe(server.SessionId);
        client.AuthenticatedFolders.Select(folder => Convert.ToHexString(folder.FolderId)).ShouldBe([Convert.ToHexString(keys.FolderId())]);
        server.AuthenticatedFolders.Select(folder => Convert.ToHexString(folder.FolderId)).ShouldBe([Convert.ToHexString(keys.FolderId())]);
        ((IPeerSessionIdentity)client.AuthenticatedFolders.Single().Transport!).SessionId.ShouldBe(client.SessionId);
        ((IPeerSessionIdentity)server.AuthenticatedFolders.Single().Transport!).SessionId.ShouldBe(server.SessionId);
        serverObserved.ShouldBe(clientId); clientObserved.ShouldBe(serverId);
        client.RemoteHello.DeviceId.Span.ToArray().ShouldBe(serverId);
        server.RemoteHello.DeviceId.Span.ToArray().ShouldBe(clientId);
    }

    [Fact]
    public async Task PendingOrDeniedFolderPolicyNeverCreatesAnEngineHandler()
    {
        if (!QuicListener.IsSupported) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var serverCertificate = CreateCertificate(); using var clientCertificate = CreateCertificate();
        using var keys = FolderKeys.CreateNew();
        string diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "PeerSync-QuicAuthTests", Guid.NewGuid().ToString("N"));
        using var diagnostics = new DiagnosticJournal(diagnosticsDirectory, "quic_auth_test", debugEnabled: true);
        byte[] serverId = PeerTls.DeviceId(serverCertificate), clientId = PeerTls.DeviceId(clientCertificate);
        var serverOptions = Options(serverCertificate, keys, clientId, clientId, _ => AuthDecision.Pending, diagnostics);
        var clientOptions = Options(clientCertificate, keys, serverId, serverId, _ => AuthDecision.Allowed);

        await using var listener = await QuicPeerListener.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), serverOptions, stop.Token);
        Task<QuicPeerSession> accepting = listener.AcceptAsync(stop.Token).AsTask();
        await using QuicPeerSession client = await QuicPeerSession.ConnectAsync((IPEndPoint)listener.LocalEndPoint, clientOptions,
            cancellationToken: stop.Token);
        await using QuicPeerSession server = await accepting;
        client.AuthenticatedFolders.ShouldBeEmpty(); server.AuthenticatedFolders.ShouldBeEmpty();
        await server.DisposeAsync(); await client.DisposeAsync();
        string log = File.ReadAllText(Path.Combine(diagnosticsDirectory, "debug.jsonl"));
        log.ShouldContain("transport.folder_auth_pending");
        log.ShouldContain("local_policy_pending");
        log.ShouldNotContain(Convert.ToHexString(keys.FolderId()));
        diagnostics.Dispose();
        string fullTempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PeerSync-QuicAuthTests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(diagnosticsDirectory).StartsWith(fullTempRoot, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(diagnosticsDirectory, recursive: true);
    }

    [Fact]
    public async Task DifferentCertificateDeviceIdFailsThePinBeforeFolderAdmission()
    {
        if (!QuicListener.IsSupported) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var serverCertificate = CreateCertificate(); using var clientCertificate = CreateCertificate(); using var wrongPinCertificate = CreateCertificate();
        using var keys = FolderKeys.CreateNew();
        byte[] serverId = PeerTls.DeviceId(serverCertificate), clientId = PeerTls.DeviceId(clientCertificate);
        byte[] wrongPin = PeerTls.DeviceId(wrongPinCertificate);
        var serverOptions = Options(serverCertificate, keys, clientId, serverId, _ => AuthDecision.Allowed);
        var clientOptions = Options(clientCertificate, keys, wrongPin, clientId, _ => AuthDecision.Allowed);
        await using var listener = await QuicPeerListener.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), serverOptions, stop.Token);
        Task<QuicPeerSession> accepting = listener.AcceptAsync(stop.Token).AsTask();
        await Should.ThrowAsync<AuthenticationException>(() => QuicPeerSession.ConnectAsync(
            (IPEndPoint)listener.LocalEndPoint, clientOptions, cancellationToken: stop.Token).AsTask());
        Exception serverFailure = await Should.ThrowAsync<Exception>(() => accepting);
        (serverFailure is QuicException or AuthenticationException).ShouldBeTrue();
    }

    [Fact]
    public async Task AuthenticatedFolderTransportCorrelatesIndexAndServesBoundedBlockStreams()
    {
        if (!QuicListener.IsSupported) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var serverCertificate = CreateCertificate(); using var clientCertificate = CreateCertificate();
        using var keys = FolderKeys.CreateNew();
        string diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "PeerSync-QuicTests", Guid.NewGuid().ToString("N"));
        using var diagnostics = new DiagnosticJournal(diagnosticsDirectory, "quic_test", debugEnabled: true);
        byte[] serverId = PeerTls.DeviceId(serverCertificate), clientId = PeerTls.DeviceId(clientCertificate);
        var serverOptions = Options(serverCertificate, keys, clientId, serverId, _ => AuthDecision.Allowed, diagnostics);
        var clientOptions = Options(clientCertificate, keys, serverId, clientId, _ => AuthDecision.Allowed, diagnostics);
        await using var listener = await QuicPeerListener.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), serverOptions, stop.Token);
        Task<QuicPeerSession> accepting = listener.AcceptAsync(stop.Token).AsTask();
        await using QuicPeerSession client = await QuicPeerSession.ConnectAsync((IPEndPoint)listener.LocalEndPoint, clientOptions,
            cancellationToken: stop.Token);
        await using QuicPeerSession server = await accepting;

        IPeerTransport transport = client.AuthenticatedFolders.Single().Transport!;
        Hello hello = new()
        {
            ProtocolVersion = 1, DeviceId = ByteString.CopyFrom(clientId), DeviceName = "client",
            ClientVersion = "test", Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            UtcTicks = DateTime.UtcNow.Ticks,
        };
        var helloTask = transport.ExchangeHelloAsync(hello, stop.Token).AsTask();
        var targetTask = transport.ReadIndexTargetAsync(stop.Token).AsTask();
        var pageTask = transport.ReadIndexAsync(0, cancellationToken: stop.Token).AsTask();
        // Exercise an actual content-addressed block on its own bounded stream.
        byte[] data = [1, 2, 3, 4];
        byte[] hash = Blake3.Hasher.Hash(data).AsSpan().ToArray();
        ReadOnlyMemory<byte> block = await transport.ReadBlockAsync(hash, data.Length, stop.Token);
        IReadOnlyList<PeerBlockResult> batch = await transport.ReadBlockBatchAsync(
            [new PeerBlockRequest(hash, data.Length), new PeerBlockRequest(hash, data.Length)], stop.Token);
        Hello remote = await helloTask;
        remote.DeviceId.Span.ToArray().ShouldBe(serverId);
        (await targetTask).ShouldBe(9);
        PeerSync.Core.Sync.PeerIndexPage page = await pageTask;
        page.AfterSequence.ShouldBe(0); page.LastSequence.ShouldBe(9); page.Records.Count.ShouldBe(1);
        FileRecordBody.Parser.ParseFrom(page.Records[0].Body).Path.ShouldBe("remote.txt");
        block.ToArray().ShouldBe(data); batch.Count.ShouldBe(2);
        batch.ShouldAllBe(item => item.ErrorCode == null && item.Data.ToArray().SequenceEqual(data));
        var clientPush = (IPeerIndexPushTransport)client.AuthenticatedFolders.Single().Transport!;
        var serverPush = (IPeerIndexPushTransport)server.AuthenticatedFolders.Single().Transport!;
        await serverPush.PublishIndexTargetAsync(17, stop.Token);
        (await clientPush.IndexTargets.ReadAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), stop.Token)).ShouldBe(17L);
        await clientPush.PublishIndexTargetAsync(19, stop.Token);
        (await serverPush.IndexTargets.ReadAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), stop.Token)).ShouldBe(19L);
        await server.DisposeAsync(); await client.DisposeAsync();
        string log = File.ReadAllText(Path.Combine(diagnosticsDirectory, "debug.jsonl"));
        log.ShouldContain("transport.authenticated"); log.ShouldContain("transport.request_start");
        log.ShouldContain("transport.request_complete"); log.ShouldContain("transport.block_stream_opened");
        log.ShouldContain("transport.index_push_sent"); log.ShouldContain("transport.index_push_received");
        log.ShouldContain("transport.remote_close"); log.ShouldContain("transport.disconnected");
        log.ShouldNotContain("remote.txt"); log.ShouldNotContain(Convert.ToHexString(keys.FolderId()));
        log.ShouldNotContain(Convert.ToHexString(hash));
        diagnostics.Dispose();
        string fullTempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PeerSync-QuicTests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(diagnosticsDirectory).StartsWith(fullTempRoot, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(diagnosticsDirectory, recursive: true);
    }

    private static QuicPeerOptions Options(X509Certificate2 certificate, FolderKeys keys,
        byte[] expectedIdentity, byte[] suppliedHelloIdentity, Func<byte[], AuthDecision> authorize,
        DiagnosticJournal? diagnostics = null, Action<byte[], MessageFailure>? addStrike = null) => new()
    {
        Certificate = certificate,
        IsDevicePinned = id => CryptographicOperations.FixedTimeEquals(id, expectedIdentity),
        Diagnostics = diagnostics,
        AddStrike = addStrike,
        CreateHello = () => new Hello
        {
            ProtocolVersion = 1,
            DeviceId = ByteString.CopyFrom(suppliedHelloIdentity), // Session replaces this untrusted input with TLS identity.
            DeviceName = "test-peer",
            ClientVersion = "test",
            Nonce = ByteString.CopyFrom(new byte[32]),
            UtcTicks = DateTime.UtcNow.Ticks,
        },
        DeviceName = "quic-test",
        ClientVersion = "1.0-test",
        Folders = [new PeerFolderRegistration(keys, authorize, id =>
        {
            id.ShouldBe(expectedIdentity);
            return new EmptyHandler(PeerTls.DeviceId(certificate), keys);
        })],
    };

    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PeerSync Loopback Test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Windows MsQuic requires a persisted private-key handle. Reimporting the
        // temporary PFX creates a short-lived user key container owned by this cert.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "test-only"), "test-only",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private sealed class EmptyHandler(byte[] localDeviceId, FolderKeys keys) : IPeerRequestHandler
    {
        private readonly PeerSync.Core.Sync.SignedRecord record = CreateRecord(keys);
        public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken)
            => ValueTask.FromResult(new Hello
            {
                ProtocolVersion = 1, DeviceId = ByteString.CopyFrom(localDeviceId), DeviceName = "test-peer",
                ClientVersion = "test", Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                UtcTicks = DateTime.UtcNow.Ticks, RequestId = hello.RequestId, Response = true,
            });
        public ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(9L);
        public ValueTask<Core.Sync.PeerIndexPage> ReadIndexAsync(long afterSequence, int limit, CancellationToken cancellationToken)
            => ValueTask.FromResult(new Core.Sync.PeerIndexPage(afterSequence, 9, afterSequence < 9 ? [record] : []));
        public ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken)
            => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1, 2, 3, 4 });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static PeerSync.Core.Sync.SignedRecord CreateRecord(FolderKeys keys)
        {
            byte[] device = new byte[32]; System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(device, 1);
            var body = new FileRecordBody
            {
                Path = "remote.txt", Size = 0, MtimeTicks = DateTime.UtcNow.Ticks, ChangedTicks = DateTime.UtcNow.Ticks,
                FileHash = ByteString.CopyFrom(Blake3.Hasher.Hash(ReadOnlySpan<byte>.Empty).AsSpan()),
                ModifiedBy = ByteString.CopyFrom(device), ChunkClass = ChunkParameters.ClassFor(0),
                Vv = { new VvEntry { Device = 1, Counter = 1 } },
            };
            return SignedRecordFactory.Create(body, keys, 9);
        }
    }
}
