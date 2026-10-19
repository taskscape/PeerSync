using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Native;
using PeerSync.Engine.Hashing;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class HistoryRetentionTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-HistoryRetention-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static readonly byte[] Device = Enumerable.Repeat((byte)1, 32).ToArray();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    public HistoryRetentionTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task NewHistoryReusingAnOlderArchiveReceivesANewRetentionWindow()
    {
        File.WriteAllText(Disk("current"), "original bytes"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
        var previous = index.LocalFiles.Find("current")!; Directory.CreateDirectory(Disk(".psp/archive")); File.WriteAllText(Disk(".psp/archive/old"), "original bytes");
        var archivedAt = DateTime.UtcNow.AddDays(-31).Ticks;
        await index.Archive.UpsertAsync([new(0, "current", ".psp/archive/old", archivedAt, previous.Size, previous.FileHash)], Token);
        File.WriteAllText(Disk("current"), "new current bytes");
        using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
        index.Archive.Count().ShouldBe(1); index.Archive.ReadAll().Single().ArchivePath.ShouldBe(".psp/archive/old");
        var result = await new ArchiveManager(Root, index).PruneAsync(force: true, cancellationToken: Token);
        result.Removed.ShouldBe(0); File.ReadAllText(Disk(".psp/archive/old")).ShouldBe("original bytes");
        index.Archive.ReadAll().Single().ArchivedTicks.ShouldBe(archivedAt);
    }

    [Fact]
    public async Task CapacityOrderingUsesNewerHistoryRetentionInsteadOfTheOldPhysicalArchiveDate()
    {
        await using var index = await PrepareReusedAsync(); var protectedRow = index.Archive.ReadAll().Single();
        byte[] other = "other version"u8.ToArray(); File.WriteAllBytes(Disk(".psp/archive/other"), other);
        await index.Archive.UpsertAsync([new(0, "other", ".psp/archive/other", DateTime.UtcNow.AddDays(-1).Ticks, other.Length, Hash(other))], Token);
        var manager = new ArchiveManager(Root, index) { CapacityOverride = () => (ulong)Math.Max(protectedRow.Size, other.Length) * 10 };
        var result = await manager.PruneAsync(force: true, cancellationToken: Token);
        result.Removed.ShouldBe(1); index.Archive.ReadAll().Single().Id.ShouldBe(protectedRow.Id);
        File.ReadAllText(Disk(protectedRow.ArchivePath)).ShouldBe("original bytes"); File.Exists(Disk(".psp/archive/other")).ShouldBeFalse();
        index.ReadArchiveRetirements().Single().Reason.ShouldBe(ArchiveRetirementReason.Capacity);
    }

    [Fact]
    public async Task APreparedRetirementCannotTakeANewHistoryHoldAndAnotherCopyIsRetained()
    {
        File.WriteAllText(Disk("current"), "original bytes"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
        var previous = index.LocalFiles.Find("current")!; byte[] nextBytes = "new current bytes"u8.ToArray(); File.WriteAllBytes(Disk("current"), nextBytes);
        var next = previous with { FileHash = Hash(nextBytes), Size = nextBytes.Length };
        ArchiveRow original;
        using (await FolderOperationGate.AcquireAsync(index, Token))
        using (var tree = new SafeFileTree(Root))
        {
            var history = new SnapshotHistory(index, keys, Device, new()); await history.EnsureAsync(tree, previous, next, null, Token);
            original = index.Archive.ReadAll().Single();
            using (var parent = tree.Directory(".psp/archive", privatePath: true))
            using (var file = parent.OpenFile(Path.GetFileName(original.ArchivePath), mutation: true))
                await index.ReserveArchiveRetirementAsync(new(Guid.NewGuid(), original, tree.Identity.ToArray(), BlockCaptureProof.Identity(file), ArchiveRetirementReason.Capacity, DateTime.UtcNow.Ticks), Token);
            await history.EnsureAsync(tree, previous, next, null, Token); index.Archive.Count().ShouldBe(2);
        }
        (await new ArchiveManager(Root, index).PruneAsync(force: true, cancellationToken: Token)).Removed.ShouldBe(1);
        var retained = index.Archive.ReadAll().Single(); retained.Id.ShouldNotBe(original.Id); File.ReadAllText(Disk(retained.ArchivePath)).ShouldBe("original bytes");
        File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
    }

    [Fact]
    public async Task MissingLegacyAccountingOrChangedMarkerDefersPruningBeforeAnyDeletion()
    {
        await using var index = await PrepareReusedAsync(); var marker = index.Meta.ReadAll().Single(m => m.Key.StartsWith("_history-", StringComparison.Ordinal));
        byte[] altered = marker.Value!.ToArray(); altered[^1] ^= 1; await index.Meta.UpsertAsync([marker with { Value = altered }], Token);
        index.HasUnaccountedHistory(index.Meta.Find("_root-identity")!.Value!).ShouldBeTrue();
        var result = await new ArchiveManager(Root, index).PruneAsync(force: true, cancellationToken: Token);
        result.WaitingForRecovery.ShouldBeTrue(); result.Removed.ShouldBe(0); File.ReadAllText(Disk(".psp/archive/old")).ShouldBe("original bytes");
    }

    [Fact]
    public async Task FailedRetentionAccountingKeepsTheRecoveryIntentAndOldBytesForRetry()
    {
        File.WriteAllText(Disk("current"), "original bytes"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
        Sql("CREATE TRIGGER reject_hold BEFORE INSERT ON history_archive_links BEGIN SELECT RAISE(ABORT,'test retention failure'); END;");
        File.WriteAllText(Disk("current"), "new current bytes");
        using (var scanner = new Scanner(Root, index, keys, Device))
            await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(() => scanner.ScanAsync(Token));
        index.ReadApplyIntents().Count().ShouldBe(1); index.Archive.Count().ShouldBe(0);
        index.LocalFiles.Find("current")!.FileHash.ShouldBe(Hash("original bytes"u8));
        Sql("DROP TRIGGER reject_hold;");
        using (var scanner = new Scanner(Root, index, keys, Device)) (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty();
        index.ReadApplyIntents().ShouldBeEmpty(); index.Archive.Count().ShouldBe(1);
        index.HasUnaccountedHistory(index.Meta.Find("_root-identity")!.Value!).ShouldBeFalse(); File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
    }

    private async Task<FolderIndex> PrepareReusedAsync()
    {
        File.WriteAllText(Disk("current"), "original bytes"); var index = await FolderIndex.OpenAsync(Database, Token);
        try
        {
            using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
            var row = index.LocalFiles.Find("current")!; Directory.CreateDirectory(Disk(".psp/archive")); File.WriteAllText(Disk(".psp/archive/old"), "original bytes");
            await index.Archive.UpsertAsync([new(0, "current", ".psp/archive/old", DateTime.UtcNow.AddDays(-31).Ticks, row.Size, row.FileHash)], Token);
            File.WriteAllText(Disk("current"), "new current bytes"); using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token); return index;
        }
        catch { await index.DisposeAsync(); throw; }
    }

    private static byte[] Hash(ReadOnlySpan<byte> bytes) => Blake3.Hasher.Hash(bytes).AsSpan().ToArray();
    private void Sql(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-HistoryRetention-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
