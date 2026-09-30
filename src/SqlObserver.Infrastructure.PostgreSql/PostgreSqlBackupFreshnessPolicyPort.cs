using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Backups;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlBackupFreshnessPolicyPort(NpgsqlDataSource dataSource) : IBackupFreshnessPolicyPort
{
    public async ValueTask<BackupPolicyMutationResult> SetAsync(SetBackupFreshnessPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        BackupFreshnessPolicy policy = request.Policy;
        if (request.ExpectedPolicyRevision < 0 || request.ExpectedPolicyRevision == long.MaxValue ||
            policy.PolicyRevision != request.ExpectedPolicyRevision + 1 || request.OperationId == Guid.Empty ||
            request.Audit.Action != AdministrativeAuditAction.UpdateObservationTarget || request.Audit.TargetId != policy.TargetId)
            throw new ArgumentException("Policy revision, operation or audit does not match the command.", nameof(request));
        using var deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT result_status,policy_revision FROM control.set_backup_freshness_policy(
                  @target,@target_revision,@database,@expected,@operation,@enabled,@full,@log,@observation,@requirement,@checksums,@actor,@correlation);
                """);
            command.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout);
            command.Parameters.AddWithValue("target", policy.TargetId.Value); command.Parameters.AddWithValue("target_revision", policy.TargetRevision.Value);
            command.Parameters.AddWithValue("database", Convert.FromHexString(policy.DatabaseFingerprint));
            command.Parameters.AddWithValue("expected", request.ExpectedPolicyRevision); command.Parameters.AddWithValue("operation", request.OperationId);
            command.Parameters.AddWithValue("enabled", policy.Enabled); command.Parameters.AddWithValue("full", (int)policy.MaximumFullAge.TotalSeconds);
            command.Parameters.Add("log", NpgsqlDbType.Integer).Value = policy.MaximumLogAge is { } log ? (int)log.TotalSeconds : DBNull.Value;
            command.Parameters.AddWithValue("observation", (int)policy.MaximumObservationAge.TotalSeconds);
            command.Parameters.AddWithValue("requirement", (int)policy.FullRequirement); command.Parameters.AddWithValue("checksums", policy.RequireChecksums);
            command.Parameters.AddWithValue("actor", request.Audit.ActorSid.Value); command.Parameters.AddWithValue("correlation", request.Audit.CorrelationId.Value);
            await using NpgsqlDataReader row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Backup policy mutation returned no result.");
            var result = new BackupPolicyMutationResult(row.GetString(0) switch
            {
                "applied" => BackupPolicyMutationStatus.Applied, "replayed" => BackupPolicyMutationStatus.Replayed,
                "not_found" => BackupPolicyMutationStatus.NotFound, "inactive" => BackupPolicyMutationStatus.Inactive,
                "revision_conflict" => BackupPolicyMutationStatus.RevisionConflict,
                "evidence_unavailable" => BackupPolicyMutationStatus.EvidenceUnavailable,
                "database_not_observed" => BackupPolicyMutationStatus.DatabaseNotObserved,
                "log_threshold_required" => BackupPolicyMutationStatus.LogThresholdRequired,
                _ => throw new InvalidDataException("Unknown backup policy mutation result."),
            }, row.IsDBNull(1) ? null : row.GetInt64(1));
            if (result.Status is BackupPolicyMutationStatus.Applied or BackupPolicyMutationStatus.Replayed &&
                result.Revision != policy.PolicyRevision)
                throw new InvalidDataException("Backup policy mutation returned a different applied revision.");
            if (await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Backup policy mutation returned multiple results.");
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("backup policy mutation", exception); }
    }

    public async ValueTask<BackupFreshnessPolicy?> ReadAsync(ReadBackupFreshnessPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using var command = dataSource.CreateCommand("SELECT * FROM control.read_backup_freshness_policy(@target,@revision,@database,@at);");
            command.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout);
            command.Parameters.AddWithValue("target", request.TargetId.Value); command.Parameters.AddWithValue("revision", request.TargetRevision.Value);
            command.Parameters.AddWithValue("database", Convert.FromHexString(request.DatabaseFingerprint)); command.Parameters.AddWithValue("at", request.AtUtc);
            await using NpgsqlDataReader row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await row.ReadAsync(deadline.Token).ConfigureAwait(false)) return null;
            var policy = new BackupFreshnessPolicy(request.TargetId, request.TargetRevision, request.DatabaseFingerprint,
                row.GetInt64(0), row.GetBoolean(1), TimeSpan.FromSeconds(row.GetInt32(2)),
                row.IsDBNull(3) ? null : TimeSpan.FromSeconds(row.GetInt32(3)), TimeSpan.FromSeconds(row.GetInt32(4)),
                (FullBackupRequirement)row.GetInt16(5), row.GetBoolean(6));
            if (await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Backup policy read returned multiple results.");
            return policy;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("backup policy read", exception); }
    }
}
