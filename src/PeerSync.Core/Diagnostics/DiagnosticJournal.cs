using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerSync.Core.Diagnostics;

/// <summary>A process-local single-writer journal. Give concurrent processes distinct directories.
/// Only explicitly named metadata is written to <c>debug.jsonl</c>. Warning and error exceptions
/// are copied to the optional <see cref="ExceptionLog"/>.</summary>
public sealed class DiagnosticJournal : IDisposable
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string component;
    private readonly string runId = Guid.NewGuid().ToString("N");
    private readonly long maxFileBytes;
    private readonly int retainedFiles;
    private readonly ExceptionLog? exceptions;
    private bool disposed;
    private long droppedEvents;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public DiagnosticJournal(string directory, string component, bool debugEnabled = false,
        long maxFileBytes = 14_000_000, int retainedFiles = 7, ExceptionLog? exceptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ValidateCode(component);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        this.directory = Path.GetFullPath(directory);
        this.component = component;
        this.maxFileBytes = maxFileBytes;
        this.retainedFiles = retainedFiles;
        this.exceptions = exceptions;
        DebugEnabled = debugEnabled;
        Directory.CreateDirectory(this.directory);
    }

    public bool DebugEnabled { get; }
    public long DroppedEvents => Interlocked.Read(ref droppedEvents);
    /// <summary>In-process, best-effort metadata feed for local status surfaces.</summary>
    public event Action<DiagnosticEvent, DateTime>? EntryWritten;

    /// <summary>Best effort engine diagnostics. Strict Write remains available for validation tools.</summary>
    public bool TryWrite(DiagnosticEvent entry)
    {
        try { Write(entry); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
        { Interlocked.Increment(ref droppedEvents); return false; }
    }

    /// <summary>No arbitrary message, exception text, paths, keys, FolderIds, or payloads are written to the structured journal.</summary>
    public void Write(DiagnosticEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Level == DiagnosticLevel.Debug && !DebugEnabled) return;
        ValidateCode(entry.Event);
        ValidateCode(entry.Operation);
        if (entry.ErrorCode is not null) ValidateCode(entry.ErrorCode);
        if (entry.RemotePort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(entry), "A diagnostic remote port must be a valid TCP or UDP port.");
        DateTime occurredUtc = DateTime.UtcNow;
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(new
        {
            timestampUtc = new DateTimeOffset(occurredUtc),
            component,
            runId,
            processId = Environment.ProcessId,
            level = entry.Level.ToString(),
            eventName = entry.Event,
            operation = entry.Operation,
            sessionId = entry.SessionId,
            traceId = Activity.Current?.TraceId.ToString(),
            transport = entry.Transport?.ToString(),
            bytes = entry.Bytes,
            elapsedMs = entry.ElapsedMilliseconds,
            clockSkewSeconds = entry.ClockSkewSeconds,
            errorCode = entry.ErrorCode,
            hResult = entry.HResult,
            nativeError = entry.NativeError,
            transportErrorCode = entry.TransportErrorCode,
            applicationErrorCode = entry.ApplicationErrorCode,
            retryInMs = entry.RetryInMilliseconds,
            itemsVisited = entry.ItemsVisited,
            itemsChanged = entry.ItemsChanged,
            itemsHashed = entry.ItemsHashed,
            itemsSkipped = entry.ItemsSkipped,
            itemsDeferred = entry.ItemsDeferred,
            remoteAddress = entry.RemoteAddress?.ToString(),
            remotePort = entry.RemotePort,
        }, Json);
        if (line.Length > 4095) throw new ArgumentException("Diagnostic event exceeds the record limit.", nameof(entry));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string file = Path.Combine(directory, "debug.jsonl");
            Rotate(file, line.Length + 1);
            using var output = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            output.Write(line);
            output.WriteByte((byte)'\n');
        }
        if (exceptions is not null && entry.Exception is not null
            && entry.Level is DiagnosticLevel.Warning or DiagnosticLevel.Error)
            exceptions.TryAppend(new DateTimeOffset(occurredUtc), component, runId, Environment.ProcessId,
                Activity.Current?.TraceId.ToString(), entry);
        foreach (Action<DiagnosticEvent, DateTime> observer in EntryWritten?.GetInvocationList() ?? [])
            try { observer(entry, occurredUtc); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private static void ValidateCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 96 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("Diagnostic codes must be short identifiers.", nameof(code));
    }

    private void Rotate(string file, int incoming)
    {
        // IP-bearing debug events expire after 24h, including the active file.
        foreach (string old in Directory.EnumerateFiles(directory, "debug*.jsonl"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-24)
                || File.GetCreationTimeUtc(old) < DateTime.UtcNow.AddHours(-24)) File.Delete(old);
        if (!File.Exists(file) || new FileInfo(file).Length + incoming <= maxFileBytes) return;
        File.Delete(Path.Combine(directory, $"debug.{retainedFiles - 1}.jsonl"));
        for (int i = retainedFiles - 2; i >= 1; i--)
        {
            string source = Path.Combine(directory, $"debug.{i}.jsonl");
            if (File.Exists(source)) File.Move(source, Path.Combine(directory, $"debug.{i + 1}.jsonl"), overwrite: true);
        }
        if (retainedFiles > 1) File.Move(file, Path.Combine(directory, "debug.1.jsonl"), overwrite: true);
        else File.Delete(file);
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; }
    }
}

public enum DiagnosticLevel { Debug, Information, Warning, Error }
public enum DiagnosticTransport { Quic, Tcp, Http2, Http3, NamedPipe, Relay }

public sealed record DiagnosticEvent(string Event, string Operation)
{
    public long? ItemsVisited { get; init; }
    public long? ItemsChanged { get; init; }
    public long? ItemsHashed { get; init; }
    public long? ItemsSkipped { get; init; }
    public long? ItemsDeferred { get; init; }
    public DiagnosticLevel Level { get; init; } = DiagnosticLevel.Information;
    public Guid? SessionId { get; init; }
    public DiagnosticTransport? Transport { get; init; }
    public long? Bytes { get; init; }
    public double? ElapsedMilliseconds { get; init; }
    public double? ClockSkewSeconds { get; init; }
    public string? ErrorCode { get; init; }
    /// <summary>In memory only. Warning and error records copy it to <see cref="ExceptionLog"/> when one is attached.
    /// The structured journal does not serialize the message or stack.</summary>
    [JsonIgnore]
    public Exception? Exception { get; init; }
    public int? HResult { get; init; }
    public int? NativeError { get; init; }
    public long? TransportErrorCode { get; init; }
    public long? ApplicationErrorCode { get; init; }
    public int? RetryInMilliseconds { get; init; }
    public System.Net.IPAddress? RemoteAddress { get; init; }
    public int? RemotePort { get; init; }
}
