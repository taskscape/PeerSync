using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Security.Cryptography;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Ignore;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Net;
using PeerSync.Protocol.Handshake;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

/// <summary>A connected folder engine. The host authenticates device and folder
/// membership before creating handlers/transports. Index/keys remain caller-owned.
/// Finish folder work and drain inbound transports before disposing the folder.</summary>
public sealed class TransportFolder : IAsyncDisposable
{
    private const int MaximumBlockPathCandidates = 4096;
    private readonly FolderIndex index;
    private readonly byte[] own;
    private readonly byte[] folderId;
    private readonly TimeProvider clock;
    private readonly string rootPath;
    private readonly IgnoreMatcher ignore;
    private readonly Puller puller;
    private readonly TransferThrottle? uploadThrottle, downloadThrottle;
    private readonly ConcurrentDictionary<string, PeerFolderConnection> peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<PeerFolderConnection, ConcurrentDictionary<string, ReplicaCoverage>> replicaCoverage = new();
    private readonly PeerClockStatusLedger clockStatuses = new();
    private readonly TransferScheduler transferScheduler = new();
    private long nextBlockPeer;
    private readonly object lifecycle = new();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    /// <summary>Verified payload bytes served to or received from an authenticated peer.</summary>
    public event Action<long, bool>? BytesTransferred;

    public TransportFolder(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        FolderSettings? settings = null, TimeProvider? timeProvider = null, DiagnosticJournal? journal = null)
        : this(rootPath, index, keys, ownDevice, settings, timeProvider, journal, directorySnapshots: true, ignore: null) { }

    internal TransportFolder(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        FolderSettings? settings, TimeProvider? timeProvider, DiagnosticJournal? journal, bool directorySnapshots,
        IgnoreMatcher? ignore = null)
        : this(rootPath, index, keys, ownDevice, settings, timeProvider, journal, directorySnapshots, null, null, ignore) { }

    internal TransportFolder(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        FolderSettings? settings, TimeProvider? timeProvider, DiagnosticJournal? journal, IgnoreMatcher ignore)
        : this(rootPath, index, keys, ownDevice, settings, timeProvider, journal, directorySnapshots: true, null, null,
            ignore ?? throw new ArgumentNullException(nameof(ignore))) { }

    internal TransportFolder(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        FolderSettings? settings, TimeProvider? timeProvider, DiagnosticJournal? journal,
        TransferThrottle? uploadThrottle, TransferThrottle? downloadThrottle, IgnoreMatcher? ignore = null)
        : this(rootPath, index, keys, ownDevice, settings, timeProvider, journal, directorySnapshots: true,
            uploadThrottle: uploadThrottle, downloadThrottle: downloadThrottle, ignore: ignore) { }

    private TransportFolder(string rootPath, FolderIndex index, FolderKeys keys, byte[] ownDevice,
        FolderSettings? settings, TimeProvider? timeProvider, DiagnosticJournal? journal, bool directorySnapshots,
        TransferThrottle? uploadThrottle, TransferThrottle? downloadThrottle, IgnoreMatcher? ignore)
    {
        ArgumentNullException.ThrowIfNull(ownDevice);
        ArgumentNullException.ThrowIfNull(keys);
        if (ownDevice.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(ownDevice));
        this.rootPath = rootPath; this.index = index; own = ownDevice.ToArray(); clock = timeProvider ?? TimeProvider.System;
        this.ignore = ignore ?? new IgnoreMatcher();
        this.uploadThrottle = uploadThrottle; this.downloadThrottle = downloadThrottle;
        folderId = keys.FolderId();
        puller = new(rootPath, index, keys, own, FetchAsync, settings, clock, journal, directorySnapshots, this.ignore);
        puller.BatchFetch = FetchBatchAsync;
    }

