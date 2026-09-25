using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Applies the verified embedded migration prefix under one bounded session advisory lock.
/// Transactional migrations commit with their ledger entry. Concurrent-index migrations
/// run outside a transaction. Partitioned indexes retain completed child builds across
/// retries and receive a ledger entry only after every child is attached.
/// </summary>
public sealed class PostgreSqlMigrationPort : IMigrationPort
{
    internal const long MigrationAdvisoryLockKey = 0x53514C4F42534D32L;
    private const string TryAcquireLockSql = "SELECT pg_catalog.pg_try_advisory_lock(@lock_key);";
    private const string ReleaseLockSql = "SELECT pg_catalog.pg_advisory_unlock(@lock_key);";
    private const string ReadServerVersionSql =
        "SELECT pg_catalog.current_setting('server_version_num')::integer;";
    private const string LedgerExistsSql = "SELECT to_regclass('system.schema_migration') IS NOT NULL;";
    private const string ReadLedgerSql = """
        SELECT migration_number, migration_name, sha256
        FROM system.schema_migration
        ORDER BY migration_number;
        """;
    private const string InsertLedgerSql = """
        INSERT INTO system.schema_migration (migration_number, migration_name, sha256)
        VALUES (@migration_number, @migration_name, @sha256);
        """;
    private const string RepositoryClockSql = "SELECT clock_timestamp();";
    private const string ValidIndexSql = """
        SELECT i.indisvalid AND i.indisready
        FROM pg_catalog.pg_index AS i
        WHERE i.indexrelid = pg_catalog.to_regclass(@index_name);
        """;
    private const string IndexExistsSql =
        "SELECT pg_catalog.to_regclass(@index_name) IS NOT NULL;";
    private const string ExistingIndexSql = """
        SELECT pg_catalog.pg_get_indexdef(i.indexrelid),
               i.indrelid = pg_catalog.to_regclass(@table_name),
               i.indisunique,
               pg_catalog.pg_get_userbyid(c.relowner) = 'sqlobserver_migrator'
        FROM pg_catalog.pg_index AS i
        JOIN pg_catalog.pg_class AS c ON c.oid = i.indexrelid
        WHERE i.indexrelid = pg_catalog.to_regclass(@index_name);
        """;
    private const string UnindexedPartitionsSql = """
        SELECT child_ns.nspname, child.relname
        FROM pg_catalog.pg_inherits AS table_link
        JOIN pg_catalog.pg_class AS child ON child.oid = table_link.inhrelid
        JOIN pg_catalog.pg_namespace AS child_ns ON child_ns.oid = child.relnamespace
        WHERE table_link.inhparent = pg_catalog.to_regclass(@table_name)
          AND NOT EXISTS (
              SELECT 1
              FROM pg_catalog.pg_inherits AS index_link
              JOIN pg_catalog.pg_index AS child_index
                ON child_index.indexrelid = index_link.inhrelid
              WHERE index_link.inhparent = pg_catalog.to_regclass(@index_name)
                AND child_index.indrelid = child.oid)
        ORDER BY child.relname;
        """;

    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(50);

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlMigrationCatalog _catalog;

    public PostgreSqlMigrationPort(
        NpgsqlDataSource dataSource,
        PostgreSqlMigrationCatalog? catalog = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _catalog = catalog ?? PostgreSqlMigrationCatalog.LoadEmbedded();
    }

