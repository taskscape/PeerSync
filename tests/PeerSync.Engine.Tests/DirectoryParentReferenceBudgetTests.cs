using PeerSync.Engine.Hashing;
using PeerSync.Engine.Scanning;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed partial class DirectorySnapshotCaptureTests
{
    [Fact]
    public async Task ExpiringAFullParentProofPageDoesNotSpendANegativeGraphRetirementBudget()
    {
        await using var index = await FolderIndex.OpenAsync(Database, Token); using var scanner = new Scanner(Root, index, keys, device); await scanner.ScanAsync(Token);
        byte[] root = index.Meta.Find("_root-identity")!.Value!;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            connection.Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO directory_parent_proofs(path_key,root_id,native_id,metadata_hash,input_hash,input_epoch,queue_version,local_seq,local_source_id,local_hash,local_length,touched_ticks) "
                + "VALUES($path,$root,zeroblob(16),zeroblob(32),zeroblob(32),0,$version,0,zeroblob(32),zeroblob(32),12,1);";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$version", Guid.NewGuid().ToString("N")); command.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            for (int n = 0; n < 1000; n++) { command.Parameters["$path"].Value = "ORPHAN-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture); command.ExecuteNonQuery(); }
            transaction.Commit();
        }
        (await new BlockReferences(Root, index, keys, device).RefreshAsync(Token)).AllKnownSourcesCovered.ShouldBeTrue();
        index.ReadDirectoryParentProof("ORPHAN-0").ShouldBeNull(); index.ReadDirectoryParentProof("ORPHAN-999").ShouldBeNull();
    }
}
