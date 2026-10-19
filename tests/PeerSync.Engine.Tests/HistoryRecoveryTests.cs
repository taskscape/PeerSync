using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Scanning;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Engine.Tests;

public sealed class HistoryRecoveryTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-HistoryBootstrap-" + Guid.NewGuid().ToString("N"));
    private readonly FolderKeys keys = FolderKeys.CreateNew();
    private static readonly byte[] Device = Enumerable.Repeat((byte)1, 32).ToArray();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(fixture, "tree");
    private string Database => Path.Combine(fixture, "index.db");
    public HistoryRecoveryTests() => Directory.CreateDirectory(Root);

    [Fact]
    public async Task ExistingGuardedHistoryIsAccountedWithoutChangingItsBytesTimeOrCurrentVersion()
    {
        await using var index = await PrepareAsync(); var marker = Marker(index); var row = index.Archive.ReadAll().Single();
        var current = index.LocalFiles.Find("current")!; DateTime mtime = File.GetLastWriteTimeUtc(Disk(row.ArchivePath));
        var result = await Recovery(index).BootstrapAsync(Token); result.Verified.ShouldBe(1); result.Accounted.ShouldBe(1);
        index.HasUnaccountedHistory(index.Meta.Find("_root-identity")!.Value!).ShouldBeFalse();
        Marker(index).Value.ShouldBe(marker.Value); File.GetLastWriteTimeUtc(Disk(row.ArchivePath)).ShouldBe(mtime);
        index.LocalFiles.Find("current")!.Record!.Body.ToArray().ShouldBe(current.Record!.Body.ToArray());
        File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
        (await Recovery(index).BootstrapAsync(Token)).Accounted.ShouldBe(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("replaced")]
    public async Task MissingOrChangedLegacyCopiesReconstructWithoutOverwritingUnknownOrCurrentData(string change)
    {
        await using var index = await PrepareAsync(); var marker = Marker(index); var state = HistoryState.DecodeMarker(marker, keys);
        string path = Disk(state.Destination);
        if (change == "missing") File.Delete(path);
        else { if (change == "replaced") File.Move(path, path + ".unknown-old"); File.WriteAllText(path, "unknown archive content"); }
        var result = await Recovery(index).BootstrapAsync(Token); result.Accounted.ShouldBe(1);
        var recovered = HistoryState.DecodeMarker(Marker(index), keys); recovered.ArchivedTicks.ShouldBe(state.ArchivedTicks);
        recovered.Source.Record.Body.ToArray().ShouldBe(state.Source.Record.Body.ToArray());
        recovered.Destination.ShouldNotBe(state.Destination); File.ReadAllText(Disk(recovered.Destination)).ShouldBe("original bytes");
        File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
        if (change != "missing") File.ReadAllText(path).ShouldBe("unknown archive content");
        if (change == "replaced") File.ReadAllText(path + ".unknown-old").ShouldBe("original bytes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSignatureOrForeignRootCannotBecomeAccountedOrChangeCurrentFiles(bool foreignRoot)
    {
        await using var index = await PrepareAsync(); var marker = Marker(index); byte[] altered = marker.Value!.ToArray();
        altered[foreignRoot ? 9 : 73] ^= 1; await index.Meta.UpsertAsync([marker with { Value = altered }], Token);
        if (foreignRoot) await Should.ThrowAsync<IOException>(() => Recovery(index).BootstrapAsync(Token));
        else await Should.ThrowAsync<CryptographicException>(() => Recovery(index).BootstrapAsync(Token));
        index.HasUnaccountedHistory(index.Meta.Find("_root-identity")!.Value!).ShouldBeTrue(); index.ReadApplyIntents().ShouldBeEmpty();
        File.ReadAllText(Disk("current")).ShouldBe("new current bytes"); index.Archive.Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("history-reserved")]
    [InlineData("history-created")]
    [InlineData("history-prepared")]
    [InlineData("history-installed")]
    [InlineData("history-committed")]
    public async Task ReconstructionResumesThroughOrdinaryApplicationRecoveryAfterEveryBoundary(string boundary)
    {
        await using (var index = await PrepareAsync())
        {
            File.Delete(Disk(HistoryState.DecodeMarker(Marker(index), keys).Destination));
            var recovery = new HistoryRecovery(Root, index, keys, Device) { Checkpoint = stage => { if (stage == boundary) throw new InvalidOperationException("process boundary"); } };
            await Should.ThrowAsync<InvalidOperationException>(() => recovery.BootstrapAsync(Token));
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token);
        if (reopened.ReadApplyIntents().Any()) (await Recovery(reopened).BootstrapAsync(Token)).WaitingForRecovery.ShouldBeTrue();
        using (var scanner = new Scanner(Root, reopened, keys, Device)) (await scanner.ScanAsync(Token)).Problems.ShouldBeEmpty();
        await Recovery(reopened).BootstrapAsync(Token); reopened.HasUnaccountedHistory(reopened.Meta.Find("_root-identity")!.Value!).ShouldBeFalse();
        File.ReadAllText(Disk(HistoryState.DecodeMarker(Marker(reopened), keys).Destination)).ShouldBe("original bytes");
        File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
    }

    [Fact]
    public async Task MissingImmutableDataKeepsTheOldMarkerAndRecoverableIntentVisible()
    {
        await using var index = await PrepareAsync(); var marker = Marker(index); var state = HistoryState.DecodeMarker(marker, keys);
        File.Delete(Disk(state.Destination)); string name = Convert.ToHexString(state.Source.ExportBody().Chunks[0].Hash.Span);
        File.Delete(Disk(".psp/blocks/" + name[..2] + "/" + name));
        await Should.ThrowAsync<IOException>(() => Recovery(index).BootstrapAsync(Token)); Marker(index).Value.ShouldBe(marker.Value);
        index.ReadApplyIntents().Count().ShouldBe(1); (await Recovery(index).BootstrapAsync(Token)).WaitingForRecovery.ShouldBeTrue();
        File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
    }

    [Fact]
    public async Task SQLiteAccountingFailureLeavesTheLegacyHoldUnchangedAndCanRetry()
    {
        await using var index = await PrepareAsync(); var marker = Marker(index);
        Sql("CREATE TRIGGER reject_bootstrap BEFORE INSERT ON history_archive_links BEGIN SELECT RAISE(ABORT,'private SQL test'); END;");
        using (var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "history-bootstrap", debugEnabled: true))
            await Should.ThrowAsync<SqliteException>(() => new HistoryRecovery(Root, index, keys, Device, journal: journal).BootstrapAsync(Token));
        string logs = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); logs.ShouldContain("history.bootstrap_failed");
        logs.ShouldContain("1811"); logs.ShouldNotContain("private SQL test"); logs.ShouldNotContain(Database);
        Marker(index).Value.ShouldBe(marker.Value);
        index.HasUnaccountedHistory(index.Meta.Find("_root-identity")!.Value!).ShouldBeTrue();
        Sql("DROP TRIGGER reject_bootstrap;"); (await Recovery(index).BootstrapAsync(Token)).Accounted.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicHistoryRetainsItsPolicyAndDoesNotInventPrivateArchiveRows(bool missing)
    {
        var settings = new FolderSettings { KeepBothCopies = true };
        File.WriteAllText(Disk("current"), "original bytes"); await using var index = await FolderIndex.OpenAsync(Database, Token);
        using (var scanner = new Scanner(Root, index, keys, Device, settings: settings)) await scanner.ScanAsync(Token);
        File.WriteAllText(Disk("current"), "new current bytes");
        using (var scanner = new Scanner(Root, index, keys, Device, settings: settings)) await scanner.ScanAsync(Token);
        var state = HistoryState.DecodeMarker(Marker(index), keys); state.PublicCopy.ShouldBeTrue(); Sql("DELETE FROM history_archive_links;");
        if (missing) File.Delete(Disk(state.Destination));
        (await Recovery(index).BootstrapAsync(Token)).Accounted.ShouldBe(1);
        var retained = HistoryState.DecodeMarker(Marker(index), keys); retained.PublicCopy.ShouldBeTrue(); retained.ArchivedTicks.ShouldBe(state.ArchivedTicks);
        File.ReadAllText(Disk(retained.Destination)).ShouldBe("original bytes"); File.ReadAllText(Disk("current")).ShouldBe("new current bytes");
        index.Archive.Count().ShouldBe(0); index.ReadArchiveProvenance().ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationReleasesTheGateAndDiagnosticOutputContainsNoSourceData()
    {
        await using var index = await PrepareAsync(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Recovery(index).BootstrapAsync(stop.Token));
        using (var journal = new DiagnosticJournal(Path.Combine(fixture, "logs"), "history-bootstrap", debugEnabled: true))
            (await new HistoryRecovery(Root, index, keys, Device, journal: journal).BootstrapAsync(Token)).Accounted.ShouldBe(1);
        string text = File.ReadAllText(Path.Combine(fixture, "logs", "debug.jsonl")); text.ShouldContain("history.bootstrap_complete");
        text.ShouldNotContain(Root); text.ShouldNotContain("original bytes"); text.ShouldNotContain("current");
    }

    private HistoryRecovery Recovery(FolderIndex index) => new(Root, index, keys, Device);
    private static MetaRow Marker(FolderIndex index) => index.Meta.ReadAll().Single(m => m.Key.StartsWith("_history-", StringComparison.Ordinal));
    private async Task<FolderIndex> PrepareAsync()
    {
        File.WriteAllText(Disk("current"), "original bytes"); var index = await FolderIndex.OpenAsync(Database, Token);
        try
        {
            using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
            File.WriteAllText(Disk("current"), "new current bytes"); using (var scanner = new Scanner(Root, index, keys, Device)) await scanner.ScanAsync(Token);
            Sql("DELETE FROM history_archive_links;"); return index;
        }
        catch { await index.DisposeAsync(); throw; }
    }
    private string Disk(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose()
    {
        keys.Dispose(); string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-HistoryBootstrap-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
