using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PeerSync.Crypto.Chunking;

if (args is not ["--seconds", var duration])
{
    Console.Error.WriteLine("--seconds <duration>: P0 FastCDC boundary fuzz smoke. Protocol/path/key targets arrive in later phases.");
    return 2;
}
int seconds = int.Parse(duration, CultureInfo.InvariantCulture);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
var clock = Stopwatch.StartNew();
int seed = 0;
int[] readSizes = [4093, 65536, 1000000];
while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
{
    var random = new Random(seed);
    byte[] corpus = new byte[random.Next(1, 1024 * 1024)];
    random.NextBytes(corpus);
    try
    {
        List<int>? baseline = null;
        foreach (int readSize in readSizes)
        {
            var boundaries = new List<int>();
            await using var stream = new MemoryStream(corpus, writable: false);
            int copied = 0;
            long total = await ChunkReader.ReadAsync(stream, 0, chunk =>
            {
                if (!chunk.SequenceEqual(corpus.AsSpan(copied, chunk.Length))) throw new InvalidDataException("Chunk contents changed.");
                copied += chunk.Length;
                boundaries.Add(copied);
            }, readSize);
            if (total != corpus.Length || (baseline is not null && !baseline.SequenceEqual(boundaries))) throw new InvalidDataException("Boundary mismatch.");
            baseline = boundaries;
        }
    }
    catch
    {
        Directory.CreateDirectory("artifacts/fuzz/crashes");
        await File.WriteAllBytesAsync($"artifacts/fuzz/crashes/fastcdc-{seed}.bin", corpus);
        throw;
    }
    seed++;
}
Console.WriteLine(JsonSerializer.Serialize(new { target = "FastCDC", seeds = seed, seconds = clock.Elapsed.TotalSeconds, crashes = 0 }));
return 0;
