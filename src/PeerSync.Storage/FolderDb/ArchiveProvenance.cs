namespace PeerSync.Storage.FolderDb;

/// <summary>Local reference protection, never proof that filesystem bytes are
/// owned or safe to delete. Detached sources outlive missing archive rows.</summary>
public sealed record ArchiveProvenance(Guid SourceId, ArchiveRow Archive, bool Attached, byte[]? RootId)
{
    public override string ToString() => "Archive protection source (metadata redacted)";
}

public sealed partial class FolderIndex
{
    /// <summary>Stable source identities distinguish replacement metadata and
    /// reused archive row IDs. The caller must separately verify native bytes.</summary>
    public IEnumerable<ArchiveProvenance> ReadArchiveProvenance() => ReadArchiveProvenanceCore(null);

    public IEnumerable<ArchiveProvenance> ReadRetainedArchiveProvenance(byte[] rootId)
    {
        FolderMaps.RequireBytes(rootId, 24); return ReadArchiveProvenanceCore(rootId.ToArray());
    }

    private IEnumerable<ArchiveProvenance> ReadArchiveProvenanceCore(byte[]? root)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id,original_archive_id,original_path,archive_path,archived_ticks,size,file_hash,archive_id,root_id FROM archive_provenance p "
            + (root is null ? "" : "WHERE NOT EXISTS(SELECT 1 FROM archive_policy_releases r WHERE r.source_id=p.source_id AND r.root_id=$root) ") + "ORDER BY source_id;";
        if (root is not null) command.Parameters.AddWithValue("$root", root);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(Guid.ParseExact(reader.GetString(0), "N"),
            new(reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6)), !reader.IsDBNull(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8));
    }
}
