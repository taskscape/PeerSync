using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Ignore;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Scanning;

public sealed record ScanProblem(string RelativePath, string Code, int? NativeError = null);
public sealed record ScanResult(int Enumerated, int Unchanged, int Hashed, int Renamed, int Updated,
    int Deleted, int Ignored, int Unsyncable, TimeSpan Elapsed, IReadOnlyList<ScanProblem> Problems);

/// <summary>Tracks a local tree. The caller owns the index, keys and optional journal.
/// Filesystem apply/recovery shares the per-index operation gate with scans.</summary>
public sealed class Scanner : IDisposable
{
    private const string RootIdentityKey = "_root-identity";
    private static readonly IReadOnlyDictionary<ulong, ulong> EmptyVector = new Dictionary<ulong, ulong>();
    private readonly string rootPath;
    private readonly FolderIndex index;
    private readonly FolderKeys keys;
    private readonly byte[] device;
    private readonly ulong deviceShort;
    private readonly IgnoreMatcher ignore;
    private readonly TimeProvider clock;
    private readonly DiagnosticJournal? journal;
    private readonly int hashConcurrency;
    private readonly SemaphoreSlim scanGate = new(1);
    internal string RootPath => rootPath;

    public Scanner(string rootPath, FolderIndex index, FolderKeys keys, byte[] deviceId,
        IgnoreMatcher? ignore = null, int? hashConcurrency = null, TimeProvider? timeProvider = null,
        DiagnosticJournal? journal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(deviceId);
        if (deviceId.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(deviceId));
        if (!keys.CanRead) throw new ArgumentException("Encrypted-only folders require the P6 opaque engine.", nameof(keys));
        this.rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        this.index = index; this.keys = keys; device = deviceId.ToArray();
        deviceShort = BinaryPrimitives.ReadUInt64LittleEndian(device);
        this.ignore = ignore ?? new();
        this.clock = timeProvider ?? TimeProvider.System;
        this.journal = journal;
        this.hashConcurrency = hashConcurrency ?? Math.Max(1, Environment.ProcessorCount - 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.hashConcurrency);
    }

    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        await scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        Guid session = Guid.NewGuid();
        try
        {
            using var operation = await FolderOperationGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
            journal?.TryWrite(new("scan.start", "scan") { SessionId = session });
            if (index.ReadApplyIntents().Any())
                throw new IOException("Pending file application must be recovered before scanning.");
            using var root = new NativeRoot(rootPath);
            byte[]? identity = index.Meta.Find(RootIdentityKey)?.Value;
            if (identity is not null && !identity.AsSpan().SequenceEqual(root.Identity))
                throw new IOException("Sync root identity changed; the existing index cannot be reused.");
            if (identity is null)
                await index.Meta.UpsertAsync([new(RootIdentityKey, root.Identity)], cancellationToken).ConfigureAwait(false);

            var previous = index.LocalFiles.ReadAll().ToDictionary(r => r.Path.ToUpperInvariant(), StringComparer.Ordinal);
            var entries = new Dictionary<string, WalkEntry>(StringComparer.Ordinal);
            var protectedPrefixes = new HashSet<string>(StringComparer.Ordinal);
            var ignoredPrefixes = new HashSet<string>(StringComparer.Ordinal);
            var problems = new ConcurrentBag<ScanProblem>();
            int ignored = 0;
            var directories = new Stack<string>();
            directories.Push("");
            while (directories.TryPop(out string? directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var native in root.Enumerate(directory, cancellationToken))
                    {
                        string path = directory.Length == 0 ? native.Name : directory + '/' + native.Name;
                        if (IsInternal(path)) continue;
                        string key = path.ToUpperInvariant();
                        bool valid = PathRules.IsValid(path);
                        if (valid && ignore.IsIgnored(path, native.IsDirectory))
                        {
                            ignored++; ignoredPrefixes.Add(key); continue;
                        }
                        var entry = new WalkEntry(path, native, !valid || native.IsReparse);
                        if (!entries.TryAdd(key, entry))
                        {
                            // A case-sensitive Windows directory can contain two names for one wire key.
                            protectedPrefixes.Add(key);
                            problems.Add(new(path, "case-collision"));
                            continue;
                        }
                        if (entry.Unsyncable)
                        {
                            protectedPrefixes.Add(key);
                            problems.Add(new(path, native.IsReparse ? "reparse" : "invalid-name"));
                        }
                        else if (native.IsDirectory) directories.Push(path);
                    }
                }
                catch (Exception error) when (IsFileError(error))
                {
                    protectedPrefixes.Add(directory.ToUpperInvariant());
                    problems.Add(new(directory, "enumeration-failed", NativeCode(error)));
                    journal?.TryWrite(new("scan.enumeration_failed", "scan") { SessionId = session,
                        Level = DiagnosticLevel.Warning, HResult = error.HResult, NativeError = NativeCode(error) });
                }
            }

