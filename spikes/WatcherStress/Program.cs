using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

int count = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 100000;
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
string root = Path.Combine(Path.GetTempPath(), "PeerSync-watcher-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var creates = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
var renames = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
int overflows = 0;
int errors = 0;
int rescanRequired = 0;
try
{
    using var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
        NotifyFilter = NotifyFilters.FileName };
    watcher.Created += (_, e) => creates.TryAdd(e.FullPath, 0);
    watcher.Renamed += (_, e) => renames.TryAdd(e.FullPath, 0);
    watcher.Error += (_, e) =>
    {
        Interlocked.Exchange(ref rescanRequired, 1);
        if (e.GetException() is InternalBufferOverflowException) Interlocked.Increment(ref overflows);
        else Interlocked.Increment(ref errors);
    };
    watcher.EnableRaisingEvents = true;
    var clock = Stopwatch.StartNew();
    Parallel.For(0, count, i =>
    {
        string path = Path.Combine(root, i.ToString(CultureInfo.InvariantCulture));
        using (File.Create(path)) { }
        File.Move(path, path + ".renamed");
    });
    await Task.Delay(TimeSpan.FromSeconds(3));
    watcher.EnableRaisingEvents = false;
    // A rescan must reconstruct the actual tree even if every event is lost.
    var actual = Directory.EnumerateFiles(root).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    bool recovered = actual.Count == count && Enumerable.Range(0, count).All(i => actual.Contains(i.ToString(CultureInfo.InvariantCulture) + ".renamed"));
    clock.Stop();
    Console.WriteLine(JsonSerializer.Serialize(new { operations = count, createdEvents = creates.Count, renamedEvents = renames.Count,
        overflows, errors, rescanRequestedByWatcher = rescanRequired == 1, rescanRecoveredTree = recovered, elapsedSeconds = clock.Elapsed.TotalSeconds }));
    return recovered && errors == 0 ? 0 : 1;
}
finally { Directory.Delete(root, recursive: true); }
