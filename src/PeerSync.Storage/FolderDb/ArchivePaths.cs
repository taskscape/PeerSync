namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    /// <summary>Missing native objects still reserve their indexed names until
    /// reconciliation removes the row. This is namespace protection, not ownership.</summary>
    public bool ArchivePathExists(string archivePath)
    {
        FolderMaps.Archive.Validate(new(0, "reserved", archivePath, 0, 0, null));
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM archive WHERE archive_path_key=$key);";
        command.Parameters.AddWithValue("$key", archivePath.ToUpperInvariant());
        return (long)command.ExecuteScalar()! != 0;
    }

    // SQLite UPPER/NOCASE only fold ASCII. Compute the same invariant Unicode
    // keys as new writes, with bounded FULL batches before exposing this index.
    private async Task BackfillArchivePathKeysAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<(long Id, string Path)>();
            using (var connection = database.OpenReader())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id,archive_path FROM archive WHERE archive_path_key IS NULL LIMIT 1000;";
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
            if (rows.Count == 0) return;
            await database.WriteDurableAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE archive SET archive_path_key=$key WHERE id=$id AND archive_path=$path AND archive_path_key IS NULL;";
                var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                var path = command.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
                var key = command.Parameters.Add("$key", Microsoft.Data.Sqlite.SqliteType.Text);
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    id.Value = row.Id; path.Value = row.Path; key.Value = row.Path.ToUpperInvariant(); command.ExecuteNonQuery();
                }
                return rows.Count;
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
