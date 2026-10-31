using System.Text.Json;
using System.Globalization;
using Grpc.Core;
using PeerSync.Control.Contracts;
using PeerSync.Core.Sync;

namespace PeerSync.Cli;

public static class StatusCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        bool json = args.Length == 1 && args[0] == "--json";
        if (args.Length > 1 || args.Length == 1 && !json)
        {
            await error.WriteLineAsync("Usage: psp status [--json]").ConfigureAwait(false);
            return 2;
        }

        try
        {
            using var client = ControlStatusClient.CreateForInstalledService();
            ControlStatusSnapshot status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(json ? ToJson(status) : ToHuman(status)).ConfigureAwait(false);
            return 0;
        }
        catch (RpcException exception)
        {
            await error.WriteLineAsync($"Service request failed: {exception.StatusCode}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or TimeoutException or System.Security.SecurityException)
        {
            await error.WriteLineAsync($"Service unavailable: {exception.GetType().Name}").ConfigureAwait(false);
            return 1;
        }
    }

    public static string ToHuman(ControlStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var lines = new List<string>
        {
            $"Service instance: {status.InstanceId:N}",
            $"Production sync: {(status.ProductionSyncAvailable ? "available" : "unavailable")}",
            $"Authenticated peer folders: {status.Clocks.Count}",
        };
        foreach (FolderPeerClockStatus row in status.Clocks)
        {
            PeerClockStatus clock = row.Session.Clock;
            string offset = clock.OffsetTicks is { } ticks
                ? FormatMinutes(ticks / (double)TimeSpan.TicksPerMinute) : "";
            string state = clock.State switch
            {
                PeerClockState.Accepted => $"clock accepted ({offset} min)",
                PeerClockState.Skewed => $"clock differs by {offset} min",
                PeerClockState.AwaitingHello => "waiting for peer clock",
                PeerClockState.InvalidTimestamp => "invalid peer timestamp",
                PeerClockState.InvalidElapsed => "invalid elapsed time",
                _ => "unknown clock state",
            };
            lines.Add($"  {Convert.ToHexString(row.Session.RemoteDeviceId)[..16]} session={row.Session.SessionId:N} connected={row.Session.IsConnected} {state}");
        }
        if (status.ClockStatusTruncated) lines.Add("  Clock status list is truncated.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatMinutes(double minutes)
        => (minutes >= 0 ? "+" : "") + minutes.ToString("0.00", CultureInfo.InvariantCulture);

    public static string ToJson(ControlStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var payload = new
        {
            instanceId = status.InstanceId,
            sequence = status.Sequence,
            productionSyncAvailable = status.ProductionSyncAvailable,
            clockStatusTruncated = status.ClockStatusTruncated,
            peers = status.Clocks.Select(row => new
            {
                folderFingerprint = PeerSync.Core.Diagnostics.DiagnosticFingerprint.FromId(row.FolderId),
                deviceId = Convert.ToHexString(row.Session.RemoteDeviceId),
                sessionId = row.Session.SessionId,
                connected = row.Session.IsConnected,
                clockState = row.Session.Clock.State.ToString(),
                offsetTicks = row.Session.Clock.OffsetTicks,
                offsetMinutes = row.Session.Clock.OffsetTicks / (double)TimeSpan.TicksPerMinute,
            }),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
