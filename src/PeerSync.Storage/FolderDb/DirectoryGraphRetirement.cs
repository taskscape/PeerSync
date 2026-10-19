using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    /// <summary>Caller holds folder/reference gates. Begin new retirements only
    /// after complete fresh reference activation touched every current source.
    /// This retires cache metadata, never native bytes or source proofs.</summary>
    public Task<DirectoryGraphPrune> PruneDirectoryGraphsAsync(byte[] rootId, long nowTicks, bool beginRetirements,
        int limit = 1000, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(rootId, 24); byte[] root = rootId.ToArray();
        if (nowTicks is < 1 or > 3155378975999999999) throw new ArgumentOutOfRangeException(nameof(nowTicks));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        long cutoff = Math.Max(0, nowTicks - TimeSpan.TicksPerDay);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); int mutations = 0;
            while (mutations < limit)
            {
                cancellationToken.ThrowIfCancellationRequested(); byte[]? id = Find(retiringOnly: true);
                if (id is null && beginRetirements)
                {
                    id = Find(retiringOnly: false); if (id is null) break;
                    using var mark = connection.CreateCommand(); mark.Transaction = transaction;
                    mark.CommandText = "UPDATE directory_graphs SET retiring=1 WHERE id=$id AND root_id=$root AND retiring=0;";
                    mark.Parameters.AddWithValue("$id", id); mark.Parameters.AddWithValue("$root", root); mutations += mark.ExecuteNonQuery();
                }
                if (id is null || mutations == limit) break;
                DeleteRows("directory_graph_work", "id");
                DeleteRows("directory_graph_members", "path_key");
                DeleteRows("directory_graph_references", "hash");
                if (mutations == limit) break;
                using var finish = connection.CreateCommand(); finish.Transaction = transaction;
                finish.CommandText = "DELETE FROM directory_graphs WHERE id=$id AND retiring=1 "
                    + "AND NOT EXISTS(SELECT 1 FROM directory_graph_work WHERE graph_id=$id) "
                    + "AND NOT EXISTS(SELECT 1 FROM directory_graph_members WHERE graph_id=$id) "
                    + "AND NOT EXISTS(SELECT 1 FROM directory_graph_references WHERE graph_id=$id);";
                finish.Parameters.AddWithValue("$id", id); int removed = finish.ExecuteNonQuery();
                if (removed != 1) throw new InvalidDataException("Directory graph retirement did not make bounded progress.");
                mutations += removed;

                void DeleteRows(string table, string key)
                {
                    if (mutations == limit) return;
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    // Identifiers come exclusively from the three literals above.
                    command.CommandText = $"DELETE FROM {table} WHERE graph_id=$id AND {key} IN (SELECT {key} FROM {table} WHERE graph_id=$id ORDER BY {key} LIMIT $limit);";
                    command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$limit", limit - mutations);
                    mutations += command.ExecuteNonQuery();
                }
            }
            return new DirectoryGraphPrune(mutations, Find(retiringOnly: true) is not null || beginRetirements && Find(retiringOnly: false) is not null);

            byte[]? Find(bool retiringOnly)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT id FROM directory_graphs WHERE root_id=$root AND retiring=$retiring "
                    + (retiringOnly ? "" : "AND last_used_ticks<=$cutoff ") + "ORDER BY last_used_ticks,id LIMIT 1;";
                command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$retiring", retiringOnly ? 1 : 0);
                if (!retiringOnly) command.Parameters.AddWithValue("$cutoff", cutoff);
                return command.ExecuteScalar() as byte[];
            }
        }, cancellationToken);
    }
}
