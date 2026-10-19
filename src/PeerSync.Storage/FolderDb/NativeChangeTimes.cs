using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record NativeChangeTimeUpdate(string Path, long ExpectedLocalSequence, byte[] FileId, long ChangeFileTime);

public sealed partial class FolderIndex
{
    /// <summary>Cache-only compare-and-swap after stable byte verification.
    /// Does not change signed metadata, chunks, readiness or publication sequence.</summary>
    public Task<int> UpdateNativeChangeTimesAsync(IReadOnlyList<NativeChangeTimeUpdate> updates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count > 1000) throw new ArgumentOutOfRangeException(nameof(updates));
        var frozen = updates.Select(r => r with { FileId = r.FileId.ToArray() }).ToArray();
        foreach (var row in frozen)
        {
            _ = PathRules.Key(row.Path); FolderMaps.RequireBytes(row.FileId, 16);
            if (row.ExpectedLocalSequence <= 0 || row.ChangeFileTime < 0) throw new ArgumentException("Invalid native change cache update.", nameof(updates));
        }
        return database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE local_files SET change_filetime=$stamp WHERE path_key=$path AND local_seq=$seq AND ntfs_file_id=$id AND type=0 AND deleted=0;";
            command.Parameters.Add("$stamp", SqliteType.Integer); command.Parameters.Add("$path", SqliteType.Text);
            command.Parameters.Add("$seq", SqliteType.Integer); command.Parameters.Add("$id", SqliteType.Blob); int count = 0;
            foreach (var row in frozen)
            {
                cancellationToken.ThrowIfCancellationRequested(); command.Parameters["$stamp"].Value = row.ChangeFileTime;
                command.Parameters["$path"].Value = PathRules.Key(row.Path); command.Parameters["$seq"].Value = row.ExpectedLocalSequence;
                command.Parameters["$id"].Value = row.FileId; count += command.ExecuteNonQuery();
            }
            return count;
        }, cancellationToken);
    }
}
