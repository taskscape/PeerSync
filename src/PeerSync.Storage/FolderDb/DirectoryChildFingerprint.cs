using System.Buffers.Binary;
using System.Text;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

/// <summary>Membership fingerprint shared by a complete native inventory and
/// the signed local index. It proves neither content nor ownership/readiness.
/// Supply canonical direct child keys in strict ordinal order.</summary>
public sealed class DirectoryChildFingerprint : IDisposable
{
    private readonly string parent;
    private readonly Blake3.Hasher digest = Blake3.Hasher.New();
    private string? last;
    private long count;
    private bool completed;
    public DirectoryChildFingerprint(string parentPath)
    {
        parent = PathRules.Key(parentPath);
        digest.Update("PeerSync directory child keys v1"u8); Append(parent);
    }
    public void Add(string childKey)
    {
        if (completed) throw new InvalidOperationException("Directory child fingerprint is complete.");
        string key = PathRules.Key(childKey); int separator = key.LastIndexOf('/');
        if (key != childKey || separator < 0 || key[..separator] != parent
            || last is not null && StringComparer.Ordinal.Compare(last, key) >= 0)
            throw new ArgumentException("Directory child keys must be canonical, direct and strictly ordered.", nameof(childKey));
        Append(key); count = checked(count + 1); last = key;
    }
    public DirectoryChildInventory Complete(long epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        if (completed) throw new InvalidOperationException("Directory child fingerprint is complete.");
        completed = true; Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, checked((ulong)count)); digest.Update(bytes);
        return new(digest.Finalize().AsSpan().ToArray(), count, epoch);
    }
    private void Append(string key)
    {
        byte[] bytes = Encoding.BigEndianUnicode.GetBytes(key); Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)bytes.Length)); digest.Update(length); digest.Update(bytes);
    }
    public void Dispose() => digest.Dispose();
}
public sealed record DirectoryChildInventory(byte[] Hash, long Count, long Epoch)
{ public override string ToString() => "Directory child inventory (redacted)"; }

public sealed partial class FolderIndex
{
    /// <summary>Read only child keys and their mutation witness in one SQLite
    /// snapshot. Pending unsigned supported children are intentionally absent,
    /// so they cannot match a complete native inventory.</summary>
    public DirectoryChildInventory ReadDirectoryChildInventory(string parentPath, CancellationToken cancellationToken = default)
    {
        using var fingerprint = new DirectoryChildFingerprint(parentPath);
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        long epoch = ReadDirectoryCaptureEpoch(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT path_key FROM local_files WHERE parent_key=$parent AND deleted=0 AND (flags&3)=0 "
            + "AND type IN (0,1) AND record_body IS NOT NULL ORDER BY path_sort_key;";
        command.Parameters.AddWithValue("$parent", PathRules.Key(parentPath));
        using var reader = command.ExecuteReader();
        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); fingerprint.Add(reader.GetString(0)); }
        cancellationToken.ThrowIfCancellationRequested(); return fingerprint.Complete(epoch);
    }
}
