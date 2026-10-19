using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Benchmarks;

internal static class IndexingBenchmark
{
    // These are synthetic storage fixtures, not valid signed sync records.
    // This benchmark measures metadata indexing, excluding file scanning/hashing/signing.
    private static readonly byte[] Hash = new byte[32], FileId = new byte[16];
    private static readonly SignedRecord Envelope = new("public synthetic storage benchmark"u8, new byte[64]);
    private static readonly IReadOnlyDictionary<ulong, ulong> Vector = new Dictionary<ulong, ulong> { [1] = 1 };

    public static async Task<int> RunAsync(string path, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        path = Path.GetFullPath(path);
        if (File.Exists(path)) throw new IOException("Refusing to overwrite an existing benchmark database.");
        var clock = Stopwatch.StartNew();
        int written;
        long actual, lastSequence;
        double insertSeconds;
        await using (var index = await FolderIndex.OpenAsync(path))
        {
            written = await index.LocalFiles.UpsertAsync(Rows(count));
            insertSeconds = clock.Elapsed.TotalSeconds;
            actual = index.LocalFiles.Count();
            lastSequence = BinaryPrimitives.ReadInt64LittleEndian(index.Meta.Find("_local-sequence")!.Value!);
        }
        // Reopen after writer disposal/checkpoint: committed rows must survive it.
        await using (var reopened = await FolderIndex.OpenAsync(path))
            if (reopened.LocalFiles.Count() != actual) throw new InvalidDataException("Row count changed after reopening.");
        clock.Stop();
        bool correct = written == count && actual == count && lastSequence == count;
        bool targetMet = correct && clock.Elapsed.TotalSeconds < 60;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            target = "FolderIndex", fixtureOnly = true, rows = count, written, actual, lastSequence,
            insertSeconds, totalSeconds = clock.Elapsed.TotalSeconds, rowsPerSecond = count / insertSeconds,
            databaseBytes = new FileInfo(path).Length, targetSeconds = 60, correct, targetMet,
            scope = "metadata indexing, 1000-row transactions, warm local SSD; no file hashing or signing",
        }));
        return correct ? 0 : 1;
    }

    private static IEnumerable<LocalFileRow> Rows(int count)
    {
        long ticks = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc).Ticks;
        for (int i = 0; i < count; i++)
            yield return new(string.Create(CultureInfo.InvariantCulture, $"corpus/{i / 1000:D4}/file-{i:D7}.bin"),
                0, 1024, ticks, 0, false, ticks, Hash, Vector, Envelope, FileId);
    }
}
