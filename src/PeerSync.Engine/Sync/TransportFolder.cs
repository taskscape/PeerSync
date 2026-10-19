using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Net;
using PeerSync.Protocol.Handshake;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

/// <summary>A connected folder engine. The host authenticates device and folder
/// membership before creating handlers/transports. Index/keys remain caller-owned.
/// Finish folder work and drain inbound transports before disposing the folder.</summary>
public sealed class TransportFolder : IAsyncDisposable
{
    private readonly FolderIndex index;
    private readonly byte[] own;
    private readonly byte[] folderId;
    private readonly TimeProvider clock;
    private readonly string rootPath;
    private readonly Puller puller;
    private readonly ConcurrentDictionary<string, PeerFolderConnection> peers = new(StringComparer.Ordinal);
    private readonly PeerClockStatusLedger clockStatuses = new();
    private readonly object lifecycle = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    public TransportFolder(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        FolderSettings? settings = null, TimeProvider? timeProvider = null, DiagnosticJournal? journal = null)
        : this(rootPath, index, keys, ownDevice, settings, timeProvider, journal, directorySnapshots: false) { }

    internal TransportFolder(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        FolderSettings? settings, TimeProvider? timeProvider, DiagnosticJournal? journal, bool directorySnapshots)
    {
        ArgumentNullException.ThrowIfNull(ownDevice);
        ArgumentNullException.ThrowIfNull(keys);
        if (ownDevice.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(ownDevice));
        this.rootPath = rootPath; this.index = index; own = ownDevice.ToArray(); clock = timeProvider ?? TimeProvider.System;
        folderId = keys.FolderId();
        puller = new(rootPath, index, keys, own, FetchAsync, settings, clock, journal, directorySnapshots);
    }

    public Task<PullPassResult> RunPassAsync(CancellationToken cancellationToken = default)
    { RequireOpen(); return puller.RunPassAsync(cancellationToken); }
    public void RequestScan() { RequireOpen(); puller.RequestScan(); }
    public byte[] FolderId { get { RequireOpen(); return folderId.ToArray(); } }

    /// <summary>Active authenticated-session clocks and bounded recent closed
    /// failures, for service status. Truncation is explicit; this grants no admission.</summary>
    public PeerClockStatusPage ReadClockStatuses(int limit = 1000)
    { RequireOpen(); return clockStatuses.Read(limit); }

    private PeerIndexSession OpenSession(byte[] authenticatedPeer)
    {
        var session = puller.OpenPeerIndexSession(authenticatedPeer); clockStatuses.Register(session); return session;
    }

    public IPeerRequestHandler CreateHandler(byte[] authenticatedPeer)
    { RequireOpen(); return new Handler(this, OpenSession(authenticatedPeer)); }

    /// <summary>Takes ownership of the supplied transport, including on failure.</summary>
    public async Task<PeerFolderConnection> ConnectAsync(IPeerTransport transport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport); PeerFolderConnection? connection = null;
        try
        {
            RequireOpen(); byte[] remote = transport.RemoteDeviceId.ToArray();
            if (remote.AsSpan().SequenceEqual(own)) throw new InvalidOperationException("Cannot connect folder to itself.");
            connection = new(this, transport, OpenSession(remote));
            await connection.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            lock (lifecycle)
            {
                RequireOpen();
                if (!peers.TryAdd(Convert.ToHexString(remote), connection)) throw new InvalidOperationException("Peer already connected.");
            }
            return connection;
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            else await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<ReadOnlyMemory<byte>> FetchAsync(ReadOnlyMemory<byte> source, ReadOnlyMemory<byte> hash, int length, CancellationToken token)
    {
        RequireOpen(); token.ThrowIfCancellationRequested();
        try
        {
            if (!peers.TryGetValue(Convert.ToHexString(source.Span), out var peer)) throw new IOException("Source peer is disconnected.");
            return await peer.FetchAsync(hash, length, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException && !token.IsCancellationRequested && Volatile.Read(ref disposed) == 0)
        { throw new IOException("Source peer disconnected during transfer.", error); }
    }
    internal Hello CreateHello() => new() { ProtocolVersion = 1, DeviceId = ByteString.CopyFrom(own),
        Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)), UtcTicks = clock.GetUtcNow().UtcDateTime.Ticks };
    internal static void ValidateHello(Hello hello, byte[] peer)
    {
        if (hello.ProtocolVersion != 1 || hello.Nonce.Length != 32 || !hello.DeviceId.Span.SequenceEqual(peer))
            throw new InvalidDataException("Hello does not match the authenticated connection.");
    }
    private void RequireOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    internal void Remove(PeerFolderConnection peer)
        => peers.TryRemove(new KeyValuePair<string, PeerFolderConnection>(Convert.ToHexString(peer.RemoteDeviceId), peer));
    internal void RetireClockSession(PeerIndexSession session) => clockStatuses.Retire(session);

    public ValueTask DisposeAsync()
    {
        PeerFolderConnection[] snapshot;
        lock (lifecycle)
        {
            if (disposed != 0) return new(closed.Task);
            Volatile.Write(ref disposed, 1); snapshot = peers.Values.ToArray();
        }
        _ = CloseAsync(snapshot); return new(closed.Task);
    }
    private async Task CloseAsync(PeerFolderConnection[] snapshot)
    {
        try
        {
            try { await Task.WhenAll(snapshot.Select(peer => peer.DisposeAsync().AsTask())).ConfigureAwait(false); }
            finally { puller.Dispose(); }
            closed.TrySetResult();
        }
        catch (Exception error) { closed.TrySetException(error); }
    }

    private sealed class Handler(TransportFolder folder, PeerIndexSession session) : IPeerRequestHandler
    {
        public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken)
        {
            folder.RequireOpen(); cancellationToken.ThrowIfCancellationRequested(); ValidateHello(hello, session.RemoteDeviceId);
            // Each endpoint needs the other's UTC sample to report its own
            // signed offset. Index/block methods still require clock admission.
            session.ObserveHello(hello); return ValueTask.FromResult(folder.CreateHello());
        }
        public ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken)
        { folder.RequireOpen(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(session.ReadIndexTarget()); }
        public ValueTask<Core.Sync.PeerIndexPage> ReadIndexAsync(long afterSequence, int limit, CancellationToken cancellationToken)
        { folder.RequireOpen(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(session.ReadIndex(afterSequence, limit)); }
        public async ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken)
        {
            folder.RequireOpen(); session.RequireClock();
            byte[] frozen = hash.ToArray();
            var bytes = await Task.Run(() =>
            {
                using var blocks = new PublishedBlocks(folder.rootPath, folder.index);
                return blocks.TryRead(frozen, length, cancellationToken) ?? throw new IOException("Published block is unavailable.");
            }, cancellationToken).ConfigureAwait(false);
            session.RequireClock(); return bytes;
        }
        public ValueTask DisposeAsync() { session.Disconnect(); folder.RetireClockSession(session); return ValueTask.CompletedTask; }
    }
}
