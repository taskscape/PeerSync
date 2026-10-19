using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public enum DirectoryGraphWorkKind { Page, Record }
public sealed record DirectoryGraphState(byte[] Id, byte[] RootId, string DirectoryPath, byte[] Hash, int Length, bool Complete)
{ public override string ToString() => "Directory graph state (redacted)"; }
public sealed record DirectoryGraphWork(long Id, string ParentPath, byte[] Hash, int Length, DirectoryGraphWorkKind Kind, int PageDepth)
{ public override string ToString() => "Directory graph work (redacted)"; }
public sealed record DirectoryGraphChild(string ParentPath, byte[] Hash, int Length, DirectoryGraphWorkKind Kind, int PageDepth)
{ public override string ToString() => "Directory graph child (redacted)"; }
public sealed record DirectoryGraphBlock(byte[] Hash, int Length);
public sealed record DirectoryGraphMember(string PathKey, byte[] RecordHash, int RecordLength)
{ public override string ToString() => "Directory graph member (redacted)"; }

public sealed partial class FolderIndex
{
    public Task<DirectoryGraphState> BeginDirectoryGraphAsync(byte[] graphId, byte[] rootId, string path, byte[] hash, int length, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(graphId, 32); FolderMaps.RequireBytes(rootId, 24); FolderMaps.RequireBytes(hash, 32); _ = PathRules.Key(path);
        if (length is < 12 or > 4620 || (length - 12) % 36 != 0) throw new ArgumentOutOfRangeException(nameof(length));
        byte[] id = graphId.ToArray(), root = rootId.ToArray(), node = hash.ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root);
            if (FindDirectoryGraph(connection, transaction, id) is { } existing)
            {
                if (!existing.RootId.AsSpan().SequenceEqual(root) || existing.DirectoryPath != path || existing.Length != length || !existing.Hash.AsSpan().SequenceEqual(node))
                    throw new InvalidDataException("Directory graph identity has conflicting inputs.");
                return existing;
            }
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO directory_graphs(id,root_id,directory_path,node_hash,node_length) VALUES($id,$root,$path,$hash,$length);";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$hash", node); command.Parameters.AddWithValue("$length", length); command.ExecuteNonQuery();
            InsertDirectoryGraphChild(connection, transaction, id, new(path, node, length, DirectoryGraphWorkKind.Page, 0));
            return new DirectoryGraphState(id.ToArray(), root.ToArray(), path, node.ToArray(), length, false);
        }, cancellationToken);
    }
    public DirectoryGraphWork? ReadDirectoryGraphWork(byte[] graphId)
    {
        FolderMaps.RequireBytes(graphId, 32); using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,parent_path,node_hash,node_length,kind,page_depth FROM directory_graph_work WHERE graph_id=$id AND complete=0 ORDER BY id LIMIT 1;";
        command.Parameters.AddWithValue("$id", graphId); using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2), reader.GetInt32(3), (DirectoryGraphWorkKind)reader.GetInt32(4), reader.GetInt32(5)) : null;
    }
    public DirectoryGraphState? ReadDirectoryGraph(byte[] graphId)
    { FolderMaps.RequireBytes(graphId, 32); using var connection = database.OpenReader(); return FindDirectoryGraph(connection, null, graphId); }

    // Reference prefixes may be replayed after cancellation. A FULL work commit
    // flushes those frames before it makes progress visible.
    public Task<int> AppendDirectoryGraphReferencesAsync(byte[] graphId, IReadOnlyList<DirectoryGraphBlock> references, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(graphId, 32); ArgumentNullException.ThrowIfNull(references);
        if (references.Count > 1000) throw new ArgumentException("Directory reference batch exceeds its bound.", nameof(references));
        byte[] id = graphId.ToArray(); var rows = references.Select(row => row with { Hash = row.Hash.ToArray() }).ToArray();
        foreach (var row in rows) { FolderMaps.RequireBytes(row.Hash, 32); if (row.Length is < 1 or > 8388608) throw new ArgumentException("Invalid directory graph reference.", nameof(references)); }
        return database.WriteAsync((connection, transaction) =>
        {
            var state = RequireOpenDirectoryGraph(connection, transaction, id); RequireBlockReferenceRoot(connection, transaction, state.RootId);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO directory_graph_references(graph_id,hash,length) VALUES($id,$hash,$length) ON CONFLICT(graph_id,hash) DO UPDATE SET length=excluded.length WHERE directory_graph_references.length=excluded.length;";
            command.Parameters.AddWithValue("$id", id); command.Parameters.Add("$hash", SqliteType.Blob); command.Parameters.Add("$length", SqliteType.Integer);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested(); command.Parameters["$hash"].Value = row.Hash; command.Parameters["$length"].Value = row.Length;
                if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Directory graph reference has conflicting lengths.");
            }
            return rows.Length;
        }, cancellationToken);
    }
    public Task<bool> CompleteDirectoryGraphWorkAsync(byte[] graphId, long workId, IReadOnlyList<DirectoryGraphChild> children,
        DirectoryGraphMember? member = null, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(graphId, 32); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workId); ArgumentNullException.ThrowIfNull(children);
        if (children.Count > 128) throw new ArgumentException("Directory work batch exceeds its bound.", nameof(children));
        byte[] id = graphId.ToArray(); var frozen = children.Select(row => row with { Hash = row.Hash.ToArray() }).ToArray();
        foreach (var row in frozen) ValidateDirectoryChild(row);
        var owned = member is null ? null : member with { RecordHash = member.RecordHash.ToArray(), PathKey = PathRules.Key(member.PathKey) };
        if (owned is not null) { FolderMaps.RequireBytes(owned.RecordHash, 32); if (owned.RecordLength is < 48 or > 624 || (owned.RecordLength - 12) % 36 != 0) throw new ArgumentException("Invalid directory graph member.", nameof(member)); }
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var state = RequireOpenDirectoryGraph(connection, transaction, id); RequireBlockReferenceRoot(connection, transaction, state.RootId);
            using var pending = connection.CreateCommand(); pending.Transaction = transaction;
            pending.CommandText = "SELECT kind,parent_key,node_hash,node_length,page_depth FROM directory_graph_work WHERE id=$work AND graph_id=$id AND complete=0;";
            pending.Parameters.AddWithValue("$work", workId); pending.Parameters.AddWithValue("$id", id);
            using (var reader = pending.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("Directory graph work is no longer pending.");
                if ((reader.GetInt32(0) == (int)DirectoryGraphWorkKind.Record) != (owned is not null)) throw new InvalidDataException("Directory graph work kind does not match its result.");
                if (owned is not null && (ParentDirectoryKey(owned.PathKey) != reader.GetString(1) || !owned.RecordHash.AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(2)) || owned.RecordLength != reader.GetInt32(3)))
                    throw new InvalidDataException("Directory graph member does not match its pending work.");
                if (owned is not null && (frozen.Length > 1 || frozen.Any(child => child.Kind != DirectoryGraphWorkKind.Page || child.PageDepth != 0 || PathRules.Key(child.ParentPath) != owned.PathKey)))
                    throw new InvalidDataException("Nested directory work does not match its member.");
                if (owned is null && frozen.Any(child => PathRules.Key(child.ParentPath) != reader.GetString(1)
                    || child.PageDepth != reader.GetInt32(4) + (child.Kind == DirectoryGraphWorkKind.Page ? 1 : 0)))
                    throw new InvalidDataException("Directory page work does not match its context.");
            }
            try
            {
                if (owned is not null)
                {
                    using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO directory_graph_members(graph_id,path_key,record_hash,record_length) VALUES($id,$path,$hash,$length);";
                    insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$path", owned.PathKey);
                    insert.Parameters.AddWithValue("$hash", owned.RecordHash); insert.Parameters.AddWithValue("$length", owned.RecordLength); insert.ExecuteNonQuery();
                }
                foreach (var child in frozen) { cancellationToken.ThrowIfCancellationRequested(); InsertDirectoryGraphChild(connection, transaction, id, child); }
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new InvalidDataException("Directory graph contains duplicate or invalid structure.", error); }
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_graph_work SET complete=1 WHERE id=$work AND graph_id=$id AND complete=0; "
                + "UPDATE directory_graphs SET complete=1 WHERE id=$id AND NOT EXISTS(SELECT 1 FROM directory_graph_work WHERE graph_id=$id AND complete=0);";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$work", workId); command.ExecuteNonQuery();
            return FindDirectoryGraph(connection, transaction, id)!.Complete;
        }, cancellationToken);
    }
    public IEnumerable<DirectoryGraphBlock> ReadDirectoryGraphReferences(byte[] graphId)
    {
        FolderMaps.RequireBytes(graphId, 32); byte[] id = graphId.ToArray(); return Read();
        IEnumerable<DirectoryGraphBlock> Read()
        {
            using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
            var state = FindDirectoryGraph(connection, transaction, id) ?? throw new InvalidOperationException("Directory graph is unavailable.");
            RequireBlockReferenceRoot(connection, transaction, state.RootId);
            if (!state.Complete) throw new InvalidOperationException("Directory graph coverage is incomplete.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT hash,length FROM directory_graph_references WHERE graph_id=$id ORDER BY hash;"; command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader(); while (reader.Read()) yield return new(reader.GetFieldValue<byte[]>(0), reader.GetInt32(1));
        }
    }
    public IEnumerable<DirectoryGraphMember> ReadDirectoryGraphMembers(byte[] graphId)
    {
        FolderMaps.RequireBytes(graphId, 32); byte[] id = graphId.ToArray(); return Read();
        IEnumerable<DirectoryGraphMember> Read()
        {
            using var connection = database.OpenReader(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT path_key,record_hash,record_length FROM directory_graph_members WHERE graph_id=$id ORDER BY path_key;"; command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader(); while (reader.Read()) yield return new(reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetInt32(2));
        }
    }
    private static DirectoryGraphState? FindDirectoryGraph(SqliteConnection connection, SqliteTransaction? transaction, byte[] id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT root_id,directory_path,node_hash,node_length,complete FROM directory_graphs WHERE id=$id;"; command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader(); return reader.Read() ? new(id.ToArray(), reader.GetFieldValue<byte[]>(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2), reader.GetInt32(3), reader.GetBoolean(4)) : null;
    }
    private static DirectoryGraphState RequireOpenDirectoryGraph(SqliteConnection connection, SqliteTransaction transaction, byte[] id)
    {
        var state = FindDirectoryGraph(connection, transaction, id) ?? throw new InvalidOperationException("Directory graph is unavailable.");
        if (state.Complete) throw new InvalidOperationException("Completed directory graphs are immutable."); return state;
    }
    private static void ValidateDirectoryChild(DirectoryGraphChild child)
    {
        _ = PathRules.Key(child.ParentPath); FolderMaps.RequireBytes(child.Hash, 32);
        if (!Enum.IsDefined(child.Kind) || child.Length is < 12 or > 1048588 || child.PageDepth is < 0 or > 9)
            throw new ArgumentException("Invalid directory graph work.", nameof(child));
    }
    private static void InsertDirectoryGraphChild(SqliteConnection connection, SqliteTransaction transaction, byte[] id, DirectoryGraphChild child)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO directory_graph_work(graph_id,parent_path,parent_key,node_hash,node_length,kind,page_depth) VALUES($id,$path,$key,$hash,$length,$kind,$depth);";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$path", child.ParentPath); command.Parameters.AddWithValue("$key", PathRules.Key(child.ParentPath));
        command.Parameters.AddWithValue("$hash", child.Hash); command.Parameters.AddWithValue("$length", child.Length);
        command.Parameters.AddWithValue("$kind", (int)child.Kind); command.Parameters.AddWithValue("$depth", child.PageDepth); command.ExecuteNonQuery();
    }
    private static string ParentDirectoryKey(string path) { int separator = path.LastIndexOf('/'); return separator < 0 ? "" : path[..separator]; }
}
