using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Engine.Scanning;

public sealed record WatchBatch(IReadOnlyList<string> RelativePaths, bool FullScan, string? Reason = null);

/// <summary>Native callbacks only enqueue; a single consumer debounces and requests scans.</summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly Channel<Notification> notifications;
    private readonly string root;
    private readonly string prefix;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly DiagnosticJournal? journal;
    private readonly Guid session = Guid.NewGuid();
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken shutdownToken;
    private readonly int pendingCapacity;
    private int fullScan;
    private int reading;
    private int disposed;

    public FolderWatcher(string root, TimeProvider? timeProvider = null, DiagnosticJournal? journal = null)
        : this(root, 8192, 8192, timeProvider, journal) { }

    internal FolderWatcher(string root, int queueCapacity, int pendingCapacity,
        TimeProvider? timeProvider = null, DiagnosticJournal? journal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pendingCapacity);
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        prefix = Path.EndsInDirectorySeparator(this.root) ? this.root : this.root + Path.DirectorySeparatorChar;
        this.pendingCapacity = pendingCapacity;
        clock = timeProvider ?? TimeProvider.System;
        started = clock.GetTimestamp();
        this.journal = journal;
        shutdownToken = shutdown.Token;
        notifications = Channel.CreateBounded<Notification>(new BoundedChannelOptions(queueCapacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        watcher = new FileSystemWatcher(this.root)
        {
            IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime,
        };
        watcher.Created += Changed; watcher.Changed += Changed; watcher.Deleted += Changed;
        watcher.Renamed += Renamed;
        watcher.Error += (_, _) => NotifyOverflow();
        try { watcher.EnableRaisingEvents = true; }
        catch { watcher.Dispose(); shutdown.Dispose(); throw; }
    }

    private void Changed(object sender, FileSystemEventArgs entry) => Notify(entry.FullPath);
    private void Renamed(object sender, RenamedEventArgs entry) { Notify(entry.OldFullPath); Notify(entry.FullPath); }

    internal void Notify(string fullPath)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (!notifications.Writer.TryWrite(new(fullPath, clock.GetElapsedTime(started))))
            Interlocked.Or(ref fullScan, 2);
    }

    internal void NotifyOverflow() => Interlocked.Or(ref fullScan, 1);

    public async IAsyncEnumerable<WatchBatch> ReadyBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0) throw new InvalidOperationException("Only one watcher consumer is allowed.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(250), clock);
        var pending = new PathDebouncer(pendingCapacity);
        Task<bool> readable = notifications.Reader.WaitToReadAsync(linked.Token).AsTask();
        Task<bool> ticking = tick.WaitForNextTickAsync(linked.Token).AsTask();
        try
        {
            while (true)
            {
                await Task.WhenAny(readable, ticking).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (shutdownToken.IsCancellationRequested) yield break;
                if (readable.IsCompleted)
                {
                    if (!await readable.ConfigureAwait(false)) yield break;
                    // Bound each drain so a hot writer cannot starve the maximum-debounce tick.
                    for (int i = 0; i < 8192 && notifications.Reader.TryRead(out var notification); i++)
                    {
                        if (!notification.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        { Interlocked.Or(ref fullScan, 4); continue; }
                        string relative = notification.FullPath[prefix.Length..].Replace('\\', '/');
                        if (relative.Length == 0 || Scanner.IsInternal(relative)) continue;
                        if (!pending.Add(relative, notification.Time)) Interlocked.Or(ref fullScan, 4);
                    }
                    readable = notifications.Reader.WaitToReadAsync(linked.Token).AsTask();
                }
                int overflow = Interlocked.Exchange(ref fullScan, 0);
                if (overflow != 0)
                {
                    pending.Clear();
                    string reason = (overflow & 1) != 0 ? "watcher-overflow" : (overflow & 2) != 0 ? "queue-overflow" : "pending-overflow";
                    journal?.Write(new("watcher.full_scan", "watch") { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = reason });
                    yield return new([], true, reason);
                }
                if (!ticking.IsCompleted) continue;
                if (!await ticking.ConfigureAwait(false)) yield break;
                ticking = tick.WaitForNextTickAsync(linked.Token).AsTask();
                string[] ready = pending.Ready(clock.GetElapsedTime(started));
                if (ready.Length == 0) continue;
                journal?.Write(new("watcher.batch", "watch") { SessionId = session, Level = DiagnosticLevel.Debug, ItemsChanged = ready.Length });
                yield return new(ready, false);
            }
        }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(readable, ticking).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            Volatile.Write(ref reading, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        watcher.Dispose(); notifications.Writer.TryComplete(); shutdown.Cancel(); shutdown.Dispose();
    }

    private sealed record Notification(string FullPath, TimeSpan Time);
}

internal sealed class PathDebouncer(int capacity)
{
    private readonly Dictionary<string, (TimeSpan First, TimeSpan Last)> pending = new(StringComparer.OrdinalIgnoreCase);
    public bool Add(string path, TimeSpan time)
    {
        if (pending.TryGetValue(path, out var old)) pending[path] = (time < old.First ? time : old.First, time > old.Last ? time : old.Last);
        else if (pending.Count < capacity) pending.Add(path, (time, time));
        else return false;
        return true;
    }
    public string[] Ready(TimeSpan now)
    {
        string[] ready = pending.Where(p => now - p.Value.Last >= TimeSpan.FromSeconds(2)
            || now - p.Value.First >= TimeSpan.FromSeconds(30)).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        foreach (string path in ready) pending.Remove(path);
        return ready;
    }
    public void Clear() => pending.Clear();
}
