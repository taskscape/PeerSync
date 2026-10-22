using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Google.Protobuf;
using Grpc.Core;
using PeerSync.Rendezvous.Contracts.V1;
using PeerSync.Rendezvous.Hosting;
using PeerSync.Rendezvous.Tracker;

namespace PeerSync.Rendezvous.Relay;

internal sealed class RelayRegistry : IDisposable
{
    private static readonly TimeSpan OfferLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<string, Ticket> tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceLimiter> deviceLimiters = new(StringComparer.Ordinal);
    private readonly TokenBucketRateLimiter totalLimiter;
    private readonly RendezvousOptions options;
    private readonly TimeProvider time;

    public RelayRegistry(RendezvousOptions options) : this(options, TimeProvider.System) { }

    internal RelayRegistry(RendezvousOptions options, TimeProvider time)
    {
        this.options = options;
        this.time = time;
        totalLimiter = CreateLimiter(options.RelayTotalBytesPerSecond);
    }

    internal bool TryOffer(PresenceSession source, PresenceSession target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(source, target)) return false;
        lock (gate)
        {
            ExpireLocked();
            if (tickets.Count >= 1024 || CountForDeviceLocked(source.Key) >= options.MaximumRelaySessionsPerDevice
                || CountForDeviceLocked(target.Key) >= options.MaximumRelaySessionsPerDevice) return false;
            var ticket = new Ticket(source.Key, target.Key, time.GetUtcNow());
            tickets.Add(ticket.TokenHex, ticket);
            var initiatorOffer = new RelayOffer
            {
                SessionId = ByteString.CopyFrom(ticket.SessionId.ToByteArray()),
                Token = ByteString.CopyFrom(ticket.Token),
                PeerDeviceId = ByteString.CopyFrom(target.DeviceId), Initiator = true,
            };
            var targetOffer = new RelayOffer
            {
                SessionId = ByteString.CopyFrom(ticket.SessionId.ToByteArray()),
                Token = ByteString.CopyFrom(ticket.Token),
                PeerDeviceId = ByteString.CopyFrom(source.DeviceId), Initiator = false,
            };
            if (source.Outgoing.Writer.TryWrite(new ServerMessage { RelayOffer = initiatorOffer })
                && target.Outgoing.Writer.TryWrite(new ServerMessage { RelayOffer = targetOffer })) return true;
            tickets.Remove(ticket.TokenHex);
            ticket.Abort();
            return false;
        }
    }

    internal async ValueTask<RelayJoin> JoinAsync(ReadOnlyMemory<byte> token, ReadOnlyMemory<byte> deviceId,
        IServerStreamWriter<RelayChunk> writer, CancellationToken cancellationToken)
    {
        if (token.Length != 32 || deviceId.Length != 32)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid relay token or device identity."));
        string tokenHex = Convert.ToHexString(token.Span);
        string deviceKey = Convert.ToHexString(deviceId.Span);
        RelayLeg leg;
        Ticket ticket;
        lock (gate)
        {
            ExpireLocked();
            if (!tickets.TryGetValue(tokenHex, out ticket!) || time.GetUtcNow() - ticket.CreatedUtc >= OfferLifetime)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Relay offer expired or unavailable."));
            if (deviceKey == ticket.InitiatorKey && ticket.Initiator is null)
                ticket.Initiator = leg = new RelayLeg(writer, deviceKey);
            else if (deviceKey == ticket.TargetKey && ticket.Target is null)
                ticket.Target = leg = new RelayLeg(writer, deviceKey);
            else
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Relay token is not available to this device."));
            if (ticket.Initiator is { } first && ticket.Target is { } second)
            {
                ticket.Idle.CancelAfter(IdleTimeout);
                first.Partner.TrySetResult(second);
                second.Partner.TrySetResult(first);
            }
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ticket.Idle.Token);
            timeout.CancelAfter(OfferLifetime);
            RelayLeg partner = await leg.Partner.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return new RelayJoin(ticket, leg, partner);
        }
        catch
        {
            Leave(ticket);
            throw;
        }
    }

    internal async ValueTask ThrottleAsync(RelayJoin join, int bytes, CancellationToken cancellationToken)
    {
        if (bytes is < 1 or > 64 * 1024)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Relay chunk exceeds the 64 KiB limit."));
        TokenBucketRateLimiter limiter;
        lock (gate)
        {
            if (!tickets.TryGetValue(join.Ticket.TokenHex, out Ticket? current)
                || !ReferenceEquals(current, join.Ticket))
                throw new RpcException(new Status(StatusCode.Aborted, "Relay session ended."));
            if (!deviceLimiters.TryGetValue(join.Leg.DeviceKey, out DeviceLimiter? state))
            {
                state = new DeviceLimiter(CreateLimiter(options.RelayPerDeviceBytesPerSecond), time.GetUtcNow());
                deviceLimiters.Add(join.Leg.DeviceKey, state);
            }
            state.LastUsedUtc = time.GetUtcNow();
            limiter = state.Limiter;
        }
        using RateLimitLease deviceLease = await limiter.AcquireAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (!deviceLease.IsAcquired)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Device relay quota exceeded."));
        using RateLimitLease totalLease = await totalLimiter.AcquireAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (!totalLease.IsAcquired)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Server relay quota exceeded."));
        join.Ticket.Touch(IdleTimeout);
    }

    internal void Leave(RelayJoin join) => Leave(join.Ticket);

    private void Leave(Ticket ticket)
    {
        lock (gate)
        {
            if (tickets.TryGetValue(ticket.TokenHex, out Ticket? current) && ReferenceEquals(current, ticket))
                tickets.Remove(ticket.TokenHex);
            ticket.Abort();
        }
    }

    internal int Expire()
    {
        lock (gate) return ExpireLocked();
    }

    internal void AbortAll()
    {
        lock (gate)
        {
            foreach (Ticket ticket in tickets.Values) ticket.Abort();
            tickets.Clear();
        }
    }

    private int ExpireLocked()
    {
        Ticket[] expired = tickets.Values.Where(item =>
            (item.Initiator is null || item.Target is null) && time.GetUtcNow() - item.CreatedUtc >= OfferLifetime).ToArray();
        foreach (Ticket ticket in expired)
        {
            tickets.Remove(ticket.TokenHex);
            ticket.Abort();
        }
        foreach ((string key, DeviceLimiter state) in deviceLimiters.ToArray())
        {
            if (time.GetUtcNow() - state.LastUsedUtc < TimeSpan.FromMinutes(15)
                || CountForDeviceLocked(key) != 0) continue;
            deviceLimiters.Remove(key);
            state.Limiter.Dispose();
        }
        return expired.Length;
    }

    private int CountForDeviceLocked(string deviceKey)
        => tickets.Values.Count(item => item.InitiatorKey == deviceKey || item.TargetKey == deviceKey);

    private static TokenBucketRateLimiter CreateLimiter(int bytesPerSecond) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = Math.Max(bytesPerSecond, 64 * 1024),
        TokensPerPeriod = bytesPerSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        AutoReplenishment = true,
        QueueLimit = 4 * 1024 * 1024,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    public void Dispose()
    {
        lock (gate)
        {
            foreach (Ticket ticket in tickets.Values) ticket.Abort();
            tickets.Clear();
            foreach (DeviceLimiter state in deviceLimiters.Values) state.Limiter.Dispose();
            deviceLimiters.Clear();
            totalLimiter.Dispose();
        }
    }

    private sealed class DeviceLimiter(TokenBucketRateLimiter limiter, DateTimeOffset lastUsedUtc)
    {
        internal TokenBucketRateLimiter Limiter { get; } = limiter;
        internal DateTimeOffset LastUsedUtc { get; set; } = lastUsedUtc;
    }

    internal sealed class Ticket(string initiatorKey, string targetKey, DateTimeOffset createdUtc)
    {
        internal Guid SessionId { get; } = Guid.NewGuid();
        internal byte[] Token { get; } = RandomNumberGenerator.GetBytes(32);
        internal string TokenHex => Convert.ToHexString(Token);
        internal string InitiatorKey { get; } = initiatorKey;
        internal string TargetKey { get; } = targetKey;
        internal DateTimeOffset CreatedUtc { get; } = createdUtc;
        internal RelayLeg? Initiator { get; set; }
        internal RelayLeg? Target { get; set; }
        internal CancellationTokenSource Idle { get; } = new();
        internal void Touch(TimeSpan timeout) => Idle.CancelAfter(timeout);
        internal void Abort()
        {
            if (!Idle.IsCancellationRequested) Idle.Cancel();
            Initiator?.Partner.TrySetCanceled();
            Target?.Partner.TrySetCanceled();
        }
    }

    internal sealed class RelayLeg(IServerStreamWriter<RelayChunk> writer, string deviceKey)
    {
        internal IServerStreamWriter<RelayChunk> Writer { get; } = writer;
        internal string DeviceKey { get; } = deviceKey;
        internal TaskCompletionSource<RelayLeg> Partner { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed record RelayJoin(Ticket Ticket, RelayLeg Leg, RelayLeg Partner)
    {
        internal CancellationToken SessionEnded => Ticket.Idle.Token;
        internal Guid SessionId => Ticket.SessionId;
        internal IServerStreamWriter<RelayChunk> PartnerWriter => Partner.Writer;
    }
}