            var updates = new ConcurrentBag<LocalFileRow>();
            int unchanged = 0, hashed = 0, renamed = 0, deleted = 0, unsyncable = 0;
            // Identity matching is restricted to paths absent from the successful walk.
            var renameCandidates = previous.Values.Where(r => !r.Deleted && r.Type == 0 && r.Record is not null
                && r.Flags == LocalFileState.None && r.FileId is not null && !entries.ContainsKey(r.Path.ToUpperInvariant())
                && !Under(r.Path.ToUpperInvariant(), protectedPrefixes) && !Under(r.Path.ToUpperInvariant(), ignoredPrefixes))
                .GroupBy(r => Convert.ToHexString(r.FileId!), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            await Parallel.ForEachAsync(entries.Values, new ParallelOptions
            {
                MaxDegreeOfParallelism = hashConcurrency, CancellationToken = cancellationToken,
            }, async (entry, token) =>
            {
                string key = entry.Path.ToUpperInvariant();
                previous.TryGetValue(key, out var old);
                // An incomplete directory listing must not produce changes in that subtree.
                if (Under(key, protectedPrefixes, includeExact: !entry.Unsyncable)) return;
                if (entry.Unsyncable)
                {
                    Interlocked.Increment(ref unsyncable);
                    try
                    {
                        _ = new UTF8Encoding(false, true).GetByteCount(entry.Path);
                        if (old is null || old.Flags != LocalFileState.Unsyncable || !MetadataEqual(old, entry))
                            updates.Add(Metadata(entry, old, LocalFileState.Unsyncable));
                    }
                    catch (EncoderFallbackException) { /* Cannot represent this local name in SQLite UTF-8. */ }
                    return;
                }
                if (old is { Deleted: false, Flags: LocalFileState.None } && MetadataEqual(old, entry)
                    && (!keys.CanWrite || old.Record is not null))
                { Interlocked.Increment(ref unchanged); return; }
                try
                {
                    if (entry.Native.IsDirectory)
                    {
                        updates.Add(CreateVersion(entry, old, null));
                        return;
                    }
                    // Renames reuse only a validated, exact original chunk list.
                    if (renameCandidates.TryGetValue(Convert.ToHexString(entry.Native.FileId), out var candidates))
                    {
                        var source = candidates.FirstOrDefault(r => r.Size == entry.Native.Length
                            && r.MtimeTicks == entry.Native.MtimeTicks && r.Attributes == (uint)entry.Native.Attributes);
                        if (source is not null && SignedRecordFactory.TryOpen(source.Record!, keys, out var body)
                            && !body.Deleted && body.Type == 0 && body.Size == (ulong)entry.Native.Length)
                        {
                            var vector = VersionVector.Merge(old?.Vector ?? EmptyVector, source.Vector);
                            var chunks = new List<ChunkInfo>();
                            long offset = 0;
                            foreach (var chunk in body.Chunks)
                            { chunks.Add(new(offset, checked((int)chunk.Length), chunk.Hash.ToByteArray())); offset += chunk.Length; }
                            if (offset == entry.Native.Length && body.FileHash.Length == 32)
                            {
                                using var current = root.OpenFile(entry.Path);
                                var snapshot = FileSnapshot.Read(current);
                                if (snapshot.Length != entry.Native.Length || snapshot.LastWrite != entry.Native.LastWriteFileTime
                                    || snapshot.Changed != entry.Native.ChangeFileTime || snapshot.Attributes != entry.Native.Attributes
                                    || snapshot.Volume != root.Volume
                                    || snapshot.IdLow != BinaryPrimitives.ReadUInt64LittleEndian(entry.Native.FileId)
                                    || snapshot.IdHigh != BinaryPrimitives.ReadUInt64LittleEndian(entry.Native.FileId.AsSpan(8)))
                                { Defer(entry.Path, old, "changed-during-read", null); return; }
                                var reuse = new FileHash(entry.Native.Length, new DateTime(entry.Native.MtimeTicks, DateTimeKind.Utc),
                                    body.ChunkClass, chunks.AsReadOnly(), body.FileHash.ToByteArray());
                                updates.Add(CreateVersion(entry, old, reuse, vector));
                                Interlocked.Increment(ref renamed); return;
                            }
                        }
                    }
                    var result = await FileHasher.HashRelativeAsync(root, entry.Path, cancellationToken: token).ConfigureAwait(false);
                    Interlocked.Increment(ref hashed);
                    if (result is null || result.Length != entry.Native.Length || result.MtimeUtc.Ticks != entry.Native.MtimeTicks
                        || result.Attributes != entry.Native.Attributes || result.Volume != root.Volume
                        || !entry.Native.FileId.AsSpan().SequenceEqual(result.FileId))
                    { Defer(entry.Path, old, "changed-during-read", null); return; }
                    updates.Add(CreateVersion(entry, old, result));
                }
                catch (Exception error) when (IsFileError(error))
                { Defer(entry.Path, old, "read-failed", NativeCode(error)); }
            }).ConfigureAwait(false);

            foreach (var old in previous.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = old.Path.ToUpperInvariant();
                if (Under(key, ignoredPrefixes))
                {
                    if ((old.Flags & LocalFileState.Ignored) == 0) updates.Add(old with { Flags = old.Flags | LocalFileState.Ignored });
                    continue;
                }
                if (entries.ContainsKey(key) || Under(key, protectedPrefixes) || IsInternal(old.Path)) continue;
                // Ignore rules can also match paths that disappeared before this walk.
                if (PathRules.IsValid(old.Path) && ignore.IsIgnored(old.Path, old.Type == 1))
                {
                    if ((old.Flags & LocalFileState.Ignored) == 0) updates.Add(old with { Flags = old.Flags | LocalFileState.Ignored });
                    continue;
                }
                if (old.Deleted)
                {
                    LocalFileState flags = old.Flags & LocalFileState.Unsyncable;
                    if (keys.CanWrite && old.Record is null && old.Vector.Count != 0 && flags == LocalFileState.None && PathRules.IsValid(old.Path))
                    {
                        var pending = old with { Flags = flags, Vector = VersionVector.Increment(old.Vector, deviceShort), IndexedChunks = [] };
                        updates.Add(pending with { Record = Sign(pending, 0, []) });
                    }
                    else if (flags != old.Flags) updates.Add(old with { Flags = flags });
                    continue;
                }
                long now = clock.GetUtcNow().UtcTicks;
                bool publish = keys.CanWrite && (old.Flags & LocalFileState.Unsyncable) == 0
                    && (old.Record is not null || old.Vector.Count != 0);
                var vector = publish ? VersionVector.Increment(old.Vector, deviceShort) : old.Vector;
                var tombstone = old with
                {
                    Deleted = true, Size = 0, ChangedTicks = now, FileHash = null, FileId = null, Vector = vector,
                    Flags = old.Flags & LocalFileState.Unsyncable, Record = null, IndexedChunks = [],
                };
                if (publish) tombstone = tombstone with { Record = Sign(tombstone, 0, []) };
                updates.Add(tombstone); deleted++;
            }
            // The root's pathname may have been replaced while its pinned handle stayed valid.
            using (var currentRoot = new NativeRoot(rootPath))
                if (!currentRoot.Identity.AsSpan().SequenceEqual(root.Identity))
                    throw new IOException("Sync root was replaced during the scan.");
            int written = await index.LocalFiles.UpsertAsync(updates.OrderBy(r => r.Path, StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            journal?.TryWrite(new("scan.complete", "scan") { SessionId = session, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                ItemsVisited = entries.Count, ItemsChanged = written, ItemsHashed = hashed,
                ItemsSkipped = unchanged + ignored + unsyncable, ItemsDeferred = problems.Count });
            return new(entries.Count, unchanged, hashed, renamed, written, deleted, ignored, unsyncable,
                stopwatch.Elapsed, problems.OrderBy(p => p.RelativePath, StringComparer.Ordinal).ToArray());

            void Defer(string path, LocalFileRow? old, string code, int? native)
            {
                problems.Add(new(path, code, native));
                if (old is not null && (old.Flags & LocalFileState.NeedsRescan) == 0)
                    updates.Add(old with { Flags = old.Flags | LocalFileState.NeedsRescan });
                journal?.TryWrite(new("scan.defer", "scan")
                { SessionId = session, Level = DiagnosticLevel.Debug, ErrorCode = code, NativeError = native, RetryInMilliseconds = 10000 });
            }
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("scan.failed", "scan") { SessionId = session, Level = DiagnosticLevel.Error,
                ErrorCode = error is OperationCanceledException ? "cancelled" : "scan-error", HResult = error.HResult,
                NativeError = NativeCode(error), ElapsedMilliseconds = stopwatch.ElapsedMilliseconds });
            throw;
        }
        finally { scanGate.Release(); }
    }

