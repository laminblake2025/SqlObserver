using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Applies the verified embedded migration prefix under one bounded session advisory lock.
/// Each migration and its ledger insert commit in exactly one transaction.
/// </summary>
public sealed class PostgreSqlMigrationPort : IMigrationPort
{
    private const long MigrationAdvisoryLockKey = 0x53514C4F42534D32L;
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

                IReadOnlyList<AppliedMigration> history = await ReadHistoryAsync(
                        connection,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                ValidateExactPrefix(history);

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

    private static async Task<IReadOnlyList<AppliedMigration>> ReadHistoryAsync(
        NpgsqlConnection connection,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using (var existsCommand = new NpgsqlCommand(LedgerExistsSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        })
        {
            object? exists = await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is not true)
            {
                return Array.Empty<AppliedMigration>();
            }
        }

        var result = new List<AppliedMigration>();
        await using var command = new NpgsqlCommand(ReadLedgerSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AppliedMigration(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2)));

            if (result.Count > MigrationBatchResult.MaximumResults)
            {
                throw new InvalidDataException("Repository migration history exceeds the supported bound.");
            }
        }

        return result;
    }

    private void ValidateExactPrefix(IReadOnlyList<AppliedMigration> history)
    {
        if (history.Count > _catalog.Migrations.Count)
        {
            throw new InvalidDataException("Repository migration history contains an unknown migration.");
        }

        for (int index = 0; index < history.Count; index++)
        {
            AppliedMigration applied = history[index];
            PostgreSqlMigrationResource expected = _catalog.Migrations[index];
            int expectedNumber = index + 1;

            if (applied.Number != expectedNumber)
            {
                throw new InvalidDataException(
                    $"Repository migration history has a gap or non-prefix entry at {expectedNumber:D4}.");
            }

            if (!string.Equals(applied.FileName, expected.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Repository migration {expectedNumber:D4} has an unexpected immutable name.");
            }

            if (!string.Equals(applied.Checksum, expected.ChecksumHex, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Repository migration {expected.FileName} failed checksum drift validation.");
            }
        }
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

    private sealed record AppliedMigration(int Number, string FileName, string Checksum);
}
