using System.Text.Json;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Rendezvous.Hosting;

internal static class RendezvousDiagnosticsCommand
{
    internal static int Run(string[] args)
    {
        string? output = null;
        string? logsOverride = null;
        bool addresses = false;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--output" && index + 1 < args.Length) output = args[++index];
            else if (args[index] == "--logs" && index + 1 < args.Length) logsOverride = args[++index];
            else if (args[index] == "--include-network-addresses") addresses = true;
            else { Console.Error.WriteLine("Usage: diagnostics --output <new-zip> [--logs <directory>] [--include-network-addresses]"); return 2; }
        }
        if (string.IsNullOrWhiteSpace(output))
        { Console.Error.WriteLine("Usage: diagnostics --output <new-zip> [--logs <directory>] [--include-network-addresses]"); return 2; }
        if (logsOverride is not null && !Path.IsPathFullyQualified(logsOverride))
        { Console.Error.WriteLine("--logs must be an absolute directory path."); return 2; }
        string logs = logsOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PeerSync.Rendezvous", "logs");
        try
        {
            DiagnosticBundleResult result = DiagnosticBundleCollector.Collect(logs, output, addresses);
            Console.WriteLine(JsonSerializer.Serialize(new
            { bundle = result.Destination, eventsIncluded = result.EventsIncluded,
                expiredEvents = result.ExpiredEvents, invalidEvents = result.InvalidEvents,
                networkAddressesIncluded = addresses }));
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}"); return 1; }
    }
}
