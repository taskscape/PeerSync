using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Sync;
using PeerSync.Net.InMemory;
using PeerSync.Storage.FolderDb;
using Xunit;

namespace PeerSync.Simulation;

public sealed partial class RealEngineProperties
{
    [Fact]
    public async Task ConnectedMaintenanceExpiresArchivesAndBlocksWithoutLosingDelayedPeerVersions()
    {
        string evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("PEERSYNC_SIMULATION_ARTIFACTS") ?? "artifacts/simulation");
        string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-RetentionSimulation-" + Guid.NewGuid().ToString("N"));
        string reportPath = Path.Combine(evidence, "connected-maintenance.json");
        Directory.CreateDirectory(evidence);
        var nodes = new List<MaintenanceNode>(); var peers = new List<PeerFolderConnection>();
        var network = new InMemoryNetwork(); var trace = new List<string>();
        byte[] old = "PeerSync retention simulation original published version"u8.ToArray();
        byte[] current = "PeerSync retention simulation replacement published version"u8.ToArray();
        byte[] secret = SHA256.HashData("PeerSync connected retention simulation test keys v1"u8);
        using var keys = FolderKeys.FromReadWrite(secret); CryptographicOperations.ZeroMemory(secret);
        DateTimeOffset start = DateTimeOffset.UtcNow; string stage = "initialize"; Exception? failure = null;
        int deletedBlocks = 0, retiredArchives = 0, daysAdvanced = 0; var watch = System.Diagnostics.Stopwatch.StartNew();
        output.WriteLine("RETENTION_FIXTURE " + fixture);
        try
        {
            for (int number = 0; number < 3; number++)
            {
                string directory = Path.Combine(fixture, "node-" + number); string root = Path.Combine(directory, "tree"); Directory.CreateDirectory(root);
                byte[] id = new byte[32]; id[0] = checked((byte)(number + 1));
                var index = await FolderIndex.OpenAsync(Path.Combine(directory, "index.db"), Token);
                var clock = new MaintenanceClock(start); var journal = new DiagnosticJournal(Path.Combine(directory, "logs"), "retention-simulation", debugEnabled: true);
                var folder = new TransportFolder(root, index, keys, id, timeProvider: clock, journal: journal);
                nodes.Add(new(root, id, index, folder, clock, journal));
                await folder.RunPassAsync(Token);
            }
            var endpoints = nodes.Select(node => network.Register(node.Device, node.Folder.CreateHandler)).ToArray();
            for (int number = 1; number < 3; number++) peers.Add(await nodes[number].Folder.ConnectAsync(endpoints[number].Connect(nodes[0].Device), Token));
            stage = "initial-transfer"; Write(nodes[0], old); await Scan(nodes[0]);
            for (int number = 1; number < 3; number++) { await peers[number - 1].SynchronizeAsync(cancellationToken: Token); await nodes[number].Folder.RunPassAsync(Token); AssertBytes(nodes[number], old); }
            stage = "delayed-update"; foreach (var node in nodes) node.Clock.Advance(TimeSpan.FromSeconds(1));
            Write(nodes[0], current); await Scan(nodes[0]);
            await peers[0].SynchronizeAsync(cancellationToken: Token); await nodes[1].Folder.RunPassAsync(Token); AssertBytes(nodes[1], current);
            await peers[1].SynchronizeAsync(cancellationToken: Token);
            Assert.Equal(1, nodes[2].Index.Needed.Count()); AssertBytes(nodes[2], old);
            network.ConfigureLink(nodes[2].Device, nodes[0].Device, new(Partitioned: true));
            network.ConfigureLink(nodes[0].Device, nodes[2].Device, new(Partitioned: true));
            Assert.True(nodes[0].Index.Archive.Count() > 0); Assert.True(nodes[1].Index.Archive.Count() > 0);
            string unknown = Path.Combine(nodes[0].Root, ".psp", "blocks", "retention-unindexed-control"); File.WriteAllText(unknown, "preserve unknown bytes");
            var expiredArchivePaths = nodes.Take(2).SelectMany(node => node.Index.Archive.ReadAll().Select(row => Path.Combine(node.Root, row.ArchivePath.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
            string oldBlock = Convert.ToHexString(Blake3.Hasher.Hash(old).AsSpan());
            var oldBlockPaths = nodes.Take(2).Select(node => Path.Combine(node.Root, ".psp", "blocks", oldBlock[..2], oldBlock)).ToArray();
            Assert.All(oldBlockPaths, path => Assert.True(File.Exists(path)));
            RequireAllVersions();
            foreach (var node in nodes) { var result = await Sweep(node); Assert.Equal(0, result.Deleted); }
            stage = "archive-expiry"; foreach (var node in nodes) node.Clock.Advance(TimeSpan.FromDays(32)); daysAdvanced += 32;
            foreach (var node in nodes.Take(2)) retiredArchives += checked((int)node.Index.Archive.Count());
            foreach (var node in nodes) await Sweep(node);
            Assert.Equal(0, nodes[0].Index.Archive.Count()); Assert.Equal(0, nodes[1].Index.Archive.Count());
            Assert.All(expiredArchivePaths, path => Assert.False(File.Exists(path)));
            AssertBytes(nodes[0], current); AssertBytes(nodes[1], current); AssertBytes(nodes[2], old);
            Assert.Equal(1, nodes[2].Index.Needed.Count()); RequireAllVersions();
            stage = "block-eviction"; foreach (var node in nodes) node.Clock.Advance(TimeSpan.FromDays(32)); daysAdvanced += 32;
            foreach (var node in nodes) deletedBlocks += (await Sweep(node)).Deleted;
            Assert.True(deletedBlocks > 0, "No eligible owned block was actually evicted.");
            Assert.Contains(oldBlockPaths, path => !File.Exists(path));
            AssertBytes(nodes[0], current); AssertBytes(nodes[1], current); AssertBytes(nodes[2], old);
            Assert.Equal(1, nodes[2].Index.Needed.Count()); Assert.Equal("preserve unknown bytes", File.ReadAllText(unknown)); RequireAllVersions();
            stage = "delayed-transfer";
            network.ConfigureLink(nodes[2].Device, nodes[0].Device, new()); network.ConfigureLink(nodes[0].Device, nodes[2].Device, new());
            await peers[1].SynchronizeAsync(cancellationToken: Token); await nodes[2].Folder.RunPassAsync(Token);
            foreach (var node in nodes) { AssertBytes(node, current); Assert.Equal(0, node.Index.Needed.Count()); Assert.Empty(node.Index.ReadApplyIntents()); Assert.False(node.Index.HasPendingDirectoryPublications()); await Sweep(node); }
            Assert.True(nodes[2].Index.Archive.Count() > 0); RequireAllVersions();
            var baseline = VisibleTree(nodes[0].Root);
            foreach (var node in nodes) { Assert.Equal(baseline, VisibleTree(node.Root)); Assert.Empty(node.Index.ReadBlockDeletionIntents()); Assert.Empty(node.Index.ReadPendingCasePreservations()); }
            stage = "passed";
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            foreach (var peer in peers) await peer.DisposeAsync();
            foreach (var node in nodes) { await node.Folder.DisposeAsync(); await node.Index.DisposeAsync(); node.Journal.Dispose(); }
            var assembly = typeof(RealEngineProperties).Assembly;
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { status = failure is null ? "passed" : "failed", stage,
                nodes = nodes.Count, retiredArchives, deletedBlocks, daysAdvanced, publishedVersions = 2, scenario = "connected-maintenance-v1",
                seconds = watch.Elapsed.TotalSeconds, fixture = failure is null ? null : fixture,
                error = failure?.GetType().Name, trace,
                replay = "dotnet test --project tests/PeerSync.Simulation -c Release --no-build --filter-method '*ConnectedMaintenance*'",
                sourceInformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                testBinarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))), gateP1Passed = false }, Json));
            if (failure is null) Directory.Delete(fixture, recursive: true);
            else
            {
                string zip = Path.Combine(evidence, "connected-maintenance-failure-" + Guid.NewGuid().ToString("N") + ".zip");
                ZipFile.CreateFromDirectory(fixture, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
                output.WriteLine("RETENTION_FAILURE " + reportPath);
            }
        }

        static void Write(MaintenanceNode node, byte[] bytes)
        { string path = Path.Combine(node.Root, "current.txt"); File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, node.Clock.GetUtcNow().UtcDateTime); }
        static void AssertBytes(MaintenanceNode node, byte[] bytes) => Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(node.Root, "current.txt")));
        async Task Scan(MaintenanceNode node) { node.Folder.RequestScan(); Assert.Equal(0, (await node.Folder.RunPassAsync(Token)).Failed); }
        void RequireAllVersions() => RequireVersions([Convert.ToHexString(SHA256.HashData(old)), Convert.ToHexString(SHA256.HashData(current))], nodes.Select(node => (node.Root, node.Index)));
        async Task<BlockSweepResult> Sweep(MaintenanceNode node)
        {
            var result = await new BlockRetention(node.Root, node.Index, keys, node.Device, clock: node.Clock, journal: node.Journal).SweepAsync(Token);
            trace.Add($"stage={stage} node={nodes.IndexOf(node)} deleted={result.Deleted} waiting={result.WaitingForRecovery} archives={node.Index.Archive.Count()} needed={node.Index.Needed.Count()}");
            Assert.False(result.WaitingForRecovery); return result;
        }
    }

    private sealed record MaintenanceNode(string Root, byte[] Device, FolderIndex Index, TransportFolder Folder, MaintenanceClock Clock, DiagnosticJournal Journal);
    private sealed class MaintenanceClock(DateTimeOffset start) : TimeProvider
    {
        private long elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed;
        public override DateTimeOffset GetUtcNow() => start.AddTicks(elapsed);
        internal void Advance(TimeSpan amount) => elapsed = checked(elapsed + amount.Ticks);
    }
}
