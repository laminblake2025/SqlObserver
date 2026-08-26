using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Reads the explicit target/revision replication binding from PostgreSQL.
/// The resolver intentionally has no endpoint or discovered-name fallback.
/// Migration 0014 (owned by the database integrator) supplies the fixed
/// SECURITY DEFINER resolver function used here.
/// </summary>
public sealed class PostgreSqlReplicationDistributionBindingResolver : IReplicationDistributionBindingResolver
{
    private readonly NpgsqlDataSource dataSource;

    public PostgreSqlReplicationDistributionBindingResolver(NpgsqlDataSource dataSource) =>
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<ReplicationDistributionBinding?> ResolveAsync(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (NpgsqlCommand scope = new("SELECT set_config('sqlobserver.target_scope', @scope, true)", connection, transaction) { CommandTimeout = 5 })
            {
                scope.Parameters.AddWithValue("scope", targetId.Value);
                await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using NpgsqlCommand command = new(
                "SELECT database_name,tcp_port FROM control.resolve_m10_replication_distribution_binding(@instance_id,@target_revision)",
                connection,
                transaction) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("instance_id", targetId.Value);
            command.Parameters.AddWithValue("target_revision", targetRevision.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            ReplicationDistributionBinding? result = null;
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) throw new InvalidDataException("The replication binding returned no database name.");
                string databaseName = reader.GetString(0);
                int? port = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                result = new ReplicationDistributionBinding(databaseName, port);
            }
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
            throw;
        }
    }
}
