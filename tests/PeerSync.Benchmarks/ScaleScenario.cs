using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Sync;
using PeerSync.Net.InMemory;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Benchmarks;

internal sealed partial class ScaleScenario
{
    private readonly string owner = "PeerSync-Scale-" + Guid.NewGuid().ToString("N");
    private readonly List<Node> nodes = [];
    private readonly List<Link> links = [];
    private readonly InMemoryNetwork network = new();
    private readonly HashSet<string> expected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> timings = [];
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly DateTimeOffset initialUtc = DateTimeOffset.UtcNow;
    private readonly int files;
    private readonly string output, revision, fixture;
    private string phase = "oracle-controls";
    private double phaseStart;
    private int generated, rounds, overwrites, renames, deletes, initialNativeFiles, finalNativeFiles;
    private NodeState[] lastStates = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private ScaleScenario(int files, string output, string revision)
    {
        this.files = files; this.output = Path.GetFullPath(output); this.revision = revision;
        fixture = Path.Combine(Path.GetTempPath(), owner);
    }

    internal static async Task<int> RunAsync(int files, int seconds, string output, string revision, bool oracleOnly = false)
    {
        if ((!oracleOnly && files is < 32 or > 100000) || seconds is < 1 or > 86400) throw new ArgumentOutOfRangeException(nameof(files));
        if (revision.Length != 40 || revision.Any(character => !char.IsAsciiHexDigit(character))) throw new ArgumentException("A full Git source revision is required.", nameof(revision));
        var binaries = new[] { typeof(ScaleScenario).Assembly, typeof(TransportFolder).Assembly, typeof(FolderIndex).Assembly, typeof(FolderKeys).Assembly }
            .Select(assembly => new
            {
                name = assembly.GetName().Name,
                version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
            }).ToArray();
        if (!binaries[0].version.EndsWith("+" + revision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scale executable does not match the requested Git revision. Rebuild before using -NoBuild.");
        var run = new ScaleScenario(files, output, revision);
        if (Directory.Exists(run.output) || File.Exists(run.output)) throw new IOException("Scale evidence directory must be new.");
        Directory.CreateDirectory(run.output); Directory.CreateDirectory(run.fixture);
        File.WriteAllText(Path.Combine(run.fixture, "owner.txt"), run.owner);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var heartbeatStop = new CancellationTokenSource(); var heartbeat = run.Heartbeat(heartbeatStop.Token);
        byte[] testSecret = SHA256.HashData(Encoding.UTF8.GetBytes("PeerSync scale corpus test keys v1:" + run.owner));
        using var keys = FolderKeys.FromReadWrite(testSecret); CryptographicOperations.ZeroMemory(testSecret);
        Exception? failure = null; bool removed = false;
        try
        {
            ScaleOracle.SelfCheck(run.fixture, deadline.Token);
            if (!oracleOnly) await run.ExecuteAsync(keys, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            foreach (var link in run.links)
                try { await link.Peer.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
            foreach (var node in run.nodes)
            {
                try { await node.Engine.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
                try { await node.Index.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
                try { node.Journal.Dispose(); } catch (Exception error) { failure ??= error; }
            }
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            try { await heartbeat.ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
        }
        if (failure is null)
        {
            try { run.RemoveOwnedFixture(); removed = true; } catch (Exception error) { failure = error; }
        }
        run.timings[run.phase] = run.watch.Elapsed.TotalSeconds - run.phaseStart;
        var report = new
        {
            status = failure is null ? "passed" : "failed", mode = oracleOnly ? "native-oracle-controls" : "five-node-corpus-v1",
            sourceRevision = revision, binaries, runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
            gcAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, startedUtc = run.initialUtc,
            requestedFiles = files, generatedFiles = run.generated, nodes = run.nodes.Count, run.initialNativeFiles, run.finalNativeFiles,
            run.overwrites, run.renames, run.deletes, expectedPayloadVersions = run.expected.Count, run.rounds,
            seconds = run.watch.Elapsed.TotalSeconds, phaseSeconds = run.timings, states = run.lastStates,
            fixture = run.fixture, fixtureRemoved = removed, failure = failure?.ToString(),
            corpusRequirementSatisfied = failure is null && !oracleOnly && files == 100000, gateP1Passed = false
        };
        File.WriteAllText(Path.Combine(run.output, "summary.json"), JsonSerializer.Serialize(report, Json));
        Console.WriteLine(JsonSerializer.Serialize(report)); return failure is null ? 0 : 1;
    }

    private async Task ExecuteAsync(FolderKeys keys, CancellationToken token)
    {
        SetPhase("initialize");
        for (int number = 0; number < 5; number++)
        {
            string directory = Path.Combine(fixture, "node-" + number.ToString(CultureInfo.InvariantCulture));
            string root = Path.Combine(directory, "tree"); Directory.CreateDirectory(root);
            var index = await FolderIndex.OpenAsync(Path.Combine(directory, "index.db"), token).ConfigureAwait(false);
            byte[] id = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id, (ulong)number + 1);
            var clock = new ScaleClock(initialUtc); var journal = new DiagnosticJournal(Path.Combine(directory, "logs"), "scale", debugEnabled: true);
            nodes.Add(new(root, id, index, new(root, index, keys, id, timeProvider: clock, journal: journal), clock, journal));
        }
        SetPhase("generate");
        for (int number = 0; number < files; number++) { Write(nodes[0], number, "original"); generated++; token.ThrowIfCancellationRequested(); }
        SetPhase("initial-publication");
        foreach (var node in nodes) await DrainLocal(node, token).ConfigureAwait(false);
        var endpoints = nodes.Select(node => network.Register(node.Id, node.Engine.CreateHandler)).ToArray();
        for (int from = 0; from < nodes.Count; from++)
        for (int to = 0; to < nodes.Count; to++)
            if (from != to) links.Add(new(from, to, await nodes[from].Engine.ConnectAsync(endpoints[from].Connect(nodes[to].Id), token).ConfigureAwait(false)));
        SetPhase("initial-convergence"); initialNativeFiles = await Converge(keys, requireInitialCount: true, token).ConfigureAwait(false);
        SetPhase("partition-mutations");
        foreach (var link in links) network.ConfigureLink(nodes[link.From].Id, nodes[link.To].Id, new(Partitioned: (link.From < 2) != (link.To < 2)));
        int sample = Math.Min(12, files / 4);
        for (int number = 0; number < sample; number++) { Write(nodes[1], number, "left"); Write(nodes[3], number, "right"); overwrites += 2; }
        for (int number = 0; number < 4; number++)
        {
            string source = Disk(nodes[1], files - 1 - number); File.Move(source, source + ".renamed"); renames++;
            File.Delete(Disk(nodes[3], files - 5 - number)); deletes++;
        }
        nodes[1].Engine.RequestScan(); nodes[3].Engine.RequestScan();
        await DrainLocal(nodes[1], token).ConfigureAwait(false); await DrainLocal(nodes[3], token).ConfigureAwait(false);
        foreach (var link in links) network.ConfigureLink(nodes[link.From].Id, nodes[link.To].Id, new());
        SetPhase("healed-convergence"); finalNativeFiles = await Converge(keys, requireInitialCount: false, token).ConfigureAwait(false);
    }

    private void Write(Node node, int number, string version)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"PeerSync scale corpus v1; file={number.ToString(CultureInfo.InvariantCulture)}; version={version}");
        string path = Disk(node, number); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, node.Clock.GetUtcNow().UtcDateTime);
        expected.Add(Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static string Disk(Node node, int number) => Path.Combine(node.Root,
        "bucket-" + (number / 128).ToString("D4", CultureInfo.InvariantCulture), "file-" + number.ToString("D6", CultureInfo.InvariantCulture));
    private void SetPhase(string next)
    {
        double now = watch.Elapsed.TotalSeconds; timings[phase] = now - phaseStart; phaseStart = now; phase = next;
        Console.WriteLine(JsonSerializer.Serialize(new { phase, generated, rounds, seconds = now }));
    }
    private async Task Heartbeat(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                string line = JsonSerializer.Serialize(new { phase = Volatile.Read(ref phase), generated, rounds,
                    states = Volatile.Read(ref lastStates), seconds = watch.Elapsed.TotalSeconds, fixture });
                Console.WriteLine(line); File.AppendAllText(Path.Combine(output, "progress.jsonl"), line + Environment.NewLine);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void RemoveOwnedFixture()
    {
        string parent = Path.GetFullPath(Path.GetTempPath()); string actual = Path.GetFullPath(fixture);
        string relative = Path.GetRelativePath(parent, actual);
        if (relative != owner || Path.GetFileName(actual) != owner || (File.GetAttributes(actual) & FileAttributes.ReparsePoint) != 0
            || File.ReadAllText(Path.Combine(actual, "owner.txt")) != owner) throw new IOException("Generated fixture ownership changed; preserve it.");
        Directory.Delete(actual, recursive: true);
    }
    private sealed record Node(string Root, byte[] Id, FolderIndex Index, TransportFolder Engine, ScaleClock Clock, DiagnosticJournal Journal);
    private sealed record Link(int From, int To, PeerFolderConnection Peer);
    private sealed record NodeState(long HighWater, long Needed, bool Apply, bool Cases, bool Publications, long? NextAttemptTicks);
    private sealed class ScaleClock(DateTimeOffset start) : TimeProvider
    {
        private long elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed;
        public override DateTimeOffset GetUtcNow() => start.AddTicks(elapsed);
        internal void Advance(long ticks) => elapsed = checked(elapsed + ticks);
    }
}
