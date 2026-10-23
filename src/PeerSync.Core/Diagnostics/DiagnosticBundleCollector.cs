using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PeerSync.Core.Diagnostics;

public sealed record DiagnosticBundleResult(string Destination, int FilesFound, int FilesIncluded,
    int EventsIncluded, int ExpiredEvents, int InvalidEvents, long BytesIncluded);

/// <summary>Creates a sanitized, recent-only archive from structured diagnostic JSONL.</summary>
public static class DiagnosticBundleCollector
{
    private const int MaximumDirectories = 2048;
    private const int MaximumFiles = 512;
    private const int MaximumEvents = 100_000;
    private const int MaximumLineCharacters = 16_384;
    private const long MaximumInputFileBytes = 20_000_000;
    private const long MaximumBundleBytes = 100_000_000;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public static DiagnosticBundleResult Collect(string logRoot, string destination,
        bool includeNetworkAddresses = false, DateTimeOffset? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(logRoot));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The diagnostic log directory was not found.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The diagnostic log root cannot be a reparse point.");

        string target = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("The destination has no parent directory.", nameof(destination));
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The destination directory was not found.");
        if (IsWithin(root, target)) throw new ArgumentException("The diagnostic bundle destination must be outside the log directory.", nameof(destination));
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("The diagnostic bundle destination already exists.");

        DateTimeOffset cutoff = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime() - Retention;
        List<string> files = FindLogFiles(root);
        int filesIncluded = 0, eventsIncluded = 0, expired = 0, invalid = 0;
        long bytesIncluded = 0;
        string temporary = Path.Combine(parent, $".peersync-diagnostics-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                int fileNumber = 0;
                foreach (string file in files)
                {
                    if (new FileInfo(file).Length > MaximumInputFileBytes) { invalid++; continue; }
                    using var sanitized = new MemoryStream();
                    int thisFileEvents = 0;
                    try
                    {
                        using var input = new FileStream(file, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, bufferSize: 16 * 1024, FileOptions.SequentialScan);
                        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false,
                            bufferSize: 16 * 1024, leaveOpen: false);
                        string? line;
                        while ((line = reader.ReadLine()) is not null)
                        {
                            if (line.Length > MaximumLineCharacters) { invalid++; continue; }
                            byte[] clean; bool wasExpired;
                            try
                            {
                                if (!TrySanitize(line, cutoff, includeNetworkAddresses, out clean, out wasExpired))
                                { if (wasExpired) expired++; else invalid++; continue; }
                            }
                            catch (JsonException) { invalid++; continue; }
                            if (eventsIncluded + thisFileEvents >= MaximumEvents) throw new IOException("The diagnostic bundle event limit was exceeded.");
                            sanitized.Write(clean); sanitized.WriteByte((byte)'\n'); thisFileEvents++;
                            if (sanitized.Length > MaximumBundleBytes) throw new IOException("The diagnostic bundle size limit was exceeded.");
                        }
                    }
                    catch (DecoderFallbackException) { invalid++; continue; }
                    if (thisFileEvents == 0) continue;
                    long newTotal = checked(bytesIncluded + sanitized.Length);
                    if (newTotal > MaximumBundleBytes) throw new IOException("The diagnostic bundle size limit was exceeded.");
                    ZipArchiveEntry entry = archive.CreateEntry($"debug-{fileNumber++:D3}.jsonl", CompressionLevel.Optimal);
                    using (Stream entryStream = entry.Open())
                    { sanitized.Position = 0; sanitized.CopyTo(entryStream); }
                    filesIncluded++; eventsIncluded += thisFileEvents; bytesIncluded = newTotal;
                }

