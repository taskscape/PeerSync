using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace PeerSync.Storage;

public sealed record DbMigration(int Version, string Sql);

/// <summary>One writer per normalized path in this process. Readers must be disposed by their callers.</summary>
public sealed class Db : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, byte> Owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<IWrite> writes = Channel.CreateBounded<IWrite>(new BoundedChannelOptions(1024)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    private readonly string writerConnectionString;
    private readonly string readerConnectionString;
    private readonly object disposalGate = new();
    private Task? disposal;
    private int disposed;

    private Db(string path, IReadOnlyList<DbMigration> migrations, CancellationToken cancellationToken)
    {
        Path = path;
        writerConnectionString = ConnectionString(path, SqliteOpenMode.ReadWriteCreate);
        readerConnectionString = ConnectionString(path, SqliteOpenMode.ReadOnly);
        worker = Task.Run(() => RunWriterAsync(migrations, cancellationToken), CancellationToken.None);
    }

    public string Path { get; }

    public static async Task<Db> OpenAsync(string path, IReadOnlyList<DbMigration>? migrations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = System.IO.Path.GetFullPath(path);
        DbMigration[] steps = migrations?.ToArray() ?? [];
        for (int i = 0; i < steps.Length; i++)
        {
            if (steps[i].Version != i + 1 || string.IsNullOrWhiteSpace(steps[i].Sql))
                throw new ArgumentException("Migrations must be a complete ascending sequence starting at one.", nameof(migrations));
        }
        if (!Owners.TryAdd(fullPath, 0)) throw new InvalidOperationException("This database already has a writer owner.");
        Db? database = null;
        try
        {
            database = new(fullPath, steps, cancellationToken);
            await database.ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return database;
        }
        catch
        {
            if (database is not null)
            {
                try { await database.DisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve the original startup failure. */ }
            }
            else Owners.TryRemove(fullPath, out _);
            throw;
        }
    }

    /// <summary>Materialize results within the callback; Db owns the connection/transaction and commits it.</summary>
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> operation,
        CancellationToken cancellationToken = default) => EnqueueAsync(operation, durable: false, cancellationToken);

    /// <summary>Synchronize the WAL commit before returning, for intent-before-filesystem ordering.
    /// Uses the same writer queue and restores ordinary NORMAL mode outside the transaction.</summary>
    public Task<T> WriteDurableAsync<T>(Func<SqliteConnection, SqliteTransaction, T> operation,
        CancellationToken cancellationToken = default) => EnqueueAsync(operation, durable: true, cancellationToken);

    private async Task<T> EnqueueAsync<T>(Func<SqliteConnection, SqliteTransaction, T> operation,
        bool durable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var request = new Write<T>(operation, durable, cancellationToken);
        await writes.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return await request.Completion.Task.ConfigureAwait(false);
    }

    public SqliteConnection OpenReader()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var connection = OpenConnection(readerConnectionString, writer: false);
        if (Volatile.Read(ref disposed) == 0) return connection;
        connection.Dispose();
        throw new ObjectDisposedException(nameof(Db));
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new(disposal ??= StopAsync());
    }

    private async Task StopAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        writes.Writer.TryComplete();
        try { await worker.ConfigureAwait(false); }
        finally
        {
            // Clear only this database's two pools, never unrelated databases.
            try
            {
                using var writer = new SqliteConnection(writerConnectionString);
                using var reader = new SqliteConnection(readerConnectionString);
                SqliteConnection.ClearPool(writer);
                SqliteConnection.ClearPool(reader);
                _ = ready.Task.Exception; // Observe a startup fault even if its caller cancelled waiting.
            }
            finally { Owners.TryRemove(Path, out _); }
        }
    }

    private async Task RunWriterAsync(IReadOnlyList<DbMigration> migrations, CancellationToken startupCancellation)
    {
        try
        {
            startupCancellation.ThrowIfCancellationRequested();
            bool created = false;
            try
            {
                using var reservation = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 80 or 183)
            { /* An existing file always takes the backed-up upgrade path. */ }
            using var connection = OpenConnection(writerConnectionString, writer: true);
            if (!created || !TryInitialize(connection, migrations, startupCancellation))
                Migrate(connection, migrations, startupCancellation);
            ready.TrySetResult();
            // The opening caller's token governs startup only. Each write owns
            // its cancellation, and disposal drains the channel gracefully.
            await foreach (var request in writes.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false)) request.Run(connection);
        }
        catch (Exception error)
        {
            ready.TrySetException(error);
            writes.Writer.TryComplete(error);
            while (writes.Reader.TryRead(out var request)) request.Fail(error);
            throw;
        }
    }

    private static bool TryInitialize(SqliteConnection connection, IReadOnlyList<DbMigration> migrations,
        CancellationToken cancellationToken)
    {
        // Reserve the SQLite writer before checking emptiness: another process may
        // have populated the newly reserved file before we opened the connection.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return false;
        command.CommandText = "SELECT count(*) FROM sqlite_schema;";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return false;
        foreach (var migration in migrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = migration.Sql;
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture) + ";";
            command.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return true;
    }

    private void Migrate(SqliteConnection connection, IReadOnlyList<DbMigration> migrations, CancellationToken cancellationToken)
    {
        using var readVersion = connection.CreateCommand();
        readVersion.CommandText = "PRAGMA user_version;";
        int version = Convert.ToInt32(readVersion.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version < 0) throw new InvalidOperationException("Invalid database schema version.");
        if (version > migrations.Count) throw new InvalidOperationException("Database schema is newer than this service supports.");
        while (version < migrations.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var migration = migrations[version];
            string backupPath = Path + $".pre-v{version}-to-v{migration.Version}-{Guid.NewGuid():N}.bak";
            using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var backupString = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
            }.ToString();
            using (var backup = new SqliteConnection(backupString))
            {
                backup.Open();
                connection.BackupDatabase(backup);
            }
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            command.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture) + ";";
            command.ExecuteNonQuery();
            transaction.Commit();
            version = migration.Version;
        }
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Cache = SqliteCacheMode.Private, Pooling = true, DefaultTimeout = 30,
    }.ToString();

    private static SqliteConnection OpenConnection(string connectionString, bool writer)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = (writer ? "PRAGMA journal_mode=WAL;" : "") +
                "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private interface IWrite
    {
        void Run(SqliteConnection connection);
        void Fail(Exception error);
    }

    private sealed class Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation, bool durable, CancellationToken cancellation) : IWrite
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Run(SqliteConnection connection)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                T result;
                try
                {
                    if (durable) SetSynchronous(connection, full: true);
                    using var transaction = connection.BeginTransaction();
                    result = operation(connection, transaction);
                    cancellation.ThrowIfCancellationRequested();
                    transaction.Commit();
                }
                finally { if (durable) SetSynchronous(connection, full: false); }
                Completion.TrySetResult(result);
            }
            catch (OperationCanceledException error) when (error.CancellationToken == cancellation)
            { Completion.TrySetCanceled(cancellation); }
            catch (Exception error) { Completion.TrySetException(error); }
        }

        public void Fail(Exception error) => Completion.TrySetException(error);

        private static void SetSynchronous(SqliteConnection connection, bool full)
        {
            using var command = connection.CreateCommand();
            command.CommandText = full ? "PRAGMA synchronous=FULL;" : "PRAGMA synchronous=NORMAL;";
            command.ExecuteNonQuery();
        }
    }
}
