using Microsoft.Data.Sqlite;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class DirectoryPublicationTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-DirectoryPublicationDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Root => new byte[24];
    private static long Now => 638000000000000000;
    public DirectoryPublicationTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task ReconstructionRetainsPendingAndParentHoldsButPermitsIncomingNamespaceMutation()
    {
        await using var index = await Open(); var staged = await index.StageDirectoryPublicationAsync(Spec("topic/nested") with { ReconstructionOnly = true }, Token);
        index.HasPendingDirectoryPublication("topic/nested/a").ShouldBeTrue(); index.HasPendingDirectoryPublication("topic", true).ShouldBeTrue();
        index.HasBlockingDirectoryPublication("topic/nested/a", true).ShouldBeFalse(); index.HasBlockingDirectoryPublication("topic", true).ShouldBeFalse();
        index.HasPendingChildDirectoryPublication("topic").ShouldBeTrue(); index.HasPendingDirectoryPublications().ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => index.StageDirectoryPublicationAsync(staged with { ReconstructionOnly = false }, Token));
        await index.StageDirectoryPublicationAsync(Spec("topic"), Token);
        index.HasBlockingDirectoryPublication("topic/nested/a", true).ShouldBeTrue();
    }

    [Fact]
    public async Task ReconstructionModeSurvivesRestartAndChangedEpochWakesItsBackoff()
    {
        DirectoryPublication pending;
        await using (var index = await Open())
        {
            pending = await index.StageDirectoryPublicationAsync(Spec("topic") with { ReconstructionOnly = true }, Token);
            pending = await index.BindDirectoryPublicationKeysAsync(pending, index.ReadDirectoryCaptureEpoch(), Now, Token);
            await index.RetryDirectoryPublicationAsync(pending, Now, Token);
            (await index.StageDirectoryPublicationAsync(pending, Token)).NextTicks.ShouldNotBeNull();
        }
        await using var reopened = await Open(); reopened.ReadDirectoryPublication("topic")!.ReconstructionOnly.ShouldBeTrue();
        await reopened.LocalFiles.UpsertAsync([new("topic/a", 0, 0, Now, 0, false, Now, new byte[32], new Dictionary<ulong, ulong>(), null, null)], Token);
        var resumed = await reopened.StageDirectoryPublicationAsync(pending, Token); resumed.NextTicks.ShouldBeNull(); resumed.Attempts.ShouldBe(0);
        reopened.ReadDueDirectoryPublications(Now).Single().Version.ShouldBe(pending.Version);
    }

    [Fact]
    public async Task RestartRetainsExactPrivateTemplateAndLeavesPreviousSignedRowUntouched()
    {
        DirectoryPublication staged; SignedRecord old = new([1], new byte[64]);
        await using (var index = await Open())
        {
            await index.LocalFiles.UpsertAsync([new("topic", 1, 0, Now, 16, false, Now, null, new Dictionary<ulong, ulong> { [1] = 1 }, old, new byte[16])], Token);
            var row = index.LocalFiles.Find("topic")!; var input = Spec("topic") with { ExpectedLocalSequence = row.LocalSequence };
            var task = index.StageDirectoryPublicationAsync(input, Token); input.NativeId[0] = 99; input.ExpectedKeysHash[0] = 98; staged = await task;
            staged.NativeId.ShouldBe(new byte[16]); staged.ExpectedKeysHash.ShouldBe(new byte[32]);
            index.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(old.Body.ToArray()); index.ReadIndexHighWater().ShouldBe(row.LocalSequence);
        }
        await using var reopened = await Open(); var saved = reopened.ReadDirectoryPublication("TOPIC")!;
        saved.Version.ShouldBe(staged.Version); saved.Template.Body.ToArray().ShouldBe(new byte[] { 2 }); saved.Template.Signature.ToArray().ShouldBe(new byte[64]);
        saved.NativeId[0] = 97; reopened.ReadDirectoryPublication("topic")!.NativeId.ShouldBe(new byte[16]);
        reopened.LocalFiles.Find("topic")!.Record!.Body.ToArray().ShouldBe(old.Body.ToArray());
    }

    [Fact]
    public async Task DependenciesAndNamespaceBarriersUseExactAncestorsDescendantsAndLiteralNames()
    {
        await using var index = await Open(); await index.StageDirectoryPublicationAsync(Spec("topic/nested"), Token);
        index.HasPendingDirectoryPublication("TOPIC/nested/file").ShouldBeTrue();
        index.HasPendingDirectoryPublication("topic").ShouldBeFalse(); index.HasPendingDirectoryPublication("topic", true).ShouldBeTrue();
        index.HasPendingDirectoryPublication("topical/file", true).ShouldBeFalse(); index.HasPendingChildDirectoryPublication("topic").ShouldBeTrue();
        await index.StageDirectoryPublicationAsync(Spec("literal%_"), Token);
        index.HasPendingDirectoryPublication("literal%_/child").ShouldBeTrue(); index.HasPendingDirectoryPublication("literalAB/child").ShouldBeFalse();
        await index.StageDirectoryPublicationAsync(Spec("top"), Token);
        string deep = "top/" + string.Join('/', Enumerable.Repeat("x", 2046)); deep.Length.ShouldBe(4095);
        index.HasPendingDirectoryPublication(deep).ShouldBeTrue();
        await index.StageDirectoryPublicationAsync(Spec("topic"), Token);
        var due = index.ReadDueDirectoryPublications(Now); due[0].Path.ShouldBe("topic/nested");
    }

    [Fact]
    public async Task SameVersionTouchPreservesRetryAndKeyProofWhileNewVersionRejectsOldCompletionAuthority()
    {
        await using var index = await Open(); var staged = await index.StageDirectoryPublicationAsync(Spec("topic"), Token);
        var bound = await index.BindDirectoryPublicationKeysAsync(staged, index.ReadDirectoryCaptureEpoch(), Now, Token);
        var failed = await index.RetryDirectoryPublicationAsync(bound, Now, Token); failed.Attempts.ShouldBe(1); failed.NextTicks.ShouldBe(Now + 10 * TimeSpan.TicksPerSecond);
        var touched = await index.StageDirectoryPublicationAsync(staged with { TouchedTicks = Now + 1 }, Token);
        touched.Attempts.ShouldBe(1); touched.KeysEpoch.ShouldBe(0); touched.NextTicks.ShouldBe(failed.NextTicks);
        index.ReadDueDirectoryPublications(Now).ShouldBeEmpty(); index.ReadDueDirectoryPublications(Now - 1).Count.ShouldBe(1);
        var replaced = await index.StageDirectoryPublicationAsync(Spec("topic"), Token); replaced.Version.ShouldNotBe(staged.Version); replaced.Attempts.ShouldBe(0); replaced.KeysEpoch.ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => index.RetryDirectoryPublicationAsync(staged, Now, Token));
    }

    [Fact]
    public async Task StaleLocalSequenceAndForeignRootsCannotReplaceQueueOrLocalState()
    {
        await using var index = await Open(); var staged = await index.StageDirectoryPublicationAsync(Spec("topic"), Token);
        await index.LocalFiles.UpsertAsync([new("topic", 1, 0, Now, 16, false, Now, null, new Dictionary<ulong, ulong>(), null, new byte[16])], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.StageDirectoryPublicationAsync(staged, Token));
        byte[] wrong = Root; wrong[0] = 1;
        await Should.ThrowAsync<InvalidOperationException>(() => index.StageDirectoryPublicationAsync(Spec("other") with { RootId = wrong }, Token));
        index.ReadDirectoryPublication("topic")!.Version.ShouldBe(staged.Version); index.ReadDirectoryPublication("other").ShouldBeNull();
    }

    [Fact]
    public async Task PauseAndPersistentBackoffKeepBarriersWithoutBlockingIndependentDueWork()
    {
        await using var index = await Open(); var staged = await index.StageDirectoryPublicationAsync(Spec("topic"), Token);
        await index.PauseDirectoryPublicationAsync(staged, true, Now, Token); index.ReadDueDirectoryPublications(Now).ShouldBeEmpty();
        index.ReadNextDirectoryPublicationTicks(Root, Now).ShouldBeNull(); index.HasPendingDirectoryPublications().ShouldBeTrue();
        await index.PauseDirectoryPublicationAsync(staged, false, Now, Token);
        DirectoryPublication retry = staged;
        for (int attempt = 1; attempt <= 8; attempt++) retry = await index.RetryDirectoryPublicationAsync(retry, Now, Token);
        retry.Attempts.ShouldBe(7); retry.NextTicks.ShouldBe(Now + 600 * TimeSpan.TicksPerSecond);
        await index.StageDirectoryPublicationAsync(Spec("independent"), Token);
        index.ReadDueDirectoryPublications(Now).Single().Path.ShouldBe("independent");
    }

    [Fact]
    public async Task ChildKeyWitnessCannotBindAcrossAnInputMutation()
    {
        await using var index = await Open(); var staged = await index.StageDirectoryPublicationAsync(Spec("topic"), Token);
        var bound = await index.BindDirectoryPublicationKeysAsync(staged, 0, Now, Token); bound.KeysEpoch.ShouldBe(0);
        await index.LocalFiles.UpsertAsync([new("other", 1, 0, Now, 16, false, Now, null, new Dictionary<ulong, ulong>(), null, new byte[16])], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.BindDirectoryPublicationKeysAsync(bound, 0, Now, Token));
        index.ReadDirectoryPublication("topic")!.KeysEpoch.ShouldBe(0);
    }

    [Fact]
    public async Task ExpiryDeletesAtMostOneThousandPrivateRowsWithoutTouchingLocalRows()
    {
        await using var index = await Open(); var seed = await index.StageDirectoryPublicationAsync(Spec("seed"), Token);
        using (var connection = Connect())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO directory_publications(path_key,path,parent_key,depth,version,root_id,native_id,expected_local_seq,attributes,record_body,signature,expected_keys_hash,expected_children,created_ticks,touched_ticks) "
                + "SELECT $key,$path,'',0,lower(hex(randomblob(16))),root_id,native_id,0,attributes,record_body,signature,expected_keys_hash,expected_children,created_ticks,1 FROM directory_publications WHERE path_key='SEED';";
            command.Parameters.Add("$key", SqliteType.Text); command.Parameters.Add("$path", SqliteType.Text);
            for (int value = 0; value < 1000; value++)
            {
                string path = "item" + value.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters["$key"].Value = path.ToUpperInvariant(); command.Parameters["$path"].Value = path; command.ExecuteNonQuery();
            }
            using var age = connection.CreateCommand(); age.Transaction = transaction; age.CommandText = "UPDATE directory_publications SET touched_ticks=1;"; age.ExecuteNonQuery(); transaction.Commit();
        }
        (await index.PruneDirectoryPublicationsAsync(Root, Now, cancellationToken: Token)).ShouldBe(1000); index.HasPendingDirectoryPublications().ShouldBeTrue();
        (await index.PruneDirectoryPublicationsAsync(Root, Now, cancellationToken: Token)).ShouldBe(1); index.HasPendingDirectoryPublications().ShouldBeFalse();
        index.LocalFiles.Count().ShouldBe(0); index.ReadIndexHighWater().ShouldBe(0); seed.Template.Body.ToArray().ShouldBe(new byte[] { 2 });
    }
    private static DirectoryPublication Spec(string path) => new(Guid.NewGuid(), path, Root, new byte[16], 0, 16, new SignedRecord([2], new byte[64]), new byte[32], 2, Now, Now);
    [Fact]
    public async Task MetadataReconciliationReadsPausedAndBackedOffPathsAndAtomicallyDiscardsOnlyTheExpectedVersion()
    {
        await using var index = await Open(); var first = await index.StageDirectoryPublicationAsync(Spec("topic"), Token);
        await index.BeginDirectoryCaptureAsync("topic", Root, new byte[16], new byte[32], 0, new byte[16], Token);
        await index.PauseDirectoryPublicationAsync(first, true, Now, Token);
        var other = await index.StageDirectoryPublicationAsync(Spec("zebra"), Token); await index.RetryDirectoryPublicationAsync(other, Now, Token);
        index.ReadDueDirectoryPublications(Now).ShouldBeEmpty(); index.ReadDirectoryPublicationPaths(limit: 1).ShouldBe(["TOPIC"]);
        index.ReadDirectoryPublicationPaths("topic", 1).ShouldBe(["ZEBRA"]); index.ReadDirectoryPublicationPaths("zebra", 1).ShouldBeEmpty();
        var replacement = await index.StageDirectoryPublicationAsync(first with { Version = Guid.NewGuid() }, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.DiscardDirectoryPublicationAsync(first, Token));
        index.HasDirectoryCaptures().ShouldBeTrue();
        using (var connection = Connect()) using (var trigger = connection.CreateCommand())
        { trigger.CommandText = "CREATE TRIGGER fail_discard BEFORE DELETE ON directory_publications BEGIN SELECT RAISE(ABORT,'discard failure'); END;"; trigger.ExecuteNonQuery(); }
        await Should.ThrowAsync<SqliteException>(() => index.DiscardDirectoryPublicationAsync(replacement, Token));
        index.ReadDirectoryCapture("topic").ShouldNotBeNull(); index.ReadDirectoryPublication("topic").ShouldNotBeNull();
        using (var connection = Connect()) using (var trigger = connection.CreateCommand()) { trigger.CommandText = "DROP TRIGGER fail_discard;"; trigger.ExecuteNonQuery(); }
        await index.DiscardDirectoryPublicationAsync(replacement, Token); index.ReadDirectoryCapture("topic").ShouldBeNull(); index.ReadDirectoryPublication("topic").ShouldBeNull();
        index.ReadDirectoryPublication("zebra").ShouldNotBeNull(); index.ReadIndexHighWater().ShouldBe(0); index.LocalFiles.Count().ShouldBe(0);
    }
    private async Task<FolderIndex> Open()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Root)], Token); return index; } catch { await index.DisposeAsync(); throw; }
    }
    private SqliteConnection Connect()
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open(); return connection; }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-DirectoryPublicationDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