    private LocalFileRow CreateVersion(WalkEntry entry, LocalFileRow? old, FileHash? hash,
        IReadOnlyDictionary<ulong, ulong>? inherited = null)
    {
        var row = Metadata(entry, old, LocalFileState.None) with
        {
            FileHash = hash?.WholeHash,
            Vector = keys.CanWrite ? VersionVector.Increment(inherited ?? old?.Vector ?? EmptyVector, deviceShort) : old?.Vector ?? EmptyVector,
            IndexedChunks = hash is null ? [] : hash.Chunks.Select(c => new LocalChunk(c.Offset, c.Length, c.Hash)).ToArray(),
        };
        return keys.CanWrite ? row with { Record = Sign(row, hash?.ChunkClass ?? 0, hash?.Chunks ?? []) } : row;
    }

    private LocalFileRow Metadata(WalkEntry entry, LocalFileRow? old, LocalFileState flags) =>
        new(entry.Path, entry.Unsyncable ? 2u : entry.Native.IsDirectory ? 1u : 0u, entry.Native.Length,
            entry.Native.MtimeTicks, (uint)entry.Native.Attributes, false, clock.GetUtcNow().UtcTicks,
            null, old?.Vector ?? EmptyVector, null, entry.Native.FileId, Flags: flags);

    private Core.Sync.SignedRecord Sign(LocalFileRow row, uint chunkClass, IReadOnlyList<ChunkInfo> chunks)
    {
        var body = new FileRecordBody
        {
            Path = row.Path, Type = row.Type, Size = (ulong)row.Size, MtimeTicks = row.MtimeTicks,
            Attributes = row.Attributes & (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden), Deleted = row.Deleted,
            ChangedTicks = row.ChangedTicks, ChunkClass = chunkClass,
            FileHash = row.FileHash is null ? ByteString.Empty : ByteString.CopyFrom(row.FileHash), ModifiedBy = ByteString.CopyFrom(device),
        };
        body.Vv.AddRange(row.Vector.Select(v => new VvEntry { Device = v.Key, Counter = v.Value }));
        body.Chunks.AddRange(chunks.Select(c => new Chunk { Length = (uint)c.Length, Hash = ByteString.CopyFrom(c.Hash) }));
        return SignedRecordFactory.Create(body, keys);
    }

