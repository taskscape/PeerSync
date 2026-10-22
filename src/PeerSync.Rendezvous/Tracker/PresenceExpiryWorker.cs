using PeerSync.Core.Diagnostics;

namespace PeerSync.Rendezvous.Tracker;

internal sealed class PresenceExpiryWorker(PresenceRegistry registry, Relay.RelayRegistry relays,
    DiagnosticJournal diagnostics)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                int expired = registry.Expire();
                relays.Expire();
                if (expired > 0) diagnostics.TryWrite(new("rendezvous.presence_expired", "expiry")
                    { ItemsChanged = expired });
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
