using System.Globalization;
using Microsoft.Data.Sqlite;
using PeerSync.Storage;
using Shouldly;
using Xunit;

namespace PeerSync.Storage.Tests;

public sealed class DbTests : IDisposable
{
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PeerSync-Db-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => System.IO.Path.Combine(root, "data.db");
    private static readonly DbMigration[] V1 = [new(1, "CREATE TABLE counter (value INTEGER NOT NULL); INSERT INTO counter VALUES (0);")];

    public DbTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task RequiredPragmasApplyToWriterAndIndependentReadOnlyConnections()
    {
        await using var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        foreach (string name in new[] { "journal_mode", "synchronous", "foreign_keys", "busy_timeout", "temp_store" })
        {
            string expected = name switch { "journal_mode" => "wal", "busy_timeout" => "5000", "temp_store" => "2", _ => "1" };
            (await database.WriteAsync((c, t) => Scalar(c, $"PRAGMA {name};", t)?.ToString(), TestContext.Current.CancellationToken)).ShouldBe(expected);
            using var reader = database.OpenReader();
            Scalar(reader, $"PRAGMA {name};")!.ToString().ShouldBe(expected);
        }
        using var readOnly = database.OpenReader();
        Should.Throw<SqliteException>(() => Execute(readOnly, "UPDATE counter SET value=99;")).SqliteErrorCode.ShouldBe(8);
    }

    [Fact]
    public async Task ConcurrentWritesSerializeWithoutBusyStormsOrLostUpdates()
    {
        await using var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        int active = 0;
        Task<long>[] writes = Enumerable.Range(0, 200).Select(_ => database.WriteAsync((c, t) =>
        {
            Interlocked.Increment(ref active).ShouldBe(1);
            try { return Number(Scalar(c, "UPDATE counter SET value=value+1 RETURNING value;", t)); }
            finally { Interlocked.Decrement(ref active); }
        }, TestContext.Current.CancellationToken)).ToArray();
        long[] values = await Task.WhenAll(writes);
        values.Order().ShouldBe(Enumerable.Range(1, 200).Select(i => (long)i));
        using var reader = database.OpenReader();
        Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(200);
    }

    [Fact]
    public async Task WalReaderSeesCommittedStateWhileWriterTransactionIsOpen()
    {
        await using var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writing = database.WriteAsync((c, t) =>
        {
            Execute(c, "UPDATE counter SET value=1;", t);
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Reader did not release the test writer.");
            return 1;
        }, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            using var reader = database.OpenReader();
            Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(0);
            writing.IsCompleted.ShouldBeFalse();
        }
        finally { release.Set(); }
        (await writing).ShouldBe(1);
    }

