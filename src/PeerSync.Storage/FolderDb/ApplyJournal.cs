using System.Collections.ObjectModel;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

/// <summary>Opaque engine recovery state, not a signed protocol message. Never log its payload/path.</summary>
[JsonConverter(typeof(ApplyIntentJsonConverter))]
public sealed class ApplyIntent
{
    public const int MaxPayloadBytes = 24 * 1024 * 1024;
    private readonly byte[] payload;
    public ApplyIntent(Guid id, string path, long? expectedLocalSequence, ReadOnlySpan<byte> payload)
    {
        if (id == Guid.Empty) throw new ArgumentException("Apply operation ID cannot be empty.", nameof(id));
        if (expectedLocalSequence < 0) throw new ArgumentOutOfRangeException(nameof(expectedLocalSequence));
        if (payload.Length is 0 or > MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload));
        Id = id; PathKey = PathRules.Key(path); ExpectedLocalSequence = expectedLocalSequence; this.payload = payload.ToArray();
    }
    public Guid Id { get; }
    [JsonIgnore] public string PathKey { get; }
    public long? ExpectedLocalSequence { get; }
    [JsonIgnore] public ReadOnlySpan<byte> Payload => payload;
    public override string ToString() => "Pending file application (path and recovery state redacted)";
}

internal sealed class ApplyIntentJsonConverter : JsonConverter<ApplyIntent>
{
    public override ApplyIntent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Recovery intents require explicit validated construction.");
    public override void Write(Utf8JsonWriter writer, ApplyIntent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("id", value.Id);
        if (value.ExpectedLocalSequence is { } sequence) writer.WriteNumber("expectedLocalSequence", sequence);
        writer.WriteEndObject();
    }
}

public sealed partial class FolderIndex
{
    private static readonly SearchValues<char> HistoryHex = SearchValues.Create("0123456789ABCDEF");
    /// <summary>Leave an unknown concurrent filesystem target untouched. Account
    /// for retained originals, withhold the stale local record until rescan, and
    /// remove the exact intent together. The caller has already verified archives.</summary>
    public Task<bool> DeferApplyAsync(ApplyIntent expected, IReadOnlyList<ArchiveRow> archives,
        CancellationToken cancellationToken = default) => DeferApplyCoreAsync(expected, archives, false, cancellationToken);

    /// <summary>Complete a deferred replacement after retaining a whole directory.
    /// Withhold its former live descendants and remove their needed work until rescan.</summary>
    public Task<bool> DeferMovedDirectoryApplyAsync(ApplyIntent expected, IReadOnlyList<ArchiveRow> archives,
        CancellationToken cancellationToken = default) => DeferApplyCoreAsync(expected, archives, true, cancellationToken);

