using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    /// <summary>Directory installation coverage only. A false result does not
    /// establish ownership, publication or completion of a case source.</summary>
    public bool HasIncompleteDirectoryCaseCopy(string path)
    {
        using var connection = database.OpenReader(); return HasIncompleteDirectoryCaseCopy(connection, null, PathRules.Key(path));
    }
    private static bool HasIncompleteDirectoryCaseCopy(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "WITH RECURSIVE ancestors(key) AS (VALUES($path) UNION ALL SELECT rtrim(rtrim(key,replace(key,'/','')),'/') FROM ancestors WHERE instr(key,'/')>0), "
            + "affected(id,root) AS (SELECT c.source_id,c.installed_file_id FROM ancestors a JOIN case_preservations c ON c.destination_key=a.key WHERE c.completed=0 "
            + "UNION ALL SELECT source_id,installed_file_id FROM case_preservations WHERE completed=0 AND destination_key >= $low AND destination_key < $high) "
            + "SELECT EXISTS(SELECT 1 FROM affected a JOIN directory_graphs g ON g.id=a.id LEFT JOIN directory_case_progress p ON p.source_id=a.id AND p.root_file_id=a.root WHERE coalesce(p.exhausted,0)=0);";
        command.Parameters.AddWithValue("$path", key); command.Parameters.AddWithValue("$low", key + '/'); command.Parameters.AddWithValue("$high", key + '0');
        return (long)command.ExecuteScalar()! != 0;
    }
}
