using System.ComponentModel;
using System.IO;
using System.Security;
using Grpc.Core;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;

namespace PeerSync.App.Status;

internal interface IControlStatusConnection : IDisposable
{
    Task<ControlStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<ControlStatusSnapshot> WatchEventsAsync(CancellationToken cancellationToken);
}
internal sealed class VerifiedStatusConnection(ControlStatusClient client) : IControlStatusConnection
{
    public Task<ControlStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken) => client.GetStatusAsync(cancellationToken);
    public IAsyncEnumerable<ControlStatusSnapshot> WatchEventsAsync(CancellationToken cancellationToken) => client.WatchEventsAsync(cancellationToken);
    public void Dispose() => client.Dispose();
    internal static Task<IControlStatusConnection> OpenInstalledAsync(CancellationToken cancellationToken) =>
        Task.Run<IControlStatusConnection>(() => new VerifiedStatusConnection(ControlStatusClient.CreateForInstalledService()), cancellationToken);
}

internal sealed class ClockStatusController(ClockStatusViewModel model,
    Func<Action, CancellationToken, Task> dispatch,
    Func<CancellationToken, Task<IControlStatusConnection>> connect,
    TimeProvider? timeProvider = null, DiagnosticJournal? diagnostics = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private int running;
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Status watcher is already running.");
        int failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long generation = 0;
                await dispatch(() => generation = model.BeginAttempt(), cancellationToken).ConfigureAwait(false);
                Guid session = Guid.NewGuid();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    diagnostics?.TryWrite(new("app.control_connect", "control") { SessionId = session, Transport = DiagnosticTransport.NamedPipe });
                    using var connection = await connect(attempt.Token).ConfigureAwait(false);
                    await DeliverAsync(await connection.GetStatusAsync(attempt.Token).ConfigureAwait(false)).ConfigureAwait(false);
                    await using var events = connection.WatchEventsAsync(attempt.Token).GetAsyncEnumerator(attempt.Token);
                    Task<bool>? pending = null;
                    try
                    {
                        while (await (pending = events.MoveNextAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(15), clock, attempt.Token).ConfigureAwait(false))
                        {
                            pending = null;
                            await DeliverAsync(events.Current).ConfigureAwait(false);
                        }
                        throw new IOException("Control status stream ended.");
                    }
                    finally
                    {
                        attempt.Cancel();
                        // WaitAsync timing out does not finish MoveNext. Drain its
                        // cancellation before disposing the async enumerator.
                        if (pending is not null)
                            try { await pending.ConfigureAwait(false); }
                            catch (Exception error) when (error is not OutOfMemoryException) { }
                    }
                    async Task DeliverAsync(ControlStatusSnapshot status)
                    {
                        bool applied = false;
                        await dispatch(() => applied = model.Apply(generation, status), attempt.Token).ConfigureAwait(false);
                        if (applied)
                        {
                            failures = 0;
                            diagnostics?.TryWrite(new("app.control_status", "control") { SessionId = session, ItemsVisited = status.Clocks.Count });
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    attempt.Cancel();
                    LogFailure(error, session);
                    await dispatch(() => model.Fail(generation, FailureReason(error)), cancellationToken).ConfigureAwait(false);
                }
                int seconds = Math.Min(30, 1 << Math.Min(failures, 5)); failures = Math.Min(5, failures + 1);
                await Task.Delay(TimeSpan.FromSeconds(seconds), clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref running, 0); }
    }
    private void LogFailure(Exception error, Guid session)
    {
        int depth = 0;
        foreach (var cause in Causes(error))
            diagnostics?.TryWrite(new(depth++ == 0 ? "app.control_failed" : "app.control_cause", "control") { SessionId = session, Level = DiagnosticLevel.Warning,
                ErrorCode = cause is RpcException rpc ? "RpcException." + rpc.StatusCode : cause.GetType().Name,
                Exception = cause, HResult = cause.HResult, NativeError = cause is Win32Exception native ? native.NativeErrorCode : null });
    }
    private static IEnumerable<Exception> Causes(Exception error)
    {
        for (int depth = 0; depth < 8; depth++)
        {
            yield return error;
            var next = error.InnerException ?? (error as RpcException)?.Status.DebugException;
            if (next is null || ReferenceEquals(next, error)) yield break;
            error = next;
        }
    }
    private static string FailureReason(Exception error)
    {
        if (Causes(error).Any(cause => cause is SecurityException))
            return "Service identity verification failed. Retrying with a fresh installed-service lookup.";
        return error switch
        {
            Win32Exception native => "Service lookup failed (Windows error " + native.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + "). Retrying automatically.",
            TimeoutException => "Service status updates timed out. Retrying automatically.",
            _ => "Service status could not be read. Retrying automatically."
        };
    }
}
