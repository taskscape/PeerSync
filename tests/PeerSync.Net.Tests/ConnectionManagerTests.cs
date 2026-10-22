using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Connections;
using Shouldly;
using Xunit;

namespace PeerSync.Net.Tests;

public sealed class ConnectionManagerTests
{
    [Fact]
    public async Task CandidateAttemptsFollowSourcePriorityAndFallBackAfterFailure()
    {
        byte[] local = Id(1), remote = Id(2);
        var attempted = new ConcurrentQueue<ConnectionSource>();
        var session = Session(local, remote, initiator: true);
        await using var manager = new ConnectionManager(local);
        var candidates = new[]
        {
            new ConnectionCandidate(ConnectionSource.Relay, _ => ValueTask.FromResult<ManagedPeerConnection>(Session(local, remote, true))),
            new ConnectionCandidate(ConnectionSource.Public, _ =>
            {
                attempted.Enqueue(ConnectionSource.Public);
                return ValueTask.FromResult(session);
            }),
            new ConnectionCandidate(ConnectionSource.Lan, _ =>
            {
                attempted.Enqueue(ConnectionSource.Lan);
                throw new IOException("lan unreachable");
            }),
        };

        var selected = await manager.ConnectAsync(remote, candidates, TestContext.Current.CancellationToken);

        selected.ShouldBe(session);
        attempted.ToArray().ShouldBe([ConnectionSource.Lan, ConnectionSource.Public]);
        manager.TryGetConnected(remote, out var current).ShouldBeTrue();
        current.ShouldBe(session);
    }

    [Fact]
    public async Task AllCandidateSourcesFollowPriorityWithQuarterSecondStaggering()
    {
        byte[] local = Id(1), remote = Id(2);
        var attempted = new ConcurrentQueue<ConnectionSource>();
        var delays = new ConcurrentQueue<TimeSpan>();
        await using var manager = new ConnectionManager(local, diagnostics: null, (duration, _) =>
        {
            delays.Enqueue(duration);
            return Task.CompletedTask;
        });
        var candidates = new[]
        {
            Failed(ConnectionSource.Relay),
            Failed(ConnectionSource.Punch),
            Failed(ConnectionSource.Public),
            Failed(ConnectionSource.Lan),
        };

        await Should.ThrowAsync<IOException>(() => manager.ConnectAsync(remote, candidates,
            TestContext.Current.CancellationToken).AsTask());

        attempted.ToArray().ShouldBe([
            ConnectionSource.Lan,
            ConnectionSource.Public,
            ConnectionSource.Punch,
            ConnectionSource.Relay,
        ]);
        delays.ToArray().ShouldBe([
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(750),
        ]);

        ConnectionCandidate Failed(ConnectionSource source) => new(source, _ =>
        {
            attempted.Enqueue(source);
            throw new IOException("candidate unavailable");
        });
    }

    [Fact]
    public async Task FirstAuthenticatedCandidateWinsAndCancelsLaterAttempts()
    {
        byte[] local = Id(1), remote = Id(2);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = Session(local, remote, initiator: true);
        await using var manager = new ConnectionManager(local);
        var candidates = new[]
        {
            new ConnectionCandidate(ConnectionSource.Lan, async token =>
            {
                slowStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { slowCanceled.TrySetResult(); throw; }
                return Session(local, remote, initiator: true);
            }),
            new ConnectionCandidate(ConnectionSource.Public, _ => ValueTask.FromResult(winner)),
        };

        var selected = await manager.ConnectAsync(remote, candidates, TestContext.Current.CancellationToken);

        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await slowCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        selected.ShouldBe(winner);
    }

    [Fact]
    public async Task UnknownPeerUsesStaggeredAuthenticatedTransportRaceAndInstallsWinner()
    {
        byte[] local = Id(1), remote = Id(2);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = Session(local, remote, initiator: true);
        await using var manager = new ConnectionManager(local, diagnostics: null, (_, _) => Task.CompletedTask);
        var candidates = new[]
        {
            new ConnectionCandidate(ConnectionSource.Public, async token =>
            {
                slowStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { slowCanceled.TrySetResult(); throw; }
                return Session(local, remote, initiator: true);
            }),
            new ConnectionCandidate(ConnectionSource.Public, _ => ValueTask.FromResult(winner)),
        };

        var selected = await manager.ConnectAnyAsync(candidates, TestContext.Current.CancellationToken);

        selected.ShouldBe(winner);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await slowCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        manager.TryGetConnected(remote, out var current).ShouldBeTrue();
        current.ShouldBe(winner);
    }

