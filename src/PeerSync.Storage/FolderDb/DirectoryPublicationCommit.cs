using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryPublicationCommit(LocalFileRow Row, bool Changed)
{ public override string ToString() => "Directory publication commit (redacted)"; }

public sealed partial class FolderIndex
{
    /// <summary>Engine has verified the template/root and native guards and
    /// retained old versions. Null publication retains the exact signed row.
    /// The row/cache/queue/draft transition is one FULL transaction.</summary>
    public Task<DirectoryPublicationCommit> CommitDirectoryPublicationAsync(DirectoryPublication expected, DirectoryCaptureState capture,
        LocalFileRow? publication, long nowTicks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(capture);
        var queued = FreezePublication(expected); ValidatePublication(queued); var draft = FreezeCapture(capture);
        if (queued.ReconstructionOnly && publication is not null) throw new InvalidDataException("Reconstruction cannot replace its received signed row.");
        if (nowTicks is < 1 or > 3155378975999999999) throw new ArgumentOutOfRangeException(nameof(nowTicks));
        if (draft.Hash is not { Length: 32 } || draft.Length is null || draft.BuilderState is not null || draft.Accepted != draft.MemberCount
            || draft.ParentPath != queued.Path || draft.MemberCount != queued.ExpectedChildren || draft.InputEpoch is null
            || queued.KeysEpoch != draft.InputEpoch || !draft.RootId.AsSpan().SequenceEqual(queued.RootId) || !draft.NativeId.AsSpan().SequenceEqual(queued.NativeId))
            throw new ArgumentException("Directory publication proof is incomplete or inconsistent.", nameof(capture));
        var row = publication is null ? null : publication with { FileId = publication.FileId?.ToArray(), Vector = new Dictionary<ulong, ulong>(publication.Vector), IndexedChunks = [] };
        if (row is not null) ValidateDirectoryRow(row, queued);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var actual = RequirePublicationExpected(connection, transaction, queued); RequireCaptureExpected(connection, transaction, draft);
            if (HasIncompleteDirectoryCaseCopy(connection, transaction, PathRules.Key(queued.Path))) throw new InvalidOperationException("Directory case members are not installed.");
            RequireDirectoryCaptureEpoch(connection, transaction, draft.InputEpoch.Value);
            if (actual.Paused || actual.KeysEpoch != draft.InputEpoch) throw new InvalidOperationException("Directory publication is paused or its key proof changed.");
            string key = PathRules.Key(queued.Path);
            using (var children = connection.CreateCommand())
            {
                children.Transaction = transaction; children.CommandText = "SELECT EXISTS(SELECT 1 FROM directory_publications WHERE parent_key=$key);"; children.Parameters.AddWithValue("$key", key);
                if ((long)children.ExecuteScalar()! != 0) throw new InvalidOperationException("Child directory publication is pending.");
            }
            var prior = ReadCurrent();
            if (queued.ReconstructionOnly && (prior?.Record is null || !prior.Record.Body.SequenceEqual(queued.Template.Body) || !prior.Record.Signature.SequenceEqual(queued.Template.Signature)))
                throw new InvalidOperationException("Received reconstruction template changed.");
            if ((prior?.LocalSequence ?? 0) != queued.ExpectedLocalSequence) throw new InvalidOperationException("Directory publication source changed.");
            if (row is not null) LocalFiles.WriteBatch(connection, transaction, [row], cancellationToken);
            var current = row is null ? prior : ReadCurrent();
            if (current is null) throw new InvalidOperationException("Directory publication has no current row.");
            ValidateDirectoryRow(current, queued);
            byte[] source;
            using (var digest = Blake3.Hasher.New())
            { digest.Update("PeerSync case source v1"u8); digest.Update(current.Record!.Body); digest.Update(current.Record.Signature); source = digest.Finalize().AsSpan().ToArray(); }
            long epoch = ReadDirectoryCaptureEpoch(connection, transaction);
            using var cache = connection.CreateCommand(); cache.Transaction = transaction;
            cache.CommandText = "INSERT INTO directory_publication_cache(path_key,root_id,native_id,record_source_id,input_hash,expected_keys_hash,member_count,input_epoch,touched_ticks) "
                + "VALUES($key,$root,$native,$source,$input,$keys,$count,$epoch,$now) ON CONFLICT(path_key) DO UPDATE SET root_id=excluded.root_id,native_id=excluded.native_id,"
                + "record_source_id=excluded.record_source_id,input_hash=excluded.input_hash,expected_keys_hash=excluded.expected_keys_hash,member_count=excluded.member_count,input_epoch=excluded.input_epoch,touched_ticks=excluded.touched_ticks;";
            cache.Parameters.AddWithValue("$key", key); cache.Parameters.AddWithValue("$root", queued.RootId); cache.Parameters.AddWithValue("$native", queued.NativeId);
            cache.Parameters.AddWithValue("$source", source); cache.Parameters.AddWithValue("$input", draft.InputHash); cache.Parameters.AddWithValue("$count", draft.MemberCount);
            cache.Parameters.AddWithValue("$keys", queued.ExpectedKeysHash);
            cache.Parameters.AddWithValue("$epoch", epoch); cache.Parameters.AddWithValue("$now", nowTicks); cache.ExecuteNonQuery();
            using var release = connection.CreateCommand(); release.Transaction = transaction;
            release.CommandText = "DELETE FROM directory_captures WHERE parent_key=$key; DELETE FROM directory_publications WHERE path_key=$key AND version=$version;";
            release.Parameters.AddWithValue("$key", key); release.Parameters.AddWithValue("$version", queued.Version.ToString("N"));
            if (release.ExecuteNonQuery() != 2) throw new InvalidOperationException("Directory publication release changed.");
            return new DirectoryPublicationCommit(current, row is not null);

            LocalFileRow? ReadCurrent()
            {
                using var query = connection.CreateCommand(); query.Transaction = transaction;
                query.CommandText = "SELECT " + FolderMaps.LocalColumns + " FROM local_files WHERE path_key=$key;"; query.Parameters.AddWithValue("$key", key);
                using var reader = query.ExecuteReader(); return reader.Read() ? FolderMaps.ReadLocal(reader) : null;
            }
        }, cancellationToken);
    }
    private static void ValidateDirectoryRow(LocalFileRow row, DirectoryPublication queued)
    {
        FolderMaps.Local.Validate(row);
        if (row.Path != queued.Path || row.Type != 1 || row.Deleted || row.Size != 0 || row.Flags != LocalFileState.None || row.FileHash is not null
            || row.Attributes != queued.Attributes || row.Record is null || row.FileId is null || !row.FileId.AsSpan().SequenceEqual(queued.NativeId))
            throw new InvalidDataException("Directory publication row does not match its native template.");
    }
}
