using PeerSync.Core.Diagnostics;

namespace PeerSync.Cli.Diagnostics;

public static class DiagnosticsCommand
{
    public static int Run(string[] args)
    {
        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeerSync", "logs");
        string? destination = null; bool includeAddresses = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--logs" when index + 1 < args.Length: logs = args[++index]; break;
                case "--output" when index + 1 < args.Length: destination = args[++index]; break;
                case "--include-network-addresses": includeAddresses = true; break;
                default:
                    Console.Error.WriteLine("Usage: psp diagnostics [--logs <directory>] [--output <new-zip>] [--include-network-addresses]");
                    return 2;
            }
        }

        destination ??= Path.Combine(Environment.CurrentDirectory,
            $"PeerSync-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
        try
        {
            DiagnosticBundleResult result = DiagnosticBundleCollector.Collect(logs, destination, includeAddresses);
            Console.WriteLine($"Collected {result.EventsIncluded} recent events from {result.FilesIncluded} log files; "
                + $"omitted={result.EventsOmitted}, expired={result.ExpiredEvents}, "
                + $"invalid={result.InvalidEvents}, bytes={result.BytesIncluded}.");
            Console.WriteLine($"BUNDLE {result.Destination}");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Diagnostic collection failed: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }
}