    [Fact]
    public async Task AuthenticatedWinnerClosingDuringLoserDrainStillIdentifiesPeerForReconnect()
    {
        byte[] local = Id(1), remote = Id(2);
        var winner = Session(local, remote, initiator: true);
        var loserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var manager = new ConnectionManager(local, diagnostics: null, (_, _) => Task.CompletedTask);

        ManagedPeerConnection selected = await manager.ConnectAnyAsync(
        [new(ConnectionSource.Public, _ => ValueTask.FromResult(winner)),
         new(ConnectionSource.Public, async token =>
         {
             loserStarted.TrySetResult();
             try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
             catch (OperationCanceledException)
             {
                 await winner.DisposeAsync(); // Remote resolved the transport duplicate.
                 throw;
             }
             throw new InvalidOperationException("Losing candidate unexpectedly continued.");
         })], TestContext.Current.CancellationToken);

        await loserStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        selected.ShouldBe(winner);
        selected.RemoteDeviceId.ShouldBe(remote);
        selected.Closed.IsCompleted.ShouldBeTrue();
        manager.TryGetConnected(remote, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ClosedAuthenticatedCandidateFallsThroughToLiveTransport()
    {
        byte[] local = Id(1), remote = Id(2);
        var closed = Session(local, remote, initiator: true);
        await closed.DisposeAsync();
        var live = Session(local, remote, initiator: true);
        await using var manager = new ConnectionManager(local, diagnostics: null, (_, _) => Task.CompletedTask);

        ManagedPeerConnection selected = await manager.ConnectAnyAsync(
        [new(ConnectionSource.Public, _ => ValueTask.FromResult(closed)),
         new(ConnectionSource.Public, _ => ValueTask.FromResult(live))],
            TestContext.Current.CancellationToken);

        selected.ShouldBe(live);
        manager.TryGetConnected(remote, out var current).ShouldBeTrue();
        current.ShouldBe(live);
    }

    [Fact]
    public async Task CallerCancellationWhileLosingAttemptDrainsDisposesAuthenticatedWinner()
    {
        byte[] local = Id(1), remote = Id(2);
        var loserCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = Session(local, remote, initiator: true);
        var loser = Session(local, remote, initiator: true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var manager = new ConnectionManager(local, diagnostics: null, (_, _) => Task.CompletedTask);
        var connect = manager.ConnectAsync(remote,
        [new(ConnectionSource.Lan, _ => ValueTask.FromResult(winner)),
         new(ConnectionSource.Public, async token =>
         {
             try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
             catch (OperationCanceledException)
             {
                 loserCanceled.TrySetResult();
                 await releaseLoser.Task;
                 return loser;
             }
             throw new InvalidOperationException("Losing candidate unexpectedly continued.");
         })], stop.Token).AsTask();

        await loserCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        stop.Cancel();
        releaseLoser.TrySetResult();
        await Should.ThrowAsync<OperationCanceledException>(() => connect);

        winner.Closed.IsCompleted.ShouldBeTrue();
        loser.Closed.IsCompleted.ShouldBeTrue();
        manager.TryGetConnected(remote, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task CandidateDiagnosticsIdentifySourceTransportAndOptInEndpoint()
    {
        byte[] local = Id(1), remote = Id(2);
        string directory = Path.Combine(Path.GetTempPath(), "PeerSync-ConnectionDiagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var journal = new DiagnosticJournal(directory, "net", debugEnabled: true);
        await using var manager = new ConnectionManager(local, journal, (_, _) => Task.CompletedTask);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.42"), 47821);
        var winner = Session(local, remote, initiator: true);
        try
        {
            await manager.ConnectAsync(remote,
            [new(ConnectionSource.Lan, DiagnosticTransport.Quic, endpoint, _ => throw new IOException("unavailable")),
             new(ConnectionSource.Public, DiagnosticTransport.Tcp, endpoint, _ => ValueTask.FromResult(winner))],
                TestContext.Current.CancellationToken);

            string[] records = File.ReadAllLines(Path.Combine(directory, "debug.jsonl"));
            using JsonDocument first = JsonDocument.Parse(records[0]);
            first.RootElement.GetProperty("eventName").GetString().ShouldBe("connection.candidate_started");
            first.RootElement.GetProperty("operation").GetString().ShouldBe("candidate.lan.quic");
            first.RootElement.GetProperty("transport").GetString().ShouldBe("Quic");
            first.RootElement.GetProperty("remoteAddress").GetString().ShouldBe("203.0.113.42");
            first.RootElement.GetProperty("remotePort").GetInt32().ShouldBe(47821);
            using JsonDocument failed = JsonDocument.Parse(records.Single(line => line.Contains("candidate_failed", StringComparison.Ordinal)));
            failed.RootElement.GetProperty("operation").GetString().ShouldBe("candidate.lan.quic");
            failed.RootElement.GetProperty("errorCode").GetString().ShouldBe("IOException");
            records.ShouldContain(line => line.Contains("candidate.public.tcp", StringComparison.Ordinal)
                && line.Contains("\"transport\":\"Tcp\"", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LowerDeviceKeepsItsInitiatedDuplicateAndReplacesIncomingSession()
    {
        byte[] lower = Id(1), higher = Id(2);
        var incoming = Session(lower, higher, initiator: false, sessionId: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var outgoing = Session(lower, higher, initiator: true, sessionId: Guid.Parse("00000000-0000-0000-0000-000000000003"));
        await using var manager = new ConnectionManager(lower);

        await manager.RegisterIncomingAsync(incoming, TestContext.Current.CancellationToken);
        var selected = await manager.ConnectAsync(higher,
            [new ConnectionCandidate(ConnectionSource.Lan, _ => ValueTask.FromResult(outgoing))], TestContext.Current.CancellationToken);

        selected.ShouldBe(outgoing);
        incoming.Closed.IsCompleted.ShouldBeTrue();
        manager.TryGetConnected(higher, out var current).ShouldBeTrue();
        current.ShouldBe(outgoing);
    }

    [Fact]
    public async Task HigherDeviceReplacesItsOutgoingDuplicateWithLowerDevicesIncomingSession()
    {
        byte[] higher = Id(2), lower = Id(1);
        var outgoing = Session(higher, lower, initiator: true);
        var incoming = Session(higher, lower, initiator: false);
        await using var manager = new ConnectionManager(higher);
        var selected = await manager.ConnectAsync(lower,
            [new ConnectionCandidate(ConnectionSource.Lan, _ => ValueTask.FromResult(outgoing))], TestContext.Current.CancellationToken);

        var duplicate = await manager.RegisterIncomingAsync(incoming, TestContext.Current.CancellationToken);

        selected.ShouldBe(outgoing);
        duplicate.ShouldBe(incoming);
        outgoing.Closed.IsCompleted.ShouldBeTrue();
        manager.TryGetConnected(lower, out var current).ShouldBeTrue();
        current.ShouldBe(incoming);
    }

    [Fact]
    public async Task CandidateWithUnexpectedCertificateIdentityIsNotInstalled()
    {
        byte[] local = Id(1), expected = Id(2), wrong = Id(3);
        await using var manager = new ConnectionManager(local);

        await Should.ThrowAsync<IOException>(() => manager.ConnectAsync(expected,
            [new ConnectionCandidate(ConnectionSource.Lan, _ => ValueTask.FromResult(Session(local, wrong, true))) ],
            TestContext.Current.CancellationToken).AsTask());

        manager.TryGetConnected(expected, out _).ShouldBeFalse();
    }

    [Fact]
    public void ReconnectBackoffStartsAtOneSecondAndCapsAtFiveMinutes()
    {
        ConnectionManager.BackoffForFailure(0).ShouldBe(TimeSpan.FromSeconds(1));
        ConnectionManager.BackoffForFailure(1).ShouldBe(TimeSpan.FromSeconds(2));
        ConnectionManager.BackoffForFailure(8).ShouldBe(TimeSpan.FromSeconds(256));
        ConnectionManager.BackoffForFailure(9).ShouldBe(TimeSpan.FromMinutes(5));
        Should.Throw<ArgumentOutOfRangeException>(() => ConnectionManager.BackoffForFailure(-1));
    }

    [Fact]
    public async Task MaintainConnectionRetriesWithBackoffUntilCancelled()
    {
        byte[] remote = Id(2);
        var delays = new ConcurrentQueue<TimeSpan>();
        int candidateRequests = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var manager = new ConnectionManager(Id(1), diagnostics: null, (duration, token) =>
        {
            delays.Enqueue(duration);
            if (delays.Count == 3)
            {
                stop.Cancel();
                return Task.FromCanceled(token);
            }
            return Task.CompletedTask;
        });

        await Should.ThrowAsync<OperationCanceledException>(() => manager.MaintainConnectionAsync(remote, _ =>
        {
            Interlocked.Increment(ref candidateRequests);
            return ValueTask.FromResult<IReadOnlyList<ConnectionCandidate>>([]);
        }, stop.Token));

        candidateRequests.ShouldBe(3);
        delays.ToArray().ShouldBe([
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
        ]);
    }

    private static byte[] Id(byte value)
    {
        var id = new byte[32];
        id[31] = value;
        return id;
    }

    private static ManagedPeerConnection Session(byte[] local, byte[] remote, bool initiator, Guid? sessionId = null)
    {
        var owner = new TestSession();
        return new(sessionId ?? Guid.NewGuid(), local, remote, initiator, owner, owner.Closed.Task);
    }

    private sealed class TestSession : IAsyncDisposable
    {
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DisposeAsync()
        {
            Closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
