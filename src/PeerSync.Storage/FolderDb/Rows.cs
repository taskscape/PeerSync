using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

[Flags]
public enum LocalFileState { None = 0, Ignored = 1, Unsyncable = 2, NeedsRescan = 4 }
public enum DeviceApproval { Unknown = 0, Approved = 1, Blocked = 2 }

public sealed record LocalChunk(long Offset, int Length, byte[] Hash);

public sealed record LocalFileRow(string Path, uint Type, long Size, long MtimeTicks,
    uint Attributes, bool Deleted, long ChangedTicks, byte[]? FileHash,
    IReadOnlyDictionary<ulong, ulong> Vector, SignedRecord? Record, byte[]? FileId,
    long LocalSequence = 0, LocalFileState Flags = LocalFileState.None)
{
    /// <summary>Null preserves the index. A supplied list atomically replaces it; reads leave it unloaded.</summary>
    public IReadOnlyList<LocalChunk>? IndexedChunks { get; init; }
}

// Remote original-case paths are in the signed body; these keys are uppercase invariant.
public sealed record RemoteFileRow(byte[] DeviceId, string PathKey,
    IReadOnlyDictionary<ulong, ulong> Vector, SignedRecord Record);
public sealed record NeededRow(string PathKey, byte[] SourceDevice, int Priority);
public sealed record ChunkRow(byte[] Hash, string PathKey, long Offset, int Length);
public sealed record DeviceRow(byte[] DeviceId, string? Name = null, long? LastSeenTicks = null,
    long LastSequenceReceived = 0, DeviceApproval Approval = DeviceApproval.Unknown, bool Pinned = true);
public sealed record ArchiveRow(long Id, string OriginalPath, string ArchivePath,
    long ArchivedTicks, long Size, byte[]? FileHash);
public sealed record MetaRow(string Key, byte[]? Value);
