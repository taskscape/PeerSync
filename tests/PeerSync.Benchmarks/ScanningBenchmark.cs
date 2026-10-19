extern alias native;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Hashing;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Benchmarks;

internal static class ScanningBenchmark
{
    public static async Task<int> RunAsync(string fixture, int count, bool reuseCorpus = false, bool measureReferences = false)
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
        }
        creation.Stop();
        using var keys = FolderKeys.CreateNew();
        string database = reuseCorpus ? "index-" + Guid.NewGuid().ToString("N") + ".db" : "index.db";
        await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, database));
        using var scanner = new Scanner(root, index, keys, new byte[32]);
        var initial = await scanner.ScanAsync();
        // ReadIndexBatch(0) is capped at 1000: use the persisted high-water metadata.
        long sequence = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(index.Meta.Find("_local-sequence")!.Value!);
        var unchanged = await scanner.ScanAsync();
        long finalSequence = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(index.Meta.Find("_local-sequence")!.Value!);
        long rows = index.LocalFiles.Count();
        bool correct = initial.Hashed == count && initial.Problems.Count == 0 && unchanged.Problems.Count == 0
            && unchanged.Hashed == 0 && unchanged.Updated == 0 && unchanged.Unchanged == rows && sequence == finalSequence;
        bool targetMet = correct && unchanged.Elapsed.TotalSeconds < 10;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            target = "Scanner", files = count, rows, reuseCorpus, database, creationSeconds = creation.Elapsed.TotalSeconds,
            initialSeconds = initial.Elapsed.TotalSeconds, initialHashed = initial.Hashed,
            unchangedSeconds = unchanged.Elapsed.TotalSeconds, unchanged.Hashed, unchanged.Updated,
            unchanged.Unchanged, sequencePreserved = sequence == finalSequence, targetSeconds = 10, correct, targetMet,
            scope = "real one-byte files in 1000-file directories, warm local SSD; initial full hashing/signing/indexing then unchanged full walk",
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
        // Preserve the corpus and database for inspection; never delete a caller's directory.
        return targetMet && correct ? 0 : 1;
    }
}
