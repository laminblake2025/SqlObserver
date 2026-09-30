using System.Security.Cryptography;
using System.Text;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Bounded aggregate-only read, fenced by the dedicated alert coordinator lease.</summary>
public sealed class PostgreSqlBackupAlertEvidencePort(NpgsqlDataSource dataSource) : IBackupAlertEvidencePort
{
    public async ValueTask<BackupAlertEvidence> ReadAsync(MonitoredInstanceId targetId, long targetRevision,
        WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRevision);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeout);
        if (lease.Key.Value != "alerts/evaluation/reconcile")
            throw new ArgumentException("The alert evaluation coordinator lease is required.", nameof(lease));
        var bounded = new RepositoryCallTimeout(timeout.Value < TimeSpan.FromSeconds(5) ? timeout.Value : TimeSpan.FromSeconds(5));
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(bounded, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, bounded, deadline.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(bounded);
                scope.Parameters.AddWithValue("scope", targetId.Value.ToString("D"));
                await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand(
                "SELECT read_at,payload,source_digest FROM alerting.read_backup_fleet_evidence(@target,@revision,@owner,@fencing);",
                connection, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(bounded) };
            command.Parameters.AddWithValue("target", targetId.Value);
            command.Parameters.AddWithValue("revision", targetRevision);
            command.Parameters.AddWithValue("owner", lease.Owner.Value);
            command.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
            BackupAlertEvidence result;
            await using (NpgsqlDataReader row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
            {
                if (!await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
                DateTimeOffset readAt = row.GetFieldValue<DateTimeOffset>(0);
                string? payload = row.IsDBNull(1) ? null : row.GetString(1);
                string? digest = row.IsDBNull(2) ? null : row.GetString(2);
                if (payload is not null && digest != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))))
                    throw Invalid();
                result = new(targetId, targetRevision, readAt,
                    payload is null ? null : PostgreSqlBackupFleetCacheCodec.Decode(payload, targetId, targetRevision), digest);
                _ = BackupAlertSignalProjection.Build(result); // Validate the complete context, including null/digest binding.
                if (await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
            }
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("backup alert evidence read", exception); }
    }

    private static InvalidDataException Invalid() => new("Backup alert evidence returned an invalid envelope.");
}
