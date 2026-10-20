using System.Globalization;
using System.Text.Json;
using CsCheck;
using PeerSync.Core.Paths;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Sync;
using Xunit;

namespace PeerSync.Simulation;

public sealed partial class RealEngineProperties
{
    [Theory]
    [InlineData(1878823561)] // Available old payload without its original parent advertiser.
    [InlineData(1648781653)] // Same-body parent clock growth after native convergence.
    [InlineData(1044729930)] // Multiple causally superseded parent roots.
    [InlineData(1583987140)] // Tombstone metadata needed beneath a staged ancestor.
    public async Task RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads(int seed)
    {
        string evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("PEERSYNC_SIMULATION_ARTIFACTS") ?? "artifacts/simulation");
        evidence = Path.Combine(evidence, "regressions", seed.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(evidence);
        var result = await Scenario.RunAsync(seed, evidence, true, Token);
        File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(result, Json));
    }

    [Fact]
    public async Task GeneratedHierarchicalCasesConvergeAndPreservePublishedNestedPayloads()
    {
        string evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("PEERSYNC_SIMULATION_ARTIFACTS") ?? "artifacts/simulation");
        Directory.CreateDirectory(evidence); int completed = 0;
        int count = int.Parse(Environment.GetEnvironmentVariable("PEERSYNC_SIMULATION_HIERARCHY_CASES") ?? "4", CultureInfo.InvariantCulture);
        if (count is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(count));
        string? direct = Environment.GetEnvironmentVariable("PEERSYNC_SIMULATION_HIERARCHY_CASE");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        async Task Run(int seed)
        {
            var result = await Scenario.RunAsync(seed, evidence, true, Token); completed++;
            File.AppendAllText(Path.Combine(evidence, "hierarchical-cases.jsonl"), JsonSerializer.Serialize(result) + Environment.NewLine);
            output.WriteLine($"hierarchy case={seed} nodes={result.Nodes} operations={result.Operations} versions={result.Versions} rounds={result.Rounds} seconds={result.Seconds:F3}");
        }
        try
        {
            if (direct is not null) await Run(int.Parse(direct, CultureInfo.InvariantCulture));
            else await Gen.Int[0, int.MaxValue].SampleAsync(Run, iter: count, threads: 1, writeLine: output.WriteLine);
            Save("passed");
        }
        catch { Save("failed"); throw; }
        void Save(string status) => File.WriteAllText(Path.Combine(evidence, "hierarchical-summary.json"), JsonSerializer.Serialize(new
        { status, completed, seconds = watch.Elapsed.TotalSeconds, scenario = "real-engine-hierarchical-v1", gateP1Passed = false }, Json));
    }

    private sealed partial class Scenario
    {
        private static readonly string[] FileTargets = ["work/left", "work/right", "topic/nested/shared", "TOPIC/extra", "switch"];
        private static readonly string[] DirectoryTargets = ["work", "topic", "TOPIC", "empty", "switch", "moved"];
        private readonly bool hierarchical;
        private readonly Dictionary<string, byte[]> candidates = new(StringComparer.Ordinal);
        private FolderKeys? publicationKeys;
        private int directoriesCreated, directoryRenames, subtreeDeletes, kindChanges;

        private void InitializeHierarchy(Node node, int number)
        {
            string topic = number % 2 == 0 ? "topic" : "TOPIC";
            TryWrite(node, topic + "/nested/shared", Payload(number, -2));
            Directory.CreateDirectory(Disk(node, "empty-" + number.ToString(CultureInfo.InvariantCulture)));
            directoriesCreated++;
            StampAncestors(node, topic + "/nested"); StampAncestors(node, "empty-" + number.ToString(CultureInfo.InvariantCulture));
        }
        private void RecordPublished(Node node)
        {
            foreach (var row in node.Index.LocalFiles.ReadAll())
            {
                if (row is not { Type: 0, Deleted: false, ContentReady: true, Record: not null }) continue;
                var version = VersionView.Open(row.Record, publicationKeys!, node.Id);
                if (candidates.TryGetValue(Convert.ToHexString(version.FileHash), out var bytes)) published.TryAdd(Hash(bytes), bytes);
            }
        }
        private async Task<bool> ReferencesCovered()
        {
            foreach (var node in nodes)
            {
                var references = new BlockReferences(node.Root, node.Index, publicationKeys!, node.Id);
                var result = await references.RefreshAsync(token);
                for (int attempt = 1; attempt < 4 && !result.AllKnownSourcesCovered; attempt++) result = await references.RefreshAsync(token);
                if (!result.AllKnownSourcesCovered) { trace.Add($"reference-hold node={Array.IndexOf(nodes.ToArray(), node)} state={result}"); return false; }
            }
            return true;
        }
        private void MutateHierarchy(Node node, int selected, int operation)
        {
            var visible = VisibleTree(node.Root); string[] files = visible.Where(pair => pair.Value != DirectoryMarker).Select(pair => pair.Key).ToArray();
            string[] directories = visible.Where(pair => pair.Value == DirectoryMarker).Select(pair => pair.Key).ToArray();
            string fileTarget = FileTargets[random.Next(FileTargets.Length)], directoryTarget = DirectoryTargets[random.Next(DirectoryTargets.Length)];
            bool applied = false; string description;
            switch (random.Next(7))
            {
                case 0:
                    description = "write=" + fileTarget; applied = TryWrite(node, fileTarget, Payload(selected, operation)); break;
                case 1:
                    description = "mkdir=" + directoryTarget;
                    if (!File.Exists(Disk(node, directoryTarget)) && EnsureParent(node, directoryTarget))
                    { bool existed = Directory.Exists(Disk(node, directoryTarget)); Directory.CreateDirectory(Disk(node, directoryTarget)); if (!existed) directoriesCreated++; StampAncestors(node, directoryTarget); applied = true; }
                    break;
                case 2:
                    string? file = files.Length == 0 ? null : files[random.Next(files.Length)]; description = "delete=" + file;
                    if (file is not null) { File.Delete(Disk(node, file)); StampAncestors(node, Parent(file)); deletes++; applied = true; }
                    break;
                case 3:
                    string? from = files.Length == 0 ? null : files[random.Next(files.Length)]; description = $"rename={from} to={fileTarget}";
                    if (from is not null && !PathRules.Key(from).Equals(PathRules.Key(fileTarget), StringComparison.Ordinal)
                        && !File.Exists(Disk(node, fileTarget)) && !Directory.Exists(Disk(node, fileTarget)) && EnsureParent(node, fileTarget))
                    { File.Move(Disk(node, from), Disk(node, fileTarget)); StampAncestors(node, Parent(from)); StampAncestors(node, Parent(fileTarget)); renames++; applied = true; }
                    break;
                case 4:
                    string? oldDirectory = directories.Length == 0 ? null : directories[random.Next(directories.Length)];
                    description = $"rename-directory={oldDirectory} to={directoryTarget}";
                    if (oldDirectory is not null && !PathRules.Key(directoryTarget).StartsWith(PathRules.Key(oldDirectory) + '/', StringComparison.Ordinal)
                        && !PathRules.Key(oldDirectory).Equals(PathRules.Key(directoryTarget), StringComparison.Ordinal)
                        && !File.Exists(Disk(node, directoryTarget)) && !Directory.Exists(Disk(node, directoryTarget)))
                    { Directory.Move(Disk(node, oldDirectory), Disk(node, directoryTarget)); StampAncestors(node, Parent(oldDirectory)); StampAncestors(node, directoryTarget); renames++; directoryRenames++; applied = true; }
                    break;
                case 5:
                    string? removed = directories.Length == 0 ? null : directories[random.Next(directories.Length)]; description = "delete-subtree=" + removed;
                    if (removed is not null) { Directory.Delete(Disk(node, removed), recursive: true); StampAncestors(node, Parent(removed)); deletes++; subtreeDeletes++; applied = true; }
                    break;
                default:
                    description = "replace-kind=" + directoryTarget;
                    if (Directory.Exists(Disk(node, directoryTarget)))
                    { Directory.Delete(Disk(node, directoryTarget), recursive: true); deletes++; kindChanges++; applied = TryWrite(node, directoryTarget, Payload(selected, operation)); }
                    else if (File.Exists(Disk(node, directoryTarget)))
                    { File.Delete(Disk(node, directoryTarget)); deletes++; kindChanges++; directoriesCreated++; Directory.CreateDirectory(Disk(node, directoryTarget)); applied = TryWrite(node, directoryTarget + "/new", Payload(selected, operation)); }
                    break;
            }
            trace.Add($"{operation}: node={selected} {description} applied={applied}");
        }
        private bool TryWrite(Node node, string relative, byte[] bytes)
        {
            if (Directory.Exists(Disk(node, relative)) || !EnsureParent(node, relative)) return false;
            Write(node, relative, bytes); StampAncestors(node, Parent(relative)); return true;
        }
        private static bool EnsureParent(Node node, string relative)
        {
            string parent = Parent(relative); if (parent.Length == 0) return true;
            string cursor = "";
            foreach (string part in parent.Split('/'))
            {
                cursor = cursor.Length == 0 ? part : cursor + '/' + part;
                if (File.Exists(Disk(node, cursor))) return false;
            }
            Directory.CreateDirectory(Disk(node, parent)); return true;
        }
        private static void StampAncestors(Node node, string relative)
        {
            while (relative.Length != 0)
            { string path = Disk(node, relative); if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, node.Clock.GetUtcNow().UtcDateTime); relative = Parent(relative); }
        }
        private static string Parent(string path) { int separator = path.LastIndexOf('/'); return separator < 0 ? "" : path[..separator]; }
        private static string Disk(Node node, string relative)
        {
            if (!PathRules.IsValid(relative)) throw new InvalidDataException("Generated mutation path is invalid.");
            string path = Path.GetFullPath(Path.Combine(node.Root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(node.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Generated mutation escaped its native root.");
            return path;
        }
    }
}