    private static bool MetadataEqual(LocalFileRow row, WalkEntry entry) => !row.Deleted
        && row.Type == (entry.Unsyncable ? 2u : entry.Native.IsDirectory ? 1u : 0u)
        && row.Path == entry.Path && row.Size == entry.Native.Length && row.Attributes == (uint)entry.Native.Attributes
        && (entry.Native.IsDirectory || row.MtimeTicks == entry.Native.MtimeTicks)
        && row.FileId is not null && row.FileId.AsSpan().SequenceEqual(entry.Native.FileId);

    private static bool Under(string key, HashSet<string> prefixes, bool includeExact = true)
    {
        if (prefixes.Contains("")) return true;
        if (includeExact && prefixes.Contains(key)) return true;
        int separator = key.IndexOf('/');
        while (separator >= 0)
        {
            if (prefixes.Contains(key[..separator])) return true;
            separator = key.IndexOf('/', separator + 1);
        }
        return false;
    }

    /// <summary>Stop scheduling scans and await outstanding scans before disposing.</summary>
    public void Dispose() => scanGate.Dispose();

    internal static bool IsInternal(string path) => path.Split('/').Any(p => p.Equals(".psp", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith(".!psp", StringComparison.OrdinalIgnoreCase));
    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception;
    private static int? NativeCode(Exception error) => error is Win32Exception native ? native.NativeErrorCode : null;
    private sealed record WalkEntry(string Path, NativeEntry Native, bool Unsyncable);
}
