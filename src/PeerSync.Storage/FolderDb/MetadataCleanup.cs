namespace PeerSync.Storage.FolderDb;

public sealed record MetadataCleanupResult(int Mutations, int ChunkRowsRemoved, int ManifestsRemoved, int SourcesRemoved,
    int WitnessesRemoved, bool MoreWork);

public sealed partial class FolderIndex
{
    /// <summary>Caller retains both gates through a fresh fully covered snapshot.
    /// Forget only explicitly released metadata; never change native bytes or
    /// reference hashes. Each FULL batch bounds every changed/deleted row.</summary>
    public Task<MetadataCleanupResult> PruneRetiredMetadataAsync(Guid snapshot, byte[] rootId, long nowTicks, int archiveDays,
        int limit = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 2);
        byte[] root = FreezeSweep(rootId, nowTicks, archiveDays, limit);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireSweepSnapshot(connection, transaction, snapshot, root);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", nowTicks);
            command.Parameters.AddWithValue("$cutoff", Math.Max(0, nowTicks - archiveDays * TimeSpan.TicksPerDay));
            var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            var source = command.Parameters.Add("$source", Microsoft.Data.Sqlite.SqliteType.Text);
            var remaining = command.Parameters.Add("$limit", Microsoft.Data.Sqlite.SqliteType.Integer);
            int changed = 0, chunks = 0, manifests = 0, sources = 0, witnesses = 0;
            // Materialize bounded worklists once, rather than rescanning all
            // live manifests for every small detached-source deletion.
            remaining.Value = limit;
            command.CommandText = "SELECT a.id,a.retiring " + RetirableContent + " ORDER BY a.retiring DESC,a.id LIMIT $limit;";
            var contents = new List<(string Id, bool Retiring)>(limit);
            using (var reader = command.ExecuteReader()) while (reader.Read()) contents.Add((reader.GetString(0), reader.GetBoolean(1)));
            foreach (var content in contents)
            {
                cancellationToken.ThrowIfCancellationRequested(); if (changed >= limit) break; id.Value = content.Id;
                if (!content.Retiring)
                {
                    command.CommandText = "UPDATE archive_contents SET retiring=1 WHERE id=$id AND retiring=0;"; changed += command.ExecuteNonQuery();
                }
                if (changed >= limit) break; remaining.Value = limit - changed;
                command.CommandText = "DELETE FROM archive_content_chunks WHERE content_id=$id AND file_offset IN(SELECT file_offset FROM archive_content_chunks WHERE content_id=$id ORDER BY file_offset LIMIT $limit);";
                int removed = command.ExecuteNonQuery(); changed += removed; chunks += removed;
                if (changed >= limit) break;
                command.CommandText = "DELETE FROM archive_contents WHERE id=$id AND retiring=1 AND NOT EXISTS(SELECT 1 FROM archive_content_chunks WHERE content_id=$id);";
                removed = command.ExecuteNonQuery(); changed += removed; manifests += removed;
            }
            remaining.Value = limit - changed;
            command.CommandText = "SELECT p.source_id " + RetirableSource + " ORDER BY p.source_id LIMIT $limit;";
            var releasedSources = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) releasedSources.Add(reader.GetString(0));
            foreach (string released in releasedSources)
            {
                cancellationToken.ThrowIfCancellationRequested(); if (changed >= limit) break; source.Value = released; remaining.Value = limit - changed;
                command.CommandText = "DELETE FROM block_reference_sources WHERE source_id=$source AND snapshot_id IN(SELECT c.snapshot_id FROM block_reference_sources c JOIN block_reference_snapshots s ON s.id=c.snapshot_id WHERE c.source_id=$source AND s.root_id=$root LIMIT $limit);";
                changed += command.ExecuteNonQuery(); if (changed >= limit) break; remaining.Value = limit - changed;
                command.CommandText = "DELETE FROM archive_retirement_sources WHERE source_id=$source AND retirement_id IN(SELECT b.retirement_id FROM archive_retirement_sources b JOIN archive_retirements r ON r.id=b.retirement_id WHERE b.source_id=$source AND r.root_id=$root AND r.completed_ticks IS NOT NULL LIMIT $limit);";
                changed += command.ExecuteNonQuery(); if (limit - changed < 2) break;
                command.CommandText = "DELETE FROM archive_policy_releases WHERE source_id=$source AND root_id=$root;"; changed += command.ExecuteNonQuery();
                command.CommandText = "DELETE FROM archive_provenance WHERE source_id=$source AND archive_id IS NULL;"; int removed = command.ExecuteNonQuery(); changed += removed; sources += removed;
            }
            if (changed < limit)
            {
                cancellationToken.ThrowIfCancellationRequested(); remaining.Value = limit - changed;
                command.CommandText = "DELETE FROM archive_retirements WHERE id IN(SELECT r.id " + RetirableWitness + " ORDER BY r.completed_ticks,r.id LIMIT $limit);";
                int retired = command.ExecuteNonQuery(); changed += retired; witnesses += retired;
            }
            command.CommandText = "SELECT EXISTS(SELECT 1 " + RetirableContent + ") OR EXISTS(SELECT 1 " + RetirableSource + ") OR EXISTS(SELECT 1 " + RetirableWitness + ");";
            bool more = (long)command.ExecuteScalar()! != 0;
            return new MetadataCleanupResult(changed, chunks, manifests, sources, witnesses, more);
        }, cancellationToken);
    }

    private const string NoForeignMetadata = " AND NOT EXISTS(SELECT 1 FROM history_archive_links h WHERE h.source_id=p.source_id AND h.root_id<>$root) "
        + "AND NOT EXISTS(SELECT 1 FROM block_reference_sources c JOIN block_reference_snapshots s ON s.id=c.snapshot_id WHERE c.source_id=p.source_id AND s.root_id<>$root) ";
    private const string NoPendingSource = " AND NOT EXISTS(SELECT 1 FROM archive_retirement_sources b JOIN archive_retirements r ON r.id=b.retirement_id WHERE b.source_id=p.source_id AND (r.completed_ticks IS NULL OR r.root_id<>$root)) "
        + "AND NOT EXISTS(SELECT 1 FROM archive_retirements r WHERE r.archive_id=p.original_archive_id AND r.completed_ticks IS NULL) ";
    private const string RetirableContent = "FROM archive_contents a JOIN archive_provenance p ON p.source_id=a.source_id WHERE a.root_id=$root AND (p.root_id IS NULL OR p.root_id=$root) "
        + "AND (a.retiring=1 OR (EXISTS(SELECT 1 FROM archive_policy_releases x WHERE x.source_id=p.source_id AND x.root_id=$root AND x.released_ticks<=$now) "
        + "AND NOT EXISTS(SELECT 1 FROM history_archive_links h WHERE h.source_id=p.source_id))) " + NoPendingSource + NoForeignMetadata;
    private const string RetirableSource = "FROM archive_provenance p JOIN archive_policy_releases x ON x.source_id=p.source_id "
        + "WHERE p.archive_id IS NULL AND (p.root_id IS NULL OR p.root_id=$root) AND x.root_id=$root AND x.released_ticks<=$now "
        + "AND NOT EXISTS(SELECT 1 FROM archive_contents a WHERE a.source_id=p.source_id) AND NOT EXISTS(SELECT 1 FROM history_archive_links h WHERE h.source_id=p.source_id) " + NoPendingSource + NoForeignMetadata;
    private const string RetirableWitness = "FROM archive_retirements r WHERE r.root_id=$root AND r.completed_ticks IS NOT NULL AND r.completed_ticks<$cutoff "
        + "AND NOT EXISTS(SELECT 1 FROM archive_retirement_sources b WHERE b.retirement_id=r.id) AND NOT EXISTS(SELECT 1 FROM archive_policy_releases x WHERE x.witness_id=r.id)";
}
