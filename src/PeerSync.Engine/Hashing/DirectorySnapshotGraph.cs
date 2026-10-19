using System.ComponentModel;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Directories;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal sealed record DirectoryGraphAdvance(byte[] Id, bool Complete, int Processed)
{ public override string ToString() => "Directory graph advancement (redacted)"; }

// The caller retains FolderOperationGate throughout this bounded advancement.
// This validates content-addressed metadata, not native copy ownership.
internal sealed class DirectorySnapshotGraph(string rootPath, FolderIndex index, FolderKeys keys, byte[] peer, DiagnosticJournal? journal = null)
{
    private readonly byte[] sourceHint = peer is { Length: 32 } ? peer.ToArray() : throw new ArgumentException("Invalid directory graph source hint.", nameof(peer));
    internal Action<string>? Checkpoint { get; init; }
    internal async Task<DirectoryGraphAdvance> AdvanceUnderGateAsync(SignedRecord directory,
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, int maximumWork, CancellationToken token)
    {
        if (maximumWork is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumWork));
        var source = VersionView.Open(directory, keys, sourceHint); var root = DirectorySnapshotBlocks.SnapshotOf(source);
        byte[] id = CaseConflictPolicy.SourceId(source); Guid session = Guid.NewGuid(); int processed = 0;
        try
        {
            using var blocks = new DirectorySnapshotBlocks(rootPath, index, keys, sourceHint, journal);
            byte[] physicalRoot = index.Meta.Find("_root-identity")?.Value?.ToArray() ?? throw new IOException("Directory graph root is unbound.");
            var state = await index.BeginDirectoryGraphAsync(id, physicalRoot, source.Path, root.Hash, root.EncodedLength, token).ConfigureAwait(false);
            if (state.Complete) { Log(true); return new(id, true, 0); }
            while (processed < maximumWork)
            {
                token.ThrowIfCancellationRequested(); var work = index.ReadDirectoryGraphWork(id) ?? throw new InvalidDataException("Incomplete directory graph has no pending work.");
                var link = new DirectorySnapshotLink(work.Hash, work.Length); var node = await blocks.ReadNodeAsync(link, fetch, token).ConfigureAwait(false);
                var children = new List<DirectoryGraphChild>(DirectorySnapshotNode.MaximumLinks); DirectoryGraphMember? member = null;
                var references = new List<DirectoryGraphBlock>(1000) { new(work.Hash, work.Length) };
                if (work.Kind == DirectoryGraphWorkKind.Page)
                {
                    if (node.Kind is not (DirectorySnapshotNodeKind.Entries or DirectorySnapshotNodeKind.Branch)) throw new InvalidDataException("Directory graph page has an invalid kind.");
                    bool branch = node.Kind == DirectorySnapshotNodeKind.Branch;
                    if (branch && work.PageDepth >= 9) throw new InvalidDataException("Directory graph page depth exceeds its bound.");
                    foreach (var child in node.Links)
                        children.Add(new(work.ParentPath, child.Hash, child.EncodedLength, branch ? DirectoryGraphWorkKind.Page : DirectoryGraphWorkKind.Record, work.PageDepth + (branch ? 1 : 0)));
                }
                else
                {
                    if (node.Kind != DirectorySnapshotNodeKind.Record) throw new InvalidDataException("Directory graph member has an invalid kind.");
                    var version = await blocks.ReadMemberAsync(work.ParentPath, link, fetch, token).ConfigureAwait(false);
                    member = new(PathRules.Key(version.Path), work.Hash, work.Length);
                    references.AddRange(node.Links.Select(fragment => new DirectoryGraphBlock(fragment.Hash, fragment.EncodedLength)));
                    if (version.Type == 1)
                    {
                        var nested = DirectorySnapshotBlocks.SnapshotOf(version);
                        children.Add(new(version.Path, nested.Hash, nested.EncodedLength, DirectoryGraphWorkKind.Page, 0));
                    }
                    else
                        foreach (var chunk in version.ExportBody().Chunks)
                        {
                            token.ThrowIfCancellationRequested(); references.Add(new(chunk.Hash.ToByteArray(), checked((int)chunk.Length)));
                            if (references.Count == 1000) await FlushReferencesAsync().ConfigureAwait(false);
                        }
                }
                await FlushReferencesAsync().ConfigureAwait(false);
                await blocks.FlushAsync(token).ConfigureAwait(false); Checkpoint?.Invoke("before-progress");
                bool complete = await index.CompleteDirectoryGraphWorkAsync(id, work.Id, children, member, token).ConfigureAwait(false);
                processed++; Checkpoint?.Invoke("progress");
                if (complete) { Log(true); return new(id, true, processed); }

                async Task FlushReferencesAsync()
                {
                    if (references.Count == 0) return;
                    await index.AppendDirectoryGraphReferencesAsync(id, references, token).ConfigureAwait(false);
                    references.Clear(); Checkpoint?.Invoke("references");
                }
            }
            Log(false); return new(id, false, processed);
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("directory.graph_failed", "directory-graph") { SessionId = session, Level = DiagnosticLevel.Warning,
                ItemsVisited = processed, ErrorCode = error is OperationCanceledException ? "cancelled" : error.GetType().Name,
                HResult = error.HResult, NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null }, RetryInMilliseconds = 10000 });
            throw;
        }
        void Log(bool complete) => journal?.TryWrite(new(complete ? "directory.graph_complete" : "directory.graph_pending", "directory-graph")
        { SessionId = session, Level = DiagnosticLevel.Debug, ItemsVisited = processed, RetryInMilliseconds = complete ? null : 10000 });
    }
}
