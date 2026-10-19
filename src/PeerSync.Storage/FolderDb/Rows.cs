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
    /// <summary>Caller has durably captured verified independent chunks. Required
    /// for publication of live regular files; legacy rows default to withheld.</summary>
    public bool ContentReady { get; init; }
    /// <summary>Native FILETIME change stamp for cache validation, not a signed
    /// edit time. Null on legacy rows forces one content verification pass.</summary>
    public long? ChangeFileTime { get; init; }
}

// Remote original-case paths are in the signed body; these keys are uppercase invariant.
public sealed record RemoteFileRow(byte[] DeviceId, string PathKey,
    IReadOnlyDictionary<ulong, ulong> Vector, SignedRecord Record);
public sealed record NeededRow(string PathKey, byte[] SourceDevice, int Priority)
{
    public PullWorkDescriptor? Work { get; init; }
    /// <summary>CPU-only resolver output, admitted atomically with needed work.
    /// Not part of the scheduling cache; read the durable preservation ledger.</summary>
    public IReadOnlyList<CasePreservationSource> CasePreservations { get; init; } = [];
    /// <summary>Resolver-only directive: admit sources atomically but remove
    /// ordinary scheduled work when the original already matches its decision.</summary>
    public bool PreservationAdmissionOnly { get; init; }
}

/// <summary>Scheduling cache only; the applier revalidates the signed decision.</summary>
public sealed class PullWorkDescriptor
{
    private readonly byte[] fingerprint;
    public int Stage { get; }
    public long Size { get; }
    public long MtimeTicks { get; }
    public byte[] Fingerprint => fingerprint.ToArray();
    public PullWorkDescriptor(int stage, long size, long mtimeTicks, byte[] fingerprint)
    {
        FolderMaps.RequireBytes(fingerprint, 32);
        if (stage is < 0 or > 3 || size < 0 || mtimeTicks < 0 || mtimeTicks > DateTime.MaxValue.Ticks) throw new ArgumentException("Invalid pull work descriptor.");
        Stage = stage; Size = size; MtimeTicks = mtimeTicks; this.fingerprint = fingerprint.ToArray();
    }
    public override string ToString() => "Pull work descriptor (redacted)";
}
public sealed record ChunkRow(byte[] Hash, string PathKey, long Offset, int Length);
public sealed record DeviceRow(byte[] DeviceId, string? Name = null, long? LastSeenTicks = null,
    long LastSequenceReceived = 0, DeviceApproval Approval = DeviceApproval.Unknown, bool Pinned = true);
public sealed record ArchiveRow(long Id, string OriginalPath, string ArchivePath,
    long ArchivedTicks, long Size, byte[]? FileHash);
public sealed record MetaRow(string Key, byte[]? Value);
