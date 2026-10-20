namespace PeerSync.Storage.FolderDb;

public sealed record CaseBlockRoutePage(byte[]? Hint, IReadOnlyList<byte[]> Devices);

public sealed partial class FolderIndex
{
    /// <summary>Advance one source-bound page before network work so restart cannot starve later peers.</summary>
    public Task<CaseBlockRoutePage> AdvanceCaseBlockRoutesAsync(PendingCasePreservation expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequirePendingCase(connection, transaction, expected);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT route_hint,route_after FROM case_block_routes WHERE source_id=$id;";
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); byte[]? hint = null, after = null;
            using (var reader = command.ExecuteReader())
                if (reader.Read()) { hint = reader.IsDBNull(0) ? null : reader.GetFieldValue<byte[]>(0); after = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1); }
            command.CommandText = "SELECT device_id FROM devices " + (after is null ? "" : "WHERE device_id>$after ") + "ORDER BY device_id LIMIT 7;";
            if (after is not null) command.Parameters.AddWithValue("$after", after);
            var rows = new List<byte[]>(); bool more = false;
            using (var reader = command.ExecuteReader())
                while (reader.Read()) { if (rows.Count == 6) { more = true; break; } rows.Add(reader.GetFieldValue<byte[]>(0)); }
            command.CommandText = "INSERT INTO case_block_routes(source_id,route_after) VALUES($id,$next) ON CONFLICT(source_id) DO UPDATE SET route_after=excluded.route_after;";
            command.Parameters.AddWithValue("$next", more ? rows[^1] : DBNull.Value);
            cancellationToken.ThrowIfCancellationRequested(); command.ExecuteNonQuery();
            return new CaseBlockRoutePage(hint, rows.AsReadOnly());
        }, cancellationToken);
    }

    public Task RememberCaseBlockRouteAsync(PendingCasePreservation expected, byte[] device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); FolderMaps.RequireBytes(device, 32); byte[] frozen = device.ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequirePendingCase(connection, transaction, expected);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO case_block_routes(source_id,route_hint) VALUES($id,$route) ON CONFLICT(source_id) DO UPDATE SET route_hint=excluded.route_hint;";
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); command.Parameters.AddWithValue("$route", frozen);
            cancellationToken.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); return 0;
        }, cancellationToken);
    }
}