                if (eventsIncluded == 0)
                    throw new InvalidDataException("No recent valid diagnostic events were found. Reproduce the issue and collect the LOGS directory printed by the service or probe.");
                ZipArchiveEntry environment = archive.CreateEntry("environment.json", CompressionLevel.Optimal);
                using (Stream stream = environment.Open())
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("collectedUtc", (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    writer.WriteString("os", RuntimeInformation.OSDescription);
                    writer.WriteString("osVersion", Environment.OSVersion.Version.ToString());
                    writer.WriteString("processArchitecture", RuntimeInformation.ProcessArchitecture.ToString());
                    writer.WriteString("framework", RuntimeInformation.FrameworkDescription);
                    writer.WriteNumber("filesFound", files.Count); writer.WriteNumber("filesIncluded", filesIncluded);
                    writer.WriteNumber("eventsIncluded", eventsIncluded); writer.WriteNumber("expiredEvents", expired);
                    writer.WriteNumber("invalidEvents", invalid); writer.WriteBoolean("networkAddressesIncluded", includeNetworkAddresses);
                    writer.WriteEndObject(); writer.Flush();
                }
            }
            File.Move(temporary, target);
            return new(target, files.Count, filesIncluded, eventsIncluded, expired, invalid, bytesIncluded);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static List<string> FindLogFiles(string root)
    {
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(root); int visited = 0;
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (++visited > MaximumDirectories) throw new IOException("The diagnostic directory limit was exceeded.");
                    pending.Push(entry); continue;
                }
                if (!IsDiagnosticFileName(Path.GetFileName(entry))) continue;
                if (files.Count == MaximumFiles) throw new IOException("The diagnostic file limit was exceeded.");
                files.Add(entry);
            }
        }
        return files;
    }

    private static bool IsDiagnosticFileName(string name)
    {
        if (name == "debug.jsonl") return true;
        if (!name.StartsWith("debug.", StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal)) return false;
        ReadOnlySpan<char> digits = name.AsSpan(6, name.Length - 12);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            && value is > 0 and < 64 && digits.SequenceEqual(value.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TrySanitize(string line, DateTimeOffset cutoff, bool includeAddresses,
        out byte[] clean, out bool expired)
    {
        clean = []; expired = false;
        using JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !TryString(root, "timestampUtc", out string timestampText)
            || !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp)) return false;
        if (timestamp < cutoff) { expired = true; return false; }
        if (!TryCode(root, "component", required: true, out _)
            || !TryCode(root, "eventName", required: true, out _)
            || !TryCode(root, "operation", required: true, out _)
            || !TryCode(root, "level", required: true, out string level)
            || level is not ("Debug" or "Information" or "Warning" or "Error")) return false;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("timestampUtc", timestamp.ToString("O", CultureInfo.InvariantCulture));
            CopyCode(root, writer, "component"); CopyGuid(root, writer, "runId"); CopyNonnegativeInt(root, writer, "processId");
            writer.WriteString("level", level); CopyCode(root, writer, "eventName"); CopyCode(root, writer, "operation");
            CopyGuid(root, writer, "sessionId"); CopyHex(root, writer, "traceId", 32);
            CopyEnum(root, writer, "transport", "Quic", "Tcp", "Http2", "Http3", "NamedPipe", "Relay");
            CopyNonnegativeLong(root, writer, "bytes"); CopyFiniteDouble(root, writer, "elapsedMs");
            CopyFiniteDouble(root, writer, "clockSkewSeconds"); CopyCode(root, writer, "errorCode");
            CopyInteger(root, writer, "hResult", int.MinValue, int.MaxValue); CopyInteger(root, writer, "nativeError", int.MinValue, int.MaxValue);
            CopyInteger(root, writer, "transportErrorCode", long.MinValue, long.MaxValue);
            CopyInteger(root, writer, "applicationErrorCode", long.MinValue, long.MaxValue);
            CopyNonnegativeInt(root, writer, "retryInMs"); CopyNonnegativeLong(root, writer, "itemsVisited");
            CopyNonnegativeLong(root, writer, "itemsChanged"); CopyNonnegativeLong(root, writer, "itemsHashed");
            CopyNonnegativeLong(root, writer, "itemsSkipped"); CopyNonnegativeLong(root, writer, "itemsDeferred");
            CopyNonnegativeLong(root, writer, "workingSetBytes"); CopyNonnegativeLong(root, writer, "privateBytes");
            CopyNonnegativeLong(root, writer, "managedHeapBytes");
            if (includeAddresses && TryString(root, "remoteAddress", out string address) && IPAddress.TryParse(address, out IPAddress? parsed))
            {
                writer.WriteString("remoteAddress", parsed.ToString());
                CopyInteger(root, writer, "remotePort", 1, 65535);
            }
            writer.WriteEndObject(); writer.Flush();
        }
        clean = buffer.ToArray(); return true;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            && (value = property.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryCode(JsonElement element, string name, bool required, out string value)
    {
        value = string.Empty;
        if (!TryString(element, name, out string candidate)) return !required;
        if (candidate.Length > 96 || candidate.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')) return false;
        value = candidate; return true;
    }

    private static void CopyCode(JsonElement root, Utf8JsonWriter writer, string name)
    { if (TryCode(root, name, required: false, out string value) && value.Length != 0) writer.WriteString(name, value); }
    private static void CopyGuid(JsonElement root, Utf8JsonWriter writer, string name)
    { if (TryString(root, name, out string text) && Guid.TryParse(text, out Guid value)) writer.WriteString(name, value); }
    private static void CopyHex(JsonElement root, Utf8JsonWriter writer, string name, int length)
    { if (TryString(root, name, out string value) && value.Length == length && value.All(Uri.IsHexDigit)) writer.WriteString(name, value.ToLowerInvariant()); }
    private static void CopyEnum(JsonElement root, Utf8JsonWriter writer, string name, params string[] allowed)
    { if (TryString(root, name, out string value) && allowed.Contains(value, StringComparer.Ordinal)) writer.WriteString(name, value); }
    private static void CopyNonnegativeInt(JsonElement root, Utf8JsonWriter writer, string name)
    { if (TryInteger(root, name, out long value) && value is >= 0 and <= int.MaxValue) writer.WriteNumber(name, value); }
    private static void CopyNonnegativeLong(JsonElement root, Utf8JsonWriter writer, string name)
    { if (TryInteger(root, name, out long value) && value >= 0) writer.WriteNumber(name, value); }
    private static void CopyFiniteDouble(JsonElement root, Utf8JsonWriter writer, string name)
    { if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number)) writer.WriteNumber(name, number); }
    private static void CopyInteger(JsonElement root, Utf8JsonWriter writer, string name, long minimum, long maximum)
    { if (TryInteger(root, name, out long value) && value >= minimum && value <= maximum) writer.WriteNumber(name, value); }
    private static bool TryInteger(JsonElement root, string name, out long value)
    { value = 0; return root.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value); }

    private static bool IsWithin(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
