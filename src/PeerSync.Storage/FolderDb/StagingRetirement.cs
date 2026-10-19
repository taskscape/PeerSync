using System.Text;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record StagingCleanupPage(IReadOnlyList<StagedObject> Objects, Guid? Cursor, bool MoreWork);

public sealed partial class FolderIndex
{
    public const long StagingGraceTicks = TimeSpan.TicksPerDay;

    /// <summary>Count every parked witness, including needed/fresh/locked rows.
    /// Caller owns the folder gate through processing and cursor advancement.</summary>
    public StagingCleanupPage ReadStagingCleanupPage(byte[] rootId, int limit = 1000)
    {
        FolderMaps.RequireBytes(rootId,24); if(limit is <1 or >1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection=database.OpenReader(); using var transaction=connection.BeginTransaction(deferred:true); RequireBlockReferenceRoot(connection,transaction,rootId);
        using var command=connection.CreateCommand(); command.Transaction=transaction;
        command.CommandText="SELECT last_id FROM staging_cleanup_state WHERE root_id=$root;"; command.Parameters.AddWithValue("$root",rootId);
        string? cursor=command.ExecuteScalar() as string;
        command.CommandText="SELECT "+StagingColumns+" FROM staged_objects WHERE root_id=$root AND state=0 AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit;";
        command.Parameters.AddWithValue("$after",(object?)cursor??DBNull.Value); command.Parameters.AddWithValue("$limit",limit);
        var rows=new List<StagedObject>(); using(var reader=command.ExecuteReader()) while(reader.Read()) rows.Add(ReadStagedObject(reader));
        return new(rows.AsReadOnly(),cursor is null?null:Guid.ParseExact(cursor,"N"),rows.Count==limit);
    }

    public Task AdvanceStagingCleanupCursorAsync(byte[] rootId,Guid? expected,Guid? next,CancellationToken cancellationToken=default)
    {
        FolderMaps.RequireBytes(rootId,24); byte[] root=rootId.ToArray();
        if(expected==Guid.Empty||next==Guid.Empty) throw new ArgumentException("Invalid staging cursor.");
        return database.WriteDurableAsync((connection,transaction)=>
        {
            RequireBlockReferenceRoot(connection,transaction,root); using var command=connection.CreateCommand(); command.Transaction=transaction;
            command.CommandText="SELECT last_id FROM staging_cleanup_state WHERE root_id=$root;"; command.Parameters.AddWithValue("$root",root);
            string? actual=command.ExecuteScalar() as string;
            if(actual!=expected?.ToString("N")) throw new InvalidOperationException("Staging cleanup cursor changed.");
            command.CommandText="INSERT INTO staging_cleanup_state(root_id,last_id) VALUES($root,$next) ON CONFLICT(root_id) DO UPDATE SET last_id=excluded.last_id;";
            command.Parameters.AddWithValue("$next",(object?)next?.ToString("N")??DBNull.Value); command.ExecuteNonQuery(); return true;
        },cancellationToken);
    }

    /// <summary>Revoke destructive permissions before cold apply recovery or
    /// scanning. Keep all witnesses; future expiry must use freshly rebuilt needs.</summary>
    public Task<int> RecoverStagingRetirementsAsync(byte[] rootId,CancellationToken cancellationToken=default)
    {
        FolderMaps.RequireBytes(rootId,24); byte[] root=rootId.ToArray();
        return database.WriteDurableAsync((connection,transaction)=>
        {
            RequireBlockReferenceRoot(connection,transaction,root); using var command=connection.CreateCommand(); command.Transaction=transaction;
            command.CommandText="SELECT count(*) FROM staging_retirements r JOIN staged_objects s ON s.id=r.staged_id WHERE s.root_id=$root;";
            command.Parameters.AddWithValue("$root",root); int count=checked((int)(long)command.ExecuteScalar()!);
            if(count>1000) throw new InvalidDataException("Staging retirement bound exceeded.");
            command.CommandText="UPDATE staged_objects SET state=0 WHERE root_id=$root AND state=1; DELETE FROM staging_retirements WHERE staged_id IN (SELECT id FROM staged_objects WHERE root_id=$root);";
            command.ExecuteNonQuery(); return count;
        },cancellationToken);
    }

    public bool HasStagingRetirements()
    {
        using var connection=database.OpenReader(); using var command=connection.CreateCommand(); command.CommandText="SELECT EXISTS(SELECT 1 FROM staging_retirements);";
        return (long)command.ExecuteScalar()! != 0;
    }

    /// <summary>Caller holds matching native guards and filesystem/reference
    /// gates. FULL permission commit must finish before any native removal.</summary>
    public Task<bool> ReserveStagingRetirementAsync(StagedObject expected,long nowTicks,CancellationToken cancellationToken=default)
    {
        var row=FreezeStaging(expected); RequireStagingNow(nowTicks);
        if(row.State!=StagingObjectState.Parked) throw new ArgumentException("Only parked staging can expire.",nameof(expected));
        return database.WriteDurableAsync((connection,transaction)=>
        {
            RequireBlockReferenceRoot(connection,transaction,row.RootId); var actual=ReadStagingById(connection,transaction,row.Id);
            if(actual is null||!SameStaging(actual,row)||!StagingMayExpire(connection,transaction,row,nowTicks)) return false;
            using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText="SELECT count(*) FROM staging_retirements;";
            if((long)command.ExecuteScalar()!>=1000) return false;
            command.CommandText="UPDATE staged_objects SET state=1 WHERE id=$id; INSERT INTO staging_retirements VALUES($id,$now);";
            command.Parameters.AddWithValue("$id",row.Id.ToString("N")); command.Parameters.AddWithValue("$now",nowTicks); command.ExecuteNonQuery(); return true;
        },cancellationToken);
    }

    /// <summary>Revoke permission, optionally protecting a changed object.
    /// This never grants permission or deletes a filesystem object.</summary>
    public Task<bool> CancelStagingRetirementAsync(StagedObject expected,bool protect=false,CancellationToken cancellationToken=default)
    {
        var row=FreezeStaging(expected);
        return database.WriteDurableAsync((connection,transaction)=>
        {
            RequireBlockReferenceRoot(connection,transaction,row.RootId); var actual=ReadStagingById(connection,transaction,row.Id);
            if(actual is null||actual.State==StagingObjectState.Protected||!SameStaging(actual with { State=row.State },row)) return false;
            using var command=connection.CreateCommand(); command.Transaction=transaction;
            command.CommandText="UPDATE staged_objects SET state=$state WHERE id=$id; DELETE FROM staging_retirements WHERE staged_id=$id;";
            command.Parameters.AddWithValue("$state",protect?2:0); command.Parameters.AddWithValue("$id",row.Id.ToString("N")); command.ExecuteNonQuery(); return true;
        },cancellationToken);
    }

    /// <summary>Caller proves exact-object absence while holding filesystem and
    /// reference gates. No native deletion is performed by this method.</summary>
    public Task<bool> CompleteMissingStagingAsync(StagedObject expected,long nowTicks,CancellationToken cancellationToken=default)
    {
        var row=FreezeStaging(expected); RequireStagingNow(nowTicks);
        return database.WriteDurableAsync((connection,transaction)=>
        {
            RequireBlockReferenceRoot(connection,transaction,row.RootId); var actual=ReadStagingById(connection,transaction,row.Id);
            if(actual is null) return true;
            if(actual.State==StagingObjectState.Protected||!SameStaging(actual with { State=row.State },row)||!StagingMayExpire(connection,transaction,row,nowTicks)) return false;
            using var command=connection.CreateCommand(); command.Transaction=transaction;
            // Remove only the exact legacy alias. Unknown metadata remains untouched.
            string key="_apply-staging-"+Convert.ToHexString(Blake3.Hasher.Hash(Encoding.UTF8.GetBytes(PathRules.Key(row.TargetPath))).AsSpan())+(row.IsDirectory?"-directory":"");
            byte[] value=[..row.RootId,..row.FileId,..Encoding.UTF8.GetBytes(row.TemporaryPath)];
            command.CommandText="DELETE FROM meta WHERE key=$key AND value=$value; DELETE FROM staged_objects WHERE id=$id;";
            command.Parameters.AddWithValue("$key",key); command.Parameters.AddWithValue("$value",value); command.Parameters.AddWithValue("$id",row.Id.ToString("N"));
            command.ExecuteNonQuery(); return true;
        },cancellationToken);
    }

    private static void RequireStagingNow(long ticks)
    { if(ticks is <=0||ticks>DateTime.MaxValue.Ticks) throw new ArgumentOutOfRangeException(nameof(ticks)); }
    private static bool StagingMayExpire(SqliteConnection connection,SqliteTransaction transaction,StagedObject row,long now)
    {
        if(now<row.ParkedTicks||now-row.ParkedTicks<StagingGraceTicks) return false;
        using var command=connection.CreateCommand(); command.Transaction=transaction;
        command.CommandText="SELECT EXISTS(SELECT 1 FROM needed WHERE path_key=$path) OR EXISTS(SELECT 1 FROM apply_intents) "
            + "OR EXISTS(SELECT 1 FROM case_preservations WHERE completed=0 AND (path_key=$path OR destination_key=$path));";
        command.Parameters.AddWithValue("$path",PathRules.Key(row.TargetPath)); return (long)command.ExecuteScalar()! == 0;
    }
    private static StagedObject? ReadStagingById(SqliteConnection connection,SqliteTransaction transaction,Guid id)
    {
        using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText="SELECT "+StagingColumns+" FROM staged_objects WHERE id=$id;";
        command.Parameters.AddWithValue("$id",id.ToString("N")); using var reader=command.ExecuteReader(); return reader.Read()?ReadStagedObject(reader):null;
    }
    private static bool SameStaging(StagedObject left,StagedObject right)=>left.Id==right.Id&&left.State==right.State&&left.RootId.AsSpan().SequenceEqual(right.RootId)
        &&left.FileId.AsSpan().SequenceEqual(right.FileId)&&left.TargetPath==right.TargetPath&&left.TemporaryPath==right.TemporaryPath&&left.IsDirectory==right.IsDirectory
        &&left.ParkedTicks==right.ParkedTicks&&left.Length==right.Length&&left.ChangeFileTime==right.ChangeFileTime&&StagingHashEqual(left.ContentHash,right.ContentHash);
}
