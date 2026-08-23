using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Repository-clock worker leases with monotonic PostgreSQL-issued fencing values.</summary>
public sealed class PostgreSqlWorkerLeasePort : IWorkerLeasePort
{
    private const string AcquireSql = """
        SELECT acquired, fencing_token, acquired_at, renewed_at, expires_at, repository_time
        FROM control.acquire_worker_lease(@work_key, @owner_execution_id, @ttl);
        """;
    private const string RenewSql = """
        SELECT renewed, acquired_at, renewed_at, expires_at, repository_time
        FROM control.renew_worker_lease(@work_key, @owner_execution_id, @fencing_token, @ttl);
        """;
    private const string ReleaseSql = """
        SELECT control.release_worker_lease(@work_key, @owner_execution_id, @fencing_token);
        """;
    private const string AssertSql = """
        SELECT control.assert_worker_lease(@work_key, @owner_execution_id, @fencing_token);
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlWorkerLeasePort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<LeaseAcquisitionResult> AcquireAsync(
        AcquireWorkerLeaseRequest request,
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
            await using var command = new NpgsqlCommand(AcquireSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            AddLeaseKeyAndOwner(command, request.Key, request.Owner);
            command.Parameters.AddWithValue("ttl", request.Duration.Value);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Lease acquisition returned no result row.");
            }

            DateTimeOffset repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 5);
            if (!reader.GetBoolean(0))
            {
                return LeaseAcquisitionResult.Contended(repositoryTime);
            }

            var identity = new WorkerLeaseIdentity(
                request.Key,
                request.Owner,
                new FencingToken(reader.GetInt64(1)));
            var lease = new WorkerLease(
                identity,
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 2),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 3),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 4));
            return LeaseAcquisitionResult.Acquired(lease, repositoryTime);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("lease acquisition", exception);
        }
    }

    public async ValueTask<LeaseRenewalResult> RenewAsync(
        RenewWorkerLeaseRequest request,
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
            await using var command = new NpgsqlCommand(RenewSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            AddLeaseIdentity(command, request.Identity);
            command.Parameters.AddWithValue("ttl", request.Duration.Value);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Lease renewal returned no result row.");
            }

            DateTimeOffset repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 4);
            if (!reader.GetBoolean(0))
            {
                return LeaseRenewalResult.OwnershipLost(repositoryTime);
            }

            var lease = new WorkerLease(
                request.Identity,
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 2),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 3));
            return LeaseRenewalResult.Renewed(lease, repositoryTime);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("lease renewal", exception);
        }
    }

    public async ValueTask<LeaseReleaseStatus> ReleaseAsync(
        ReleaseWorkerLeaseRequest request,
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
            await using var command = new NpgsqlCommand(ReleaseSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            AddLeaseIdentity(command, request.Identity);
            object? released = await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);
            return released is true ? LeaseReleaseStatus.Released : LeaseReleaseStatus.NotOwned;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("lease release", exception);
        }
    }

    public async ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(
        AssertWorkerLeaseRequest request,
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
            await using var command = new NpgsqlCommand(AssertSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            AddLeaseIdentity(command, request.Identity);
            object? assertedToken = await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);
            return assertedToken is long value && value == request.Identity.FencingToken.Value
                ? LeaseOwnershipStatus.Current
                : LeaseOwnershipStatus.NotCurrent;
        }
        catch (PostgresException exception) when (exception.SqlState == "55000")
        {
            return LeaseOwnershipStatus.NotCurrent;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("lease assertion", exception);
        }
    }

    internal static void AddLeaseIdentity(NpgsqlCommand command, WorkerLeaseIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(command);
        AddLeaseKeyAndOwner(command, identity.Key, identity.Owner);
        command.Parameters.AddWithValue("fencing_token", identity.FencingToken.Value);
    }

    private static void AddLeaseKeyAndOwner(
        NpgsqlCommand command,
        WorkerLeaseKey key,
        WorkerExecutionId owner)
    {
        command.Parameters.AddWithValue("work_key", key.Value);
        command.Parameters.AddWithValue("owner_execution_id", owner.Value);
    }
}
