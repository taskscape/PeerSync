using PeerSync.Core.Paths;
using System.Text;

namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    /// <summary>Commit a scanner's ordered, disposable update stream as one
    /// transaction. Cancellation or process interruption leaves no partial
    /// local index publication.</summary>
    public Task<int> CommitScanRowsAsync(IEnumerable<LocalFileRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return database.WriteAsync((connection, transaction) =>
        {
            int total = 0;
            foreach (var batch in rows.Chunk(RowRepository<LocalFileRow>.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + LocalFiles.WriteBatch(connection, transaction, batch, cancellationToken));
            }
            return total;
        }, cancellationToken);
    }

    /// <summary>Stream distinct path keys from the local, remote and scheduled
    /// indexes for a bounded needed-work rebuild.</summary>
    public IEnumerable<string> ReadAllNeededPathKeys()
    {
        // Merge the three ordered indexes rather than a SQL UNION: this
        // database configures SQLite temporary storage in memory, where a
        // million-key UNION would undo the scanner's memory bound.
        using var localConnection = database.OpenReader();
        using var remoteConnection = database.OpenReader();
        using var neededConnection = database.OpenReader();
        using var localCommand = localConnection.CreateCommand();
        using var remoteCommand = remoteConnection.CreateCommand();
        using var neededCommand = neededConnection.CreateCommand();
        localCommand.CommandText = "SELECT path_key FROM local_files ORDER BY path_key;";
        remoteCommand.CommandText = "SELECT path_key FROM remote_files INDEXED BY ix_remote_path ORDER BY path_key;";
        neededCommand.CommandText = "SELECT path_key FROM needed ORDER BY path_key;";
        using var localReader = localCommand.ExecuteReader();
        using var remoteReader = remoteCommand.ExecuteReader();
        using var neededReader = neededCommand.ExecuteReader();
        var readers = new[] { localReader, remoteReader, neededReader };
        var current = readers.Select(reader => reader.Read() ? reader.GetString(0) : null).ToArray();
        while (current.Any(path => path is not null))
        {
            string? next = null;
            foreach (string? candidate in current)
                if (candidate is not null && (next is null || Utf8PathComparer.Instance.Compare(candidate, next) < 0)) next = candidate;
            string path = next!;
            if (PathRules.IsValid(path)) yield return path;
            for (int i = 0; i < readers.Length; i++)
                while (current[i] == path)
                    current[i] = readers[i].Read() ? readers[i].GetString(0) : null;
        }
    }

    private sealed class Utf8PathComparer : IComparer<string>
    {
        public static readonly Utf8PathComparer Instance = new();
        public int Compare(string? left, string? right) => left is null ? right is null ? 0 : -1
            : right is null ? 1 : Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
    }
}
