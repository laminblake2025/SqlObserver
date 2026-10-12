using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlObservationTargetGroupingPort(NpgsqlDataSource dataSource) : IObservationTargetGroupingPort
{
    private const string ReadSql = """
        SELECT instance_id, environment_name, tags, revision
        FROM reporting.observation_target_grouping
        WHERE instance_id = ANY(@target_ids);
        """;
    private const string SetSql = """
        SELECT result_status, grouping_revision
        FROM control.set_observation_target_grouping(
            @instance_id, @expected_revision, @environment_name, @tags,
            @actor_identifier, @correlation_id);
        """;

    public async Task<IReadOnlyDictionary<Guid, ObservationTargetGrouping>> ReadAsync(
        IReadOnlyList<Guid> targetIds, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        ArgumentNullException.ThrowIfNull(timeout);
        if (targetIds.Count > 1000 || targetIds.Any(id => id == Guid.Empty))
            throw new ArgumentOutOfRangeException(nameof(targetIds));
        if (targetIds.Count == 0) return new Dictionary<Guid, ObservationTargetGrouping>();
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(deadline.Token);
            await using var command = new NpgsqlCommand(ReadSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
            };
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("target_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = targetIds.ToArray(),
            });
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(deadline.Token);
            var result = new Dictionary<Guid, ObservationTargetGrouping>();
            while (await reader.ReadAsync(deadline.Token))
            {
                Guid id = reader.GetGuid(0);
                result.Add(id, new ObservationTargetGrouping(id, reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetFieldValue<string[]>(2), reader.GetInt64(3)));
            }
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("target grouping read", exception);
        }
    }

    public async Task<ObservationTargetGroupingMutationResult> SetAsync(
        SetObservationTargetGroupingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(deadline.Token);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(deadline.Token);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, request.Timeout, deadline.Token);
            await using var command = new NpgsqlCommand(SetSql, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("instance_id", request.TargetId.Value);
            command.Parameters.AddWithValue("expected_revision", request.ExpectedRevision);
            command.Parameters.Add(new NpgsqlParameter<string?>("environment_name", NpgsqlDbType.Text)
            {
                TypedValue = request.Environment,
            });
            command.Parameters.Add(new NpgsqlParameter<string[]>("tags", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                TypedValue = request.Tags.ToArray(),
            });
            command.Parameters.AddWithValue("actor_identifier", request.Audit.ActorSid.Value);
            command.Parameters.AddWithValue("correlation_id", request.Audit.CorrelationId.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(deadline.Token);
            if (!await reader.ReadAsync(deadline.Token))
                throw new InvalidDataException("PostgreSQL returned no target grouping result.");
            string status = reader.GetString(0);
            long? revision = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            await reader.DisposeAsync();
            await transaction.CommitAsync(deadline.Token);
            return new ObservationTargetGroupingMutationResult(status switch
            {
                "applied" => ObservationTargetGroupingMutationStatus.Applied,
                "not_found" => ObservationTargetGroupingMutationStatus.NotFound,
                "revision_conflict" => ObservationTargetGroupingMutationStatus.RevisionConflict,
                "already_retired" => ObservationTargetGroupingMutationStatus.AlreadyRetired,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown target grouping status."),
            }, revision);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("target grouping update", exception);
        }
    }
}