    private Task<bool> DeferApplyCoreAsync(ApplyIntent expected, IReadOnlyList<ArchiveRow> archives,
        bool movedDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(archives);
        if (archives.Count > 1000) throw new ArgumentOutOfRangeException(nameof(archives));
        var retained = archives.Select(a => a with { FileHash = a.FileHash?.ToArray() }).ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in retained)
        {
            FolderMaps.Archive.Validate(archive);
            if (archive.Id != 0 || PathRules.Key(archive.OriginalPath) != expected.PathKey || !paths.Add(archive.ArchivePath))
                throw new ArgumentException("Invalid deferred archives.", nameof(archives));
        }
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var current = ReadIntent(connection, transaction, expected.Id);
            if (current is null) return false;
            RequireSameIntent(current, expected); RequireExpectedSequence(connection, transaction, expected);
            Archive.WriteBatch(connection, transaction, retained, cancellationToken);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE local_files SET flags=flags|4 WHERE path_key=$path;";
            command.Parameters.AddWithValue("$path", expected.PathKey); command.ExecuteNonQuery();
            if (movedDirectory) WithholdDescendants(connection, transaction, expected.PathKey);
            cancellationToken.ThrowIfCancellationRequested();
            RemoveIntent(connection, transaction, expected.Id); return true;
        }, cancellationToken);
    }

    /// <summary>Commit intent before destructive filesystem work. Null expected sequence means no local row.
    /// An exact retry is idempotent; another pending operation on the same path is rejected.</summary>
    public Task<bool> ReserveApplyAsync(ApplyIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var existing = ReadIntent(connection, transaction, intent.Id);
            if (existing is not null) { RequireSameIntent(existing, intent); return false; }
            RequireExpectedSequence(connection, transaction, intent);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO apply_intents(id,path_key,expected_local_seq,payload) VALUES($id,$path,$sequence,$payload);";
            BindIntent(command, intent); command.ExecuteNonQuery(); return true;
        }, cancellationToken);
    }

    /// <summary>Read durable pending operations before scanning/applying this folder after restart.</summary>
    public IEnumerable<ApplyIntent> ReadApplyIntents()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,path_key,expected_local_seq,payload FROM apply_intents ORDER BY path_key;";
        using var reader = command.ExecuteReader(); while (reader.Read()) yield return ReadIntent(reader);
    }

    /// <summary>Compare-and-swap recovery state, for example after choosing another collision slot.</summary>
    public Task<ApplyIntent> RewriteApplyAsync(ApplyIntent expected, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var updated = new ApplyIntent(expected.Id, expected.PathKey, expected.ExpectedLocalSequence, payload.Span);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireSameIntent(ReadIntent(connection, transaction, expected.Id), expected);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE apply_intents SET payload=$payload WHERE id=$id;";
            command.Parameters.AddWithValue("$id", expected.Id.ToString("N")); command.Parameters.AddWithValue("$payload", updated.Payload.ToArray());
            command.ExecuteNonQuery(); return updated;
        }, cancellationToken);
    }

    /// <summary>Only abandon after verifying no mutation occurred or completing a safe filesystem rollback.</summary>
    public Task<bool> AbandonApplyAsync(ApplyIntent expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var existing = ReadIntent(connection, transaction, expected.Id);
            if (existing is null) return false;
            RequireSameIntent(existing, expected); RemoveIntent(connection, transaction, expected.Id); return true;
        }, cancellationToken);
    }

    /// <summary>Commit metadata only after verifying successful filesystem application. Resolver is CPU-only,
    /// cannot re-enter this DB or retain its snapshot. Changed local state retains the intent for recovery.</summary>
    public Task<bool> CommitApplyAsync(ApplyIntent expected, LocalFileRow applied, IReadOnlyList<ArchiveRow> archives,
        Func<FolderPathSnapshot, NeededRow?> resolver, CancellationToken cancellationToken = default) =>
        CommitApplyCoreAsync(expected, applied, archives, resolver, false, cancellationToken);

    /// <summary>Commit replacement of a retained whole directory and atomically
    /// withhold former descendants/remove their needed work until rescan.</summary>
    public Task<bool> CommitMovedDirectoryApplyAsync(ApplyIntent expected, LocalFileRow applied, IReadOnlyList<ArchiveRow> archives,
        Func<FolderPathSnapshot, NeededRow?> resolver, CancellationToken cancellationToken = default) =>
        CommitApplyCoreAsync(expected, applied, archives, resolver, true, cancellationToken);

    private Task<bool> CommitApplyCoreAsync(ApplyIntent expected, LocalFileRow applied, IReadOnlyList<ArchiveRow> archives,
        Func<FolderPathSnapshot, NeededRow?> resolver, bool movedDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(archives); ArgumentNullException.ThrowIfNull(resolver);
        FolderMaps.Local.Validate(applied);
        var frozen = Freeze(applied); FolderMaps.Local.Validate(frozen);
        if (PathRules.Key(frozen.Path) != expected.PathKey || frozen.Record is null || frozen.Flags != LocalFileState.None)
            throw new ArgumentException("Applied state must match its pending path and contain a published record.", nameof(applied));
        if (archives.Count > 1000) throw new ArgumentOutOfRangeException(nameof(archives));
        ArchiveRow[] retained = archives.Select(a => a with { FileHash = a.FileHash?.ToArray() }).ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in retained)
        {
            FolderMaps.Archive.Validate(archive);
            if (archive.Id != 0 || PathRules.Key(archive.OriginalPath) != expected.PathKey || !paths.Add(archive.ArchivePath))
                throw new ArgumentException("New archives require this original path, distinct destinations and allocated IDs.", nameof(archives));
        }
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var current = ReadIntent(connection, transaction, expected.Id);
            if (current is null) return false; // A completion retry cannot allocate another sequence or archive row.
            RequireSameIntent(current, expected); RequireExpectedSequence(connection, transaction, expected);
            LocalFiles.WriteBatch(connection, transaction, [frozen], cancellationToken);
            Archive.WriteBatch(connection, transaction, retained, cancellationToken);
            using var needed = new NeededRewrite(connection, transaction, resolver);
            needed.Update(expected.PathKey);
            if (movedDirectory) WithholdDescendants(connection, transaction, expected.PathKey);
            cancellationToken.ThrowIfCancellationRequested();
            RemoveIntent(connection, transaction, expected.Id); return true;
        }, cancellationToken);
    }

    /// <summary>Withhold publication after failed recovery while retaining the
    /// exact intent and source sequence needed to retry safely.</summary>
    public Task<bool> WithholdApplySourceAsync(ApplyIntent expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var current = ReadIntent(connection, transaction, expected.Id);
            if (current is null) return false;
            RequireSameIntent(current, expected); RequireExpectedSequence(connection, transaction, expected);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE local_files SET flags=flags|4 WHERE path_key=$path;";
            command.Parameters.AddWithValue("$path", expected.PathKey); command.ExecuteNonQuery(); return true;
        }, cancellationToken);
    }

    /// <summary>Account for a reconstructed historical version without changing
    /// the current user file or its local row. The opaque authenticated location
    /// marker, optional archive and exact intent removal commit together.</summary>
    public Task<bool> CommitHistoryAsync(ApplyIntent expected, MetaRow marker, ArchiveRow? archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(marker);
        if (marker.Key.Length != 73 || !marker.Key.StartsWith("_history-", StringComparison.Ordinal)
            || marker.Key.AsSpan(9).ContainsAnyExcept(HistoryHex)
            || marker.Value is not { Length: > 0 and <= ApplyIntent.MaxPayloadBytes })
            throw new ArgumentException("Invalid history marker.", nameof(marker));
        var frozen = marker with { Value = marker.Value.ToArray() };
        ArchiveRow? retained = archive is null ? null : archive with { FileHash = archive.FileHash?.ToArray() };
        if (retained is not null)
        {
            FolderMaps.Archive.Validate(retained);
            if (retained.Id != 0 || PathRules.Key(retained.OriginalPath) != expected.PathKey)
                throw new ArgumentException("History archive does not match its source.", nameof(archive));
        }
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var current = ReadIntent(connection, transaction, expected.Id);
            if (current is null) return false;
            RequireSameIntent(current, expected); RequireExpectedSequence(connection, transaction, expected);
            if (retained is not null) Archive.WriteBatch(connection, transaction, [retained], cancellationToken);
            Meta.WriteBatch(connection, transaction, [frozen], cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); RemoveIntent(connection, transaction, expected.Id); return true;
        }, cancellationToken);
    }

    private static void WithholdDescendants(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        // Indexed BINARY range, not LIKE: percent/underscore are legal path characters.
        command.CommandText = "UPDATE local_files SET flags=flags|4 WHERE deleted=0 AND path_key >= $low AND path_key < $high;"
            + "DELETE FROM needed WHERE path_key >= $low AND path_key < $high;";
        command.Parameters.AddWithValue("$low", key + "/"); command.Parameters.AddWithValue("$high", key + "0");
        command.ExecuteNonQuery();
    }

    private static LocalFileRow Freeze(LocalFileRow row) => row with
    {
        FileHash = row.FileHash?.ToArray(), FileId = row.FileId?.ToArray(),
        Vector = new ReadOnlyDictionary<ulong, ulong>(row.Vector.ToDictionary()),
        IndexedChunks = row.IndexedChunks?.Select(c => c with { Hash = c.Hash.ToArray() }).ToArray(),
    };

    private static void RequireExpectedSequence(SqliteConnection connection, SqliteTransaction transaction, ApplyIntent intent)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT local_seq FROM local_files WHERE path_key=$path;"; command.Parameters.AddWithValue("$path", intent.PathKey);
        long? sequence = (long?)command.ExecuteScalar();
        if (sequence != intent.ExpectedLocalSequence) throw new InvalidOperationException("Local state changed during pending application.");
    }

    private static void RequireSameIntent(ApplyIntent? actual, ApplyIntent expected)
    {
        if (actual is null || actual.PathKey != expected.PathKey || actual.ExpectedLocalSequence != expected.ExpectedLocalSequence
            || !actual.Payload.SequenceEqual(expected.Payload)) throw new InvalidOperationException("Pending application state changed.");
    }

    private static ApplyIntent? ReadIntent(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,path_key,expected_local_seq,payload FROM apply_intents WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("N")); using var reader = command.ExecuteReader();
        return reader.Read() ? ReadIntent(reader) : null;
    }
    private static ApplyIntent ReadIntent(SqliteDataReader reader) => new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetFieldValue<byte[]>(3));
    private static void BindIntent(SqliteCommand command, ApplyIntent intent)
    {
        command.Parameters.AddWithValue("$id", intent.Id.ToString("N")); command.Parameters.AddWithValue("$path", intent.PathKey);
        command.Parameters.AddWithValue("$sequence", (object?)intent.ExpectedLocalSequence ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", intent.Payload.ToArray());
    }
    private static void RemoveIntent(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM apply_intents WHERE id=$id;"; command.Parameters.AddWithValue("$id", id.ToString("N")); command.ExecuteNonQuery();
    }
}
