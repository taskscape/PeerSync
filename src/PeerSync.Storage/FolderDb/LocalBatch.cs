using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

internal sealed class LocalBatch : RowBatch<LocalFileRow>
{
    private readonly SqliteCommand deleteChunks;
    private readonly SqliteCommand insertChunk;
    private readonly SqliteCommand storeSequence;
    private long sequence;

    public LocalBatch(SqliteConnection connection, SqliteTransaction transaction)
    {
        sequence = FolderIndex.ReadSequence(connection, transaction);
        deleteChunks = Make(connection, transaction, "DELETE FROM chunks WHERE path_key=$path;", ("$path", SqliteType.Text));
        insertChunk = Make(connection, transaction, "INSERT INTO chunks (hash,path_key,file_offset,length) VALUES ($hash,$path,$offset,$length);",
            ("$hash", SqliteType.Blob), ("$path", SqliteType.Text), ("$offset", SqliteType.Integer), ("$length", SqliteType.Integer));
        storeSequence = Make(connection, transaction, "INSERT INTO meta (key,value) VALUES ($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            ("$key", SqliteType.Text), ("$value", SqliteType.Blob));
    }

    public override LocalFileRow Prepare(LocalFileRow row)
    {
        long next = checked(++sequence);
        string key = row.Path.ToUpperInvariant();
        if (row.IndexedChunks is not null || row.Deleted || row.Type != 0)
        {
            deleteChunks.Parameters[0].Value = key;
            deleteChunks.ExecuteNonQuery();
            foreach (var chunk in row.IndexedChunks ?? [])
            {
                insertChunk.Parameters[0].Value = chunk.Hash;
                insertChunk.Parameters[1].Value = key;
                insertChunk.Parameters[2].Value = chunk.Offset;
                insertChunk.Parameters[3].Value = chunk.Length;
                insertChunk.ExecuteNonQuery();
            }
        }
        return row with { LocalSequence = next, Record = row.Record?.WithLocalSequence((ulong)next) };
    }

    public override void Finish()
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, sequence);
        storeSequence.Parameters[0].Value = FolderIndex.SequenceKey;
        storeSequence.Parameters[1].Value = bytes;
        storeSequence.ExecuteNonQuery();
    }

    public override void Dispose()
    {
        deleteChunks.Dispose();
        insertChunk.Dispose();
        storeSequence.Dispose();
    }

    private static SqliteCommand Make(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, SqliteType Type)[] parameters)
    {
        var command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, type) in parameters) command.Parameters.Add(name, type);
            command.Prepare();
            return command;
        }
        catch { command.Dispose(); throw; }
    }
}