    [Fact]
    public async Task FailedAndCancelledOperationsRollBackAndNextWriteStillWorks()
    {
        await using var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() => database.WriteAsync<int>((c, t) =>
        {
            Execute(c, "UPDATE counter SET value=20;", t);
            throw new InvalidOperationException("Synthetic failure.");
        }, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(() => database.WriteAsync((c, t) =>
        {
            Execute(c, "UPDATE counter SET value=30;", t);
            cancelled.Cancel();
            return 30;
        }, cancelled.Token));
        using (var reader = database.OpenReader()) Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(0);
        (await database.WriteAsync((c, t) => Execute(c, "UPDATE counter SET value=40;", t), TestContext.Current.CancellationToken)).ShouldBe(1);
        using var final = database.OpenReader();
        Number(Scalar(final, "SELECT value FROM counter;")).ShouldBe(40);
    }

    [Fact]
    public async Task QueuedCancellationDoesNotExecuteTheWrite()
    {
        await using var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = database.WriteAsync((c, t) =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test writer was not released.");
            return Execute(c, "UPDATE counter SET value=1;", t);
        }, TestContext.Current.CancellationToken);
        Task<int>? second = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            second = database.WriteAsync((c, t) => Execute(c, "UPDATE counter SET value=2;", t), cancellation.Token);
            cancellation.Cancel();
        }
        finally { release.Set(); }
        await first;
        await Should.ThrowAsync<OperationCanceledException>(() => second!);
        using var reader = database.OpenReader();
        Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(1);
    }

    [Fact]
    public async Task FreshSchemaCommitsAllStepsWithoutIntermediateBackups()
    {
        DbMigration[] steps = [.. V1, new(2, "ALTER TABLE counter ADD COLUMN extra INTEGER NOT NULL DEFAULT 7;")];
        await using var database = await Db.OpenAsync(DatabasePath, steps, TestContext.Current.CancellationToken);
        using var reader = database.OpenReader();
        Number(Scalar(reader, "PRAGMA user_version;")).ShouldBe(2);
        Number(Scalar(reader, "SELECT extra FROM counter;")).ShouldBe(7);
        Directory.GetFiles(root, "*.bak").ShouldBeEmpty();
    }

    [Fact]
    public async Task FailedFreshInitializationRollsBackEveryStepAndCanReopen()
    {
        DbMigration[] bad = [.. V1, new(2, "CREATE TABLE partial(id INTEGER); INSERT INTO missing VALUES(1);")];
        await Should.ThrowAsync<SqliteException>(() => Db.OpenAsync(DatabasePath, bad, TestContext.Current.CancellationToken));
        using (var empty = OpenDirect(DatabasePath))
        {
            Number(Scalar(empty, "PRAGMA user_version;")).ShouldBe(0);
            Number(Scalar(empty, "SELECT count(*) FROM sqlite_schema;")).ShouldBe(0);
        }
        Directory.GetFiles(root, "*.bak").ShouldBeEmpty();
        await using var reopened = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        using var reader = reopened.OpenReader();
        Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(0);
        Directory.GetFiles(root, "*.pre-v0-to-v1-*.bak").Length.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingVersionZeroFilesAlwaysRetainBackups(bool hasData)
    {
        using (var existing = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = DatabasePath, Pooling = false }.ToString()))
        {
            existing.Open();
            if (hasData) Execute(existing, "CREATE TABLE prior(value INTEGER); INSERT INTO prior VALUES(43);");
        }
        await using var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        using var backup = OpenDirect(Directory.GetFiles(root, "*.pre-v0-to-v1-*.bak").Single());
        Number(Scalar(backup, "PRAGMA user_version;")).ShouldBe(0);
        if (hasData) Number(Scalar(backup, "SELECT value FROM prior;")).ShouldBe(43);
        using var reader = database.OpenReader();
        Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(0);
        if (hasData) Number(Scalar(reader, "SELECT value FROM prior;")).ShouldBe(43);
    }

    [Fact]
    public async Task MigrationBacksUpCommittedWalAndUpdatesSchemaAtomically()
    {
        var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        using var keeper = database.OpenReader();
        await database.WriteAsync((c, t) => Execute(c, "UPDATE counter SET value=77;", t), TestContext.Current.CancellationToken);
        await database.DisposeAsync();
        File.Exists(DatabasePath + "-wal").ShouldBeTrue();
        DbMigration[] v2 = [.. V1, new(2, "CREATE TABLE added (id INTEGER PRIMARY KEY);")];
        await using var upgraded = await Db.OpenAsync(DatabasePath, v2, TestContext.Current.CancellationToken);
        string backup = Directory.GetFiles(root, "*.pre-v1-to-v2-*.bak").Single();
        using var saved = OpenDirect(backup);
        Number(Scalar(saved, "PRAGMA user_version;")).ShouldBe(1);
        Number(Scalar(saved, "SELECT value FROM counter;")).ShouldBe(77);
        Number(Scalar(saved, "SELECT count(*) FROM sqlite_master WHERE name='added';")).ShouldBe(0);
        using var current = upgraded.OpenReader();
        Number(Scalar(current, "PRAGMA user_version;")).ShouldBe(2);
        Number(Scalar(current, "SELECT value FROM counter;")).ShouldBe(77);
        Number(Scalar(current, "SELECT count(*) FROM sqlite_master WHERE name='added';")).ShouldBe(1);
    }

    [Fact]
    public async Task FailedMigrationLeavesPriorSchemaAndDataAndReleasesOwnership()
    {
        await using (var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken))
            await database.WriteAsync((c, t) => Execute(c, "UPDATE counter SET value=9;", t), TestContext.Current.CancellationToken);
        DbMigration[] bad = [.. V1, new(2, "CREATE TABLE partial (id INTEGER); INSERT INTO missing VALUES (1);")];
        await Should.ThrowAsync<SqliteException>(() => Db.OpenAsync(DatabasePath, bad, TestContext.Current.CancellationToken));
        await using var reopened = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        using var reader = reopened.OpenReader();
        Number(Scalar(reader, "PRAGMA user_version;")).ShouldBe(1);
        Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(9);
        Number(Scalar(reader, "SELECT count(*) FROM sqlite_master WHERE name='partial';")).ShouldBe(0);
        File.Exists(Directory.GetFiles(root, "*.pre-v1-to-v2-*.bak").Single()).ShouldBeTrue();
    }

    [Fact]
    public async Task FutureSchemaAndInvalidMigrationSequenceAreRejected()
    {
        DbMigration[] v2 = [.. V1, new(2, "CREATE TABLE added (id INTEGER);")];
        await using (await Db.OpenAsync(DatabasePath, v2, TestContext.Current.CancellationToken)) { }
        await Should.ThrowAsync<InvalidOperationException>(() => Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken));
        await using var valid = await Db.OpenAsync(DatabasePath, v2, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<ArgumentException>(() => Db.OpenAsync(System.IO.Path.Combine(root, "bad.db"), [new(2, "SELECT 1;")], TestContext.Current.CancellationToken));
        File.Exists(System.IO.Path.Combine(root, "bad.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task DisposalDrainsAcceptedWritesAndIsIdempotentAcrossAReopen()
    {
        var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        var writes = Enumerable.Range(0, 100).Select(_ => database.WriteAsync((c, t) => Execute(c, "UPDATE counter SET value=value+1;", t), TestContext.Current.CancellationToken)).ToArray();
        await database.DisposeAsync();
        await Task.WhenAll(writes);
        Should.Throw<ObjectDisposedException>(() => database.OpenReader());
        await Should.ThrowAsync<ObjectDisposedException>(() => database.WriteAsync((_, _) => 0, TestContext.Current.CancellationToken));
        await using var reopened = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        await database.DisposeAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken));
        using var reader = reopened.OpenReader();
        Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(100);
    }

    [Fact]
    public async Task DuplicateNormalizedPathCannotAcquireASecondWriter()
    {
        await using var first = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        string alias = System.IO.Path.Combine(root, ".", "data.db");
        await Should.ThrowAsync<InvalidOperationException>(() => Db.OpenAsync(alias, V1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DurableWritesSynchronizeCommitAndRestoreNormalModeAfterSuccessFailureAndCancellation()
    {
        await using var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        await database.WriteDurableAsync((c, t) =>
        {
            Number(Scalar(c, "PRAGMA synchronous;", t)).ShouldBe(2);
            return Execute(c, "UPDATE counter SET value=7;", t);
        }, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() => database.WriteDurableAsync<int>((c, t) =>
        {
            Number(Scalar(c, "PRAGMA synchronous;", t)).ShouldBe(2);
            Execute(c, "UPDATE counter SET value=99;", t); throw new InvalidOperationException("injected failure");
        }, TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<OperationCanceledException>(() => database.WriteDurableAsync((c, t) =>
        {
            Number(Scalar(c, "PRAGMA synchronous;", t)).ShouldBe(2);
            Execute(c, "UPDATE counter SET value=88;", t); cancellation.Cancel(); return 0;
        }, cancellation.Token));
        await database.WriteAsync((c, t) =>
        {
            Number(Scalar(c, "PRAGMA synchronous;", t)).ShouldBe(1);
            Number(Scalar(c, "SELECT value FROM counter;", t)).ShouldBe(7); return 0;
        }, TestContext.Current.CancellationToken);
        using var reader = database.OpenReader(); Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(7);
    }

    [Fact]
    public async Task MixedDurableAndOrdinaryRequestsShareTheSameSerializedWriterAndDrainOnDisposal()
    {
        var database = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        var tasks = Enumerable.Range(0, 40).Select(i =>
        {
            int expected = (i & 1) == 0 ? 2 : 1;
            int Update(SqliteConnection c, SqliteTransaction t)
            {
                Number(Scalar(c, "PRAGMA synchronous;", t)).ShouldBe(expected);
                return Execute(c, "UPDATE counter SET value=value+1;", t);
            }
            return expected == 2 ? database.WriteDurableAsync(Update, TestContext.Current.CancellationToken)
                : database.WriteAsync(Update, TestContext.Current.CancellationToken);
        }).ToArray();
        await database.DisposeAsync(); await Task.WhenAll(tasks);
        await using var reopened = await Db.OpenAsync(DatabasePath, V1, TestContext.Current.CancellationToken);
        using var reader = reopened.OpenReader(); Number(Scalar(reader, "SELECT value FROM counter;")).ShouldBe(40);
    }

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static long Number(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static int Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenDirect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
