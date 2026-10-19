extern alias native;
extern alias managed;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using PeerSync.Crypto.Chunking;

if (args is ["--micro"])
{
    BenchmarkRunner.Run<PeerSync.Benchmarks.ChunkingBenchmark>();
    return 0;
}
if (args is ["--index", var databasePath, "--rows", var rowCount])
    return await PeerSync.Benchmarks.IndexingBenchmark.RunAsync(databasePath, int.Parse(rowCount, CultureInfo.InvariantCulture));
if (args.Length < 1)
{
    Console.Error.WriteLine("<file> [--create <bytes>] | --micro | --index <new-database> --rows <count>");
    return 2;
}
string path = Path.GetFullPath(args[0]);
if (args.Length == 3 && args[1] == "--create")
{
    long length = long.Parse(args[2], CultureInfo.InvariantCulture);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
    byte[] data = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, data.Length, FileOptions.SequentialScan);
    for (long written = 0; written < length; written += data.Length)
        await output.WriteAsync(data.AsMemory(0, (int)Math.Min(data.Length, length - written)));
    output.Flush(flushToDisk: true);
}
var info = new FileInfo(path);
foreach (bool useNative in new[] { false, true })
{
    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan | FileOptions.Asynchronous);
    using var managedHasher = managed::Blake3.Hasher.New();
    using var accelerated = native::Blake3.Hasher.New();
    int chunks = 0;
    var clock = Stopwatch.StartNew();
    long total = await ChunkReader.ReadAsync(input, ChunkParameters.ClassFor(info.Length), chunk =>
    {
        if (useNative) { accelerated.Update(chunk); _ = native::Blake3.Hasher.Hash(chunk); }
        else { managedHasher.Update(chunk); _ = managed::Blake3.Hasher.Hash(chunk); }
        chunks++;
    });
    var digest = useNative ? accelerated.Finalize().AsSpan().ToArray() : managedHasher.Finalize().AsSpan().ToArray();
    clock.Stop();
    Console.WriteLine(JsonSerializer.Serialize(new { implementation = useNative ? "Blake3.Native" : "Blake3", bytes = total, chunks,
        seconds = clock.Elapsed.TotalSeconds, gigabytesPerSecond = total / 1e9 / clock.Elapsed.TotalSeconds,
        wholeHash = Convert.ToHexString(digest), targetGigabytesPerSecond = 1.0, targetMet = total / 1e9 / clock.Elapsed.TotalSeconds >= 1.0 }));
}
return 0;

namespace PeerSync.Benchmarks
{
    [MemoryDiagnoser]
    public class ChunkingBenchmark
    {
        private readonly byte[] data = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
        private readonly ChunkParameters parameters = ChunkParameters.ForClass(2);
        [Benchmark] public int NextCut() => FastCdc.NextCut(data, parameters);
    }
}
