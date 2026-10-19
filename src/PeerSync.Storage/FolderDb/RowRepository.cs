using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

/// <summary>Parameterized access to a fixed, code-defined table map; no caller-supplied SQL identifiers.</summary>
public sealed class RowRepository<T> where T : class
{
    public const int BatchSize = 1000;
    private readonly Db database;
    private readonly RowMap<T> map;
    private readonly string select;
    private readonly string upsert;

    internal RowRepository(Db database, RowMap<T> map)
    {
        this.database = database;
        this.map = map;
        string columns = string.Join(',', map.Fields.Select(f => '"' + f.Name + '"'));
        select = $"SELECT {columns} FROM \"{map.Table}\"";
        string values = string.Join(',', Enumerable.Range(0, map.Fields.Length).Select(i => "$p" + i));
        string keys = string.Join(',', map.Keys.Select(k => '"' + k + '"'));
        string updates = string.Join(',', map.Fields.Where(f => !map.Keys.Contains(f.Name))
            .Select(f => $"\"{f.Name}\"=excluded.\"{f.Name}\""));
        upsert = $"INSERT INTO \"{map.Table}\" ({columns}) VALUES ({values}) ON CONFLICT ({keys}) DO UPDATE SET {updates};";
    }

    public async Task<int> UpsertAsync(IEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int total = 0;
        foreach (T[] batch in rows.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in batch) map.Validate(row);
            int count = await database.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = upsert;
                for (int i = 0; i < map.Fields.Length; i++) command.Parameters.Add("$p" + i, map.Fields[i].Type);
                command.Prepare();
                using var session = map.BeginBatch?.Invoke(connection, transaction);
                foreach (var supplied in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = session is null ? supplied : session.Prepare(supplied);
                    for (int i = 0; i < map.Fields.Length; i++) command.Parameters[i].Value = map.Fields[i].Value(row) ?? DBNull.Value;
                    command.ExecuteNonQuery();
                }
                session?.Finish();
                return batch.Length;
            }, cancellationToken).ConfigureAwait(false);
            total = checked(total + count);
        }
        return total;
    }

    public IEnumerable<T> ReadAll() => ReadWhere("");

    public long Count()
    {
        using var connection = database.OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{map.Table}\";";
        return (long)command.ExecuteScalar()!;
    }

    public T? Find(params object?[] keys)
    {
        ValidateKeys(keys);
        return ReadWhere(KeyClause(), NormalizeKeys(keys)).FirstOrDefault();
    }

    public Task<int> DeleteAsync(object?[] keys, CancellationToken cancellationToken = default)
    {
        ValidateKeys(keys);
        return database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM \"{map.Table}\" WHERE {KeyClause()};";
            BindValues(command, NormalizeKeys(keys));
            return command.ExecuteNonQuery();
        }, cancellationToken);
    }

    internal IEnumerable<T> ReadWhere(string where, params object?[] keys)
    {
        using var connection = database.OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = select + (where.Length == 0 ? "" : " WHERE " + where) + ";";
        BindValues(command, keys);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return map.Read(reader);
    }

    private string KeyClause() => string.Join(" AND ", map.Keys.Select((key, index) => $"\"{key}\"=$k{index}"));
    private void ValidateKeys(object?[] keys)
    {
        if (keys.Length != map.Keys.Length) throw new ArgumentException("Incorrect number of row keys.", nameof(keys));
    }
    private object?[] NormalizeKeys(object?[] keys)
    {
        var normalized = keys.ToArray();
        for (int i = 0; i < keys.Length; i++)
            if (map.Keys[i] == "path_key" && keys[i] is string path) normalized[i] = path.ToUpperInvariant();
        return normalized;
    }
    private static void BindValues(SqliteCommand command, object?[] keys)
    {
        for (int i = 0; i < keys.Length; i++) command.Parameters.AddWithValue("$k" + i, keys[i] ?? DBNull.Value);
    }
}

internal sealed record RowField<T>(string Name, SqliteType Type, Func<T, object?> Value);
internal sealed record RowMap<T>(string Table, string[] Keys, RowField<T>[] Fields,
    Func<SqliteDataReader, T> Read, Action<T> Validate,
    Func<SqliteConnection, SqliteTransaction, RowBatch<T>>? BeginBatch = null);

internal abstract class RowBatch<T> : IDisposable
{
    public abstract T Prepare(T row);
    public abstract void Finish();
    public abstract void Dispose();
}
