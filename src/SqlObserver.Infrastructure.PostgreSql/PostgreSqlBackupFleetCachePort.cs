using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Atomic fenced publication and context-validated aggregate reads, without fleet-time database paging.</summary>
public sealed class PostgreSqlBackupFleetCachePort(NpgsqlDataSource dataSource) : IBackupFleetCachePort, IBackupFleetLatestRepositoryPort
{
    public async ValueTask<bool> PublishAsync(BackupFleetSummary summary, WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary); ArgumentNullException.ThrowIfNull(lease);
        if (lease.Key.Value != "overview/latest-status") throw new ArgumentException("Backup publication requires the overview lease.", nameof(lease));
        RepositoryCallTimeout bounded = Bound(timeout);
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(bounded, cancellationToken);
        string payload = PostgreSqlBackupFleetCacheCodec.Encode(summary);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            await ConfigureAsync(connection, transaction, summary.Context.TargetId, bounded, deadline.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT reporting.publish_overview_backup_latest(@target,@revision,@payload,@owner,@fencing);", connection, transaction)
                { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(bounded) };
            command.Parameters.AddWithValue("target", summary.Context.TargetId.Value); command.Parameters.AddWithValue("revision", summary.Context.TargetRevision);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            command.Parameters.AddWithValue("owner", lease.Owner.Value); command.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
            object? result = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            if (result is not bool applied) throw new InvalidDataException("Backup cache publication returned no result.");
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return applied;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("backup cache publication", exception); }
    }

    public async ValueTask<BackupFleetSummary?> ReadAsync(MonitoredInstanceId targetId, long targetRevision, DateTimeOffset cutoffUtc,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRevision);
        if (cutoffUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Backup cache cutoff must be UTC.", nameof(cutoffUtc));
        RepositoryCallTimeout bounded = Bound(timeout);
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(bounded, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            await ConfigureAsync(connection, transaction, targetId, bounded, deadline.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT payload FROM reporting.get_overview_backup_latest(@target,@revision,@cutoff);", connection, transaction)
                { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(bounded) };
            command.Parameters.AddWithValue("target", targetId.Value); command.Parameters.AddWithValue("revision", targetRevision);
            command.Parameters.AddWithValue("cutoff", cutoffUtc);
            BackupFleetSummary? summary = null;
            await using (NpgsqlDataReader row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
            {
                if (await row.ReadAsync(deadline.Token).ConfigureAwait(false))
                {
                    summary = PostgreSqlBackupFleetCacheCodec.Decode(row.GetString(0), targetId, targetRevision);
                    if (summary.Context.AtUtc > cutoffUtc || summary.ValidUntilUtc <= cutoffUtc) throw new InvalidDataException("Backup cache time binding is invalid.");
                    if (await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Backup cache returned multiple targets.");
                }
            }
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return summary;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("backup cache read", exception); }
    }

    private static async Task ConfigureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        MonitoredInstanceId target, RepositoryCallTimeout timeout, CancellationToken token)
    {
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        command.Parameters.AddWithValue("scope", target.Value.ToString("D")); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private static RepositoryCallTimeout Bound(RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        return new(timeout.Value < TimeSpan.FromSeconds(5) ? timeout.Value : TimeSpan.FromSeconds(5));
    }
}
