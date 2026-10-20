namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    /// <summary>Caller pins the physical root and owns folder/reference gates.
    /// Initial binding is FULL and cannot adopt unbound recovery state.</summary>
    public Task BindPhysicalRootAsync(byte[] rootId, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(rootId, 24); byte[] root = rootId.ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT value FROM meta WHERE key='_root-identity';";
            object? prior = command.ExecuteScalar();
            if (prior is byte[] bound)
            {
                if (!bound.AsSpan().SequenceEqual(root)) throw new InvalidDataException("Physical root binding changed.");
                return 0;
            }
            if (prior is not null and not DBNull) throw new InvalidDataException("Physical root binding is invalid.");
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM apply_intents UNION ALL SELECT 1 FROM staging_retirements);";
            if ((long)command.ExecuteScalar()! != 0) throw new InvalidDataException("Unbound index has recovery state.");
            command.CommandText = "INSERT INTO meta(key,value) VALUES('_root-identity',$root) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
            command.Parameters.AddWithValue("$root", root); command.ExecuteNonQuery(); return 0;
        }, cancellationToken);
    }
}
