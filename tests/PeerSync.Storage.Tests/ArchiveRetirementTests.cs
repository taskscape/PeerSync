using Microsoft.Data.Sqlite;
using PeerSync.Storage.FolderDb;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class ArchiveRetirementTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PeerSync-RetirementDb-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(fixture, "index.db");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ArchiveRetirementTests() => Directory.CreateDirectory(fixture);

    [Fact]
    public async Task ExactPolicyWitnessPersistsAndCompletionAtomicallyRemovesRowWithIdempotentReplay()
    {
        var witness = Witness();
        await using (var index = await PrepareAsync())
        {
            (await index.ReserveArchiveRetirementAsync(witness, Token)).ShouldBeTrue();
            (await index.ReserveArchiveRetirementAsync(witness, Token)).ShouldBeFalse();
            index.ReadArchiveRetirements().Single().CompletedTicks.ShouldBeNull(); index.Archive.Find(7L).ShouldNotBeNull();
        }
        await using var reopened = await FolderIndex.OpenAsync(Database, Token); var saved = reopened.ReadArchiveRetirements().Single();
        saved.FileId.ShouldBe(witness.FileId); saved.Archive.FileHash.ShouldBe(witness.Archive.FileHash); saved.Reason.ShouldBe(ArchiveRetirementReason.Capacity);
        (await reopened.CompleteArchiveRetirementAsync(saved, 200, Token)).ShouldBeTrue(); reopened.Archive.Count().ShouldBe(0);
        reopened.ReadArchiveRetirements().Single().CompletedTicks.ShouldBe(200);
        // SQLite row IDs can be reused after the previous maximum is deleted.
        await reopened.Archive.UpsertAsync([saved.Archive with { OriginalPath = "new-file" }], Token);
        (await reopened.CompleteArchiveRetirementAsync(saved, 300, Token)).ShouldBeFalse(); reopened.Archive.Find(7L)!.OriginalPath.ShouldBe("new-file");
        (await reopened.ReserveArchiveRetirementAsync(saved with { Id = Guid.NewGuid(), Archive = saved.Archive with { OriginalPath = "new-file" } }, Token)).ShouldBeTrue();
        reopened.ReadArchiveRetirements().Count().ShouldBe(2);
    }

    [Fact]
    public async Task RootsPendingApplicationAndChangedArchiveRowsCannotAuthorizeRetirement()
    {
        await using var index = await PrepareAsync(); var witness = Witness();
        await index.Meta.DeleteAsync(["_root-identity"], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveArchiveRetirementAsync(witness, Token));
        await index.Meta.UpsertAsync([new("_root-identity", witness.RootId)], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveArchiveRetirementAsync(witness with { RootId = new byte[24] }, Token));
        var intent = new ApplyIntent(Guid.NewGuid(), "file", null, [1]); await index.ReserveApplyAsync(intent, Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveArchiveRetirementAsync(witness, Token));
        await index.AbandonApplyAsync(intent, Token); index.ReadArchiveRetirements().ShouldBeEmpty();
        await index.ReserveArchiveRetirementAsync(witness, Token);
        await Should.ThrowAsync<SqliteException>(() => index.ReserveArchiveRetirementAsync(witness with { Id = Guid.NewGuid() }, Token));
        await index.Archive.UpsertAsync([witness.Archive with { ArchivePath = ".psp/archive/changed" }], Token);
        await Should.ThrowAsync<InvalidOperationException>(() => index.CompleteArchiveRetirementAsync(witness, 200, Token));
        index.ReadArchiveRetirements().Single().CompletedTicks.ShouldBeNull(); index.Archive.Find(7L)!.ArchivePath.ShouldBe(".psp/archive/changed");
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveArchiveRetirementAsync(witness with { PreparedTicks = 101 }, Token));
    }

    [Fact]
    public async Task RealDatabaseFailureRollsBackBothRowRemovalAndWitnessCompletion()
    {
        await using var index = await PrepareAsync(); var witness = Witness(); await index.ReserveArchiveRetirementAsync(witness, Token);
        Sql("CREATE TRIGGER reject_retirement BEFORE UPDATE ON archive_retirements BEGIN SELECT RAISE(ABORT,'test completion failure'); END;");
        await Should.ThrowAsync<SqliteException>(() => index.CompleteArchiveRetirementAsync(witness, 200, Token));
        index.Archive.Find(7L).ShouldNotBeNull(); index.ReadArchiveRetirements().Single().CompletedTicks.ShouldBeNull();
        Sql("DROP TRIGGER reject_retirement;"); (await index.CompleteArchiveRetirementAsync(witness, 200, Token)).ShouldBeTrue();
    }

    [Fact]
    public async Task SubmissionOwnsArraysAndCancellationCannotPrepareOrCompletePartially()
    {
        await using var index = await PrepareAsync(); var witness = Witness();
        Task<bool> task = index.ReserveArchiveRetirementAsync(witness, Token); witness.RootId[0] ^= 1; witness.FileId[0] ^= 1; witness.Archive.FileHash![0] ^= 1;
        (await task).ShouldBeTrue(); var saved = index.ReadArchiveRetirements().Single(); saved.RootId.ShouldBe(Witness().RootId);
        saved.FileId.ShouldBe(Witness().FileId); saved.Archive.FileHash.ShouldBe(Witness().Archive.FileHash);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => index.CompleteArchiveRetirementAsync(saved, 200, stop.Token));
        index.ReadArchiveRetirements().Single().CompletedTicks.ShouldBeNull(); index.Archive.Find(7L).ShouldNotBeNull();
    }

    [Fact]
    public async Task MissingArchiveWithoutPreparedWitnessNeverCreatesCompletionEvidence()
    {
        await using var index = await PrepareAsync(); var witness = Witness(); await index.Archive.DeleteAsync([7L], Token);
        (await index.CompleteArchiveRetirementAsync(witness, 200, Token)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => index.ReserveArchiveRetirementAsync(witness, Token)); index.ReadArchiveRetirements().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("file-id")]
    [InlineData("root")]
    [InlineData("reason")]
    [InlineData("date")]
    [InlineData("completed")]
    public async Task InvalidWitnessesCannotBePrepared(string invalid)
    {
        await using var index = await PrepareAsync(); var witness = Witness();
        witness = invalid switch
        {
            "id" => witness with { Id = Guid.Empty }, "file-id" => witness with { FileId = new byte[15] },
            "root" => witness with { RootId = new byte[23] }, "reason" => witness with { Reason = (ArchiveRetirementReason)3 },
            "date" => witness with { PreparedTicks = 0 }, _ => witness with { CompletedTicks = 200 },
        };
        await Should.ThrowAsync<ArgumentException>(() => index.ReserveArchiveRetirementAsync(witness, Token)); index.ReadArchiveRetirements().ShouldBeEmpty();
    }

    private static ArchiveRetirement Witness() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        new(7, "file", ".psp/archive/file", 50, 16, Enumerable.Repeat((byte)2, 32).ToArray()), Enumerable.Repeat((byte)3, 24).ToArray(),
        Enumerable.Repeat((byte)4, 16).ToArray(), ArchiveRetirementReason.Capacity, 100);
    private async Task<FolderIndex> PrepareAsync()
    {
        var index = await FolderIndex.OpenAsync(Database, Token);
        try { await index.Meta.UpsertAsync([new("_root-identity", Witness().RootId)], Token); await index.Archive.UpsertAsync([Witness().Archive], Token); return index; }
        catch { await index.DisposeAsync(); throw; }
    }
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(fixture);
        if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(resolved).StartsWith("PeerSync-RetirementDb-", StringComparison.Ordinal)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test cleanup escaped its fixture.");
        Directory.Delete(resolved, recursive: true);
    }
}
