using System.Reflection;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Service.Update;

/// <summary>Checks an administrator-enabled signed feed without blocking sync.</summary>
internal sealed class UpdatePoller : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;

    private UpdatePoller(string dataRoot, DiagnosticJournal journal)
    {
        worker = Task.Run(() => RunAsync(dataRoot, journal, stop.Token));
    }

    internal static UpdatePoller Start(string dataRoot, DiagnosticJournal journal) => new(dataRoot, journal);

    private static async Task RunAsync(string dataRoot, DiagnosticJournal journal, CancellationToken token)
    {
        string policyPath = Path.Combine(dataRoot, "update-policy", "update-policy.json");
        string pendingRoot = Path.Combine(dataRoot, "updates");
        Version current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        var downloader = new UpdateDownloader(client, pendingRoot, current);
        while (!token.IsCancellationRequested)
        {
            try
            {
                UpdatePolicy? policy = new UpdatePolicyStore(policyPath).ReadIfPresent();
                if (policy is { Enabled: true })
                {
                    bool staged = await downloader.CheckOnceAsync(policy, token).ConfigureAwait(false);
                    journal.TryWrite(new(staged ? "service.update_staged" : "service.update_no_new_offer", "update"));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                journal.TryWrite(new("service.update_check_failed", "update")
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
            }
            try { await Task.Delay(TimeSpan.FromDays(1), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        try { await worker.ConfigureAwait(false); }
        finally { stop.Dispose(); }
    }
}
