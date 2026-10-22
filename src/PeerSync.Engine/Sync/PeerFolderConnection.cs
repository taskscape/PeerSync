using PeerSync.Core.Sync;
using PeerSync.Net;

namespace PeerSync.Engine.Sync;

public sealed record PeerIndexSyncResult(int Pages, long LastSequence, bool Complete);

/// <summary>One connected index receiver and chunk source. Transfers use the
/// interface only; index mutations always pass through peer clock admission.</summary>
public sealed class PeerFolderConnection : IAsyncDisposable
{
    private readonly TransportFolder owner;
    private readonly IPeerTransport transport;
    private readonly PeerIndexSession session;
    private readonly byte[] remote;
    private readonly SemaphoreSlim synchronization = new(1, 1);
    private readonly object lifecycle = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? liveIndexPump;
    private int active;
    private int disposed;
    internal PeerFolderConnection(TransportFolder owner, IPeerTransport transport, PeerIndexSession session)
    { this.owner = owner; this.transport = transport; this.session = session; remote = session.RemoteDeviceId; }
    public byte[] RemoteDeviceId => remote.ToArray();
    public PeerClockStatus ClockStatus => session.ClockStatus;
    public Guid? SharedSessionId => (transport as IPeerSessionIdentity)?.SessionId;

    internal void StartLiveIndexPump()
    {
        if (transport is not IPeerIndexPushTransport push) return;
        lock (lifecycle)
        {
            RequireOpen();
            if (liveIndexPump is not null) throw new InvalidOperationException("Live index listener already started.");
            liveIndexPump = ListenForIndexPushesAsync(push);
        }
    }

    internal async ValueTask PublishIndexTargetAsync(long highestSequence, CancellationToken cancellationToken)
    {
        if (transport is not IPeerIndexPushTransport push) return;
        Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try { await push.PublishIndexTargetAsync(highestSequence, linked.Token).ConfigureAwait(false); }
        finally { Exit(); }
    }

    private async Task ListenForIndexPushesAsync(IPeerIndexPushTransport push)
    {
        try
        {
            await foreach (long target in push.IndexTargets.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    PeerIndexSyncResult result;
                    do { result = await SynchronizeAsync(cancellationToken: lifetime.Token).ConfigureAwait(false); }
                    while (!result.Complete && !lifetime.IsCancellationRequested);
                    owner.RecordIndexPush("index.push_applied", target, SharedSessionId);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception error) { owner.RecordIndexPush("index.push_failed", target, SharedSessionId, error); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    internal async Task HandshakeAsync(CancellationToken token)
    {
        var hello = await transport.ExchangeHelloAsync(owner.CreateHello(), token).ConfigureAwait(false);
        TransportFolder.ValidateHello(hello, remote); session.ObserveHello(hello); session.RequireClock();
    }

    public async Task<PeerIndexSyncResult> SynchronizeAsync(int maxPages = 64, CancellationToken cancellationToken = default)
    {
        if (maxPages is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxPages));
        Enter(); bool acquired = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        try
        {
            await synchronization.WaitAsync(token).ConfigureAwait(false); acquired = true;
            RequireOpen(); session.RequireClock();
            long target = await transport.ReadIndexTargetAsync(token).ConfigureAwait(false);
            var range = await session.AnnounceAsync(target, token).ConfigureAwait(false);
            long cursor = range.ReceivedSequence; target = range.TargetSequence; int pages = 0;
            while (cursor < target && pages < maxPages)
            {
                session.RequireClock(); var page = await transport.ReadIndexAsync(cursor, 1000, token).ConfigureAwait(false);
                if (page.AfterSequence != cursor || page.LastSequence <= cursor) throw new InvalidDataException("Index response did not advance.");
                if (page.LastSequence > target)
                {
                    long currentTarget = await transport.ReadIndexTargetAsync(token).ConfigureAwait(false);
                    if (currentTarget < page.LastSequence) throw new InvalidDataException("Sender target precedes its page cursor.");
                    target = currentTarget; await session.AnnounceAsync(target, token).ConfigureAwait(false);
                }
                var result = await session.ReceiveAsync(page, token).ConfigureAwait(false); cursor = result.LastSequence; pages++;
            }
            return new(pages, cursor, cursor >= target);
        }
        finally { if (acquired) synchronization.Release(); Exit(); }
    }

    internal async ValueTask<ReadOnlyMemory<byte>> FetchAsync(ReadOnlyMemory<byte> hash, int length, CancellationToken token)
    {
        Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try
        {
            session.RequireClock(); var bytes = await transport.ReadBlockAsync(hash, length, linked.Token).ConfigureAwait(false);
            RequireOpen(); session.RequireClock(); return bytes;
        }
        finally { Exit(); }
    }

    internal async ValueTask<IReadOnlyList<PeerBlockResult>> FetchBatchAsync(
        IReadOnlyList<PeerBlockRequest> requests, CancellationToken token)
    {
        Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try
        {
            session.RequireClock();
            IReadOnlyList<PeerBlockResult> results = await transport.ReadBlockBatchAsync(requests, linked.Token).ConfigureAwait(false);
            RequireOpen(); session.RequireClock();
            if (results.Count != requests.Count) throw new InvalidDataException("Peer returned the wrong number of block responses.");
            return results;
        }
        finally { Exit(); }
    }
    private void RequireOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private void Enter() { lock (lifecycle) { RequireOpen(); active++; } }
    private void Exit() { lock (lifecycle) { active--; if (disposed != 0 && active == 0) drained.TrySetResult(); } }
    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposed != 0) return new(closed.Task);
            Volatile.Write(ref disposed, 1); if (active == 0) drained.TrySetResult();
        }
        owner.Remove(this); session.Disconnect(); owner.RetireClockSession(session); _ = CloseAsync(); return new(closed.Task);
    }
    private async Task CloseAsync()
    {
        Exception? failure = null;
        try { await lifetime.CancelAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        if (liveIndexPump is not null)
            try { await liveIndexPump.ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
        try { await transport.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
        await drained.Task.ConfigureAwait(false);
        synchronization.Dispose(); lifetime.Dispose();
        if (failure is not null) closed.TrySetException(failure); else closed.TrySetResult();
    }
}
