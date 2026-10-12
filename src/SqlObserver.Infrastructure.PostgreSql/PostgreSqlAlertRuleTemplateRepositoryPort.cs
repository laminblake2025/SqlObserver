using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Native immutable versions and complete, explicitly authorized deployment cohorts.</summary>
public sealed class PostgreSqlAlertRuleTemplateRepositoryPort(NpgsqlDataSource dataSource) : IAlertRuleTemplateRepositoryPort
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly NpgsqlDataSource source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public ValueTask<AlertRuleTemplateWriteReceipt> WriteTemplateAsync(AlertRuleTemplateWriteRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        WriteAsync<AlertRuleTemplateWriteReceipt>("rule_template_write", new
        {
            request.OperationId, request.TemplateId, request.ExpectedVersion, request.Title,
            request.SourceTemplateName, request.CatalogDigest, request.Values, request.Reason,
        }, request.ActorSid, request.CorrelationId, TargetAuthorizationScope.None(), request.AllTargetAdministrator,
            request.CurrentApproval, false, timeout, cancellationToken);

    public ValueTask<AlertRuleTemplateVersion?> GetTemplateAsync(Guid templateId, long? version, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        ReadAsync<AlertRuleTemplateVersion>("SELECT alerting.rule_template_get(@id,@version,true);",
            [Parameter("id", NpgsqlDbType.Uuid, templateId), Parameter("version", NpgsqlDbType.Bigint, version)], false, timeout, cancellationToken);

    public ValueTask<AlertRuleTemplateListPage> ListTemplatesAsync(int limit, Guid? beforeTemplateId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        RequiredAsync<AlertRuleTemplateListPage>("SELECT alerting.rule_template_list(@limit,@before,true);",
            [Parameter("limit", NpgsqlDbType.Integer, limit), Parameter("before", NpgsqlDbType.Uuid, beforeTemplateId)], false, timeout, cancellationToken);

    public ValueTask<AlertRuleTemplateVersionPage> ListVersionsAsync(Guid templateId, int limit, long? beforeVersion, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        RequiredAsync<AlertRuleTemplateVersionPage>("SELECT alerting.rule_template_versions(@id,@limit,@before,true);",
            [Parameter("id", NpgsqlDbType.Uuid, templateId), Parameter("limit", NpgsqlDbType.Integer, limit), Parameter("before", NpgsqlDbType.Bigint, beforeVersion)], false, timeout, cancellationToken);

    public ValueTask<AlertRuleDeploymentPreviewReceipt> PreviewAsync(AlertRuleDeploymentPreviewRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        WriteAsync<AlertRuleDeploymentPreviewReceipt>("rule_deployment_preview", new
        {
            request.OperationId, request.DeploymentId, request.TemplateId, request.TemplateVersion, request.Group, request.Mode, request.Reason,
        }, request.ActorSid, request.CorrelationId, request.AdministratorScope, false, request.CurrentApproval, true, timeout, cancellationToken);

    public ValueTask<AlertRuleDeploymentPreviewReceipt> RollbackPreviewAsync(AlertRuleDeploymentRollbackPreviewRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        WriteAsync<AlertRuleDeploymentPreviewReceipt>("rule_deployment_rollback_preview", new
        {
            request.OperationId, request.DeploymentId, request.SourceDeploymentId, request.Reason,
        }, request.ActorSid, request.CorrelationId, request.AdministratorScope, false, request.CurrentCatalog, true, timeout, cancellationToken);

    public ValueTask<AlertRuleDeploymentBatchReceipt> BatchAsync(AlertRuleDeploymentBatchRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        WriteAsync<AlertRuleDeploymentBatchReceipt>("rule_deployment_batch", new
        {
            request.OperationId, request.DeploymentId, request.ExpectedRevision, request.Action, request.Reason,
        }, request.ActorSid, request.CorrelationId, request.AdministratorScope, false, request.CurrentCatalog, false, timeout, cancellationToken);

    public ValueTask<AlertRuleDeploymentStatus?> GetDeploymentAsync(Guid deploymentId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        ReadAsync<AlertRuleDeploymentStatus>("SELECT alerting.rule_deployment_get(@id,@all,@scope,true);",
            [Parameter("id", NpgsqlDbType.Uuid, deploymentId), .. Scope(administratorScope)], false, timeout, cancellationToken);

    public ValueTask<AlertRuleDeploymentListPage> ListDeploymentsAsync(int limit, Guid? beforeDeploymentId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        RequiredAsync<AlertRuleDeploymentListPage>("SELECT alerting.rule_deployment_list(@limit,@before,@all,@scope,true);",
            [Parameter("limit", NpgsqlDbType.Integer, limit), Parameter("before", NpgsqlDbType.Uuid, beforeDeploymentId), .. Scope(administratorScope)], false, timeout, cancellationToken);

    public ValueTask<AlertRuleDeploymentHistoryPage> ListHistoryAsync(Guid deploymentId, int limit, Guid? beforeOperationId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        RequiredAsync<AlertRuleDeploymentHistoryPage>("SELECT alerting.rule_deployment_history(@id,@limit,@before,@all,@scope,true);",
            [Parameter("id", NpgsqlDbType.Uuid, deploymentId), Parameter("limit", NpgsqlDbType.Integer, limit), Parameter("before", NpgsqlDbType.Uuid, beforeOperationId), .. Scope(administratorScope)], false, timeout, cancellationToken);

    private ValueTask<T> WriteAsync<T>(string function, object payload, string actor, Guid correlation,
        TargetAuthorizationScope scope, bool allAdministrator, object? approval, bool consistentPreview,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken) where T : class =>
        RequiredAsync<T>($"SELECT alerting.{function}(@command,@actor,@correlation,@all,@scope,true,@approval);",
        [Parameter("command", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(payload, JsonOptions)),
            Parameter("actor", NpgsqlDbType.Text, actor), Parameter("correlation", NpgsqlDbType.Uuid, correlation),
            Parameter("all", NpgsqlDbType.Boolean, allAdministrator || scope.AllTargets),
            Parameter("scope", NpgsqlDbType.Array | NpgsqlDbType.Uuid, scope.TargetIds.Select(static id => id.Value).ToArray()),
            Parameter("approval", NpgsqlDbType.Jsonb, approval is null ? null : JsonSerializer.Serialize(approval, JsonOptions))], consistentPreview, timeout, cancellationToken);

    private async ValueTask<T> RequiredAsync<T>(string sql, NpgsqlParameter[] parameters, bool consistentPreview,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken) where T : class =>
        await ReadAsync<T>(sql, parameters, consistentPreview, timeout, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("The rule template repository returned no authoritative receipt.");

    private async ValueTask<T?> ReadAsync<T>(string sql, NpgsqlParameter[] parameters, bool consistentPreview,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(timeout);
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        try
        {
          for (int attempt = 0; ; attempt++)
          {
           try
           {
            await using NpgsqlConnection connection = await source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            // Only cohort previews require a stable inventory/group/rule snapshot.
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
                consistentPreview ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted, deadline.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, deadline.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection, transaction)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            command.Parameters.AddRange(parameters.Select(static parameter => parameter.Clone()).ToArray());
            object? result = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            T? parsed = result is null or DBNull ? null : JsonSerializer.Deserialize<T>((string)result, JsonOptions)
                ?? throw new InvalidDataException("The rule template response is invalid.");
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return parsed;
           }
           catch (PostgresException error) when (consistentPreview && attempt < 2 && IsPreviewSnapshotRace(error))
           {
               // A waiter may have acquired its RR snapshot before the prior
               // exact command committed. Reopen that same command in a fresh
               // snapshot; never retry a deliberate native revision conflict.
               deadline.Token.ThrowIfCancellationRequested();
           }
          }
        }
        catch (PostgresException error)
        {
            throw error.SqlState switch
            {
                "40001" or "23505" or "55000" => new AlertRepositoryOperationException("conflict", "The template or deployment changed; read current state.", 409, error),
                "P0002" => new AlertRepositoryOperationException("not_found", "The template, deployment or page anchor was not found.", 404, error),
                "42501" => new AlertRepositoryOperationException("forbidden", "Target administrator access is required for the complete cohort.", 403, error),
                "22023" or "22P02" or "22003" or "23502" => new AlertRepositoryOperationException("invalid_request", "The bounded template request is invalid.", 400, error),
                "57014" or "55P03" => new AlertRepositoryOperationException("request_timed_out", "The template repository deadline expired.", 503, error),
                _ => new AlertRepositoryOperationException("repository_unavailable", "The template repository is unavailable.", 503, error),
            };
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("rule templates", error); }
    }

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };
    private static bool IsPreviewSnapshotRace(PostgresException error) =>
        error.SqlState == "40001" && error.Routine is "ExecUpdate" or "ExecDelete" or "ExecLockRows" or "ExecCheckTupleVisible" or "ExecOnConflictUpdate" or "CheckForSerializableConflictOut" or "CheckForSerializableConflictIn" ||
        error.SqlState == "23505" && error.ConstraintName is "rule_deployment_pkey" or "rule_template_operation_pkey";
    private static NpgsqlParameter[] Scope(TargetAuthorizationScope scope) =>
        [Parameter("all", NpgsqlDbType.Boolean, scope.AllTargets), Parameter("scope", NpgsqlDbType.Array | NpgsqlDbType.Uuid, scope.TargetIds.Select(static id => id.Value).ToArray())];
}
