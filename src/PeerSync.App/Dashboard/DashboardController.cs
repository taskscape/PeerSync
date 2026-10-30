using Grpc.Core;
using System.ComponentModel;
using System.IO;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;

namespace PeerSync.App.Dashboard;

internal sealed class DashboardController(DashboardViewModel model,
    Func<Action, CancellationToken, Task> dispatch,
    Func<ControlStatusClient> connect,
    DiagnosticJournal? diagnostics = null)
{
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await dispatch(model.Connecting, cancellationToken).ConfigureAwait(false);
            try
            {
                using var client = connect();
                var metrics = ReadMetricsAsync(client, attempt.Token);
                try
                {
                    await using var updates = client.WatchControlEventsAsync(attempt.Token).GetAsyncEnumerator(attempt.Token);
                    Task<bool>? pending = null;
                    try
                    {
                        while (await (pending = updates.MoveNextAsync().AsTask()).WaitAsync(
                            TimeSpan.FromSeconds(15), attempt.Token).ConfigureAwait(false))
                        {
                            pending = null;
                            var frame = updates.Current;
                            await dispatch(() => model.Apply(frame), attempt.Token).ConfigureAwait(false);
                            failures = 0;
                        }
                        throw new IOException("Control event stream ended.");
                    }
                    finally
                    {
                        attempt.Cancel();
                        if (pending is not null)
                            try { await pending.ConfigureAwait(false); }
                            catch (Exception error) when (error is not OutOfMemoryException) { }
                    }
                }
                finally { attempt.Cancel(); await metrics.ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                bool unauthorized = IsAuthorizationFailure(error);
                diagnostics?.TryWrite(new("app.dashboard_connection_failed", "control")
                    { Level = DiagnosticLevel.Warning, ErrorCode = unauthorized ? "not_authorized" : error.GetType().Name,
                        Exception = error, HResult = error.HResult });
                await dispatch(() => model.Disconnected(unauthorized), cancellationToken).ConfigureAwait(false);
            }
            int seconds = Math.Min(30, 1 << Math.Min(failures++, 5));
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    internal static bool IsAuthorizationFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is RpcException { StatusCode: StatusCode.PermissionDenied }
                || current is UnauthorizedAccessException
                || current is Win32Exception { NativeErrorCode: 5 }
                || current.HResult == unchecked((int)0x80070005)) return true;
        return false;
    }

    private async Task ReadMetricsAsync(ControlStatusClient client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var sample in client.WatchMetricsAsync(cancellationToken).ConfigureAwait(false))
                await dispatch(() => model.Apply(sample), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            diagnostics?.TryWrite(new("app.metrics_unavailable", "control")
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, Exception = error, HResult = error.HResult });
        }
    }
}