    public async ValueTask<MigrationBatchResult> ApplyPendingAsync(
        MigrationApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            bool ownsAdvisoryLock = false;

            try
            {
                await EnsureSupportedServerVersionAsync(connection, request.Timeout, timeout.Token)
                    .ConfigureAwait(false);
                await AcquireAdvisoryLockAsync(connection, request.Timeout, timeout.Token)
                    .ConfigureAwait(false);
                ownsAdvisoryLock = true;

                IReadOnlyList<MigrationHistoryEntry> history = await ReadHistoryAsync(
                        connection,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                PostgreSqlMigrationHistoryValidator.ThrowIfInvalid(history, _catalog);

                var results = new List<MigrationExecutionResult>();
                IEnumerable<PostgreSqlMigrationResource> pending = _catalog.Migrations
                    .Skip(history.Count)
                    .Take(request.MaxMigrations);

                foreach (PostgreSqlMigrationResource migration in pending)
                {
                    DateTimeOffset startedAt = await ReadRepositoryClockAsync(
                            connection,
                            request.Timeout,
                            timeout.Token)
                        .ConfigureAwait(false);

                    if (!migration.Descriptor.IsTransactional)
                    {
                        try
                        {
                            if (migration.ConcurrentIndex?.Partitioned == true)
                                await ApplyPartitionedConcurrentIndexAsync(connection, migration,
                                    request.Timeout, timeout.Token).ConfigureAwait(false);
                            else
                                await ApplyConcurrentIndexAsync(connection, migration,
                                    request.Timeout, timeout.Token).ConfigureAwait(false);
                            DateTimeOffset completedAt = await ReadRepositoryClockAsync(
                                connection, request.Timeout, timeout.Token).ConfigureAwait(false);
                            results.Add(new MigrationExecutionResult(
                                migration.Descriptor, MigrationOutcome.Applied, startedAt, completedAt));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
                        {
                            DateTimeOffset completedAt = await TryReadRepositoryClockAsync(
                                connection, request.Timeout, startedAt, timeout.Token).ConfigureAwait(false);
                            results.Add(new MigrationExecutionResult(
                                migration.Descriptor, MigrationOutcome.Failed, startedAt, completedAt,
                                PostgreSqlRuntimeSupport.GetSafeFailureCode(exception)));
                            break;
                        }

                        continue;
                    }

                    await using NpgsqlTransaction transaction = await connection
                        .BeginTransactionAsync(timeout.Token)
                        .ConfigureAwait(false);

                    try
                    {
                        await using (var migrationCommand = new NpgsqlCommand(
                            migration.Sql,
                            connection,
                            transaction)
                        {
                            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
                        })
                        {
                            await migrationCommand.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
                        }

                        await using (var ledgerCommand = new NpgsqlCommand(
                            InsertLedgerSql,
                            connection,
                            transaction)
                        {
                            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
                        })
                        {
                            ledgerCommand.Parameters.AddWithValue(
                                "migration_number",
                                migration.Descriptor.Number.Value);
                            ledgerCommand.Parameters.AddWithValue("migration_name", migration.FileName);
                            ledgerCommand.Parameters.AddWithValue("sha256", migration.ChecksumHex);
                            await ledgerCommand.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
                        }

                        await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
                        DateTimeOffset completedAt = await ReadRepositoryClockAsync(
                                connection,
                                request.Timeout,
                                timeout.Token)
                            .ConfigureAwait(false);
                        results.Add(new MigrationExecutionResult(
                            migration.Descriptor,
                            MigrationOutcome.Applied,
                            startedAt,
                            completedAt));
                    }
                    catch (OperationCanceledException)
                    {
                        await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
                    {
                        await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
                        DateTimeOffset completedAt = await TryReadRepositoryClockAsync(
                                connection,
                                request.Timeout,
                                startedAt,
                                timeout.Token)
                            .ConfigureAwait(false);
                        results.Add(new MigrationExecutionResult(
                            migration.Descriptor,
                            MigrationOutcome.Failed,
                            startedAt,
                            completedAt,
                            PostgreSqlRuntimeSupport.GetSafeFailureCode(exception)));
                        break;
                    }
                }

                DateTimeOffset batchCompletedAt = await ReadRepositoryClockAsync(
                        connection,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                return new MigrationBatchResult(results, batchCompletedAt);
            }
            finally
            {
                if (ownsAdvisoryLock)
                {
                    await ReleaseAdvisoryLockAsync(connection).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("migration", exception);
        }
    }

    private static async Task ApplyConcurrentIndexAsync(
        NpgsqlConnection connection,
        PostgreSqlMigrationResource migration,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        ConcurrentIndexSpec index = migration.ConcurrentIndex
            ?? throw new InvalidOperationException("A nontransactional migration must declare its index.");
        bool roleSet = false;
        Exception? resetFailure = null;
        try
        {
            await ExecuteMigrationCommandAsync(connection, "SET ROLE sqlobserver_migrator;", timeout, cancellationToken)
                .ConfigureAwait(false);
            roleSet = true;
            await ExecuteMigrationCommandAsync(connection, "SET lock_timeout = '5s';", timeout, cancellationToken)
                .ConfigureAwait(false);
            await ExecuteMigrationCommandAsync(connection, "SET statement_timeout = '5min';", timeout, cancellationToken)
                .ConfigureAwait(false);

            await EnsureExistingIndexMatchesAsync(connection, index, timeout, cancellationToken)
                .ConfigureAwait(false);

            // A failed concurrent build can leave an invalid index. A completed build can
            // likewise precede a crash before the ledger insert. Rebuild either case.
            await ExecuteMigrationCommandAsync(connection,
                $"DROP INDEX CONCURRENTLY IF EXISTS {index.IndexName};", timeout, cancellationToken)
                .ConfigureAwait(false);
            await ExecuteMigrationCommandAsync(connection, migration.Sql, timeout, cancellationToken)
                .ConfigureAwait(false);

            await using var validityCommand = new NpgsqlCommand(ValidIndexSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
            };
            validityCommand.Parameters.AddWithValue("index_name", index.IndexName);
            if (await validityCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new InvalidOperationException("Concurrent index build did not produce a valid ready index.");
            }
        }
        finally
        {
            if (roleSet)
            {
                try
                {
                    await ExecuteMigrationCommandAsync(connection,
                        "RESET ROLE; RESET lock_timeout; RESET statement_timeout;",
                        timeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
                {
                    NpgsqlConnection.ClearPool(connection);
                    resetFailure = exception;
                }
            }
        }

        if (resetFailure is not null)
        {
            throw new InvalidOperationException("Migration session state could not be reset.", resetFailure);
        }

        await RecordNontransactionalMigrationAsync(connection, migration, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ApplyPartitionedConcurrentIndexAsync(
        NpgsqlConnection connection, PostgreSqlMigrationResource migration,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ConcurrentIndexSpec index = migration.ConcurrentIndex is { Partitioned: true } value
            ? value : throw new InvalidOperationException("A partitioned-index migration requires a partitioned index declaration.");
        bool roleSet = false;
        Exception? resetFailure = null;
        try
        {
            await ExecuteMigrationCommandAsync(connection, "SET ROLE sqlobserver_migrator;",
                timeout, cancellationToken).ConfigureAwait(false);
            roleSet = true;
            await ExecuteMigrationCommandAsync(connection, "SET lock_timeout = '5s';",
                timeout, cancellationToken).ConfigureAwait(false);
            await ExecuteMigrationCommandAsync(connection, "SET statement_timeout = '5min';",
                timeout, cancellationToken).ConfigureAwait(false);
            await EnsureExistingIndexMatchesAsync(connection, index, timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!await IndexExistsAsync(connection, index.IndexName, timeout, cancellationToken)
                    .ConfigureAwait(false))
                await ExecuteMigrationCommandAsync(connection, migration.Sql, timeout, cancellationToken)
                    .ConfigureAwait(false);

            // The parent is metadata-only and invalid until every child is attached.
            // Retain valid child builds across retries; only failed builds are replaced.
            for (int attempt = 0; attempt < 64; attempt++)
            {
                IReadOnlyList<(string Schema, string Name)> pending =
                    await ReadUnindexedPartitionsAsync(connection, index, timeout, cancellationToken)
                        .ConfigureAwait(false);
                if (pending.Count == 0) break;
                foreach ((string schema, string child) in pending)
                {
                    string childIndexName = ChildIndexName(index, schema, child);
                    var childSpec = new ConcurrentIndexSpec(childIndexName,
                        $"{schema}.{child}", index.Columns);
                    await EnsureExistingIndexMatchesAsync(connection, childSpec,
                        timeout, cancellationToken).ConfigureAwait(false);
                    if (!await ValidIndexAsync(connection, childIndexName, timeout,
                            cancellationToken).ConfigureAwait(false))
                    {
                        await ExecuteMigrationCommandAsync(connection,
                            $"DROP INDEX CONCURRENTLY IF EXISTS {childIndexName};",
                            timeout, cancellationToken).ConfigureAwait(false);
                        await ExecuteMigrationCommandAsync(connection,
                            $"CREATE INDEX CONCURRENTLY {childIndexName.Split('.')[1]} " +
                            $"ON {schema}.{child} ({index.Columns});",
                            timeout, cancellationToken).ConfigureAwait(false);
                        if (!await ValidIndexAsync(connection, childIndexName,
                                timeout, cancellationToken).ConfigureAwait(false))
                            throw new InvalidOperationException("Concurrent child index is not valid and ready.");
                    }
                    await ExecuteMigrationCommandAsync(connection,
                        $"ALTER INDEX {index.IndexName} ATTACH PARTITION {childIndexName};",
                        timeout, cancellationToken).ConfigureAwait(false);
                }
            }
            if (!await ValidIndexAsync(connection, index.IndexName, timeout,
                    cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Partitioned index is not valid after child attachment.");
        }
        finally
        {
            if (roleSet)
            {
                try
                {
                    await ExecuteMigrationCommandAsync(connection,
                        "RESET ROLE; RESET lock_timeout; RESET statement_timeout;",
                        timeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
                {
                    NpgsqlConnection.ClearPool(connection);
                    resetFailure = exception;
                }
            }
        }
        if (resetFailure is not null)
            throw new InvalidOperationException("Migration session state could not be reset.", resetFailure);
        await RecordNontransactionalMigrationAsync(connection, migration, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection connection,
        string indexName, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(IndexExistsSql, connection)
        { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        command.Parameters.AddWithValue("index_name", indexName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<bool> ValidIndexAsync(NpgsqlConnection connection,
        string indexName, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ValidIndexSql, connection)
        { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        command.Parameters.AddWithValue("index_name", indexName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<IReadOnlyList<(string Schema, string Name)>> ReadUnindexedPartitionsAsync(
        NpgsqlConnection connection, ConcurrentIndexSpec index,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(UnindexedPartitionsSql, connection)
        { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        command.Parameters.AddWithValue("table_name", index.TableName);
        command.Parameters.AddWithValue("index_name", index.IndexName);
        var result = new List<(string Schema, string Name)>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static string ChildIndexName(ConcurrentIndexSpec index, string schema,
        string child)
    {
        string[] table = index.TableName.Split('.');
        string[] parentIndex = index.IndexName.Split('.');
        string prefix = table[1] + "_";
        if (schema != table[0] || !child.StartsWith(prefix, StringComparison.Ordinal) ||
            child.Length != prefix.Length + 8 ||
            !child.AsSpan(prefix.Length).ToString().All(char.IsAsciiDigit))
            throw new InvalidOperationException("A partitioned-index child has an unexpected name or schema.");
        string name = parentIndex[1] + "_" + child[prefix.Length..];
        if (name.Length > 63) throw new InvalidOperationException("A partitioned child index name is too long.");
        return $"{schema}.{name}";
    }

    private static async Task RecordNontransactionalMigrationAsync(
        NpgsqlConnection connection, PostgreSqlMigrationResource migration,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var ledgerCommand = new NpgsqlCommand(InsertLedgerSql, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
            };
            ledgerCommand.Parameters.AddWithValue("migration_number", migration.Descriptor.Number.Value);
            ledgerCommand.Parameters.AddWithValue("migration_name", migration.FileName);
            ledgerCommand.Parameters.AddWithValue("sha256", migration.ChecksumHex);
            await ledgerCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task EnsureExistingIndexMatchesAsync(
        NpgsqlConnection connection,
        ConcurrentIndexSpec index,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ExistingIndexSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        command.Parameters.AddWithValue("index_name", index.IndexName);
        command.Parameters.AddWithValue("table_name", index.TableName);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string expectedSuffix = $"USING btree ({index.Columns})";
        if (!reader.GetBoolean(1) || reader.GetBoolean(2) || !reader.GetBoolean(3) ||
            !reader.GetString(0).EndsWith(expectedSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An unrelated index already uses the migration index name.");
        }
    }

    private static async Task ExecuteMigrationCommandAsync(
        NpgsqlConnection connection,
        string sql,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSupportedServerVersionAsync(
        NpgsqlConnection connection,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ReadServerVersionSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not int versionNumber)
        {
            throw new InvalidOperationException("PostgreSQL server version query returned an unexpected value.");
        }

        if (!IsSupportedServerVersionNumber(versionNumber))
        {
            throw new InvalidOperationException("PostgreSQL migration requires server major version 18.");
        }
    }

    internal static bool IsSupportedServerVersionNumber(int versionNumber) =>
        versionNumber / 10_000 == 18;

    private static async Task AcquireAdvisoryLockAsync(
        NpgsqlConnection connection,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(TryAcquireLockSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        command.Parameters.AddWithValue("lock_key", MigrationAdvisoryLockKey);

        while (true)
        {
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is true)
            {
                return;
            }

            await Task.Delay(LockRetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<IReadOnlyList<MigrationHistoryEntry>> ReadHistoryAsync(
        NpgsqlConnection connection,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null,
        int maxHistory = MigrationNumber.MaximumValue)
    {
        if (maxHistory is <= 0 or > MigrationNumber.MaximumValue)
            throw new ArgumentOutOfRangeException(nameof(maxHistory));
        await using (var existsCommand = new NpgsqlCommand(LedgerExistsSql, connection, transaction)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        })
        {
            object? exists = await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is not true)
            {
                return Array.Empty<MigrationHistoryEntry>();
            }
        }

        var result = new List<MigrationHistoryEntry>();
        await using var command = new NpgsqlCommand(ReadLedgerSql, connection, transaction)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                result.Add(new MigrationHistoryEntry(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Repository migration history contains an invalid ledger row.", exception);
            }

            if (result.Count > maxHistory)
            {
                throw new InvalidDataException("Repository migration history exceeds the supported bound.");
            }
        }

        return result;
    }

    private static async Task<DateTimeOffset> ReadRepositoryClockAsync(
        NpgsqlConnection connection,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RepositoryClockSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not DateTime timestamp)
        {
            throw new InvalidOperationException("PostgreSQL repository clock returned an unexpected value.");
        }

        return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
    }

    private static async Task<DateTimeOffset> TryReadRepositoryClockAsync(
        NpgsqlConnection connection,
        RepositoryCallTimeout timeout,
        DateTimeOffset fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadRepositoryClockAsync(connection, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            return fallback;
        }
    }

    private static async Task RollbackWithoutMaskingAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // The original failure remains authoritative; disposing a broken connection releases its transaction.
        }
    }

    private static async Task ReleaseAdvisoryLockAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand(ReleaseLockSql, connection)
            {
                CommandTimeout = 5,
            };
            command.Parameters.AddWithValue("lock_key", MigrationAdvisoryLockKey);
            object? result = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            if (result is not true)
            {
                NpgsqlConnection.ClearPool(connection);
            }
        }
        catch (NpgsqlException)
        {
            NpgsqlConnection.ClearPool(connection);
        }
    }

}
