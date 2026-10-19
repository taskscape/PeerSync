using System.Text;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

internal static class FolderMaps
{
    private const SqliteType Text = SqliteType.Text, Integer = SqliteType.Integer, Blob = SqliteType.Blob;
    public const string LocalColumns = "path_key,path,type,size,mtime_ticks,attributes,deleted,changed_ticks,file_hash,vv,record_body,signature,ntfs_file_id,local_seq,flags,content_ready,change_filetime";
    public static RowMap<LocalFileRow> Local { get; } = new("local_files", ["path_key"],
    [
        new("path_key", Text, r => r.Path.ToUpperInvariant()), new("path", Text, r => r.Path),
        new("type", Integer, r => (long)r.Type), new("size", Integer, r => r.Size),
        new("mtime_ticks", Integer, r => r.MtimeTicks), new("attributes", Integer, r => (long)r.Attributes),
        new("deleted", Integer, r => r.Deleted ? 1 : 0), new("changed_ticks", Integer, r => r.ChangedTicks),
        new("file_hash", Blob, r => r.FileHash), new("vv", Blob, r => VersionVector.Serialize(r.Vector)),
        new("record_body", Blob, r => r.Record?.Body.ToArray()), new("signature", Blob, r => r.Record?.Signature.ToArray()),
        new("ntfs_file_id", Blob, r => r.FileId), new("local_seq", Integer, r => r.LocalSequence),
        new("flags", Integer, r => (int)r.Flags),
        new("content_ready", Integer, r => r.ContentReady ? 1 : 0),
        new("change_filetime", Integer, r => r.ChangeFileTime),
    ], ReadLocal, ValidateLocal, (c, t) => new LocalBatch(c, t));

    public static RowMap<RemoteFileRow> Remote { get; } = new("remote_files", ["device_id", "path_key"],
    [
        new("device_id", Blob, r => r.DeviceId), new("path_key", Text, r => r.PathKey.ToUpperInvariant()),
        new("vv", Blob, r => VersionVector.Serialize(r.Vector)), new("record_body", Blob, r => r.Record.Body.ToArray()),
        new("signature", Blob, r => r.Record.Signature.ToArray()),
    ], r => new(r.GetFieldValue<byte[]>(0), r.GetString(1), VersionVector.Deserialize(r.GetFieldValue<byte[]>(2)),
        new(r.GetFieldValue<byte[]>(3), r.GetFieldValue<byte[]>(4))),
        r => { RequireBytes(r.DeviceId, 32); RequirePath(r.PathKey); ArgumentNullException.ThrowIfNull(r.Vector); ArgumentNullException.ThrowIfNull(r.Record); });

    public static RowMap<NeededRow> Needed { get; } = new("needed", ["path_key"],
    [new("path_key", Text, r => r.PathKey.ToUpperInvariant()), new("source_device", Blob, r => r.SourceDevice), new("priority", Integer, r => r.Priority)],
        r => new(r.GetString(0), r.GetFieldValue<byte[]>(1), r.GetInt32(2)),
        r => { RequirePath(r.PathKey); RequireBytes(r.SourceDevice, 32); });

    public static RowMap<ChunkRow> Chunks { get; } = new("chunks", ["hash", "path_key", "file_offset"],
    [new("hash", Blob, r => r.Hash), new("path_key", Text, r => r.PathKey.ToUpperInvariant()),
        new("file_offset", Integer, r => r.Offset), new("length", Integer, r => r.Length)],
        r => new(r.GetFieldValue<byte[]>(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3)),
        r => { RequireBytes(r.Hash, 32); RequirePath(r.PathKey); ArgumentOutOfRangeException.ThrowIfNegative(r.Offset); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(r.Length); });

