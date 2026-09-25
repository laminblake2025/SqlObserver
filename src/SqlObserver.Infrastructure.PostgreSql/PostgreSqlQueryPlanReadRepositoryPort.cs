using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlQueryPlanReadRepositoryPort(NpgsqlDataSource dataSource)
    : IQueryPlanReadRepositoryPort
{
    public async ValueTask<ProtectedSensitivePayload?> ReadAsync(
        QueryPlanReadRequest request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target,false);", connection))
        {
            scope.Parameters.AddWithValue("target", request.TargetId.Value.ToString());
            await scope.ExecuteNonQueryAsync(timeout.Token);
        }
        await using var command = new NpgsqlCommand(
            "SELECT * FROM control.get_query_plan_payload(@target,@run,@database,@query,@plan);", connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout)
        };
        AddIdentity(command, request);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token);
        if (!await reader.ReadAsync(timeout.Token)) return null;
        var payload = new ProtectedSensitivePayload(
            SensitivePayloadKind.ExecutionPlan,
            new SensitivePayloadFingerprint(reader.GetFieldValue<byte[]>(0)),
            reader.GetString(1), reader.GetString(2),
            reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4),
            reader.GetFieldValue<byte[]>(5));
        if (await reader.ReadAsync(timeout.Token))
            throw new InvalidDataException("Query plan lookup returned more than one payload.");
        return payload;
    }

    public async ValueTask AuditAsync(
        QueryPlanReadRequest request, string actorSid, string outcome,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token);
        await using var command = new NpgsqlCommand(
            "SELECT control.audit_query_plan_access(@target,@run,@database,@query,@plan,@actor,@outcome);", connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout)
        };
        AddIdentity(command, request);
        command.Parameters.AddWithValue("actor", actorSid);
        command.Parameters.AddWithValue("outcome", outcome);
        await command.ExecuteNonQueryAsync(timeout.Token);
    }

    private static void AddIdentity(NpgsqlCommand command, QueryPlanReadRequest request)
    {
        command.Parameters.AddWithValue("target", request.TargetId.Value);
        command.Parameters.AddWithValue("run", request.CollectionRunId);
        command.Parameters.AddWithValue("database", request.Plan.Query.DatabaseId);
        command.Parameters.AddWithValue("query", Convert.FromHexString(request.Plan.Query.QueryFingerprint));
        command.Parameters.AddWithValue("plan", Convert.FromHexString(request.Plan.PlanFingerprint));
    }
}