    public async Task<PullPassResult> RunPassAsync(CancellationToken cancellationToken = default)
    {
        RequireOpen(); long before = index.ReadIndexHighWater();
        PullPassResult result = await puller.RunPassAsync(cancellationToken).ConfigureAwait(false);
        long after = index.ReadIndexHighWater();
        if (after > before) await PublishIndexTargetAsync(after, cancellationToken).ConfigureAwait(false);
        return result;
    }
    internal Task RecoverBeforeExternalScanAsync(CancellationToken cancellationToken = default)
    { RequireOpen(); return puller.RecoverBeforeExternalScanAsync(cancellationToken); }
    internal Scanner Scanner { get { RequireOpen(); return puller.Scanner; } }
    public void RequestScan() { RequireOpen(); puller.RequestScan(); }
    internal void RequestWatcherScan(WatchBatch batch) { RequireOpen(); puller.RequestWatcherScan(batch); }
    internal void RecordIndexPush(string eventName, long targetSequence, Guid? sessionId = null, Exception? error = null)
        => puller.RecordIndexPush(eventName, targetSequence, sessionId, error);
    public byte[] FolderId { get { RequireOpen(); return folderId.ToArray(); } }
    public int PendingTransferRequests => transferScheduler.WaitingCount;

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
                connection.StartLiveIndexPump();
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
        if (hash.Length != 32 || length is <= 0 or > 8 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(length), "Block request is outside the supported bounds.");

