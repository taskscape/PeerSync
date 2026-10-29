using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using PeerSync.Net.Relay;
using PeerSync.Net;
using PeerSync.Net.Rendezvous;
using PeerSync.Net.Security;
using PeerSync.Net.Quic;
using PeerSync.Crypto;
using PeerSync.Core.Sync;
using PeerSync.Core.Diagnostics;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Wire;
using PeerSync.Rendezvous.Contracts.V1;
using PeerSync.Rendezvous.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Rendezvous.Tests;

public sealed class RendezvousFlowTests
{
    [Fact]
    public async Task TrackerAndServerJournalsShareTheObservedSessionId()
    {
        using X509Certificate2 server = Certificate(server: true);
        using X509Certificate2 device = Certificate(server: false);
        int port = RendezvousTestPort.Allocate();
        var origin = new Uri($"https://127.0.0.1:{port}");
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests", "tracker-session-correlation",
            Guid.NewGuid().ToString("N")));
        string serverLogs = Path.Combine(root, "server");
        string clientLogs = Path.Combine(root, "client");
        await using WebApplication app = RendezvousHost.Build(
            ["--Rendezvous:Port=" + port, "--Rendezvous:PublicUrl=" + origin,
                "--Rendezvous:LogDirectory=" + serverLogs], server, IPAddress.Loopback);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var journal = new DiagnosticJournal(clientLogs, "tracker");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var messages = Channel.CreateUnbounded<ServerMessage>();
        var announcement = new Announce { ListenPort = 47821 };
        await using (var tracker = new TrackerClient(origin, device, () => announcement.Clone(),
            (message, _) => { messages.Writer.TryWrite(message.Clone()); return ValueTask.CompletedTask; },
            diagnostics: journal, loopbackServerCertificate: server))
        {
            tracker.Start();
            ServerMessage observed = await ReadUntilAsync(messages.Reader,
                message => message.ObservedAddress is not null, timeout.Token);
            observed.ObservedAddress.SessionId.Length.ShouldBe(16);
            Guid sessionId = new(observed.ObservedAddress.SessionId.Span);
            Event(clientLogs, "tracker.session_identified")
                .GetProperty("sessionId").GetGuid().ShouldBe(sessionId);
            Event(serverLogs, "rendezvous.session_opened")
                .GetProperty("sessionId").GetGuid().ShouldBe(sessionId);
        }
        await app.StopAsync(timeout.Token);
        Guid closedSession = Event(serverLogs, "rendezvous.session_closed")
            .GetProperty("sessionId").GetGuid();
        Event(clientLogs, "tracker.session_disconnected")
            .GetProperty("sessionId").GetGuid().ShouldBe(closedSession);
        File.ReadAllText(Path.Combine(clientLogs, "debug.jsonl"))
            .ShouldNotContain("\"eventName\":\"tracker.session_failed\"");
        Event(serverLogs, "rendezvous.session_closed")
            .GetProperty("elapsedMs").GetDouble().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task TwoDeviceTrackersIsolateFoldersAndRelayCarriesEndToEndTls()
    {
        if (!System.Net.Quic.QuicListener.IsSupported) return;
        using X509Certificate2 server = Certificate(server: true);
        using X509Certificate2 alice = Certificate(server: false);
        using X509Certificate2 bob = Certificate(server: false);
        using X509Certificate2 charlie = Certificate(server: false);
        byte[] shared = RandomNumberGenerator.GetBytes(32);
        byte[] separate = RandomNumberGenerator.GetBytes(32);
        int port = RendezvousTestPort.Allocate();
        var origin = new Uri($"https://127.0.0.1:{port}");
        string logs = Path.GetFullPath(Path.Combine("artifacts", "tests", "rendezvous-flow"));
        await using WebApplication app = RendezvousHost.Build(
            ["--Rendezvous:Port=" + port, "--Rendezvous:PublicUrl=" + origin,
                "--Rendezvous:LogDirectory=" + logs], server, IPAddress.Loopback);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var aliceMessages = Channel.CreateUnbounded<ServerMessage>();
        var bobMessages = Channel.CreateUnbounded<ServerMessage>();
        var charlieMessages = Channel.CreateUnbounded<ServerMessage>();
        await using var aliceTracker = Client(origin, server, alice, shared, aliceMessages);
        await using var bobTracker = Client(origin, server, bob, shared, bobMessages);
        await using var charlieTracker = Client(origin, server, charlie, separate, charlieMessages);
        aliceTracker.Start(); bobTracker.Start(); charlieTracker.Start();
        byte[] aliceId = PeerTls.DeviceId(alice);
        byte[] bobId = PeerTls.DeviceId(bob);
        await ReadUntilAsync(aliceMessages.Reader, message =>
            message.PeerList?.Peers.Any(peer => peer.DeviceId.Span.SequenceEqual(bobId)) == true, timeout.Token);
        await ReadUntilAsync(bobMessages.Reader, message =>
            message.PeerList?.Peers.Any(peer => peer.DeviceId.Span.SequenceEqual(aliceId)) == true, timeout.Token);
        ServerMessage isolated = await ReadUntilAsync(charlieMessages.Reader,
            message => message.PeerList is not null, timeout.Token);
        isolated.PeerList.Peers.ShouldBeEmpty();

        aliceTracker.RequestRelay(bobId, shared).ShouldBeTrue();
        RelayOffer aliceOffer = (await ReadUntilAsync(aliceMessages.Reader,
            message => message.RelayOffer is not null, timeout.Token)).RelayOffer;
        RelayOffer bobOffer = (await ReadUntilAsync(bobMessages.Reader,
            message => message.RelayOffer is not null, timeout.Token)).RelayOffer;
        aliceOffer.Initiator.ShouldBeTrue();
        bobOffer.Initiator.ShouldBeFalse();
        aliceOffer.Token.ShouldBe(bobOffer.Token);

        using var connectionAttempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<RelayTransport> dial = RelayClient.ConnectAsync(origin, aliceOffer, alice,
            peer => peer.AsSpan().SequenceEqual(bobId), connectionAttempt.Token, server).AsTask();
        Task<RelayTransport> accept = RelayClient.ConnectAsync(origin, bobOffer, bob,
            peer => peer.AsSpan().SequenceEqual(aliceId), connectionAttempt.Token, server).AsTask();
        await Task.WhenAll(dial, accept);
        await using RelayTransport left = await dial;
        await using RelayTransport right = await accept;
        connectionAttempt.Cancel();
        byte[] payload = "end-to-end relay proof"u8.ToArray();
        await left.Stream.WriteAsync(payload, timeout.Token);
        byte[] received = new byte[payload.Length];
        await right.Stream.ReadExactlyAsync(received, timeout.Token);
        received.ShouldBe(payload);

        await left.DisposeAsync();
        await right.DisposeAsync();
        aliceTracker.RequestRelay(bobId, shared).ShouldBeTrue();
        RelayOffer protocolAlice = (await ReadUntilAsync(aliceMessages.Reader,
            message => message.RelayOffer is not null, timeout.Token)).RelayOffer;
        RelayOffer protocolBob = (await ReadUntilAsync(bobMessages.Reader,
            message => message.RelayOffer is not null, timeout.Token)).RelayOffer;
        Task<RelayTransport> protocolDial = RelayClient.ConnectAsync(origin, protocolAlice, alice,
            peer => peer.AsSpan().SequenceEqual(bobId), timeout.Token, server).AsTask();
        Task<RelayTransport> protocolAccept = RelayClient.ConnectAsync(origin, protocolBob, bob,
            peer => peer.AsSpan().SequenceEqual(aliceId), timeout.Token, server).AsTask();
        await Task.WhenAll(protocolDial, protocolAccept);
        using FolderKeys keys = FolderKeys.CreateNew();
        byte[] block = [1, 2, 3, 4];
        byte[] hash = Blake3.Hasher.Hash(block).AsSpan().ToArray();
        var aliceFolders = new PeerFolderRegistration[]
        { new(keys, _ => AuthDecision.Allowed, _ => new TestHandler(aliceId, block)) };
        var bobFolders = new PeerFolderRegistration[]
        { new(keys, _ => AuthDecision.Allowed, _ => new TestHandler(bobId, block)) };
        Func<Hello> hello = () => new Hello { ProtocolVersion = 1, UtcTicks = DateTime.UtcNow.Ticks };
        Task<RelayPeerProtocolSession> startAlice = RelayPeerProtocolSession.StartAsync(await protocolDial,
            hello, aliceFolders, null, null, TimeSpan.FromSeconds(15), timeout.Token).AsTask();
        Task<RelayPeerProtocolSession> startBob = RelayPeerProtocolSession.StartAsync(await protocolAccept,
            hello, bobFolders, null, null, TimeSpan.FromSeconds(15), timeout.Token).AsTask();
        await Task.WhenAll(startAlice, startBob);
        await using RelayPeerProtocolSession aliceSession = await startAlice;
        await using RelayPeerProtocolSession bobSession = await startBob;
        aliceSession.AuthenticatedFolders.Count.ShouldBe(1);
        bobSession.AuthenticatedFolders.Count.ShouldBe(1);
        IPeerTransport aliceFolder = aliceSession.AuthenticatedFolders[0].Transport!;
        (await aliceFolder.ReadIndexTargetAsync(timeout.Token)).ShouldBe(0);
        (await aliceFolder.ReadBlockAsync(hash, block.Length, timeout.Token)).ToArray().ShouldBe(block);
        IPeerTransport bobFolder = bobSession.AuthenticatedFolders[0].Transport!;
        (await bobFolder.ReadIndexTargetAsync(timeout.Token)).ShouldBe(0);
        (await bobFolder.ReadBlockAsync(hash, block.Length, timeout.Token)).ToArray().ShouldBe(block);
    }

    private static TrackerClient Client(Uri origin, X509Certificate2 server, X509Certificate2 identity, byte[] folderId,
        Channel<ServerMessage> messages)
    {
        var announce = new Announce { ListenPort = 47821 };
        announce.FolderIds.Add(ByteString.CopyFrom(folderId));
        return new TrackerClient(origin, identity, () => announce.Clone(),
            (message, _) => { messages.Writer.TryWrite(message.Clone()); return ValueTask.CompletedTask; },
            loopbackServerCertificate: server);
    }

    private static async Task<ServerMessage> ReadUntilAsync(ChannelReader<ServerMessage> messages,
        Func<ServerMessage, bool> match, CancellationToken cancellationToken)
    {
        await foreach (ServerMessage message in messages.ReadAllAsync(cancellationToken))
            if (match(message)) return message;
        throw new EndOfStreamException("Tracker session closed before the expected message.");
    }

    private static JsonElement Event(string directory, string eventName)
    {
        foreach (string line in File.ReadLines(Path.Combine(directory, "debug.jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("eventName").GetString() == eventName)
                return document.RootElement.Clone();
        }
        throw new InvalidOperationException($"Missing diagnostic event {eventName}.");
    }

    private static X509Certificate2 Certificate(bool server)
    {
        if (server)
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using X509Certificate2 ephemeral = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "server-test"),
                "server-test", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        using ECDsa deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceRequest = new CertificateRequest("CN=PeerSync Device", deviceKey, HashAlgorithmName.SHA256);
        deviceRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 temporary = deviceRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx, "device-test"),
            "device-test", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private sealed class TestHandler(byte[] localId, byte[] block) : IPeerRequestHandler
    {
        public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken)
            => ValueTask.FromResult(new Hello { ProtocolVersion = 1, DeviceId = ByteString.CopyFrom(localId),
                UtcTicks = DateTime.UtcNow.Ticks, Response = true, RequestId = hello.RequestId });
        public ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(0L);
        public ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit, CancellationToken cancellationToken)
            => ValueTask.FromResult(new PeerIndexPage(afterSequence, afterSequence, []));
        public ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<ReadOnlyMemory<byte>>(block);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
