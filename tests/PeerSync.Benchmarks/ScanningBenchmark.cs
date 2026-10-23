extern alias native;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Hashing;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Benchmarks;

internal static class ScanningBenchmark
{
    public static async Task<int> RunAsync(string fixture, int count, bool reuseCorpus = false,
        bool measureReferences = false, bool syncFreshPeer = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        fixture = Path.GetFullPath(fixture);
        if (!reuseCorpus && (Directory.Exists(fixture) || File.Exists(fixture))) throw new IOException("Benchmark requires a new directory.");
        Directory.CreateDirectory(fixture);
        string root = Path.Combine(fixture, "tree");
        Directory.CreateDirectory(root);
        var creation = Stopwatch.StartNew();
        for (int i = 0; !reuseCorpus && i < count; i++)
        {
            string directory = Path.Combine(root, (i / 1000).ToString("D4", CultureInfo.InvariantCulture));
            if (i % 1000 == 0) Directory.CreateDirectory(directory);
            using var file = new FileStream(Path.Combine(directory, i.ToString("D7", CultureInfo.InvariantCulture)),
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // Small nonempty files exercise real hashing and chunk indexing in the initial scan.
            file.WriteByte((byte)(i % 251));
            if (count == 1_000_000 && i > 0 && i % 100_000 == 0)
                Console.WriteLine(JsonSerializer.Serialize(new
                { stage = "generate", files = i, seconds = creation.Elapsed.TotalSeconds }));
        }
        creation.Stop();
        using var keys = FolderKeys.CreateNew();
        string database = reuseCorpus ? "index-" + Guid.NewGuid().ToString("N") + ".db" : "index.db";
        var firstIndex = Stopwatch.StartNew();
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, database));
        string? diagnosticPath = count == 1_000_000
            ? Path.Combine(fixture, "logs-" + Guid.NewGuid().ToString("N")) : null;
        using var journal = diagnosticPath is null ? null : new DiagnosticJournal(diagnosticPath, "scale", debugEnabled: true);
        using var scanner = new Scanner(root, index, keys, new byte[32], journal: journal);
        long hashedProgress = 0;
        string scanStage = "first-index";
        var stageElapsed = Stopwatch.StartNew();
        if (count == 1_000_000)
        {
            scanner.Checkpoint = checkpoint =>
            {
                if (checkpoint is not ("after-enumerate" or "after-hash" or "before-scan-commit" or "after-upsert")) return;
                using Process current = Process.GetCurrentProcess();
                current.Refresh();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    target = "P5 scanner-stage-memory", scanStage, checkpoint,
                    seconds = stageElapsed.Elapsed.TotalSeconds,
                    workingSetBytes = current.WorkingSet64,
                    privateBytes = current.PrivateMemorySize64,
                    peakWorkingSetBytes = current.PeakWorkingSet64,
                    managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                    gcCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
                    hashed = Interlocked.Read(ref hashedProgress),
                }));
            };
            scanner.BytesHashed += _ =>
            {
                long completed = Interlocked.Increment(ref hashedProgress);
                if (completed % 100_000 == 0)
                    Console.WriteLine(JsonSerializer.Serialize(new
                    { stage = "first-index", hashed = completed, seconds = firstIndex.Elapsed.TotalSeconds }));
            };
        }
        var initial = await scanner.ScanAsync();
        firstIndex.Stop();
        scanStage = "unchanged";
        stageElapsed.Restart();
        // ReadIndexBatch(0) is capped at 1000: use the persisted high-water metadata.
        long sequence = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(index.Meta.Find("_local-sequence")!.Value!);
        var unchanged = await scanner.ScanAsync();
        long finalSequence = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(index.Meta.Find("_local-sequence")!.Value!);
        long rows = index.LocalFiles.Count();
        bool changedPathsBounded = count <= 10_000 || initial.RequiresNeededRebuild && initial.ChangedPaths.Count == 0;
        bool correct = initial.Hashed == count && initial.Problems.Count == 0 && unchanged.Problems.Count == 0
            && unchanged.Hashed == 0 && unchanged.Updated == 0 && unchanged.Unchanged == rows && sequence == finalSequence
            && changedPathsBounded;
        double? idleCpuPercent = null;
        long? idleWorkingSetBytes = null, idlePrivateBytes = null;
        long? peakWorkingSetBytes = null, managedIdleBytes = null;
        long? gcCommittedIdleBytes = null, sqliteMemoryIdleBytes = null;
        if (count == 1_000_000)
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            var idle = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(30));
            idle.Stop();
            process.Refresh();
            idleCpuPercent = (process.TotalProcessorTime - cpuBefore).TotalSeconds /
                idle.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
            idleWorkingSetBytes = process.WorkingSet64;
            idlePrivateBytes = process.PrivateMemorySize64;
            peakWorkingSetBytes = process.PeakWorkingSet64;
            managedIdleBytes = GC.GetTotalMemory(forceFullCollection: false);
            gcCommittedIdleBytes = GC.GetGCMemoryInfo().TotalCommittedBytes;
            sqliteMemoryIdleBytes = SQLitePCL.raw.sqlite3_memory_used();
        }
        const long memoryLimit = 300L * 1024 * 1024;
        bool targetMet = count == 1_000_000
            ? correct && firstIndex.Elapsed.TotalSeconds < 1200 && idleCpuPercent < 1
                && idleWorkingSetBytes < memoryLimit && idlePrivateBytes < memoryLimit
            : correct && unchanged.Elapsed.TotalSeconds < 10;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            target = count == 1_000_000 ? "P5 million-file scanner" : "Scanner",
            files = count, rows, reuseCorpus, database, diagnosticPath,
            creationSeconds = creation.Elapsed.TotalSeconds,
            firstIndexSeconds = firstIndex.Elapsed.TotalSeconds,
            initialSeconds = initial.Elapsed.TotalSeconds, initialHashed = initial.Hashed,
            unchangedSeconds = unchanged.Elapsed.TotalSeconds, unchanged.Hashed, unchanged.Updated,
            unchanged.Unchanged, sequencePreserved = sequence == finalSequence, changedPathsBounded,
            idleCpuPercent, idleWorkingSetBytes, idlePrivateBytes,
            peakWorkingSetBytes, managedIdleBytes, gcCommittedIdleBytes, sqliteMemoryIdleBytes,
            targetSeconds = count == 1_000_000 ? 1200 : 10, correct, targetMet,
            scope = "real one-byte files in 1000-file directories, local SSD; first index includes database open and full hashing/signing/indexing, followed by unchanged walk",
        }));
        if (measureReferences)
        {
            var clock = Stopwatch.StartNew(); var references = await new BlockReferences(root, index, keys, new byte[32]).RefreshAsync(); clock.Stop();
            // This corpus has 251 distinct one-byte contents and no archives or
            // needed rows. Verify actual coverage as well as the deduplicated count.
            var hashes = index.ReadBlockReferences().ToDictionary(r => Convert.ToHexString(r.Hash));
            bool referencesCorrect = references.AllKnownSourcesCovered && references.References == Math.Min(251, count)
                && Enumerable.Range(0, Math.Min(251, count)).All(i => hashes.ContainsKey(Convert.ToHexString(native::Blake3.Hasher.Hash(new[] { (byte)i }).AsSpan())))
                && hashes.Values.All(r => r.Length == 1 && r.Kinds == BlockReferenceKind.Current);
            Console.WriteLine(JsonSerializer.Serialize(new { target = "BlockReferences", files = count, rows, database, references.References, references.Batches,
                seconds = clock.Elapsed.TotalSeconds, correct = referencesCorrect, scope = "authenticated production snapshot over the real scanner index; 251 distinct one-byte values" }));
            correct &= referencesCorrect;
        }
        if (syncFreshPeer && targetMet && correct)
            correct = await MillionPeerBenchmark.SyncAsync(fixture, root, index, keys, count);
        // Preserve the corpus and database for inspection; never delete a caller's directory.
        return targetMet && correct ? 0 : 1;
    }
}
