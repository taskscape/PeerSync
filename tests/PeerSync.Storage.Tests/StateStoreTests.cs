using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Keys;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Storage.FolderDb;
using PeerSync.Storage.StateDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PeerSync-State-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(root, "peersync.db");
    private const string Actor = "S-1-5-18";
    private static readonly string[] RemovalAudit = ["folder-register", "folder-settings-update", "folder-remove"];
    public StateStoreTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task EveryRoleIsProtectedRoundTrippedAndAuditedWithoutKeyMaterialInListings()
    {
        await using var state = await StateStore.OpenAsync(DatabasePath, cancellationToken: TestContext.Current.CancellationToken);
        using var rw = FolderKeys.CreateNew();
        using var ro = FolderKeys.FromText(rw.ExportReadOnlyText());
        using var encrypted = FolderKeys.FromText(rw.ExportEncryptedText());
        foreach (var keys in new[] { rw, ro, encrypted })
        {
            var folder = await state.RegisterFolderAsync(keys, Path.Combine(root, "share"), new(), Actor, cancellationToken: TestContext.Current.CancellationToken);
            folder.Access.ShouldBe(keys.Kind);
            using var opened = await state.ReadFolderKeysAsync(folder.Id, Actor, TestContext.Current.CancellationToken);
            string.Equals(opened.ExportText(), keys.ExportText(), StringComparison.Ordinal).ShouldBeTrue();
            var listing = state.ListFolders().Single();
            listing.Access.ShouldBe(keys.Kind);
            JsonSerializer.Serialize(listing).Contains("protected", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
            JsonSerializer.Serialize(listing).Contains("\"Id\"", StringComparison.Ordinal).ShouldBeFalse();
        }
        state.ReadAudit().Count(a => a.Action == "folder-key-read").ShouldBe(3);
        state.ReadAudit().ShouldAllBe(a => a.ActorSid == Actor && a.OccurredTicks > 0);
        byte[] secret = rw.ReadWriteSecret!;
        byte[] text = Encoding.ASCII.GetBytes(rw.ExportReadWriteText());
        try
        {
            // Register RW again so the active row contains the actual private seed.
            await state.RegisterFolderAsync(rw, Path.Combine(root, "share"), new(), Actor, cancellationToken: TestContext.Current.CancellationToken);
            foreach (string file in Directory.GetFiles(root, "peersync.db*"))
            {
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                input.CopyTo(copy);
                byte[] bytes = copy.ToArray();
                bytes.AsSpan().IndexOf(secret).ShouldBe(-1);
                bytes.AsSpan().IndexOf(text).ShouldBe(-1);
            }
        }
        finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(text); }
    }

    [Fact]
    public async Task DeviceAndApprovalStatePersistsAndRemovingFolderNeverDeletesItsFiles()
    {
        using var keys = FolderKeys.CreateNew();
        byte[] peer = Enumerable.Repeat((byte)7, 32).ToArray();
        string share = Path.Combine(root, "share");
        Directory.CreateDirectory(share);
        string marker = Path.Combine(share, "keep.txt");
        await File.WriteAllTextAsync(marker, "owned test content", TestContext.Current.CancellationToken);
        byte[] id;
        await using (var state = await StateStore.OpenAsync(DatabasePath, cancellationToken: TestContext.Current.CancellationToken))
        {
            var folder = await state.RegisterFolderAsync(keys, share, new(), Actor, cancellationToken: TestContext.Current.CancellationToken);
            id = folder.Id;
            await state.Devices.UpsertAsync([new(peer, "Second PC", 638000000000000000, 4, DeviceApproval.Approved)], TestContext.Current.CancellationToken);
            await state.PendingApprovals.UpsertAsync([new(id, peer, "Second PC", 638000000000000000)], TestContext.Current.CancellationToken);
        }
        await using var reopened = await StateStore.OpenAsync(DatabasePath, cancellationToken: TestContext.Current.CancellationToken);
        reopened.Devices.Find(peer)!.Name.ShouldBe("Second PC");
        reopened.PendingApprovals.Find(id, peer).ShouldNotBeNull();
        await reopened.UpdateFolderAsync(id, new() { KeepBothCopies = true, RequireApproval = true }, true, Actor, TestContext.Current.CancellationToken);
        reopened.ListFolders().Single().Paused.ShouldBeTrue();
        reopened.ListFolders().Single().Settings.KeepBothCopies.ShouldBeTrue();
        await reopened.RemoveFolderAsync(id, Actor, TestContext.Current.CancellationToken);
        reopened.ListFolders().ShouldBeEmpty(); reopened.PendingApprovals.Count().ShouldBe(0);
        File.Exists(marker).ShouldBeTrue();
        reopened.ReadAudit().Select(a => a.Action).ShouldBe(RemovalAudit);
    }

    [Fact]
    public async Task DuplicateWindowsRootRollsBackWithoutReplacingTheOriginalFolder()
    {
        await using var state = await StateStore.OpenAsync(DatabasePath, cancellationToken: TestContext.Current.CancellationToken);
        using var first = FolderKeys.CreateNew(); using var second = FolderKeys.CreateNew();
        string share = Path.Combine(root, "Folder");
        await state.RegisterFolderAsync(first, share, new(), Actor, cancellationToken: TestContext.Current.CancellationToken);
        await Should.ThrowAsync<SqliteException>(() => state.RegisterFolderAsync(second, share.ToUpperInvariant() + Path.DirectorySeparatorChar, new(), Actor, cancellationToken: TestContext.Current.CancellationToken));
        state.ListFolders().Count().ShouldBe(1); state.ReadAudit().Count().ShouldBe(1);
        using var opened = await state.ReadFolderKeysAsync(first.FolderId(), Actor, TestContext.Current.CancellationToken);
        opened.PublicKey.SequenceEqual(first.PublicKey).ShouldBeTrue();
    }

    [Fact]
    public async Task TamperedProtectedBlobFailsWithoutReturningAKey()
    {
        await using var state = await StateStore.OpenAsync(DatabasePath, cancellationToken: TestContext.Current.CancellationToken);
        using var keys = FolderKeys.CreateNew();
        var folder = await state.RegisterFolderAsync(keys, Path.Combine(root, "share"), new(), Actor, cancellationToken: TestContext.Current.CancellationToken);
        using (var connection = Direct())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE folders SET protected_key=$key WHERE id=$id;";
            command.Parameters.AddWithValue("$key", new byte[] { 1, 2, 3 });
            command.Parameters.AddWithValue("$id", folder.Id); command.ExecuteNonQuery();
        }
        await Should.ThrowAsync<CryptographicException>(() => state.ReadFolderKeysAsync(folder.Id, Actor, TestContext.Current.CancellationToken));
        state.ReadAudit().Count(a => a.Action == "folder-key-read").ShouldBe(0);
    }

    [Fact]
    public async Task KeyReadCannotEscapeIfItsAuditTransactionFails()
    {
        await using var state = await StateStore.OpenAsync(DatabasePath, cancellationToken: TestContext.Current.CancellationToken);
        using var keys = FolderKeys.CreateNew();
        var folder = await state.RegisterFolderAsync(keys, Path.Combine(root, "share"), new(), Actor, cancellationToken: TestContext.Current.CancellationToken);
        using (var connection = Direct())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_key_audit BEFORE INSERT ON audit_log WHEN NEW.action='folder-key-read' BEGIN SELECT RAISE(ABORT,'synthetic audit failure'); END;";
            command.ExecuteNonQuery();
        }
        await Should.ThrowAsync<SqliteException>(() => state.ReadFolderKeysAsync(folder.Id, Actor, TestContext.Current.CancellationToken));
        state.ReadAudit().Count().ShouldBe(1);
    }

    [Fact]
    public async Task CancellationAtTheKeyAuditBoundaryReturnsNoKeyAndRollsBackAudit()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var time = new CancellingTime(cancellation);
        await using var state = await StateStore.OpenAsync(DatabasePath, time, TestContext.Current.CancellationToken);
        using var keys = FolderKeys.CreateNew();
        var folder = await state.RegisterFolderAsync(keys, Path.Combine(root, "share"), new(), Actor, cancellationToken: TestContext.Current.CancellationToken);
        time.Enabled = true;
        await Should.ThrowAsync<OperationCanceledException>(() => state.ReadFolderKeysAsync(folder.Id, Actor, cancellation.Token));
        state.ReadAudit().Count().ShouldBe(1);
    }

    private SqliteConnection Direct()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class CancellingTime(CancellationTokenSource cancellation) : TimeProvider
    {
        public bool Enabled { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            if (Enabled) cancellation.Cancel();
            return DateTimeOffset.UtcNow;
        }
    }
}
