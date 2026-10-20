using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsCheck;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Net.InMemory;
using PeerSync.Storage.FolderDb;
using Xunit;

namespace PeerSync.Simulation;

public sealed partial class RealEngineProperties(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [Fact]
    public async Task GeneratedFlatPathCasesConvergeAndPreserveEveryPublishedPayload()
    {
        string evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("PEERSYNC_SIMULATION_ARTIFACTS") ?? "artifacts/simulation");
        Directory.CreateDirectory(evidence); var watch = Stopwatch.StartNew(); int completed = 0;
        string? direct = Environment.GetEnvironmentVariable("PEERSYNC_SIMULATION_CASE");
        int count = int.Parse(Environment.GetEnvironmentVariable("PEERSYNC_SIMULATION_CASES") ?? "8", CultureInfo.InvariantCulture);
        if (count is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(count));
        async Task Run(int seed)
        {
            var result = await Scenario.RunAsync(seed, evidence, Token); completed++;
            File.AppendAllText(Path.Combine(evidence, "cases.jsonl"), JsonSerializer.Serialize(result) + Environment.NewLine);
            output.WriteLine($"case={seed} nodes={result.Nodes} operations={result.Operations} writes={result.Writes} renames={result.Renames} deletes={result.Deletes} versions={result.Versions} rounds={result.Rounds} stagingFiles={result.StagingFiles} seconds={result.Seconds:F3}");
        }
        try
        {
            if (direct is not null) await Run(int.Parse(direct, CultureInfo.InvariantCulture));
            else await Gen.Int[0, int.MaxValue].SampleAsync(Run, iter: count, threads: 1, writeLine: output.WriteLine);
            File.WriteAllText(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(new
            { status = "passed", completed, seconds = watch.Elapsed.TotalSeconds, scenario = "real-engine-flat-path-v1", gateP1Passed = false }, Json));
        }
        catch
        {
            File.WriteAllText(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(new
            { status = "failed", completed, seconds = watch.Elapsed.TotalSeconds, scenario = "real-engine-flat-path-v1", gateP1Passed = false }, Json));
            throw;
        }
    }

    [Fact]
    public async Task Regression1146310622ParkedInternalStagingDoesNotPreventUserTreeConvergence()
    {
        string evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("PEERSYNC_SIMULATION_ARTIFACTS") ?? "artifacts/simulation");
        Directory.CreateDirectory(evidence); await Scenario.RunAsync(1146310622, evidence, Token);
    }

    [Fact]
    public async Task LossOracleRejectsPrivateCacheAloneAndAcceptsReconstructedArchive()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-LossOracle-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "tree"); Directory.CreateDirectory(root); using var keys = FolderKeys.CreateNew();
        byte[] bytes = Encoding.UTF8.GetBytes("published original survives only when a user version is accounted");
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            await using var index = await FolderIndex.OpenAsync(Path.Combine(fixture, "index.db"), Token);
            await using var engine = new TransportFolder(root, index, keys, new byte[32]);
            File.WriteAllBytes(Path.Combine(root, "file"), bytes); await engine.RunPassAsync(Token);
            File.Delete(Path.Combine(root, "file"));
            Assert.Contains(Directory.EnumerateFiles(Path.Combine(root, ".psp", "blocks"), "*", SearchOption.AllDirectories),
                path => File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes));
            Assert.Throws<InvalidDataException>(() => RequireVersions([hash], [(root, index)]));
            engine.RequestScan(); await engine.RunPassAsync(Token);
            RequireVersions([hash], [(root, index)]);
        }
        finally { Directory.Delete(fixture, true); }
    }

    private static void RequireVersions(IEnumerable<string> expected, IEnumerable<(string Root, FolderIndex Index)> sources)
    {
        var surviving = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, index) in sources)
        {
            foreach (var entry in VisibleTree(root))
                if (entry.Value != DirectoryMarker) surviving.Add(entry.Value);
            foreach (var archive in index.Archive.ReadAll())
            {
                string? hash = ReadGuardedHash(root, archive.ArchivePath, privatePath: true);
                if (hash is not null) surviving.Add(hash);
            }
        }
        string[] lost = expected.Where(hash => !surviving.Contains(hash)).ToArray();
        if (lost.Length != 0) throw new InvalidDataException("Published versions missing from current/archive/conflict bytes: " + string.Join(',', lost));
    }

    private sealed record CaseResult(int Seed, int Nodes, int Operations, int Writes, int Renames, int Deletes, int Versions, int Rounds, int StagingFiles, double Seconds);

    private sealed class Scenario
    {
        private readonly int seed;
        private readonly string evidence;
        private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-Simulation-" + Guid.NewGuid().ToString("N"));
        private readonly Random random;
        private readonly InMemoryNetwork network = new();
        private readonly List<Node> nodes = [];
        private readonly List<Connection> connections = [];
        private readonly Dictionary<string, byte[]> published = new(StringComparer.Ordinal);
        private readonly List<string> trace = [];
        private readonly CancellationToken token;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private int operations;
        private int writes, renames, deletes;

        private Scenario(int seed, string evidence, CancellationToken token)
        { this.seed = seed; this.evidence = evidence; this.token = token; random = new(seed); }

        public static async Task<CaseResult> RunAsync(int seed, string evidence, CancellationToken token)
        {
            var scenario = new Scenario(seed, evidence, token); Exception? failure = null;
            using var keys = FolderKeys.CreateNew();
            try { return await scenario.ExecuteAsync(keys); }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                foreach (var connection in scenario.connections) await connection.Peer.DisposeAsync();
                foreach (var node in scenario.nodes) { await node.Engine.DisposeAsync(); await node.Index.DisposeAsync(); node.Journal.Dispose(); }
                if (failure is null) Directory.Delete(scenario.fixture, true);
                else scenario.SaveFailure(failure);
            }
        }

        private async Task<CaseResult> ExecuteAsync(FolderKeys keys)
        {
            int nodeCount = random.Next(3, 8); operations = random.Next(8, 21);
            for (int number = 0; number < nodeCount; number++)
            {
                string directory = Path.Combine(fixture, "node-" + number.ToString(CultureInfo.InvariantCulture)); string root = Path.Combine(directory, "tree"); Directory.CreateDirectory(root);
                byte[] id = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(id, (ulong)number + 1);
                var index = await FolderIndex.OpenAsync(Path.Combine(directory, "index.db"), token); var time = new NodeClock();
                var journal = new DiagnosticJournal(Path.Combine(directory, "logs"), "simulation");
                var engine = new TransportFolder(root, index, keys, id, timeProvider: time, journal: journal);
                var node = new Node(root, id, index, engine, time, journal); nodes.Add(node);
                Write(node, "shared", Payload(number, -1)); await Scan(node);
            }
            var endpoints = nodes.Select(node => network.Register(node.Id, node.Engine.CreateHandler)).ToArray();
            for (int from = 0; from < nodes.Count; from++)
            for (int to = 0; to < nodes.Count; to++)
                if (from != to) connections.Add(new(from, to, await nodes[from].Engine.ConnectAsync(endpoints[from].Connect(nodes[to].Id), token)));

            for (int operation = 0; operation < operations; operation++)
            {
                token.ThrowIfCancellationRequested(); AdvanceAll(TimeSpan.FromSeconds(1));
                int selected = random.Next(nodes.Count); var node = nodes[selected]; string name = "file-" + random.Next(4).ToString(CultureInfo.InvariantCulture);
                string path = Path.Combine(node.Root, name);
                switch (random.Next(3))
                {
                    case 0:
                        trace.Add($"{operation}: node={selected} write={name}"); Write(node, name, Payload(selected, operation)); break;
                    case 1:
                        string destination = "file-" + random.Next(4).ToString(CultureInfo.InvariantCulture);
                        trace.Add($"{operation}: node={selected} rename={name} to={destination}");
                        if (name != destination && File.Exists(path) && !File.Exists(Path.Combine(node.Root, destination)))
                        { File.Move(path, Path.Combine(node.Root, destination)); renames++; }
                        break;
                    default:
                        trace.Add($"{operation}: node={selected} delete={name}"); if (File.Exists(path)) { File.Delete(path); deletes++; } break;
                }
                await Scan(node);
                var link = connections[random.Next(connections.Count)]; int condition = random.Next(4);
                network.ConfigureLink(nodes[link.From].Id, nodes[link.To].Id, condition switch
                { 0 => new(Partitioned: true), 1 => new(DropEvery: 3), 2 => new(TimeSpan.FromMilliseconds(1)), _ => new() });
                trace.Add($"{operation}: link={link.From}->{link.To} condition={condition}");
                // Some peers exchange while others remain offline, creating
                // concurrent vectors rather than globally serializing all edits.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var connection = connections[random.Next(connections.Count)];
                    try { await connection.Peer.SynchronizeAsync(cancellationToken: token); }
                    catch (IOException error) when (error is not PeerClockException) { trace.Add($"{operation}: delivery-failed={connection.From}->{connection.To}"); }
                    await nodes[connection.From].Engine.RunPassAsync(token);
                }
            }
            foreach (var link in connections) network.ConfigureLink(nodes[link.From].Id, nodes[link.To].Id, new());
            long[]? previous = null;
            for (int round = 1; round <= 24; round++)
            {
                AdvanceAll(TimeSpan.FromSeconds(601));
                foreach (var connection in connections) await connection.Peer.SynchronizeAsync(cancellationToken: token);
                foreach (var node in nodes)
                {
                    var result = await node.Engine.RunPassAsync(token);
                    if (result.Failed != 0) throw new InvalidDataException($"Healed pass failed: round={round} failed={result.Failed}");
                }
                var trees = nodes.Select(node => Visible(node.Root)).ToArray(); long[] high = nodes.Select(node => node.Index.ReadIndexHighWater()).ToArray();
                bool same = trees.Skip(1).All(tree => tree.Count == trees[0].Count && tree.All(entry => trees[0].TryGetValue(entry.Key, out string? hash) && hash == entry.Value));
                trace.Add($"heal={round} same={same} high={string.Join(',', high)} needed={string.Join(',', nodes.Select(node => node.Index.Needed.Count()))}");
                if (same && previous is not null && high.SequenceEqual(previous) && nodes.All(node => node.Index.Needed.Count() == 0 && !node.Index.ReadApplyIntents().Any()))
                {
                    RequireVersions(published.Keys, nodes.Select(node => (node.Root, node.Index)));
                    int staging = nodes.Sum(node => Directory.EnumerateFiles(node.Root, "*.!psp").Count());
                    return new(seed, nodes.Count, operations, writes, renames, deletes, published.Count, round, staging, watch.Elapsed.TotalSeconds);
                }
                previous = high;
            }
            throw new InvalidDataException("Visible trees or pending work did not converge after healing.");
        }

        private void Write(Node node, string relative, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(node.Root, relative), bytes); File.SetLastWriteTimeUtc(Path.Combine(node.Root, relative), node.Clock.GetUtcNow().UtcDateTime);
            published.TryAdd(Hash(bytes), bytes);
            writes++;
        }
        private byte[] Payload(int node, int operation) => Encoding.UTF8.GetBytes($"case={seed};node={node};operation={operation};value={random.NextInt64()}");
        private async Task Scan(Node node) { node.Engine.RequestScan(); var result = await node.Engine.RunPassAsync(token); if (result.Failed != 0) trace.Add($"scan-pull-failed={result.Failed}"); }
        private void AdvanceAll(TimeSpan duration) { foreach (var node in nodes) node.Clock.Advance(duration); }
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static SortedDictionary<string, string> Visible(string root)
            => VisibleTree(root);
        private void SaveFailure(Exception error)
        {
            string failure = Path.Combine(evidence, "failures", seed.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(failure);
            File.WriteAllText(Path.Combine(failure, "replay.json"), JsonSerializer.Serialize(new
            { seed, scenario = "real-engine-flat-path-v1", error = error.ToString(), trace, published = published.Keys, retainedFixture = fixture,
                replay = $"$env:PEERSYNC_SIMULATION_CASE='{seed}'; dotnet test --project tests/PeerSync.Simulation -c Release --no-build" }, Json));
            try { ZipFile.CreateFromDirectory(fixture, Path.Combine(failure, "generated-fixture.zip")); }
            catch (Exception packing) when (packing is IOException or UnauthorizedAccessException)
            { File.WriteAllText(Path.Combine(failure, "packing-error.txt"), packing.ToString()); }
            // The fixture contains only generated data. Preserve native logs,
            // indexes and user/archive bytes for investigation; never clean a failure.
            File.WriteAllText(Path.Combine(evidence, "latest-failure.txt"), Path.Combine(failure, "replay.json"));
        }
        private sealed record Connection(int From, int To, PeerFolderConnection Peer);
        private sealed record Node(string Root, byte[] Id, FolderIndex Index, TransportFolder Engine, NodeClock Clock, DiagnosticJournal Journal);
        private sealed class NodeClock : TimeProvider
        {
            private long utc = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc).Ticks;
            private long elapsed;
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => elapsed;
            public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
            public void Advance(TimeSpan amount) { utc += amount.Ticks; elapsed += amount.Ticks; }
        }
    }
}
