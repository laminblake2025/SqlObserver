using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlOverviewHistoryPort(NpgsqlDataSource dataSource) : IOverviewHistoryRepositoryPort
{
    public async Task<IReadOnlyList<OverviewSeries>> ReadAsync(MonitoredInstanceId targetId, long targetRevision,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.role','Viewer',true),set_config('sqlobserver.target_scope',@scope,true)", connection, transaction))
        {
            scope.CommandTimeout = 5; scope.Parameters.AddWithValue("scope", targetId.Value.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand("SELECT metric_key,bucket_at,metric_value,sample_count FROM reporting.overview_workload_history(@id,@revision,@from,@to,@cutoff)", connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", targetId.Value); command.Parameters.AddWithValue("revision", targetRevision);
        command.Parameters.AddWithValue("from", fromUtc); command.Parameters.AddWithValue("to", toUtc); command.Parameters.AddWithValue("cutoff", cutoffUtc);
        var groups = new Dictionary<string, List<OverviewPoint>>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            int count = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (++count > 2000) throw new InvalidDataException("Overview history exceeded its bound.");
                string key = reader.GetString(0);
                if (key is not ("engine.user_connections" or "engine.batch_requests_per_second")) throw new InvalidDataException();
                if (!groups.TryGetValue(key, out var points)) groups[key] = points = [];
                points.Add(new(new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero), reader.IsDBNull(2) ? null : reader.GetDouble(2), reader.GetInt32(3)));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return groups.Select(g => new OverviewSeries(targetId.Value, "", g.Key, g.Key.EndsWith("per_second", StringComparison.Ordinal) ? "batches/sec" : "connections",
            g.Value.Any(x => x.Value.HasValue) ? "observed" : "no_data", null, g.Value)).ToArray();
    }
}
