using System.Text.Json;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Sync;

namespace PeerSync.Benchmarks;

internal sealed partial class ScaleScenario
{
    private async Task DrainLocal(Node node, CancellationToken token)
    {
        node.Engine.RequestScan();
        for (int pass = 0; pass < 100000; pass++)
        {
            token.ThrowIfCancellationRequested(); var result = await node.Engine.RunPassAsync(token).ConfigureAwait(false);
            var state = State(node, result);
            if (Settled(state) && !result.MoreWork && result.Failed == 0) return;
            AdvanceWhenWaiting([result]);
        }
        throw new InvalidDataException("Local signed publication exceeded its bounded pass count.");
    }

    private async Task<int> Converge(FolderKeys keys, bool requireInitialCount, CancellationToken token)
    {
        long[]? previous = null;
        for (int round = 0; round < 100000; round++)
        {
            token.ThrowIfCancellationRequested(); rounds++;
            foreach (var link in links) await link.Peer.SynchronizeAsync(maxPages: 2, cancellationToken: token).ConfigureAwait(false);
            var passes = new List<PullPassResult>();
            foreach (var node in nodes) passes.Add(await node.Engine.RunPassAsync(token).ConfigureAwait(false));
            var states = nodes.Select((node, number) => State(node, passes[number])).ToArray(); lastStates = states;
            long[] high = states.Select(state => state.HighWater).ToArray();
            if (previous is not null && high.SequenceEqual(previous) && states.All(Settled) && passes.All(pass => !pass.MoreWork && pass.Failed == 0))
            {
                bool references = true;
                foreach (var node in nodes)
                    references &= (await new BlockReferences(node.Root, node.Index, keys, node.Id, node.Journal).RefreshAsync(token).ConfigureAwait(false)).AllKnownSourcesCovered;
                if (references) return VerifyNative(requireInitialCount, token);
            }
            previous = high; AdvanceWhenWaiting(passes);
            if (round % 100 == 0) File.AppendAllText(Path.Combine(output, "rounds.jsonl"), JsonSerializer.Serialize(new { phase, round, states }) + Environment.NewLine);
        }
        throw new InvalidDataException("Scale convergence exceeded its bounded round count.");
    }

    private int VerifyNative(bool requireInitialCount, CancellationToken token)
    {
        var baseline = ScaleOracle.Snapshot(nodes[0].Root, token);
        int actualFiles = baseline.Values.Count(value => value != ScaleOracle.DirectoryMarker);
        if (requireInitialCount && actualFiles != files) throw new InvalidDataException($"Initial native corpus has {actualFiles} files instead of {files}.");
        var surviving = baseline.Values.Where(value => value != ScaleOracle.DirectoryMarker).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes.Skip(1))
        {
            var tree = ScaleOracle.Snapshot(node.Root, token);
            if (tree.Count != baseline.Count || tree.Any(pair => !baseline.TryGetValue(pair.Key, out string? value) || value != pair.Value))
                throw new InvalidDataException("Quiescent native trees differ in path spelling, kind or bytes.");
            surviving.UnionWith(tree.Values.Where(value => value != ScaleOracle.DirectoryMarker));
        }
        foreach (var node in nodes) ScaleOracle.AddArchives(node.Root, node.Index, surviving, token);
        ScaleOracle.RequireSurvival(expected, surviving);
        File.WriteAllText(Path.Combine(output, requireInitialCount ? "initial-native.json" : "final-native.json"),
            JsonSerializer.Serialize(new { files = actualFiles, directories = baseline.Count - actualFiles, expectedVersions = expected.Count, survivingVersions = surviving.Count }, Json));
        return actualFiles;
    }

    private static NodeState State(Node node, PullPassResult result) => new(node.Index.ReadIndexHighWater(), node.Index.Needed.Count(),
        node.Index.ReadApplyIntents().Any(), node.Index.ReadPendingCasePreservations().Count != 0, node.Index.HasPendingDirectoryPublications(), result.NextAttemptTicks);
    private static bool Settled(NodeState state) => state.Needed == 0 && !state.Apply && !state.Cases && !state.Publications;
    private void AdvanceWhenWaiting(IEnumerable<PullPassResult> passes)
    {
        var rows = passes.ToArray(); if (rows.Any(pass => pass.MoreWork)) return;
        long now = nodes[0].Clock.GetUtcNow().UtcTicks;
        long? next = rows.Where(pass => pass.NextAttemptTicks > now).Select(pass => pass.NextAttemptTicks).Min();
        if (next is long due) foreach (var node in nodes) node.Clock.Advance(due - now);
    }
}