    public static RowMap<DeviceRow> Devices { get; } = new("devices", ["device_id"],
    [new("device_id", Blob, r => r.DeviceId), new("name", Text, r => r.Name), new("last_seen_ticks", Integer, r => r.LastSeenTicks),
        new("last_seq_received", Integer, r => r.LastSequenceReceived), new("approval", Integer, r => (int)r.Approval), new("pinned", Integer, r => r.Pinned ? 1 : 0)],
        r => new(r.GetFieldValue<byte[]>(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetInt64(3), (DeviceApproval)r.GetInt32(4), r.GetBoolean(5)),
        r => { RequireBytes(r.DeviceId, 32); if (r.Name?.Length > 256) throw new ArgumentException("Device name exceeds limit.");
            if (r.LastSeenTicks is { } ticks) RequireTicks(ticks); ArgumentOutOfRangeException.ThrowIfNegative(r.LastSequenceReceived);
            if (!Enum.IsDefined(r.Approval)) throw new ArgumentException("Invalid approval state."); });

    public static RowMap<ArchiveRow> Archive { get; } = new("archive", ["id"],
    [new("id", Integer, r => r.Id == 0 ? null : r.Id), new("original_path", Text, r => r.OriginalPath), new("archive_path", Text, r => r.ArchivePath),
        new("archived_ticks", Integer, r => r.ArchivedTicks), new("size", Integer, r => r.Size), new("file_hash", Blob, r => r.FileHash),
        new("archive_path_key", Text, r => r.ArchivePath.ToUpperInvariant())],
        r => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), OptionalBytes(r, 5)),
        r => { ArgumentOutOfRangeException.ThrowIfNegative(r.Id); RequirePath(r.OriginalPath); RequireTicks(r.ArchivedTicks);
            ArgumentOutOfRangeException.ThrowIfNegative(r.Size); if (r.FileHash is not null) RequireBytes(r.FileHash, 32);
            if (!r.ArchivePath.StartsWith(".psp/archive/", StringComparison.OrdinalIgnoreCase) || !PathRules.IsValid(r.ArchivePath[13..]))
                throw new ArgumentException("Invalid archive path."); });

    public static RowMap<MetaRow> Meta { get; } = new("meta", ["key"],
    [new("key", Text, r => r.Key), new("value", Blob, r => r.Value)], r => new(r.GetString(0), OptionalBytes(r, 1)),
        r => { ArgumentException.ThrowIfNullOrWhiteSpace(r.Key); if (r.Key.Length > 256) throw new ArgumentException("Metadata key exceeds limit."); });

    public static LocalFileRow ReadLocal(SqliteDataReader reader)
    {
        byte[]? body = OptionalBytes(reader, 10), signature = OptionalBytes(reader, 11);
        if ((body is null) != (signature is null)) throw new InvalidDataException("Incomplete signed record in folder index.");
        long sequence = reader.GetInt64(13);
        return new(reader.GetString(1), reader.GetFieldValue<uint>(2), reader.GetInt64(3), reader.GetInt64(4),
            reader.GetFieldValue<uint>(5), reader.GetBoolean(6), reader.GetInt64(7), OptionalBytes(reader, 8),
            VersionVector.Deserialize(reader.GetFieldValue<byte[]>(9)), body is null ? null : new(body, signature!, checked((ulong)sequence)),
            OptionalBytes(reader, 12), sequence, (LocalFileState)reader.GetInt32(14)) { ContentReady = reader.GetBoolean(15), ChangeFileTime = reader.IsDBNull(16) ? null : reader.GetInt64(16) };
    }

    public static void RequireBytes(byte[] value, int length)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != length) throw new ArgumentException("Invalid identifier/hash length.", nameof(value));
    }

    private static byte[]? OptionalBytes(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetFieldValue<byte[]>(column);
    private static void RequirePath(string path) { if (!PathRules.IsValid(path)) throw new ArgumentException("Invalid record path.", nameof(path)); }
    private static void RequireTicks(long ticks) { if (ticks < 0 || ticks > DateTime.MaxValue.Ticks) throw new ArgumentOutOfRangeException(nameof(ticks)); }

    private static void ValidateLocal(LocalFileRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        bool unsyncable = (row.Flags & LocalFileState.Unsyncable) != 0;
        if (unsyncable)
        {
            if (row.Record is not null || row.Path.Length is 0 or > 32767) throw new ArgumentException("Unsyncable metadata cannot publish a record.");
            // Preserve a non-NFC local name in SQLite without silently changing it.
            _ = new UTF8Encoding(false, true).GetByteCount(row.Path);
        }
        else RequirePath(row.Path);
        if (row.Type > 2 || ((int)row.Flags & ~7) != 0) throw new ArgumentException("Invalid file type/state.");
        ArgumentOutOfRangeException.ThrowIfNegative(row.Size);
        RequireTicks(row.MtimeTicks); RequireTicks(row.ChangedTicks);
        if (row.ChangeFileTime is < 0) throw new ArgumentOutOfRangeException(nameof(row));
        ArgumentNullException.ThrowIfNull(row.Vector);
        if (row.FileHash is not null) RequireBytes(row.FileHash, 32);
        if (row.FileId is not null) RequireBytes(row.FileId, 16);
        if (row.Record is not null && !row.Deleted && row.Type == 0 && row.FileHash is null) throw new ArgumentException("Published files require a whole-file hash.");
        if (row.IndexedChunks is { } chunks)
        {
            long offset = 0;
            foreach (var chunk in chunks)
            {
                RequireBytes(chunk.Hash, 32);
                if (chunk.Offset != offset || chunk.Length <= 0) throw new ArgumentException("Invalid chunk offsets/lengths.");
                offset = checked(offset + chunk.Length);
            }
            if (row.Deleted || row.Type != 0)
            {
                if (chunks.Count != 0) throw new ArgumentException("Non-file versions cannot index chunks.");
            }
            else if (offset != row.Size) throw new ArgumentException("Chunk lengths do not cover the file.");
        }
    }
}
