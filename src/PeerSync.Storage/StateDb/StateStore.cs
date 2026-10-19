using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Keys;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Storage.StateDb;

/// <summary>Machine-wide metadata. Caller authorization/ACLs belong to the service; key writes protect internally.</summary>
public sealed class StateStore : IAsyncDisposable
{
    private readonly Db database;
    private readonly TimeProvider time;

    private StateStore(Db database, TimeProvider time)
    {
        this.database = database;
        this.time = time;
        Devices = new(database, FolderMaps.Devices);
        PendingApprovals = new(database, new("pending_approvals", ["folder_id", "device_id"],
        [new("folder_id", SqliteType.Blob, r => r.FolderId), new("device_id", SqliteType.Blob, r => r.DeviceId),
            new("name", SqliteType.Text, r => r.Name), new("requested_ticks", SqliteType.Integer, r => r.RequestedTicks)],
            r => new(r.GetFieldValue<byte[]>(0), r.GetFieldValue<byte[]>(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3)),
            r => { FolderMaps.RequireBytes(r.FolderId, 32); FolderMaps.RequireBytes(r.DeviceId, 32);
                if (r.Name?.Length > 256 || r.RequestedTicks < 0 || r.RequestedTicks > DateTime.MaxValue.Ticks)
                    throw new ArgumentException("Invalid pending approval metadata."); }));
    }

    public RowRepository<DeviceRow> Devices { get; }
    public RowRepository<PendingApproval> PendingApprovals { get; }

