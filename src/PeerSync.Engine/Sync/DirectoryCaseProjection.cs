using System.ComponentModel;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Protocol.Directories;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

internal sealed record DirectoryCaseMember(VersionView Original, string Destination)
{
    internal required DirectorySnapshotLink RecordLink { get; init; }
    public override string ToString() => "Projected directory member (redacted)";
}
internal sealed record DirectoryCasePage(VersionView OriginalRoot, string DestinationRoot, IReadOnlyList<DirectoryCaseMember> Members, string? NextKey, bool More)
{ public override string ToString() => "Projected directory page (redacted)"; }

// Caller retains FolderOperationGate. Authenticated metadata projection only;
// neither the destination nor cursor proves native ownership or completion.
internal sealed class DirectoryCaseProjection(string rootPath, FolderIndex index, FolderKeys keys, byte[] peer, DiagnosticJournal? journal = null)
{
    private readonly byte[] sourceHint = peer is { Length: 32 } ? peer.ToArray() : throw new ArgumentException("Invalid projection source.", nameof(peer));
    internal async Task<DirectoryCasePage> ReadUnderGateAsync(SignedRecord directory,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch,
        string? afterKey = null, int collision = 1, int limit = 1000, int byteBudget = 8 * 1024 * 1024, CancellationToken token = default)
    {
        if (limit is < 1 or > 1000 || byteBudget is < 1 or > 8 * 1024 * 1024 || collision is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        ArgumentNullException.ThrowIfNull(fetch); token.ThrowIfCancellationRequested(); Guid session = Guid.NewGuid(); string stage = "source";
        try
        {
            var source = VersionView.Open(directory, keys, sourceHint); var snapshot = DirectorySnapshotBlocks.SnapshotOf(source);
            byte[] id = CaseConflictPolicy.SourceId(source); string rootKey = PathRules.Key(source.Path);
            string? cursor = afterKey is null ? null : PathRules.Key(afterKey);
            if (cursor is not null && !cursor.StartsWith(rootKey + '/', StringComparison.Ordinal)) throw new ArgumentException("Projection cursor is outside its source.", nameof(afterKey));
            string destination = ConflictNames.CaseSourcePath(source.Path, id, collision); _ = PathRules.Key(destination);
            stage = "graph"; var state = index.ReadDirectoryGraph(id);
            if (state is null || !state.Complete || state.Retiring) throw new InvalidOperationException("Directory projection requires a complete non-retiring graph.");
            using var tree = new SafeFileTree(rootPath);
            if (!tree.Identity.SequenceEqual(state.RootId) || index.Meta.Find("_root-identity")?.Value is not { Length: 24 } bound || !tree.Identity.SequenceEqual(bound)
                || state.DirectoryPath != source.Path || state.Length != snapshot.EncodedLength || !state.Hash.AsSpan().SequenceEqual(snapshot.Hash))
                throw new InvalidDataException("Directory projection graph/source/root binding changed.");
            await index.BeginDirectoryGraphAsync(id, state.RootId, source.Path, snapshot.Hash, snapshot.EncodedLength, token).ConfigureAwait(false);
            var members = index.ReadDirectoryGraphMemberPage(id, cursor, limit); var output = new List<DirectoryCaseMember>(); long bytes = 0;
            bool more = members.Count == limit; int rootSegments = source.Path.Count(value => value == '/') + 1;
            using var blocks = new DirectorySnapshotBlocks(rootPath, index, keys, sourceHint, journal);
            try
            {
                foreach (var member in members)
                {
                    token.ThrowIfCancellationRequested(); stage = "member-header";
                    if (!member.PathKey.StartsWith(rootKey + '/', StringComparison.Ordinal)) throw new InvalidDataException("Directory projection member escaped its source.");
                    var link = new DirectorySnapshotLink(member.RecordHash, member.RecordLength);
                    var node = await blocks.ReadNodeAsync(link, fetch, token).ConfigureAwait(false);
                    if (node.Kind != DirectorySnapshotNodeKind.Record) throw new InvalidDataException("Directory projection member has another node kind.");
                    if (output.Count != 0 && node.PayloadLength > byteBudget - bytes) { more = true; break; }
                    stage = "member-envelope"; var original = await blocks.ReadMemberAsync(ApplyState.Parent(member.PathKey), link, fetch, token).ConfigureAwait(false);
                    if (PathRules.Key(original.Path) != member.PathKey) throw new InvalidDataException("Directory projection member key changed.");
                    string relative = string.Join('/', original.Path.Split('/').Skip(rootSegments)); string target = destination + '/' + relative;
                    stage = "destination";
                    if (!PathRules.IsValid(target)) throw new InvalidDataException("Directory projection destination cannot be represented.");
                    output.Add(new(original, target) { RecordLink = link }); bytes = checked(bytes + node.PayloadLength); cursor = member.PathKey;
                }
                stage = "flush"; await blocks.FlushAsync(token).ConfigureAwait(false);
            }
            finally { await blocks.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested();
            journal?.TryWrite(new("directory.case_projection", "directory-projection") { SessionId = session, Level = DiagnosticLevel.Debug,
                ItemsVisited = output.Count, Bytes = bytes });
            return new(source, destination, output.AsReadOnly(), cursor, more);
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("directory.case_projection_failed", "directory-projection") { SessionId = session, Level = DiagnosticLevel.Warning,
                ErrorCode = stage + "." + (error is OperationCanceledException ? "cancelled" : error.GetType().Name), HResult = error.HResult,
                NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null } });
            throw;
        }
    }
}