        // The record author is the one known holder. Probe other authenticated
        // replicas, then prefer them only when they actually serve this author's
        // blocks. A fresh peer must not ask another fresh peer for every chunk.
        PeerFolderConnection[] candidates = OrderBlockPeers(source);
        if (candidates.Length == 0) throw new IOException("No authenticated folder peer is connected.");
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < candidates.Length; attempt++)
        {
            token.ThrowIfCancellationRequested();
            PeerFolderConnection peer = candidates[attempt];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            TransferScheduler.Lease? windowLease = null;
            try
            {
                if (downloadThrottle is not null)
                {
                    TimeSpan waited = await downloadThrottle.AcquireAsync(length, token).ConfigureAwait(false);
                    if (waited > TimeSpan.FromMilliseconds(10))
                        puller.RecordBlockRoute("transfer.download_rate_limited", 1, length, peer.SharedSessionId);
                }
                windowLease = await transferScheduler.AcquireAsync(Convert.ToHexString(peer.RemoteDeviceId), length, token).ConfigureAwait(false);
                deadline.CancelAfter(TimeSpan.FromSeconds(30 + length / (256.0 * 1024)));
                ReadOnlyMemory<byte> response = await peer.FetchAsync(hash, length, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (response.Length != length || !CryptographicOperations.FixedTimeEquals(
                    Blake3.Hasher.Hash(response.Span).AsSpan(), hash.Span))
                    throw new CryptographicException("Authenticated peer returned a block that failed content verification.");
                windowLease.Complete(succeeded: true);
                ObserveReplica(source, peer, found: 1, missing: 0);
                if (attempt != 0)
                    puller.RecordBlockRoute("transfer.block_peer_failover", attempt, length, peer.SharedSessionId);
                ReportBytesTransferred(response.Length, download: true);
                return response.ToArray();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException or CryptographicException or AuthenticationException
                || error is OperationCanceledException && !token.IsCancellationRequested)
            {
                if (error is PeerBlockException { ErrorCode: PeerSync.Protocol.Wire.BlockError.NotFound or PeerSync.Protocol.Wire.BlockError.Forbidden })
                    windowLease?.CompleteWithoutCongestion();
                else windowLease?.Complete(succeeded: false);
                lastFailure = error;
                if (error is PeerBlockException { ErrorCode: PeerSync.Protocol.Wire.BlockError.NotFound or PeerSync.Protocol.Wire.BlockError.Forbidden })
                    ObserveReplica(source, peer, found: 0, missing: 1);
                puller.RecordBlockRoute("transfer.block_peer_unavailable", attempt + 1, length, peer.SharedSessionId, error);
            }
            finally { windowLease?.Dispose(); }
        }
        throw new IOException("No authenticated folder peer could serve the requested block.", lastFailure);
    }

    private async ValueTask<IReadOnlyList<PeerBlockResult>> FetchBatchAsync(ReadOnlyMemory<byte> source,
        IReadOnlyList<PeerBlockRequest> requests, bool endgame, CancellationToken token)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(requests); token.ThrowIfCancellationRequested();
        if (requests.Count is < 1 or > 64 || requests.Any(item => item.ChunkHash.Length != 32 || item.Length is <= 0 or > 8 * 1024 * 1024))
            throw new ArgumentOutOfRangeException(nameof(requests));
        int totalBytes = checked(requests.Sum(item => item.Length));
        if (totalBytes > TransferScheduler.MaximumWindowBytes)
            throw new ArgumentOutOfRangeException(nameof(requests), "A block batch exceeds the bounded assembly window.");

        PeerFolderConnection[] candidates = OrderBlockPeers(source);
        if (candidates.Length == 0) throw new IOException("No authenticated folder peer is connected.");

        var results = new PeerBlockResult?[requests.Count];
        var failures = new Exception?[requests.Count];
        var unresolved = Enumerable.Range(0, requests.Count).ToList();
        int fallbackStart = 0;
        if (endgame && candidates.Length > 1)
        {
            fallbackStart = 2;
            PeerBlockRequest[] finalBatch = requests.ToArray();
            using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
            PeerFolderConnection[] endgamePeers = [candidates[0], candidates[1]];
            Task<IReadOnlyList<PeerBlockResult>>[] attempts = endgamePeers
                .Select(peer => SendBlockBatchAsync(peer, finalBatch, race.Token).AsTask()).ToArray();
            var waiting = new List<int> { 0, 1 };
            try
            {
                while (waiting.Count != 0 && unresolved.Count != 0)
                {
                    token.ThrowIfCancellationRequested();
                    Task<IReadOnlyList<PeerBlockResult>> completed = await Task.WhenAny(waiting.Select(index => attempts[index])).ConfigureAwait(false);
                    int completedIndex = Array.IndexOf(attempts, completed);
                    waiting.Remove(completedIndex);
                    try
                    {
                        IReadOnlyList<PeerBlockResult> received = await completed.ConfigureAwait(false);
                        if (received.Count != requests.Count) throw new InvalidDataException("Authenticated peer returned the wrong endgame batch size.");
                        ObserveReplica(source, endgamePeers[completedIndex], received);
                        for (int i = 0; i < received.Count; i++)
                        {
                            if (received[i].ErrorCode is { } code) failures[i] = new PeerBlockException(code);
                            else { results[i] = received[i]; failures[i] = null; }
                        }
                        unresolved = Enumerable.Range(0, requests.Count).Where(index => results[index] is null).ToList();
                        if (unresolved.Count == 0)
                            puller.RecordBlockRoute("transfer.block_endgame_winner", 2, totalBytes, endgamePeers[completedIndex].SharedSessionId);
                    }
                    catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException or CryptographicException or AuthenticationException
                        || error is OperationCanceledException && !token.IsCancellationRequested)
                    {
                        foreach (int index in unresolved) failures[index] = error;
                    }
                }
            }
            finally
            {
                race.Cancel();
                foreach (Task<IReadOnlyList<PeerBlockResult>> attempt in attempts)
                {
                    try { await attempt.ConfigureAwait(false); }
                    catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException
                        or TimeoutException or CryptographicException or AuthenticationException) { }
                }
            }
            token.ThrowIfCancellationRequested();
            if (unresolved.Count == 0) return CompleteBatch(results);
        }

        for (int attempt = fallbackStart; attempt < candidates.Length && unresolved.Count != 0; attempt++)
        {
            token.ThrowIfCancellationRequested();
            PeerFolderConnection peer = candidates[attempt];
            int[] pendingIndexes = unresolved.ToArray();
            PeerBlockRequest[] pending = pendingIndexes.Select(index => requests[index]).ToArray();
            try
            {
                IReadOnlyList<PeerBlockResult> received = await SendBlockBatchAsync(peer, pending, token).ConfigureAwait(false);
                if (received.Count != pending.Length) throw new InvalidDataException("Authenticated peer returned the wrong block batch size.");
                ObserveReplica(source, peer, received);
                for (int i = 0; i < received.Count; i++)
                {
                    int index = pendingIndexes[i];
                    if (received[i].ErrorCode is { } code) failures[index] = new PeerBlockException(code);
                    else { results[index] = received[i]; failures[index] = null; }
                }
                unresolved = Enumerable.Range(0, requests.Count).Where(index => results[index] is null).ToList();
                if (attempt != 0)
                    puller.RecordBlockRoute("transfer.block_peer_failover", attempt, pending.Sum(item => item.Length), peer.SharedSessionId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException or CryptographicException or AuthenticationException
                || error is OperationCanceledException && !token.IsCancellationRequested)
            {
                foreach (int index in pendingIndexes) failures[index] = error;
                unresolved = unresolved.ToList();
                puller.RecordBlockRoute("transfer.block_peer_unavailable", attempt + 1,
                    pending.Sum(item => item.Length), peer.SharedSessionId, error);
            }
        }
        foreach (int index in unresolved)
        {
            PeerSync.Protocol.Wire.BlockError errorCode = failures[index] is PeerBlockException peerError
                ? peerError.ErrorCode : PeerSync.Protocol.Wire.BlockError.Unavailable;
            results[index] = new(ReadOnlyMemory<byte>.Empty, errorCode);
        }
        return CompleteBatch(results);
    }

    private PeerBlockResult[] CompleteBatch(PeerBlockResult?[] results)
    {
        PeerBlockResult[] complete = results.Select(item =>
            item ?? throw new InvalidDataException("A block batch completed with a missing result.")).ToArray();
        foreach (var item in complete)
            if (item.ErrorCode is null) ReportBytesTransferred(item.Data.Length, download: true);
        return complete;
    }

    private PeerFolderConnection[] OrderBlockPeers(ReadOnlyMemory<byte> source)
    {
        PeerFolderConnection[] candidates = peers.Values
            .Where(peer => !peer.RemoteDeviceId.AsSpan().SequenceEqual(own))
            .OrderBy(peer => Convert.ToHexString(peer.RemoteDeviceId), StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length < 2) return candidates;

        long turn = Interlocked.Increment(ref nextBlockPeer);
        int author = Array.FindIndex(candidates, peer => peer.RemoteDeviceId.AsSpan().SequenceEqual(source.Span));
        if (author < 0)
            return candidates.Skip((int)((ulong)turn % (uint)candidates.Length))
                .Concat(candidates.Take((int)((ulong)turn % (uint)candidates.Length))).ToArray();

        PeerFolderConnection authorPeer = candidates[author];
        PeerFolderConnection[] alternatives = candidates.Where((_, index) => index != author).ToArray();
        int offset = (int)((ulong)turn % (uint)alternatives.Length);
        PeerFolderConnection candidate = alternatives[offset];
        bool tryReplicaFirst = CoverageFor(candidate, source)?.ShouldProbe() ?? false;
        PeerFolderConnection[] ordered = new PeerFolderConnection[candidates.Length];
        ordered[0] = tryReplicaFirst ? candidate : authorPeer;
        ordered[1] = tryReplicaFirst ? authorPeer : candidate;
        for (int i = 1; i < alternatives.Length; i++)
            ordered[i + 1] = alternatives[(offset + i) % alternatives.Length];
        return ordered;
    }

    private ReplicaCoverage? CoverageFor(PeerFolderConnection peer, ReadOnlyMemory<byte> source)
    {
        if (peer.RemoteDeviceId.AsSpan().SequenceEqual(source.Span)) return null;
        var byAuthor = replicaCoverage.GetOrAdd(peer, static _ => new(StringComparer.Ordinal));
        string author = Convert.ToHexString(source.Span);
        if (byAuthor.TryGetValue(author, out ReplicaCoverage? known)) return known;
        // A folder can contain records authored by many offline devices. Bound
        // routing memory by active peer, and use the known author when full.
        return byAuthor.Count < 1024 ? byAuthor.GetOrAdd(author, static _ => new()) : null;
    }

    private void ObserveReplica(ReadOnlyMemory<byte> source, PeerFolderConnection peer, int found, int missing)
        => CoverageFor(peer, source)?.Observe(found, missing);

    private void ObserveReplica(ReadOnlyMemory<byte> source, PeerFolderConnection peer,
        IReadOnlyList<PeerBlockResult> received)
    {
        int found = 0, missing = 0;
        foreach (PeerBlockResult block in received)
        {
            if (block.ErrorCode is null) found++;
            else if (block.ErrorCode is PeerSync.Protocol.Wire.BlockError.NotFound or PeerSync.Protocol.Wire.BlockError.Forbidden) missing++;
        }
        ObserveReplica(source, peer, found, missing);
    }

    private sealed class ReplicaCoverage
    {
        private long requests;
        private int recentlyServed;
        private int missing;

        internal bool ShouldProbe()
        {
            long turn = Interlocked.Increment(ref requests);
            if (turn == 1) return true;
            if (Volatile.Read(ref missing) != 0) return turn % 64 == 0;
            return Volatile.Read(ref recentlyServed) != 0 ? turn % 2 == 0 : turn % 16 == 0;
        }

        internal void Observe(int found, int notFound)
        {
            if (notFound > 0 && notFound >= found)
            {
                Volatile.Write(ref missing, 1);
                Volatile.Write(ref recentlyServed, 0);
            }
            else if (found > 0)
            {
                Volatile.Write(ref missing, 0);
                Volatile.Write(ref recentlyServed, 1);
            }
        }
    }

    private void ReportBytesTransferred(long bytes, bool download)
    {
        foreach (Action<long, bool> subscriber in BytesTransferred?.GetInvocationList() ?? [])
            try { subscriber(bytes, download); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private void RequirePublishedPath(ReadOnlyMemory<byte> hash, int length, Guid sessionId, CancellationToken token)
    {
        int matching = 0;
        bool allowed = false;
        foreach (var candidate in index.FindChunks(hash.ToArray()).Take(MaximumBlockPathCandidates + 1))
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Length != length) continue;
            if (++matching > MaximumBlockPathCandidates)
            {
                var error = new IOException("Block path authorization exceeded its bounded candidate limit.");
                puller.RecordBlockRoute("transfer.block_authorization_unavailable", matching, length, sessionId, error);
                throw error;
            }
            if (!ignore.IsIgnored(candidate.PathKey)) { allowed = true; break; }
        }
        if (!allowed)
        {
            // Directory snapshots are also published content. Their authenticated
            // page/member blocks are cataloged in complete directory graphs rather
            // than the ordinary file-chunk table.
            foreach (DirectoryGraphState candidate in index.FindDirectoryGraphsForBlock(hash.ToArray(), length)
                .Take(MaximumBlockPathCandidates + 1))
            {
                token.ThrowIfCancellationRequested();
                if (++matching > MaximumBlockPathCandidates)
                {
                    var error = new IOException("Block path authorization exceeded its bounded candidate limit.");
                    puller.RecordBlockRoute("transfer.block_authorization_unavailable", matching, length, sessionId, error);
                    throw error;
                }
                if (!ignore.IsIgnored(candidate.DirectoryPath)) { allowed = true; break; }
            }
        }
        if (matching == 0)
        {
            var error = new FileNotFoundException("No indexed file or directory graph publishes this block.");
            puller.RecordBlockRoute("transfer.block_not_found", 0, length, sessionId, error);
            throw error;
        }
        if (!allowed)
        {
            var error = new UnauthorizedAccessException("Every indexed path for this block is ignored.");
            puller.RecordBlockRoute("transfer.block_forbidden", matching, length, sessionId, error);
            throw error;
        }
    }

    private async ValueTask<IReadOnlyList<PeerBlockResult>> SendBlockBatchAsync(PeerFolderConnection peer,
        PeerBlockRequest[] requests, CancellationToken token)
    {
        int bytes = checked(requests.Sum(item => item.Length));
        TransferScheduler.Lease? lease = null;
        try
        {
            if (downloadThrottle is not null)
            {
                foreach (PeerBlockRequest item in requests)
                {
                    TimeSpan waited = await downloadThrottle.AcquireAsync(item.Length, token).ConfigureAwait(false);
                    if (waited > TimeSpan.FromMilliseconds(10))
                        puller.RecordBlockRoute("transfer.download_rate_limited", 1, item.Length, peer.SharedSessionId);
                }
            }
            lease = await transferScheduler.AcquireAsync(Convert.ToHexString(peer.RemoteDeviceId), bytes, token).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30 + bytes / (256.0 * 1024)));
            IReadOnlyList<PeerBlockResult> received = await peer.FetchBatchAsync(requests, deadline.Token).ConfigureAwait(false);
            if (received.Count != requests.Length) throw new InvalidDataException("Authenticated peer returned the wrong block batch size.");
            bool congestion = false, availabilityMiss = false;
            var verified = new PeerBlockResult[received.Count];
            for (int i = 0; i < received.Count; i++)
            {
                PeerBlockResult item = received[i];
                if (item.ErrorCode is { } errorCode)
                {
                    if (errorCode is PeerSync.Protocol.Wire.BlockError.NotFound or PeerSync.Protocol.Wire.BlockError.Forbidden)
                        availabilityMiss = true;
                    else congestion = true;
                    verified[i] = item;
                    puller.RecordBlockRoute("transfer.block_peer_unavailable", 1, requests[i].Length, peer.SharedSessionId,
                        new PeerBlockException(errorCode));
                }
                else if (item.Data.Length != requests[i].Length || !CryptographicOperations.FixedTimeEquals(
                    Blake3.Hasher.Hash(item.Data.Span).AsSpan(), requests[i].ChunkHash.Span))
                {
                    congestion = true; verified[i] = new(ReadOnlyMemory<byte>.Empty, PeerSync.Protocol.Wire.BlockError.Unavailable);
                    puller.RecordBlockRoute("transfer.block_peer_unavailable", 1, requests[i].Length, peer.SharedSessionId,
                        new CryptographicException("Authenticated peer returned a block that failed content verification."));
                }
                else verified[i] = new(item.Data.ToArray(), null);
            }
            if (congestion) lease.Complete(succeeded: false);
            else if (availabilityMiss) lease.CompleteWithoutCongestion();
            else lease.Complete(succeeded: true);
            return verified;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            lease?.Complete(succeeded: false);
            throw;
        }
        finally { lease?.Dispose(); }
    }
    internal Hello CreateHello() => new() { ProtocolVersion = 1, DeviceId = ByteString.CopyFrom(own),
        Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)), UtcTicks = clock.GetUtcNow().UtcDateTime.Ticks,
        DeviceName = Environment.MachineName, ClientVersion = "PeerSync/1" };
    internal static void ValidateHello(Hello hello, byte[] peer)
    {
        if (hello.ProtocolVersion != 1 || hello.Nonce.Length != 32 || !hello.DeviceId.Span.SequenceEqual(peer))
            throw new InvalidDataException("Hello does not match the authenticated connection.");
    }
    private void RequireOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private async Task PublishIndexTargetAsync(long target, CancellationToken token)
    {
        PeerFolderConnection[] snapshot = peers.Values.ToArray();
        await Task.WhenAll(snapshot.Select(async peer =>
        {
            try { await peer.PublishIndexTargetAsync(target, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { puller.RecordIndexPush("index.push_send_failed", target, peer.SharedSessionId, error); }
        })).ConfigureAwait(false);
    }
    internal void Remove(PeerFolderConnection peer)
    {
        peers.TryRemove(new KeyValuePair<string, PeerFolderConnection>(Convert.ToHexString(peer.RemoteDeviceId), peer));
        replicaCoverage.TryRemove(peer, out _);
    }
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
            folder.RequirePublishedPath(hash, length, session.CorrelationId, cancellationToken);
            if (folder.uploadThrottle is not null)
            {
                TimeSpan waited = await folder.uploadThrottle.AcquireAsync(length, cancellationToken).ConfigureAwait(false);
                if (waited > TimeSpan.FromMilliseconds(10))
                    folder.puller.RecordBlockRoute("transfer.upload_rate_limited", 1, length);
            }
            byte[] frozen = hash.ToArray();
            var bytes = await Task.Run(() =>
            {
                using var blocks = new PublishedBlocks(folder.rootPath, folder.index);
                return blocks.TryRead(frozen, length, cancellationToken) ?? throw new FileNotFoundException("Published block is unavailable.");
            }, cancellationToken).ConfigureAwait(false);
            session.RequireClock(); folder.ReportBytesTransferred(bytes.Length, download: false); return bytes;
        }
        public ValueTask DisposeAsync() { session.Disconnect(); folder.RetireClockSession(session); return ValueTask.CompletedTask; }
    }
}
