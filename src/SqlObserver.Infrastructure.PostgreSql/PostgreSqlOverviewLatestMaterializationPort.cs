using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Calls only the fenced repository materializer, never a monitored SQL Server.</summary>
public sealed class PostgreSqlOverviewLatestMaterializationPort(NpgsqlDataSource dataSource)
    : IOverviewLatestMaterializationPort
{
    public async ValueTask<IReadOnlyList<OverviewRefreshTarget>> ListDueAsync(
        WorkerLeaseIdentity lease, int limit, CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT instance_id,target_revision FROM " +
            "reporting.list_due_overview_latest_status(@owner,@fencing,@limit);",
            connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        command.Parameters.AddWithValue("limit", limit);
        var targets = new List<OverviewRefreshTarget>(limit);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (targets.Count >= limit)
                throw new InvalidDataException("Overview refresh target list exceeded its bound.");
            targets.Add(new(new(reader.GetGuid(0)), reader.GetInt64(1)));
        }
        return targets;
    }

    public async ValueTask<bool> RefreshCoreAsync(
        OverviewRefreshTarget target, WorkerLeaseIdentity lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateLease(lease);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.TargetRevision);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@scope,true);",
            connection, transaction) { CommandTimeout = 5 })
        {
            scope.Parameters.AddWithValue("scope", target.TargetId.Value.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand(
            "SELECT reporting.refresh_overview_latest_core(@id,@revision,@owner,@fencing);",
            connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", target.TargetId.Value);
        command.Parameters.AddWithValue("revision", target.TargetRevision);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        bool applied = (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Overview refresh returned no result."));
        await transaction.CommitAsync(cancellationToken);
        return applied;
    }

    private static void ValidateLease(WorkerLeaseIdentity lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Key.Value != "overview/latest-status")
            throw new ArgumentException("Overview refresh requires its dedicated worker lease.", nameof(lease));
    }
}
