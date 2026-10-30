using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Directories;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal sealed record DirectoryMetadataPass(int Visited, int Completed, int Deferred, long? NextTicks);

internal static class DirectoryMetadataSources
{
    internal static DirectoryMetadataSource? FromVersion(VersionView version)
    {
        if (version.Type != 1 || version.Deleted || version.ExportBody().DirectorySnapshotHash.Length == 0) return null;
        var root = DirectorySnapshotBlocks.SnapshotOf(version);
        return new(CaseConflictPolicy.SourceId(version), version.Path, version.Record.WithLocalSequence(0), root.Hash, root.EncodedLength, version.SourceDevice.ToArray());
    }
}

// Own the folder gate across graph work; reference-gate scopes never include a network await.
internal sealed class DirectoryMetadataWorker(string rootPath, FolderIndex index, FolderKeys keys, byte[] own, TimeProvider clock,
    Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> fetch, DiagnosticJournal? journal = null)
{
    private const string RetainedCursor = "_directory-metadata-retained-cursor";
    private long nextRetainedTicks, nextParentTicks;
    private bool parentSources;
    private const string ParentCursor = "_directory-parent-metadata-cursor";
    private bool retainedDirectories;
    internal Action<string>? Checkpoint { get; init; }

    internal async Task<DirectoryMetadataPass> RunAsync(int maximumWork = 1000, int maximumSources = 32, CancellationToken token = default)
    {
        if (maximumWork is < 1 or > 1000 || maximumSources is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumWork));
        using var operation = await FolderOperationGate.AcquireAsync(index, token).ConfigureAwait(false);
        using var tree = new SafeFileTree(rootPath); byte[] root = index.Meta.Find("_root-identity")?.Value?.ToArray() ?? throw new IOException("Metadata root is unbound.");
        if (!tree.Identity.SequenceEqual(root)) throw new IOException("Metadata root identity changed.");
        long now = Now; int visited = 0, completed = 0, deferred = 0, remaining = maximumWork;
        bool retiring;
        using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
        {
            await index.PruneDirectoryMetadataSourcesAsync(root, now, limit: 1000, cancellationToken: token).ConfigureAwait(false);
            var retirement = await index.PruneDirectoryGraphsAsync(root, now, beginRetirements: false, limit: remaining, cancellationToken: token).ConfigureAwait(false);
            remaining -= retirement.Mutations; retiring = retirement.MoreWork;
        }
        if (now >= nextRetainedTicks)
        {
            try { await DiscoverRetained(root, now, token).ConfigureAwait(false); }
            catch (DirectoryMetadataCapacityException) { nextRetainedTicks = AddSeconds(now, 10); deferred++; }
        }
        if (index.HasDirectoryParentSources(root))
        {
            if (!parentSources || now >= nextParentTicks)
            {
                try { await DiscoverParents(root, now, token).ConfigureAwait(false); }
                catch (DirectoryMetadataCapacityException) { nextParentTicks = AddSeconds(now, 10); deferred++; }
            }
            parentSources = true;
        }
        else parentSources = false;
        foreach (var job in index.ReadDueDirectoryMetadataSources(root, now, maximumSources))
        {
            if (remaining == 0) break;
            token.ThrowIfCancellationRequested(); visited++; Guid session = Guid.NewGuid();
            using var activity = new Activity("directory-metadata").SetIdFormat(ActivityIdFormat.W3C).Start();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            byte[] route = job.Source.RouteHint.ToArray(); byte[]? routeAfter = job.RouteAfter?.ToArray();
            DirectoryMetadataRoutes? routePage = null;
            try
            {
                var version = VersionView.Open(job.Source.Record, keys, job.Source.RouteHint);
                var source = DirectoryMetadataSources.FromVersion(version) ?? throw new InvalidDataException("Metadata source has no signed directory root.");
                if (source.Path != job.Source.Path || source.Length != job.Source.Length || !source.Hash.AsSpan().SequenceEqual(job.Source.Hash)
                    || !source.SourceId.AsSpan().SequenceEqual(job.Source.SourceId)) throw new InvalidDataException("Metadata source manifest changed.");
                Log("start"); Checkpoint?.Invoke("metadata-before-graph");
                var graph = await new DirectorySnapshotGraph(rootPath, index, keys, job.Source.RouteHint, journal)
                { Checkpoint = stage => Checkpoint?.Invoke("metadata-graph-" + stage) }
                    .AdvanceUnderGateAsync(job.Source.Record, Fetch, remaining, deadline.Token).ConfigureAwait(false);
                remaining -= Math.Max(1, graph.Processed); Checkpoint?.Invoke("metadata-before-account");
                if (!graph.Complete && graph.Processed == 0) throw new IOException("Metadata graph retirement is still pending.");
                using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
                {
                    if (graph.Complete) { await index.CompleteDirectoryMetadataSourceAsync(job, token).ConfigureAwait(false); completed++; }
                    else await index.RecordDirectoryMetadataAttemptAsync(job, Now, failed: false, route, routeAfter, token).ConfigureAwait(false);
                }
                Log(graph.Complete ? "complete" : "progress", graph.Processed); Checkpoint?.Invoke("metadata-accounted");

                async ValueTask<ReadOnlyMemory<byte>> Fetch(ReadOnlyMemory<byte> hash, int length, CancellationToken cancellation)
                {
                    if (routePage is null) { routePage = index.ReadDirectoryMetadataRoutes(root, routeAfter); routeAfter = routePage.NextAfter; }
                    var candidates = new[] { route, version.ModifiedBy.ToArray() }.Concat(routePage.Devices)
                        .Where(candidate => !candidate.AsSpan().SequenceEqual(own)).DistinctBy(Convert.ToHexString);
                    foreach (var candidate in candidates)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        using var routeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        routeDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                        try
                        {
                            var response = await fetch(candidate, hash, length, routeDeadline.Token).ConfigureAwait(false); routeDeadline.Token.ThrowIfCancellationRequested();
                            if (response.Length != length) throw new InvalidDataException("Metadata response length mismatch.");
                            byte[] owned = response.ToArray(); _ = DirectorySnapshotNode.Open(new(hash.ToArray(), length), owned);
                            if (!candidate.AsSpan().SequenceEqual(route)) Log("route-failover"); route = candidate.ToArray(); return owned;
                        }
                        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or ObjectDisposedException)
                        { Log("route-unavailable", error: error); }
                        catch (OperationCanceledException error) when (routeDeadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
                        { Log("route-timeout", error: error); }
                    }
                    throw new IOException("No authenticated metadata route is available.");
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException or CryptographicException or SqliteException or InvalidOperationException
                || error is OperationCanceledException && !token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested(); deferred++; remaining = 0; // Failed work may already have persisted its entire offered prefix.
                using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
                    await index.RecordDirectoryMetadataAttemptAsync(job, Now, failed: true, cancellationToken: token, routeAfter: routeAfter).ConfigureAwait(false);
                Log("deferred", error: error);
            }
            void Log(string stage, int changed = 0, Exception? error = null) => journal?.TryWrite(new("directory.metadata_" + stage, "directory-metadata")
            {
                SessionId = session, Level = error is null ? DiagnosticLevel.Debug : DiagnosticLevel.Warning, ItemsVisited = 1, ItemsChanged = changed,
                ErrorCode = error is OperationCanceledException ? "timeout" : error?.GetType().Name, Exception = error, HResult = error?.HResult,
                NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null }
            });
        }
        long? next = index.ReadNextDirectoryMetadataTicks(root, Now);
        if (retiring) next = next is null ? Now : Math.Min(next.Value, Now);
        if (nextRetainedTicks <= Now || retainedDirectories) next = next is null ? nextRetainedTicks : Math.Min(next.Value, nextRetainedTicks);
        if (parentSources) next = next is null ? nextParentTicks : Math.Min(next.Value, nextParentTicks);
        return new(visited, completed, deferred, next);
    }

    private async Task DiscoverRetained(byte[] root, long now, CancellationToken token)
    {
        byte[]? bytes = index.Meta.Find(RetainedCursor)?.Value;
        byte[]? after = bytes is { Length: > 0 } ? bytes : null;
        var page = index.ReadRetainedDirectoryMetadata(root, now, after); var sources = new List<DirectoryMetadataSource>();
        foreach (var marker in page.Rows)
        {
            token.ThrowIfCancellationRequested(); var version = VersionView.Open(marker.Record, keys, marker.SourceDevice);
            var source = DirectoryMetadataSources.FromVersion(version) ?? throw new InvalidDataException("Retained case has no signed directory snapshot.");
            if (!source.SourceId.AsSpan().SequenceEqual(marker.SourceId)) throw new InvalidDataException("Retained metadata source ID changed.");
            retainedDirectories = true; sources.Add(source);
        }
        using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
        {
            await index.AdmitDirectoryMetadataSourcesAsync(root, sources, now, token).ConfigureAwait(false); Checkpoint?.Invoke("metadata-history-admitted");
            await index.Meta.UpsertAsync([new(RetainedCursor, page.More ? page.NextSourceId! : [])], token).ConfigureAwait(false);
        }
        nextRetainedTicks = page.More ? now : AddSeconds(now, 3600);
    }
    private async Task DiscoverParents(byte[] root, long now, CancellationToken token)
    {
        byte[]? cursor = index.Meta.Find(ParentCursor)?.Value; if (cursor?.Length == 0) cursor = null;
        var page = index.ReadDirectoryParentSources(root, cursor); var sources = new List<DirectoryMetadataSource>();
        foreach (var source in page.Sources)
        {
            var version = VersionView.Open(source.Record, keys, source.RouteHint); var verified = DirectoryMetadataSources.FromVersion(version);
            if (verified is null || verified.Path != source.Path || verified.Length != source.Length || !verified.SourceId.AsSpan().SequenceEqual(source.SourceId)
                || !verified.Hash.AsSpan().SequenceEqual(source.Hash)) throw new InvalidDataException("Parent metadata representative changed.");
            sources.Add(source);
        }
        using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
        {
            await index.AdmitDirectoryMetadataSourcesAsync(root, sources, now, token).ConfigureAwait(false);
            await index.Meta.UpsertAsync([new(ParentCursor, page.More ? page.NextSourceId! : [])], token).ConfigureAwait(false);
        }
        nextParentTicks = page.More || page.Sources.Count == 0 && cursor is not null ? now : AddSeconds(now, 3600);
    }
    private long Now => Math.Max(1, clock.GetUtcNow().UtcTicks);
    private static long AddSeconds(long now, int seconds) => now + Math.Min(DateTime.MaxValue.Ticks - now, seconds * TimeSpan.TicksPerSecond);
}