    public static async Task<StateStore> OpenAsync(string path, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        using var resource = typeof(StateStore).Assembly.GetManifestResourceStream("PeerSync.Storage.StateDb.Migrations.0001_initial.sql")
            ?? throw new InvalidOperationException("Missing state schema resource.");
        using var reader = new StreamReader(resource);
        string sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var database = await Db.OpenAsync(path, [new(1, sql)], cancellationToken).ConfigureAwait(false);
        try { return new(database, timeProvider ?? TimeProvider.System); }
        catch { await database.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<FolderDescriptor> RegisterFolderAsync(FolderKeys keys, string path, FolderSettings settings,
        string actorSid, bool paused = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(settings);
        ValidateActor(actorSid);
        string root = NormalizeRoot(path);
        string json = settings.ToJson(); // Freeze the caller's mutable host array before queuing.
        using var protectedKey = FolderKeyProtector.Protect(keys);
        byte[] id = protectedKey.ExportFolderId(), blob = protectedKey.ExportCiphertext();
        try
        {
            return await database.WriteAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO folders (id,path,path_key,access_level,protected_key,settings_json,paused)
                    VALUES ($id,$path,$pathkey,$access,$key,$settings,$paused)
                    ON CONFLICT(id) DO UPDATE SET path=excluded.path,path_key=excluded.path_key,
                      access_level=excluded.access_level,protected_key=excluded.protected_key,
                      settings_json=excluded.settings_json,paused=excluded.paused;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$path", root);
                command.Parameters.AddWithValue("$pathkey", root.ToUpperInvariant());
                command.Parameters.AddWithValue("$access", (int)protectedKey.Kind);
                command.Parameters.AddWithValue("$key", blob);
                command.Parameters.AddWithValue("$settings", json);
                command.Parameters.AddWithValue("$paused", paused ? 1 : 0);
                command.ExecuteNonQuery();
                Audit(connection, transaction, "folder-register", actorSid, id);
                return new FolderDescriptor(id.ToArray(), root, protectedKey.Kind, FolderSettings.FromJson(json), paused);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(blob); CryptographicOperations.ZeroMemory(id); }
    }

    public IEnumerable<FolderDescriptor> ListFolders()
    {
        using var connection = database.OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,path,access_level,settings_json,paused FROM folders ORDER BY path_key;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetFieldValue<byte[]>(0), reader.GetString(1),
            (FolderKeyKind)reader.GetInt32(2), FolderSettings.FromJson(reader.GetString(3)), reader.GetBoolean(4));
    }

    public async Task<FolderKeys> ReadFolderKeysAsync(byte[] folderId, string actorSid, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(folderId, 32);
        ValidateActor(actorSid);
        byte[] identity = folderId.ToArray();
        FolderKeys? opened = null;
        try
        {
            var result = await database.WriteAsync((connection, transaction) =>
            {
                byte[] blob;
                FolderKeyKind kind;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT protected_key,access_level FROM folders WHERE id=$id;";
                    command.Parameters.AddWithValue("$id", identity);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read()) throw new KeyNotFoundException("Unknown folder.");
                    blob = reader.GetFieldValue<byte[]>(0);
                    kind = (FolderKeyKind)reader.GetInt32(1);
                }
                try { opened = FolderKeyProtector.Unprotect(blob, identity, kind); }
                finally { CryptographicOperations.ZeroMemory(blob); }
                Audit(connection, transaction, "folder-key-read", actorSid, identity);
                return opened;
            }, cancellationToken).ConfigureAwait(false);
            opened = null; // Ownership transfers only after the read/audit transaction commits.
            return result;
        }
        finally { opened?.Dispose(); CryptographicOperations.ZeroMemory(identity); }
    }

    public Task<int> UpdateFolderAsync(byte[] folderId, FolderSettings settings, bool paused, string actorSid,
        CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(folderId, 32); ValidateActor(actorSid);
        ArgumentNullException.ThrowIfNull(settings);
        string json = settings.ToJson();
        byte[] identity = folderId.ToArray();
        return database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE folders SET settings_json=$settings,paused=$paused WHERE id=$id;";
            command.Parameters.AddWithValue("$settings", json); command.Parameters.AddWithValue("$paused", paused ? 1 : 0);
            command.Parameters.AddWithValue("$id", identity);
            int changed = command.ExecuteNonQuery();
            if (changed == 0) throw new KeyNotFoundException("Unknown folder.");
            Audit(connection, transaction, "folder-settings-update", actorSid, identity);
            return changed;
        }, cancellationToken);
    }

    /// <summary>Removes only database metadata and pending approvals; never touches the synced directory.</summary>
    public Task<int> RemoveFolderAsync(byte[] folderId, string actorSid, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(folderId, 32); ValidateActor(actorSid);
        byte[] identity = folderId.ToArray();
        return database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM folders WHERE id=$id;";
            command.Parameters.AddWithValue("$id", identity);
            int changed = command.ExecuteNonQuery();
            if (changed != 0) Audit(connection, transaction, "folder-remove", actorSid, identity);
            return changed;
        }, cancellationToken);
    }

    public IEnumerable<AuditEntry> ReadAudit()
    {
        using var connection = database.OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,occurred_ticks,action,actor_sid,folder_id,device_id FROM audit_log ORDER BY id;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<byte[]>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5));
    }

    public ValueTask DisposeAsync() => database.DisposeAsync();

    private void Audit(SqliteConnection connection, SqliteTransaction transaction, string action, string actorSid, byte[] folderId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO audit_log (occurred_ticks,action,actor_sid,folder_id) VALUES ($time,$action,$actor,$folder);";
        command.Parameters.AddWithValue("$time", time.GetUtcNow().UtcTicks);
        command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$actor", actorSid);
        command.Parameters.AddWithValue("$folder", folderId);
        command.ExecuteNonQuery();
    }

    private static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Folder root must be absolute.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void ValidateActor(string actorSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSid);
        if (actorSid.Length > 184)
            throw new ArgumentException("Invalid audit actor SID.", nameof(actorSid));
        try { _ = new SecurityIdentifier(actorSid); }
        catch (ArgumentException) { throw new ArgumentException("Invalid audit actor SID.", nameof(actorSid)); }
    }
}
