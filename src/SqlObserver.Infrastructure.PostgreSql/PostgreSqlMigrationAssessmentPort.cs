using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Reads migration state without applying SQL. The transaction is explicitly read-only and
/// every database operation is bounded by the request deadline.
/// </summary>
public sealed class PostgreSqlMigrationAssessmentPort : IMigrationAssessmentPort
{
    private const string ReadServerVersionSql = "SELECT pg_catalog.current_setting('server_version_num')::integer;";
    private const string SetReadOnlySql = "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;";
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlMigrationCatalog _catalog;

    public PostgreSqlMigrationAssessmentPort(
        NpgsqlDataSource dataSource,
        PostgreSqlMigrationCatalog? catalog = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _catalog = catalog ?? PostgreSqlMigrationCatalog.LoadEmbedded();
    }

    public async ValueTask<MigrationAssessmentResult> AssessAsync(
        MigrationAssessmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        DateTimeOffset evaluatedAt = DateTimeOffset.UtcNow;

        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            int version = await ReadServerVersionAsync(connection, request.Timeout, timeout.Token).ConfigureAwait(false);
            var serverVersion = new PostgreSqlVersion(version / 10_000, version % 10_000);
            if (version / 10_000 != 18)
                return Result(MigrationAssessmentStatus.UnsupportedMajor, evaluatedAt, "unsupported_major_version", [], serverVersion: serverVersion);

            bool ownsLock = false;
            try
            {
                if (!await TryAcquireAdvisoryLockAsync(connection, request.Timeout, timeout.Token).ConfigureAwait(false))
                    return Result(MigrationAssessmentStatus.LockUnavailable, evaluatedAt, "lock_unavailable", [], serverVersion: serverVersion);
                ownsLock = true;
                await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
                await SetReadOnlyAsync(connection, transaction, request.Timeout, timeout.Token).ConfigureAwait(false);
                IReadOnlyList<MigrationHistoryEntry> history = await PostgreSqlMigrationPort.ReadHistoryAsync(connection, request.Timeout, timeout.Token, transaction, request.MaxHistory).ConfigureAwait(false);
                MigrationHistoryValidation validation = PostgreSqlMigrationHistoryValidator.Validate(history, _catalog);
                if (!validation.IsValid)
                {
                    int verifiedCount = PostgreSqlMigrationHistoryValidator.VerifiedPrefixLength(history, _catalog);
                    return Result(Map(validation.Code), evaluatedAt, MapCode(validation.Code), history.Take(verifiedCount).ToArray(), serverVersion: serverVersion, observedHistoryCount: history.Count);
                }

                MigrationAssessmentStatus status = history.Count == _catalog.Migrations.Count
                    ? MigrationAssessmentStatus.Current
                    : MigrationAssessmentStatus.Pending;
                string code = status == MigrationAssessmentStatus.Current ? "current" : "pending_migrations";
                int? nextMigrationNumber = GetNextMigrationNumber(status, history.Count, _catalog.Migrations.Count);
                return Result(status, evaluatedAt, code, history, nextMigrationNumber, serverVersion);
            }
            finally
            {
                if (ownsLock) await ReleaseAdvisoryLockAsync(connection).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(MigrationAssessmentStatus.TimedOut, evaluatedAt, "timed_out", []);
        }
        catch (TimeoutException)
        {
            return Result(MigrationAssessmentStatus.TimedOut, evaluatedAt, "timed_out", []);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            return Result(MigrationAssessmentStatus.AccessDenied, evaluatedAt, "access_denied", []);
        }
        catch (NpgsqlException)
        {
            return Result(MigrationAssessmentStatus.InvalidHistory, evaluatedAt, "repository_unavailable", []);
        }
        catch (InvalidDataException)
        {
            return Result(MigrationAssessmentStatus.InvalidHistory, evaluatedAt, "invalid_history", []);
        }
        catch (ArgumentException)
        {
            return Result(MigrationAssessmentStatus.InvalidHistory, evaluatedAt, "invalid_history", []);
        }
        catch (InvalidOperationException)
        {
            return Result(MigrationAssessmentStatus.InvalidHistory, evaluatedAt, "repository_unavailable", []);
        }
    }

    private static MigrationAssessmentResult Result(
        MigrationAssessmentStatus status,
        DateTimeOffset evaluatedAt,
        string code,
        IReadOnlyList<MigrationHistoryEntry> history,
        int? next = null,
        PostgreSqlVersion? serverVersion = null,
        int? observedHistoryCount = null)
    {
        return new MigrationAssessmentResult(status, history, evaluatedAt, code, next, serverVersion, observedHistoryCount);
    }

    internal static int? GetNextMigrationNumber(
        MigrationAssessmentStatus status,
        int verifiedPrefixCount,
        int catalogCount)
    {
        if (catalogCount is <= 0 or > MigrationAssessmentRequest.MaximumHistory)
            throw new ArgumentOutOfRangeException(nameof(catalogCount));
        if (verifiedPrefixCount is < 0 || verifiedPrefixCount > catalogCount)
            throw new ArgumentOutOfRangeException(nameof(verifiedPrefixCount));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (status != MigrationAssessmentStatus.Pending || verifiedPrefixCount == catalogCount) return null;
        return checked(verifiedPrefixCount + 1);
    }

    private static MigrationAssessmentStatus Map(MigrationHistoryValidationCode code) => code switch
    {
        MigrationHistoryValidationCode.Duplicate => MigrationAssessmentStatus.InvalidHistory,
        MigrationHistoryValidationCode.Gap => MigrationAssessmentStatus.Gap,
        MigrationHistoryValidationCode.Unknown => MigrationAssessmentStatus.Unknown,
        MigrationHistoryValidationCode.NameDrift or MigrationHistoryValidationCode.ChecksumDrift => MigrationAssessmentStatus.Drift,
        MigrationHistoryValidationCode.Missing => MigrationAssessmentStatus.Pending,
        _ => MigrationAssessmentStatus.InvalidHistory,
    };

    private static string MapCode(MigrationHistoryValidationCode code) => code switch
    {
        MigrationHistoryValidationCode.Duplicate => "duplicate_history",
        MigrationHistoryValidationCode.Gap => "history_gap",
        MigrationHistoryValidationCode.Unknown => "unknown_history",
        MigrationHistoryValidationCode.NameDrift => "name_drift",
        MigrationHistoryValidationCode.ChecksumDrift => "checksum_drift",
        MigrationHistoryValidationCode.Missing => "pending_migrations",
        _ => "invalid_history",
    };

    private static async Task<int> ReadServerVersionAsync(NpgsqlConnection connection, RepositoryCallTimeout timeout, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(ReadServerVersionSql, connection) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is int version ? version : throw new InvalidDataException("The PostgreSQL version was not an integer.");
    }

    private static async Task SetReadOnlyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, RepositoryCallTimeout timeout, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(SetReadOnlySql, connection, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, token).ConfigureAwait(false);
    }

    private static async Task<bool> TryAcquireAdvisoryLockAsync(NpgsqlConnection connection, RepositoryCallTimeout timeout, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_try_advisory_lock(@lock_key);", connection) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        command.Parameters.AddWithValue("lock_key", PostgreSqlMigrationPort.MigrationAdvisoryLockKey);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is true;
    }

    private static async Task ReleaseAdvisoryLockAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_unlock(@lock_key);", connection) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("lock_key", PostgreSqlMigrationPort.MigrationAdvisoryLockKey);
            await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException) { NpgsqlConnection.ClearPool(connection); }
    }
}
