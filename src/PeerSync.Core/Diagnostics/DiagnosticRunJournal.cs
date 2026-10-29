using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace PeerSync.Core.Diagnostics;

public sealed record DiagnosticRetentionResult(int Visited, int RemovedRuns, int RemovedFiles, int ProtectedRuns, int RetainedRuns, long RetainedBytes, bool Pressure);

/// <summary>Distinct active writer directory plus shared inactive-run retention.
/// Active, unmarked and foreign objects are never deleted to meet a quota.</summary>
public sealed class DiagnosticRunJournal : IDisposable
{
    private static ReadOnlySpan<byte> Marker => "PeerSync diagnostic run v1\n"u8;
    private readonly DiagnosticDirectory root;
    private readonly SafeFileHandle active;
    private readonly TimeProvider clock;
    private readonly int maximumRuns;
    private readonly long maximumBytes;
    private readonly object gate = new();
    private readonly ITimer timer;
    private bool disposed;
    public string DirectoryPath { get; }
    public DiagnosticJournal Journal { get; }
    private DiagnosticRunJournal(string logRoot, string component, bool debugEnabled, int maximumInactiveRuns, long maximumInactiveBytes, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumInactiveRuns, 0); ArgumentOutOfRangeException.ThrowIfLessThan(maximumInactiveBytes, 0);
        maximumRuns = maximumInactiveRuns; maximumBytes = maximumInactiveBytes; clock = timeProvider; root = new(logRoot);
        try
        {
            string name = Guid.NewGuid().ToString("N"); active = DiagnosticDirectory.Open(root.Handle, name, true, 0x00100081, 3, 2);
            DirectoryPath = Path.Combine(root.Path, name);
            using (var marker = DiagnosticDirectory.Open(active, ".run", false, 0x00100183, 0, 2))
            using (var output = new FileStream(marker, FileAccess.ReadWrite)) { output.Write(Marker); output.Flush(flushToDisk: true); }
            Journal = new(DirectoryPath, component, debugEnabled, timeProvider: clock);
            SweepAndReport(); timer = clock.CreateTimer(_ => SweepAndReport(), null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        }
        catch { active?.Dispose(); root.Dispose(); throw; }
    }
    public static DiagnosticRunJournal Open(string logRoot, string component, bool debugEnabled = false,
        int maximumInactiveRuns = 8, long maximumInactiveBytes = 100_000_000, TimeProvider? timeProvider = null)
        => new(logRoot, component, debugEnabled, maximumInactiveRuns, maximumInactiveBytes, timeProvider ?? TimeProvider.System);
    public DiagnosticRetentionResult SweepInactiveRuns()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Sweep();
        }
    }
    private void SweepAndReport()
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                var result = Sweep();
                Journal.TryWrite(new("logs.retention", "logs") { ItemsVisited = result.Visited, ItemsChanged = result.RemovedRuns,
                    ItemsDeferred = result.ProtectedRuns, ErrorCode = result.Pressure ? "inactive_quota_pressure" : null });
            }
            catch (Exception error) when (Expected(error))
            { Journal.TryWrite(new("logs.retention_deferred", "logs") { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult }); }
        }
    }
    private DiagnosticRetentionResult Sweep(bool waitForLease = false)
    {
        using var sweepLock = OpenSweepLock(waitForLease);
        var candidates = new List<RunCandidate>(); int visited = 0, protectedRuns = 0, removedFiles = 0, removedRuns = 0, retainedRuns = 0; long bytes = 0;
        try
        {
            foreach (string path in Directory.EnumerateDirectories(root.Path))
            {
                string name = Path.GetFileName(path); if (!Guid.TryParseExact(name, "N", out _)) continue; visited++;
                RunCandidate? run = null;
                try
                {
                    run = ReadRun(name); long cutoff = clock.GetUtcNow().UtcTicks - TimeSpan.TicksPerDay;
                    foreach (var file in run.Files.ToArray())
                        if (DateTime.FromFileTimeUtc(file.Info.CreationTime).Ticks < cutoff || DateTime.FromFileTimeUtc(file.Info.LastWriteTime).Ticks < cutoff)
                        { DiagnosticDirectory.Delete(file.Handle); file.Dispose(); run.Files.Remove(file); removedFiles++; }
                    candidates.Add(run); run = null;
                }
                catch (Exception error) when (Expected(error)) { protectedRuns++; }
                finally { run?.Dispose(); }
            }
            foreach (var run in candidates.OrderByDescending(candidate => candidate.LatestWrite).ThenBy(candidate => candidate.Name, StringComparer.Ordinal))
            {
                long length = run.Files.Aggregate(0L, (total, file) => Add(total, file.Length));
                if (run.Files.Count != 0 && retainedRuns < maximumRuns && length <= maximumBytes - bytes)
                { retainedRuns++; bytes += length; continue; }
                try
                {
                    foreach (var file in run.Files) { DiagnosticDirectory.Delete(file.Handle); file.Dispose(); removedFiles++; }
                    DiagnosticDirectory.Delete(run.Marker); run.Marker.Dispose(); DiagnosticDirectory.Delete(run.Directory); run.Directory.Dispose(); removedRuns++;
                }
                catch (Exception error) when (Expected(error)) { protectedRuns++; retainedRuns++; bytes = Add(bytes, length); }
            }
            return new(visited, removedRuns, removedFiles, protectedRuns, retainedRuns, bytes, retainedRuns > maximumRuns || bytes > maximumBytes);
        }
        finally { foreach (var run in candidates) run.Dispose(); }
    }
    private RunCandidate ReadRun(string name)
    {
        var directory = DiagnosticDirectory.Open(root.Handle, name, true, 0x00110081, 3);
        SafeFileHandle? marker = null; var files = new List<LogFile>();
        try
        {
            marker = DiagnosticDirectory.Open(directory, ".run", false, 0x00110081, 0);
            if (RandomAccess.GetLength(marker) != Marker.Length) throw new IOException("Unrecognized diagnostic run marker.");
            byte[] bytes = new byte[Marker.Length]; if (RandomAccess.Read(marker, bytes, 0) != bytes.Length || !bytes.AsSpan().SequenceEqual(Marker)) throw new IOException("Unrecognized diagnostic run marker.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(Path.Combine(root.Path, name)))
            {
                string file = Path.GetFileName(entry); if (file == ".run") continue;
                if (files.Count >= 64 || !LogName(file)) throw new IOException("Unknown diagnostic run content is protected.");
                var handle = DiagnosticDirectory.Open(directory, file, false, 0x00110081, 1);
                try { files.Add(new(handle, DiagnosticDirectory.Basic(handle), RandomAccess.GetLength(handle))); }
                catch { handle.Dispose(); throw; }
            }
            return new(name, directory, marker, files);
        }
        catch { foreach (var file in files) file.Dispose(); marker?.Dispose(); directory.Dispose(); throw; }
    }
    private static bool LogName(string name)
    {
        if (name == "debug.jsonl") return true;
        if (!name.StartsWith("debug.", StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal)) return false;
        var digits = name.AsSpan(6, name.Length - 12);
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value is > 0 and < 64 && digits.SequenceEqual(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private static long Add(long left, long right) => right > long.MaxValue - left ? long.MaxValue : left + right;
    private SafeFileHandle OpenSweepLock(bool wait)
    {
        var elapsed = Stopwatch.StartNew();
        for (;;)
            try { return DiagnosticDirectory.Open(root.Handle, ".sweep.lock", false, 0x00100183, 0, 3); }
            catch (Win32Exception error) when (error.NativeErrorCode == 32 && wait && elapsed.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(10); }
    }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception;
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; timer.Dispose(); Journal.Dispose(); active.Dispose();
            // Closing the last process must not leave its newly inactive run
            // outside the shared budget until somebody starts the app again.
            try { Sweep(waitForLease: true); }
            catch (Exception error) when (Expected(error)) { }
            finally { root.Dispose(); }
        }
    }
    public override string ToString() => "Diagnostic run journal (redacted)";
    private sealed record LogFile(SafeFileHandle Handle, LogNative.BasicInfo Info, long Length) : IDisposable { public void Dispose() => Handle.Dispose(); }
    private sealed record RunCandidate(string Name, SafeFileHandle Directory, SafeFileHandle Marker, List<LogFile> Files) : IDisposable
    {
        internal long LatestWrite => Files.Count == 0 ? 0 : Files.Max(file => file.Info.LastWriteTime);
        public void Dispose() { foreach (var file in Files) file.Dispose(); Marker.Dispose(); Directory.Dispose(); }
    }
}
