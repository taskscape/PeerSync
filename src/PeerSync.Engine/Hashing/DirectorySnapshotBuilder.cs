using PeerSync.Protocol.Directories;

namespace PeerSync.Engine.Hashing;

// Streaming page construction only. Callers supply authenticated immediate
// children in canonical key order and retain/flush every emitted node.
internal sealed class DirectorySnapshotBuilder(Func<DirectorySnapshotNode, CancellationToken, Task<DirectorySnapshotLink>> write)
{
    private readonly List<DirectorySnapshotLink> records = new(DirectorySnapshotNode.MaximumLinks);
    private readonly List<List<DirectorySnapshotLink>> levels = [];
    private bool completed;
    internal async Task AddAsync(DirectorySnapshotLink record, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(completed, this); ArgumentNullException.ThrowIfNull(record); token.ThrowIfCancellationRequested();
        records.Add(record);
        if (records.Count == DirectorySnapshotNode.MaximumLinks)
        {
            var page = await write(DirectorySnapshotNode.Entries(records), token).ConfigureAwait(false); records.Clear();
            await CarryAsync(page, 0, token).ConfigureAwait(false);
        }
    }
    internal async Task<DirectorySnapshotLink> CompleteAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(completed, this); completed = true; token.ThrowIfCancellationRequested();
        if (records.Count != 0 || levels.Count == 0)
        {
            var page = await write(DirectorySnapshotNode.Entries(records), token).ConfigureAwait(false); records.Clear();
            await CarryAsync(page, 0, token).ConfigureAwait(false);
        }
        for (int level = 0; level < levels.Count; level++)
        {
            var links = levels[level]; if (links.Count == 0) continue;
            if (links.Count == 1 && level == levels.Count - 1) return links[0];
            var page = await write(DirectorySnapshotNode.Branch(links), token).ConfigureAwait(false); links.Clear();
            await CarryAsync(page, level + 1, token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Directory snapshot has no root.");
    }
    private async Task CarryAsync(DirectorySnapshotLink page, int level, CancellationToken token)
    {
        for (; ; level++)
        {
            token.ThrowIfCancellationRequested();
            if (level >= 10) throw new InvalidDataException("Directory snapshot exceeds its bounded page depth.");
            if (level == levels.Count) levels.Add(new(DirectorySnapshotNode.MaximumLinks));
            var links = levels[level]; links.Add(page);
            if (links.Count != DirectorySnapshotNode.MaximumLinks) return;
            page = await write(DirectorySnapshotNode.Branch(links), token).ConfigureAwait(false); links.Clear();
        }
    }
}
