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
    private readonly TimeProvider clock;
    private readonly ITimer expiryTimer;
    private DateTime nextExpiryUtc = DateTime.MaxValue;
    private bool disposed;
    private long droppedEvents;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public DiagnosticJournal(string directory, string component, bool debugEnabled = false,
        long maxFileBytes = 14_000_000, int retainedFiles = 7,
        TimeProvider? timeProvider = null, ExceptionLog? exceptions = null)
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
        clock = timeProvider ?? TimeProvider.System;
        DebugEnabled = debugEnabled;
        Directory.CreateDirectory(this.directory);
        expiryTimer = clock.CreateTimer(_ => SweepExpiredOnTimer(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try { SweepExpiredAndSchedule(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Interlocked.Increment(ref droppedEvents); expiryTimer.Change(TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan); }
    }

    public bool DebugEnabled { get; }
    public string RunId => runId;
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
        ValidateFingerprint(entry.FolderFingerprint);
        ValidateFingerprint(entry.PeerFingerprint);
        if (entry.RemotePort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(entry), "A diagnostic remote port must be a valid TCP or UDP port.");
        DateTime occurredUtc = clock.GetUtcNow().UtcDateTime;
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
            folderFingerprint = entry.FolderFingerprint,
            peerFingerprint = entry.PeerFingerprint,
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
            workingSetBytes = entry.WorkingSetBytes,
            privateBytes = entry.PrivateBytes,
            managedHeapBytes = entry.ManagedHeapBytes,
            remoteAddress = entry.RemoteAddress?.ToString(),
            remotePort = entry.RemotePort,
        }, Json);
        if (line.Length > 4095) throw new ArgumentException("Diagnostic event exceeds the record limit.", nameof(entry));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (occurredUtc >= nextExpiryUtc) SweepExpiredAndSchedule();
            string file = Path.Combine(directory, "debug.jsonl");
            bool freshFile = Rotate(file, line.Length + 1);
            using (var output = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
            { output.Write(line); output.WriteByte((byte)'\n'); }
            if (freshFile)
            {
                // Match the event clock when a test or host supplies a clock;
                // otherwise a time jump can make a newly written file look old.
                File.SetCreationTimeUtc(file, occurredUtc);
                File.SetLastWriteTimeUtc(file, occurredUtc);
                SweepExpiredAndSchedule();
            }
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

    private static void ValidateFingerprint(string? fingerprint)
    {
        if (fingerprint is not null && (fingerprint.Length != 16 || fingerprint.Any(c => !char.IsAsciiHexDigitUpper(c))))
            throw new ArgumentException("A diagnostic fingerprint must contain 16 uppercase hexadecimal digits.");
    }

    private bool Rotate(string file, int incoming)
    {
        if (!File.Exists(file)) return true;
        if (new FileInfo(file).Length + incoming <= maxFileBytes) return false;
        if (retainedFiles > 1) File.Delete(Path.Combine(directory, $"debug.{retainedFiles - 1}.jsonl"));
        for (int i = retainedFiles - 2; i >= 1; i--)
        {
            string source = Path.Combine(directory, $"debug.{i}.jsonl");
            if (File.Exists(source)) File.Move(source, Path.Combine(directory, $"debug.{i + 1}.jsonl"), overwrite: true);
        }
        if (retainedFiles > 1) File.Move(file, Path.Combine(directory, "debug.1.jsonl"), overwrite: true);
        else File.Delete(file);
        return true;
    }

    private void SweepExpiredOnTimer()
    {
        lock (gate)
        {
            if (disposed) return;
            try { SweepExpiredAndSchedule(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Interlocked.Increment(ref droppedEvents); expiryTimer.Change(TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan); }
        }
    }

    private void SweepExpiredAndSchedule()
    {
        DateTime now = clock.GetUtcNow().UtcDateTime;
        long cutoff = Math.Max(0, now.Ticks - TimeSpan.TicksPerDay);
        long earliest = DateTime.MaxValue.Ticks;
        foreach (string file in Directory.EnumerateFiles(directory, "debug*.jsonl"))
        {
            if (!IsDiagnosticFileName(Path.GetFileName(file))) continue;
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                long oldest = Math.Min(File.GetCreationTimeUtc(file).Ticks, File.GetLastWriteTimeUtc(file).Ticks);
                if (oldest <= cutoff) { File.Delete(file); continue; }
                earliest = Math.Min(earliest, oldest > DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay
                    ? DateTime.MaxValue.Ticks : oldest + TimeSpan.TicksPerDay);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        nextExpiryUtc = new DateTime(earliest, DateTimeKind.Utc);
        TimeSpan due = earliest <= now.Ticks ? TimeSpan.Zero : TimeSpan.FromTicks(earliest - now.Ticks);
        expiryTimer.Change(due < TimeSpan.FromMinutes(15) ? due : TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan);
    }

    private static bool IsDiagnosticFileName(string name)
    {
        if (name == "debug.jsonl") return true;
        if (!name.StartsWith("debug.", StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal)) return false;
        ReadOnlySpan<char> digits = name.AsSpan(6, name.Length - 12);
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value > 0 && digits.SequenceEqual(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; expiryTimer.Dispose(); }
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
    public long? WorkingSetBytes { get; init; }
    public long? PrivateBytes { get; init; }
    public long? ManagedHeapBytes { get; init; }
    public DiagnosticLevel Level { get; init; } = DiagnosticLevel.Information;
    public Guid? SessionId { get; init; }
    public string? FolderFingerprint { get; init; }
    public string? PeerFingerprint { get; init; }
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
