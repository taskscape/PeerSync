using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Quic;
using PeerSync.Net.Relay;
using PeerSync.Net.Tcp;

namespace PeerSync.Net.Connections;

/// <summary>Orders authenticated transport attempts and keeps one deterministic session per device.</summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    public static readonly TimeSpan AttemptStagger = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromMinutes(5);
    private readonly byte[] localDeviceId;
    private readonly DiagnosticJournal? diagnostics;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly ConcurrentDictionary<string, ManagedPeerConnection> active = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private bool disposed;

    private readonly record struct CandidateAttempt(ConnectionCandidate Candidate, ManagedPeerConnection Session);

    public ConnectionManager(ReadOnlySpan<byte> localDeviceId, DiagnosticJournal? diagnostics = null)
        : this(localDeviceId, diagnostics, (duration, token) => Task.Delay(duration, token)) { }

    internal ConnectionManager(ReadOnlySpan<byte> localDeviceId, DiagnosticJournal? diagnostics,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        if (localDeviceId.Length != 32) throw new ArgumentException("Local device id must contain 32 bytes.", nameof(localDeviceId));
        this.localDeviceId = localDeviceId.ToArray();
        this.diagnostics = diagnostics;
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public event Action<ManagedPeerConnection>? Authenticated;

    public ValueTask<ManagedPeerConnection> ConnectAsync(ReadOnlyMemory<byte> expectedRemoteDeviceId,
        IReadOnlyList<ConnectionCandidate> candidates, CancellationToken cancellationToken = default)
        => ConnectCandidatesAsync(expectedRemoteDeviceId, candidates, cancellationToken);

    // A configured host can identify an approved peer only after its first
    // certificate-bound handshake. Race its transport candidates, then install
    // the authenticated winner through the same duplicate-resolution gate.
    internal ValueTask<ManagedPeerConnection> ConnectAnyAsync(IReadOnlyList<ConnectionCandidate> candidates,
        CancellationToken cancellationToken = default)
        => ConnectCandidatesAsync(null, candidates, cancellationToken);

    private async ValueTask<ManagedPeerConnection> ConnectCandidatesAsync(ReadOnlyMemory<byte>? expectedRemoteDeviceId,
        IReadOnlyList<ConnectionCandidate> candidates, CancellationToken cancellationToken)
    {
        if (expectedRemoteDeviceId is { } expectedMemory) ValidateRemoteId(expectedMemory.Span);
        ArgumentNullException.ThrowIfNull(candidates);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (expectedRemoteDeviceId is { } knownRemote)
        {
            string key = Convert.ToHexString(knownRemote.Span);
            if (TryGetActive(key, out var current) && current is not null)
            {
                bool localDeviceIsLower = localDeviceId.AsSpan().SequenceCompareTo(knownRemote.Span) < 0;
                if (!localDeviceIsLower || current.IsInitiator) return current;
            }
        }
        if (candidates.Count == 0) throw new InvalidOperationException("No transport candidates are available for this peer.");

        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConnectionCandidate[] ordered = candidates.Select((candidate, index) => (candidate, index))
            .OrderBy(item => item.candidate.Source).ThenBy(item => item.index)
            .Select(item => item.candidate).ToArray();
        byte[]? expectedBytes = expectedRemoteDeviceId.HasValue ? expectedRemoteDeviceId.Value.ToArray() : null;
        var pending = ordered.Select((candidate, index) => ConnectCandidateAsync(candidate, index,
            expectedBytes, attempts.Token)).ToList();
        ManagedPeerConnection? winner = null;
        ConnectionCandidate? winnerCandidate = null;
        ManagedPeerConnection? selected = null;
        try
        {
            while (pending.Count != 0 && selected is null)
            {
                Task<CandidateAttempt?> completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                CandidateAttempt? authenticated = await completed.ConfigureAwait(false);
                if (authenticated is not { } attempt) continue;
                ManagedPeerConnection candidate = attempt.Session;
                if (cancellationToken.IsCancellationRequested)
                {
                    try { await candidate.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        diagnostics?.TryWrite(new("connection.cancelled_candidate_dispose_failed", "connection_manager")
                        { Level = DiagnosticLevel.Warning, SessionId = candidate.SessionId,
                            ErrorCode = SafeError(error), Exception = error, HResult = error.HResult });
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                try
                {
                    // A peer can resolve a simultaneous QUIC/TCP duplicate while
                    // our losing attempt drains. Install before waiting for it, so
                    // the authenticated peer identity reaches the reconnect owner.
                    selected = await InstallAsync(candidate,
                        expectedBytes ?? candidate.RemoteDeviceId).ConfigureAwait(false);
                    winner = candidate;
                    winnerCandidate = attempt.Candidate;
                }
                catch (IOException) when (candidate.Closed.IsCompleted)
                {
                    diagnostics?.TryWrite(new("connection.candidate_closed_before_install", "connection_manager")
                        { Level = DiagnosticLevel.Debug, SessionId = candidate.SessionId });
                }
            }
        }
        finally
        {
            attempts.Cancel();
            await DisposeLateAttemptsAsync(pending).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            if (winner is not null && ReferenceEquals(selected, winner))
            {
                RemoveIfCurrent(Convert.ToHexString(winner.RemoteId), winner);
                try { await winner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    diagnostics?.TryWrite(new("connection.cancelled_winner_dispose_failed", "connection_manager")
                    { Level = DiagnosticLevel.Warning, ErrorCode = SafeError(error), Exception = error, HResult = error.HResult });
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (selected is null) throw new IOException("No transport candidate completed authenticated connection.");
        if (winnerCandidate is not null && ReferenceEquals(selected, winner) && !selected.Closed.IsCompleted)
        {
            diagnostics?.TryWrite(new("connection.candidate_selected", CandidateOperation(winnerCandidate))
                { SessionId = selected.SessionId, Transport = CandidateTransport(winnerCandidate, selected),
                    RemoteAddress = winnerCandidate.RemoteAddress, RemotePort = winnerCandidate.RemotePort });
        }
        return selected;
    }

    public async ValueTask<ManagedPeerConnection> RegisterIncomingAsync(ManagedPeerConnection session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (session.IsInitiator) throw new ArgumentException("An incoming connection must have been initiated by the remote peer.", nameof(session));
        if (!CryptographicOperations.FixedTimeEquals(session.LocalDeviceId, localDeviceId))
            throw new AuthenticationException("Incoming connection has a different local device identity.");
        ManagedPeerConnection selected = await InstallAsync(session, session.RemoteDeviceId).ConfigureAwait(false);
        if (ReferenceEquals(selected, session) && !selected.Closed.IsCompleted)
        {
            var endpoint = session.Session switch
            {
                QuicPeerSession quic => quic.RemoteEndPoint as System.Net.IPEndPoint,
                TcpPeerProtocolSession tcp => tcp.RemoteEndPoint,
                _ => null,
            };
            diagnostics?.TryWrite(new("connection.incoming_selected", "candidate.incoming")
                { SessionId = selected.SessionId, Transport = CandidateTransportForSession(selected),
                    RemoteAddress = endpoint?.Address, RemotePort = endpoint?.Port });
        }
        return selected;
    }

    public bool TryGetConnected(ReadOnlySpan<byte> remoteDeviceId, out ManagedPeerConnection? session)
    {
        ValidateRemoteId(remoteDeviceId);
        return TryGetActive(Convert.ToHexString(remoteDeviceId), out session);
    }

    public async Task MaintainConnectionAsync(ReadOnlyMemory<byte> expectedRemoteDeviceId,
        Func<CancellationToken, ValueTask<IReadOnlyList<ConnectionCandidate>>> getCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getCandidates);
        ValidateRemoteId(expectedRemoteDeviceId.Span);
        int failures = 0;
        string peerKey = Convert.ToHexString(expectedRemoteDeviceId.Span);
        string peerFingerprint = DiagnosticFingerprint.FromId(expectedRemoteDeviceId.Span);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!TryGetConnected(expectedRemoteDeviceId.Span, out var current) || current is null)
                {
                    IReadOnlyList<ConnectionCandidate> candidates = await getCandidates(cancellationToken).ConfigureAwait(false);
                    current = await ConnectAsync(expectedRemoteDeviceId, candidates, cancellationToken).ConfigureAwait(false);
                }
                failures = 0;
                await current.Closed.WaitAsync(cancellationToken).ConfigureAwait(false);
                RemoveIfCurrent(peerKey, current);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                diagnostics?.TryWrite(new("connection.reconnect_failed", "connection_manager")
                { Level = DiagnosticLevel.Warning, PeerFingerprint = peerFingerprint,
                    ErrorCode = SafeError(error), Exception = error, HResult = error.HResult,
                    NativeError = SocketNativeError(error),
                    RetryInMilliseconds = (int)BackoffForFailure(failures).TotalMilliseconds });
            }

            TimeSpan backoff = BackoffForFailure(failures);
            failures = Math.Min(failures + 1, 31);
            await delay(backoff, cancellationToken).ConfigureAwait(false);
        }
    }

    public static TimeSpan BackoffForFailure(int failureIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failureIndex);
        long multiplier = 1L << Math.Min(failureIndex, 31);
        return TimeSpan.FromTicks(Math.Min(InitialReconnectDelay.Ticks * multiplier, MaximumReconnectDelay.Ticks));
    }

    private async Task<CandidateAttempt?> ConnectCandidateAsync(ConnectionCandidate candidate, int index,
        byte[]? expectedRemoteDeviceId, CancellationToken cancellationToken)
    {
        try
        {
            // Relay candidates perform their own five-second direct-path grace
            // period. Do not add a stagger for every public endpoint ahead of
            // them, or a peer with many endpoints could wait far longer.
            if (index != 0 && candidate.Source != ConnectionSource.Relay)
                await delay(TimeSpan.FromTicks(AttemptStagger.Ticks * index), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics?.TryWrite(new("connection.candidate_started", CandidateOperation(candidate))
                { Level = DiagnosticLevel.Debug, Transport = CandidateTransport(candidate),
                    PeerFingerprint = expectedRemoteDeviceId is null ? null : DiagnosticFingerprint.FromId(expectedRemoteDeviceId),
                    RemoteAddress = candidate.RemoteAddress, RemotePort = candidate.RemotePort });
            ManagedPeerConnection session = await candidate.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (expectedRemoteDeviceId is not null
                && !CryptographicOperations.FixedTimeEquals(session.RemoteId, expectedRemoteDeviceId))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new AuthenticationException("Authenticated candidate has a different device identity.");
            }
            diagnostics?.TryWrite(new("connection.candidate_authenticated", CandidateOperation(candidate))
                { SessionId = session.SessionId, Transport = CandidateTransport(candidate, session) });
            return new CandidateAttempt(candidate, session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("connection.candidate_failed", CandidateOperation(candidate))
                { Level = DiagnosticLevel.Warning, Transport = CandidateTransport(candidate),
                    PeerFingerprint = expectedRemoteDeviceId is null ? null : DiagnosticFingerprint.FromId(expectedRemoteDeviceId),
                    RemoteAddress = candidate.RemoteAddress, RemotePort = candidate.RemotePort,
                    ErrorCode = SafeError(error), Exception = error, HResult = error.HResult,
                    NativeError = SocketNativeError(error) });
            return null;
        }
    }

    private static string CandidateOperation(ConnectionCandidate candidate)
    {
        string source = candidate.Source.ToString().ToLowerInvariant();
        return candidate.Transport is { } transport
            ? $"candidate.{source}.{transport.ToString().ToLowerInvariant()}"
            : $"candidate.{source}";
    }

    private static DiagnosticTransport? CandidateTransport(ConnectionCandidate candidate,
        ManagedPeerConnection? session = null)
        => candidate.Transport ?? (candidate.Source == ConnectionSource.Relay
            ? DiagnosticTransport.Relay
            : session is null ? null : CandidateTransportForSession(session));

    private static DiagnosticTransport? CandidateTransportForSession(ManagedPeerConnection session)
        => session.Session switch
        {
            QuicPeerSession => DiagnosticTransport.Quic,
            TcpPeerProtocolSession => DiagnosticTransport.Tcp,
            RelayPeerProtocolSession => DiagnosticTransport.Relay,
            _ => null,
        };

    private async ValueTask<ManagedPeerConnection> InstallAsync(ManagedPeerConnection candidate, ReadOnlyMemory<byte> expectedRemoteId)
    {
        byte[] expectedRemote = expectedRemoteId.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(candidate.RemoteId, expectedRemote))
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException("Authenticated candidate has a different device identity.");
        }
        if (!CryptographicOperations.FixedTimeEquals(candidate.LocalDeviceId, localDeviceId))
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException("Authenticated candidate has a different local device identity.");
        }

        ManagedPeerConnection? selected;
        ManagedPeerConnection? close = null;
        bool managerDisposed;
        string key = Convert.ToHexString(expectedRemote);
        lock (gate)
        {
            managerDisposed = disposed;
            if (managerDisposed || candidate.Closed.IsCompleted)
            {
                close = candidate;
                selected = null;
            }
            else if (!active.TryGetValue(key, out var current) || current.Closed.IsCompleted)
            {
                active[key] = selected = candidate;
            }
            else if (Prefer(candidate, current))
            {
                active[key] = selected = candidate;
                close = current;
            }
            else
            {
                selected = current;
                close = candidate;
            }
        }

        if (close is not null)
        {
            diagnostics?.TryWrite(new("connection.duplicate_resolved", "connection_manager")
                { SessionId = close.SessionId, ItemsVisited = candidate.IsInitiator ? 1 : 0 });
            await close.DisposeAsync().ConfigureAwait(false);
        }
        ObjectDisposedException.ThrowIf(managerDisposed, this);
        if (selected is null) throw new IOException("The authenticated candidate closed before it could be installed.");
        if (ReferenceEquals(selected, candidate)) RaiseAuthenticated(candidate);
        return selected;
    }

    private bool Prefer(ManagedPeerConnection candidate, ManagedPeerConnection current)
    {
        bool lowerDeviceInitiates = localDeviceId.AsSpan().SequenceCompareTo(candidate.RemoteId) < 0;
        bool candidatePreferred = candidate.IsInitiator == lowerDeviceInitiates;
        bool currentPreferred = current.IsInitiator == lowerDeviceInitiates;
        if (candidatePreferred != currentPreferred) return candidatePreferred;
        return candidate.SessionId.CompareTo(current.SessionId) < 0;
    }

    private bool TryGetActive(string key, out ManagedPeerConnection? session)
    {
        lock (gate)
        {
            if (active.TryGetValue(key, out session) && !session.Closed.IsCompleted) return true;
            if (session is not null) active.TryRemove(key, out _);
            session = null;
            return false;
        }
    }

    private void RemoveIfCurrent(string key, ManagedPeerConnection session)
    {
        lock (gate)
        {
            if (active.TryGetValue(key, out var current) && ReferenceEquals(current, session)) active.TryRemove(key, out _);
        }
    }

    private void RaiseAuthenticated(ManagedPeerConnection session)
    {
        if (Authenticated is null) return;
        foreach (Action<ManagedPeerConnection> callback in Authenticated.GetInvocationList())
        {
            try { callback(session); }
            catch (Exception error)
            {
                diagnostics?.TryWrite(new("connection.authenticated_callback_failed", "connection_manager")
                    { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
            }
        }
    }

    private static async Task DisposeLateAttemptsAsync(IEnumerable<Task<CandidateAttempt?>> pending)
    {
        var tasks = pending.ToArray();
        if (tasks.Length == 0) return;
        try
        {
            CandidateAttempt?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var result in results)
                if (result is { } attempt) await attempt.Session.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
    }

    private static void ValidateRemoteId(ReadOnlySpan<byte> deviceId)
    {
        if (deviceId.Length != 32) throw new ArgumentException("Remote device id must contain 32 bytes.", nameof(deviceId));
    }

    private static string SafeError(Exception error) => error switch
    {
        AuthenticationException => "authentication_failed",
        OperationCanceledException => "cancelled",
        _ => error.GetType().Name,
    };

    private static int? SocketNativeError(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SocketException socket) return socket.NativeErrorCode;
        return null;
    }


    public async ValueTask DisposeAsync()
    {
        ManagedPeerConnection[] sessions;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            sessions = active.Values.ToArray();
            active.Clear();
        }
        List<Exception>? failures = null;
        foreach (var session in sessions)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        CryptographicOperations.ZeroMemory(localDeviceId);
        if (failures is { Count: > 0 }) throw new AggregateException(failures);
    }
}
