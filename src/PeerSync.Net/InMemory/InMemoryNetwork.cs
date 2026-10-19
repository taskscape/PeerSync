using System.Diagnostics;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Sync;
using PeerSync.Protocol.Handshake;

namespace PeerSync.Net.InMemory;

public sealed record InMemoryLinkOptions(TimeSpan Latency = default, int DropEvery = 0, bool Partitioned = false);

/// <summary>Trusted test fabric, not TLS or folder authentication. Possession of
/// a registered endpoint grants its source identity. A new server handler is
/// created for each connection. Configuration changes affect in-flight delivery.</summary>
public sealed class InMemoryNetwork(TimeProvider? timeProvider = null, DiagnosticJournal? journal = null)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Endpoint> endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string To), Link> links = [];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly DiagnosticJournal? journal = journal;

    public Endpoint Register(byte[] deviceId, Func<byte[], IPeerRequestHandler> createHandler)
    {
        ArgumentNullException.ThrowIfNull(createHandler); string key = Identity(deviceId);
        lock (gate)
        {
            if (endpoints.ContainsKey(key)) throw new InvalidOperationException("Device is already registered.");
            var endpoint = new Endpoint(this, key, createHandler); endpoints.Add(key, endpoint); return endpoint;
        }
    }

    public void ConfigureLink(byte[] from, byte[] to, InMemoryLinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Latency < TimeSpan.Zero || options.Latency > TimeSpan.FromDays(1) || options.DropEvery < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        var key = (Identity(from), Identity(to)); lock (gate) links[key] = new(options);
    }

    private async ValueTask DeliverAsync(string from, string to, CancellationToken token)
    {
        TimeSpan latency; bool lost;
        lock (gate)
        {
            if (!links.TryGetValue((from, to), out var link)) { latency = TimeSpan.Zero; lost = false; }
            else { latency = link.Options.Latency; lost = link.Options.Partitioned || (link.Options.DropEvery > 0 && ++link.Deliveries % link.Options.DropEvery == 0); }
        }
        token.ThrowIfCancellationRequested();
        if (latency > TimeSpan.Zero) await Task.Delay(latency, clock, token).ConfigureAwait(false);
        // Recheck partitions after a delayed packet: partitioning a live link
        // cannot let an already queued request/response escape.
        lock (gate) if (links.TryGetValue((from, to), out var current)) lost |= current.Options.Partitioned;
        token.ThrowIfCancellationRequested();
        if (lost) throw new IOException("In-memory delivery lost.");
    }

    private sealed class Link(InMemoryLinkOptions options)
    { public InMemoryLinkOptions Options { get; } = options; public long Deliveries { get; set; } }

    private static string Identity(byte[] id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(id));
        return Convert.ToHexString(id);
    }

    public sealed class Endpoint
    {
        private readonly InMemoryNetwork network;
        private readonly string identity;
        private readonly Func<byte[], IPeerRequestHandler> createHandler;
        internal Endpoint(InMemoryNetwork network, string identity, Func<byte[], IPeerRequestHandler> createHandler)
        { this.network = network; this.identity = identity; this.createHandler = createHandler; }
        public byte[] DeviceId => Convert.FromHexString(identity);

        public IPeerTransport Connect(byte[] remoteDeviceId)
        {
            string remote = Identity(remoteDeviceId); Endpoint endpoint;
            lock (network.gate)
            {
                if (!network.endpoints.TryGetValue(remote, out endpoint!)) throw new IOException("Peer is not registered.");
                if (remote == identity) throw new ArgumentException("Cannot connect to self.", nameof(remoteDeviceId));
            }
            // User code must never run under the fabric's lock.
            var handler = endpoint.createHandler(DeviceId) ?? throw new InvalidOperationException("Missing peer handler.");
            return new Connection(network, identity, remote, handler);
        }
    }

    private sealed class Connection(InMemoryNetwork network, string local, string remote, IPeerRequestHandler handler) : IPeerTransport
    {
        private readonly object gate = new();
        private readonly SemaphoreSlim slots = new(64, 64);
        private readonly CancellationTokenSource lifetime = new();
        private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Guid session = Guid.NewGuid();
        private int active;
        private bool disposed;
        public byte[] RemoteDeviceId => Convert.FromHexString(remote);

        public ValueTask<Hello> ExchangeHelloAsync(Hello hello, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(hello); var frozen = hello.Clone();
            return RequestAsync("hello", async token => (await handler.ExchangeHelloAsync(frozen, token).ConfigureAwait(false)).Clone(), cancellationToken);
        }
        public ValueTask<long> ReadIndexTargetAsync(CancellationToken cancellationToken = default)
            => RequestAsync("index_target", handler.ReadIndexTargetAsync, cancellationToken);
        public ValueTask<PeerIndexPage> ReadIndexAsync(long afterSequence, int limit = 1000, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
            if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            return RequestAsync("index_page", async token =>
            {
                var page = await handler.ReadIndexAsync(afterSequence, limit, token).ConfigureAwait(false);
                if (page.AfterSequence != afterSequence || page.Records.Count > limit) throw new InvalidDataException("Invalid index response.");
                return page; // PeerIndexPage and SignedRecord own immutable buffers.
            }, cancellationToken);
        }
        public ValueTask<ReadOnlyMemory<byte>> ReadBlockAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellationToken = default)
        {
            if (hash.Length != 32 || length is < 1 or > 8 * 1024 * 1024) throw new ArgumentException("Invalid block request.");
            byte[] frozen = hash.ToArray();
            return RequestAsync<ReadOnlyMemory<byte>>("block", async token =>
            {
                var bytes = await handler.ReadBlockAsync(frozen, length, token).ConfigureAwait(false);
                if (bytes.Length != length) throw new InvalidDataException("Invalid block response length.");
                return bytes.ToArray();
            }, cancellationToken);
        }

        private async ValueTask<T> RequestAsync<T>(string operation, Func<CancellationToken, ValueTask<T>> execute, CancellationToken token)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (active >= 64) throw new IOException("Outstanding request limit reached.");
                active++;
            }
            long start = Stopwatch.GetTimestamp(); bool acquired = false;
            using var activity = new Activity("PeerSync.InMemoryRequest").SetIdFormat(ActivityIdFormat.W3C).Start();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                await slots.WaitAsync(linked.Token).ConfigureAwait(false); acquired = true;
                network.journal?.TryWrite(new("transport.request_start", operation) { SessionId = session });
                await network.DeliverAsync(local, remote, linked.Token).ConfigureAwait(false);
                var result = await execute(linked.Token).ConfigureAwait(false);
                await network.DeliverAsync(remote, local, linked.Token).ConfigureAwait(false);
                network.journal?.TryWrite(new("transport.request_complete", operation) { SessionId = session, ElapsedMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
                return result;
            }
            catch (Exception error)
            {
                network.journal?.TryWrite(new("transport.request_failed", operation) { SessionId = session, Level = DiagnosticLevel.Warning,
                    ErrorCode = error.GetType().Name, HResult = error.HResult, ElapsedMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds }); throw;
            }
            finally
            {
                if (acquired) slots.Release();
                lock (gate) { active--; if (disposed && active == 0) drained.TrySetResult(); }
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposed) return new(closed.Task);
                disposed = true; if (active == 0) drained.TrySetResult();
            }
            _ = CloseAsync(); return new(closed.Task);
        }
        private async Task CloseAsync()
        {
            try
            {
                Exception? cancellationError = null;
                try { await lifetime.CancelAsync().ConfigureAwait(false); }
                catch (Exception error) { cancellationError = error; }
                await drained.Task.ConfigureAwait(false);
                await handler.DisposeAsync().ConfigureAwait(false);
                network.journal?.TryWrite(new("transport.disconnected", "connection") { SessionId = session });
                if (cancellationError is not null) closed.TrySetException(cancellationError); else closed.TrySetResult();
            }
            catch (Exception error) { closed.TrySetException(error); }
            finally { slots.Dispose(); lifetime.Dispose(); }
        }
    }
}
