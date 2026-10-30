using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PeerSync.Core.Diagnostics;

/// <summary>
/// Rolling local text log for warning and error exceptions.
/// <c>debug.jsonl</c> stays limited to named metadata. This file keeps the exception
/// chain, stack, and diagnostic fields for 30 UTC days. It is not collected into
/// support bundles.
/// </summary>
public sealed partial class ExceptionLog : IDisposable
{
    public const int DefaultRetainDays = 30;
    private const int MaxSegmentsPerDay = 16;
    private const long DefaultMaxFileBytes = 8 * 1024 * 1024;
    private const int MaxMessageChars = 8192;
    private const int MaxValueChars = 2000;
    private const int MaxStackChars = 32768;
    private const int MaxRecordChars = 200_000;
    private static readonly ConcurrentLock Gates = new();
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object gate;
    private readonly string directory;
    private readonly TimeProvider clock;
    private readonly int retainDays;
    private readonly long maxFileBytes;
    private readonly HashSet<DateOnly> overflowNoted = [];
    private bool disposed;

    public ExceptionLog(string directory, TimeProvider? clock = null, int retainDays = DefaultRetainDays, long maxFileBytes = DefaultMaxFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1024);
        this.directory = Path.GetFullPath(directory);
        this.clock = clock ?? TimeProvider.System;
        this.retainDays = retainDays;
        this.maxFileBytes = maxFileBytes;
        gate = Gates.For(this.directory);
    }

    public string DirectoryPath => directory;

    public bool TryAppend(DateTimeOffset occurredUtc, string component, string runId, int processId, string? traceId, DiagnosticEvent entry)
    {
        try { Append(occurredUtc, component, runId, processId, traceId, entry); return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    public void Append(DateTimeOffset occurredUtc, string component, string runId, int processId, string? traceId, DiagnosticEvent entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Exception is null) throw new ArgumentException("An exception log record requires an exception.", nameof(entry));
        string record = FormatRecord(occurredUtc, component, runId, processId, traceId, entry);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Directory.CreateDirectory(directory);
            Purge();
            DateOnly day = DateOnly.FromDateTime(occurredUtc.UtcDateTime);
            string? path = SelectPath(day, Encoding.GetByteCount(record) + 1);
            if (path is null)
            {
                NoteOverflow(day);
                return;
            }
            bool created = !File.Exists(path) || new FileInfo(path).Length == 0;
            string text = (created ? Header(component) : "") + record;
            using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            output.Write(Encoding.GetBytes(text));
            output.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        GC.SuppressFinalize(this);
    }

    internal static string FormatRecord(DateTimeOffset occurredUtc, string component, string runId, int processId, string? traceId, DiagnosticEvent entry)
    {
        var builder = new StringBuilder();
        builder.Append("------------------------------------------------------------------------").Append('\n');
        builder.Append("timestampUtc: ").Append(occurredUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("level: ").AppendLine(entry.Level.ToString());
        builder.Append("component: ").AppendLine(component);
        builder.Append("processId: ").AppendLine(processId.ToString(CultureInfo.InvariantCulture));
        builder.Append("runId: ").AppendLine(runId);
        builder.Append("event: ").AppendLine(entry.Event);
        builder.Append("operation: ").AppendLine(entry.Operation);
        Field(builder, "sessionId", entry.SessionId?.ToString("D"));
        Field(builder, "traceId", string.IsNullOrEmpty(traceId) ? null : traceId);
        Field(builder, "transport", entry.Transport?.ToString());
        Field(builder, "errorCode", entry.ErrorCode);
        Field(builder, "hResult", entry.HResult is int hresult ? Hex(hresult) : null);
        Field(builder, "nativeError", entry.NativeError?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "transportErrorCode", entry.TransportErrorCode?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "applicationErrorCode", entry.ApplicationErrorCode?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "bytes", entry.Bytes?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "elapsedMs", entry.ElapsedMilliseconds?.ToString("G17", CultureInfo.InvariantCulture));
        Field(builder, "clockSkewSeconds", entry.ClockSkewSeconds?.ToString("G17", CultureInfo.InvariantCulture));
        Field(builder, "retryInMs", entry.RetryInMilliseconds?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "itemsVisited", entry.ItemsVisited?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "itemsChanged", entry.ItemsChanged?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "itemsHashed", entry.ItemsHashed?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "itemsSkipped", entry.ItemsSkipped?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "itemsDeferred", entry.ItemsDeferred?.ToString(CultureInfo.InvariantCulture));
        Field(builder, "remoteAddress", entry.RemoteAddress?.ToString());
        Field(builder, "remotePort", entry.RemotePort?.ToString(CultureInfo.InvariantCulture));
        var chain = new List<Exception>();
        Collect(entry.Exception!, chain, 0);
        for (int index = 0; index < chain.Count; index++) WriteException(builder, index, chain[index]);
        if (builder.Length > MaxRecordChars)
        {
            builder.Length = MaxRecordChars;
            builder.Append('\n').AppendLine("... record truncated");
        }
        builder.Append('\n');
        return builder.ToString();
    }

    private static void WriteException(StringBuilder builder, int index, Exception error)
    {
        builder.Append('\n').Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append("] ");
        builder.AppendLine(error.GetType().FullName ?? error.GetType().Name);
        Field(builder, "message", Truncate(Redact(error.Message), MaxMessageChars));
        Field(builder, "hResult", Hex(error.HResult));
        Field(builder, "source", string.IsNullOrEmpty(error.Source) ? null : error.Source);
        Field(builder, "targetSite", Target(error));
        Field(builder, "helpLink", string.IsNullOrEmpty(error.HelpLink) ? null : error.HelpLink);
        WriteProperties(builder, error);
        WriteData(builder, error);
        string? stack = error.StackTrace;
        if (!string.IsNullOrEmpty(stack))
        {
            builder.AppendLine("stackTrace:");
            builder.AppendLine(Truncate(Redact(stack), MaxStackChars));
        }
    }

    private static void WriteProperties(StringBuilder builder, Exception error)
    {
        PropertyInfo[] properties;
        try { properties = error.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public); }
        catch (Exception failure) when (failure is not OutOfMemoryException) { return; }
        foreach (PropertyInfo property in properties)
        {
            if (Skipped(property)) continue;
            object? value;
            try { value = property.GetValue(error); }
            catch (Exception failure) when (failure is not OutOfMemoryException) { continue; }
            if (value is null) continue;
            string rendered = IsSensitiveName(property.Name) ? "[redacted]" : FormatValue(value);
            if (rendered.Length == 0) continue;
            builder.Append(property.Name).Append(": ").AppendLine(rendered);
        }
    }

    private static void WriteData(StringBuilder builder, Exception error)
    {
        try
        {
            IDictionary data = error.Data;
            if (data.Count == 0) return;
            foreach (DictionaryEntry item in data)
            {
                string key = Truncate(Redact(Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? ""), 200);
                if (key.Length == 0) continue;
                string rendered = IsSensitiveName(key) ? "[redacted]" : FormatValue(item.Value);
                builder.Append("data.").Append(key).Append(": ").AppendLine(rendered);
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException) { }
    }

    private static bool Skipped(PropertyInfo property)
    {
        if (property.GetIndexParameters().Length != 0 || !property.CanRead) return true;
        return property.Name is "Message" or "StackTrace" or "InnerException" or "InnerExceptions"
            or "Data" or "TargetSite" or "Source" or "HelpLink" or "HResult";
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "";
        if (value is string text) return Truncate(Redact(text), MaxValueChars);
        if (value is byte[] bytes) return "byte[" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "]";
        if (value is Exception) return "[exception]";
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        if (typeName.Contains("Certificate", StringComparison.Ordinal)
            || typeName.Contains("AsymmetricAlgorithm", StringComparison.Ordinal)
            || typeName.Contains("CngKey", StringComparison.Ordinal)
            || typeName.Contains("SafeHandle", StringComparison.Ordinal))
            return "[redacted]";
        if (value is DateTime date) return date.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        if (value is DateTimeOffset stamp) return stamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        if (value is IPAddress address) return address.ToString();
        if (value is IFormattable formattable) return Truncate(Redact(formattable.ToString(null, CultureInfo.InvariantCulture) ?? ""), MaxValueChars);
        return Truncate(Redact(value.ToString() ?? ""), MaxValueChars);
    }

    private static string? Target(Exception error)
    {
        try
        {
            MethodBase? site = error.TargetSite;
            if (site is null) return null;
            string type = site.DeclaringType?.FullName ?? "";
            return type.Length == 0 ? site.Name : type + "." + site.Name;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException) { return null; }
    }

    private static void Collect(Exception error, List<Exception> chain, int depth)
    {
        if (depth > 16 || chain.Exists(item => ReferenceEquals(item, error))) return;
        chain.Add(error);
        if (error is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions) Collect(inner, chain, depth + 1);
            return;
        }
        if (error.InnerException is not null) Collect(error.InnerException, chain, depth + 1);
        if (ExtraInner(error) is { } extra) Collect(extra, chain, depth + 1);
    }

    private static Exception? ExtraInner(Exception error)
    {
        try
        {
            object? status = error.GetType().GetProperty("Status")?.GetValue(error);
            return status?.GetType().GetProperty("DebugException")?.GetValue(status) as Exception;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException) { return null; }
    }

    private static bool IsSensitiveName(string name)
    {
        if (name.Equals("CancellationToken", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Token", StringComparison.OrdinalIgnoreCase);
    }

    private static string Redact(string text)
    {
        if (text.Length == 0) return text;
        text = PrivateKeyPattern().Replace(text, "[redacted-private-key]");
        return FolderKeyPattern().Replace(text, "[redacted-folder-key]");
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "... truncated";

    private static string Hex(int value)
        => "0x" + unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture)
            + " (" + value.ToString(CultureInfo.InvariantCulture) + ")";

    private static void Field(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        builder.Append(name).Append(": ").AppendLine(Redact(value));
    }

    private static string Header(string component) =>
        "# PeerSync exception log\n# component: " + component
        + "\n# runtime: " + RuntimeInformation.FrameworkDescription
        + "\n# os: " + RuntimeInformation.OSDescription
        + "\n# process: " + (Environment.ProcessPath ?? "")
        + "\n# Local text for diagnosing exceptions. Not included in diagnostics bundles."
        + "\n# Messages and stacks can contain paths and addresses. Folder keys and private keys are redacted.\n\n";

    private void Purge()
    {
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        DateOnly limit = today.AddDays(-retainDays);
        foreach (string file in Directory.EnumerateFiles(directory, "exceptions-*.log"))
        {
            if (!TryDate(Path.GetFileName(file), out DateOnly date) || date > limit) continue;
            try { File.Delete(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private string? SelectPath(DateOnly day, int incomingBytes)
    {
        for (int segment = 0; segment < MaxSegmentsPerDay - 1; segment++)
        {
            string path = Path.Combine(directory, FileName(day, segment));
            long length = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (length == 0 || length + incomingBytes <= maxFileBytes) return path;
        }
        string last = Path.Combine(directory, FileName(day, MaxSegmentsPerDay - 1));
        long used = File.Exists(last) ? new FileInfo(last).Length : 0;
        return used == 0 || used + incomingBytes <= maxFileBytes ? last : null;
    }

    private void NoteOverflow(DateOnly day)
    {
        if (!overflowNoted.Add(day)) return;
        string path = Path.Combine(directory, FileName(day, MaxSegmentsPerDay - 1));
        using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        output.Write(Encoding.GetBytes("# further exceptions were not recorded because this UTC day reached its size limit\n"));
        output.Flush(flushToDisk: true);
    }

    private static string FileName(DateOnly day, int segment)
    {
        string date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return segment == 0
            ? "exceptions-" + date + ".log"
            : "exceptions-" + date + "." + segment.ToString(CultureInfo.InvariantCulture) + ".log";
    }

    private static bool TryDate(string name, out DateOnly date)
    {
        date = default;
        if (!name.StartsWith("exceptions-", StringComparison.Ordinal) || !name.EndsWith(".log", StringComparison.Ordinal)) return false;
        ReadOnlySpan<char> body = name.AsSpan("exceptions-".Length, name.Length - "exceptions-".Length - ".log".Length);
        if (body.Length < 10) return false;
        if (!DateOnly.TryParseExact(body[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
        if (body.Length == 10) return true;
        if (body[10] != '.') return false;
        return int.TryParse(body[11..], NumberStyles.None, CultureInfo.InvariantCulture, out int segment)
            && segment is > 0 and < MaxSegmentsPerDay
            && body[11..].SequenceEqual(segment.ToString(CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"\b[ABF][A-Z2-7]{54,104}\b", RegexOptions.CultureInvariant)]
    private static partial Regex FolderKeyPattern();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PrivateKeyPattern();

    private sealed class ConcurrentLock
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> gates = new(StringComparer.OrdinalIgnoreCase);
        public object For(string path) => gates.GetOrAdd(path, static _ => new object());
    }
}
