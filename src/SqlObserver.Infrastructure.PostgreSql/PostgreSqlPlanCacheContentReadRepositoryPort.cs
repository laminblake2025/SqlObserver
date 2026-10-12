using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlPlanCacheContentReadRepositoryPort(NpgsqlDataSource dataSource) : IPlanCacheContentReadRepositoryPort
{
    public async ValueTask<PlanCacheContentStoredResult> ReadAsync(PlanCacheContentReadRequest request, CancellationToken cancellationToken)
    {
        using var timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
        await Configure(connection, transaction, request, timeout.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT * FROM control.get_plan_cache_content_payload(@target,@run,@database,@inc,@query,@plan,@entry,@kind);", connection, transaction)
        { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout) };
        AddIdentity(command, request);
        PlanCacheContentStoredResult result;
        await using (var reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false)) throw new InvalidDataException("Cache content lookup returned no availability row.");
            string reason = reader.GetString(0); ProtectedSensitivePayload? payload = null;
            if (reason == "available")
                payload = new(request.Kind, new(reader.GetFieldValue<byte[]>(2)), reader.GetString(3), reader.GetString(4),
                    reader.GetFieldValue<byte[]>(5), reader.GetFieldValue<byte[]>(6), reader.GetFieldValue<byte[]>(7));
            else if (Enumerable.Range(2, 6).Any(index => !reader.IsDBNull(index)))
                throw new InvalidDataException("Unavailable cache content returned protected data.");
            result = new(reason, reader.IsDBNull(1) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1), payload);
            if (await reader.ReadAsync(timeout.Token).ConfigureAwait(false)) throw new InvalidDataException("Cache content lookup returned multiple rows.");
        }
        await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
        return result;
    }

    public async ValueTask AuditAsync(PlanCacheContentReadRequest request, string actorSid, string outcome, CancellationToken cancellationToken)
    {
        using var timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
        await Configure(connection, transaction, request, timeout.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT control.audit_plan_cache_content_access(@target,@run,@database,@inc,@query,@plan,@entry,@kind,@actor,@outcome);", connection, transaction)
        { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout) };
        AddIdentity(command, request); command.Parameters.AddWithValue("actor", actorSid); command.Parameters.AddWithValue("outcome", outcome);
        await command.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
        await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
    }

    private static async Task Configure(NpgsqlConnection connection, NpgsqlTransaction transaction, PlanCacheContentReadRequest request, CancellationToken token)
    {
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, request.Timeout, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true),set_config('sqlobserver.role','QueryTextReader',true);", connection, transaction);
        command.Parameters.AddWithValue("target", request.TargetId.Value.ToString()); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private static void AddIdentity(NpgsqlCommand command, PlanCacheContentReadRequest request)
    {
        command.Parameters.AddWithValue("target", request.TargetId.Value); command.Parameters.AddWithValue("run", request.CollectionRunId);
        command.Parameters.AddWithValue("database", request.DatabaseId); command.Parameters.AddWithValue("inc", request.DatabaseIncarnation);
        command.Parameters.AddWithValue("query", request.QueryFingerprint); command.Parameters.AddWithValue("plan", request.PlanFingerprint);
        command.Parameters.AddWithValue("entry", request.CacheEntryFingerprint);
        command.Parameters.AddWithValue("kind", request.Kind == SensitivePayloadKind.QueryText ? "query_text" : "execution_plan");
    }
}
