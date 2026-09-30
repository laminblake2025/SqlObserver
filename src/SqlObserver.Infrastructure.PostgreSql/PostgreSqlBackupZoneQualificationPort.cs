using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlBackupZoneQualificationPort(NpgsqlDataSource dataSource)
    : IBackupZoneQualificationAdministrationPort, IBackupHistoricalZoneQualificationPort
{
    public async ValueTask<BackupZoneQualificationMutationResult> SetAsync(SetBackupZoneQualificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        BackupZoneQualificationChange change = request.Change;
        if (request.Audit.Action != AdministrativeAuditAction.UpdateObservationTarget || request.Audit.TargetId != change.TargetId)
            throw new ArgumentException("Qualification audit target/action must match the command.", nameof(request));
        using var deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT result_status,command_revision FROM control.set_backup_zone_qualification(
                  @target,@target_revision,@expected,@operation,@zone,@rules,@from,@to,@evidence,@actor,@correlation);
                """);
            command.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout);
            command.Parameters.AddWithValue("target", change.TargetId.Value);
            command.Parameters.AddWithValue("target_revision", change.TargetRevision.Value);
            command.Parameters.AddWithValue("expected", change.ExpectedRevision);
            command.Parameters.AddWithValue("operation", change.OperationId);
            command.Parameters.Add("zone", NpgsqlDbType.Text).Value = (object?)change.Qualification?.SourceZoneId ?? DBNull.Value;
            command.Parameters.Add("rules", NpgsqlDbType.Text).Value = (object?)change.SerializedRules ?? DBNull.Value;
            command.Parameters.Add("from", NpgsqlDbType.TimestampTz).Value = (object?)change.Qualification?.FromUtc ?? DBNull.Value;
            command.Parameters.Add("to", NpgsqlDbType.TimestampTz).Value = (object?)change.Qualification?.ToUtc ?? DBNull.Value;
            command.Parameters.Add("evidence", NpgsqlDbType.Bytea).Value = change.EvidenceSha256 is { } digest
                ? Convert.FromHexString(digest) : DBNull.Value;
            command.Parameters.AddWithValue("actor", request.Audit.ActorSid.Value);
            command.Parameters.AddWithValue("correlation", request.Audit.CorrelationId.Value);
            await using NpgsqlDataReader row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Qualification command returned no status.");
            var result = new BackupZoneQualificationMutationResult(row.GetString(0) switch
            {
                "applied" => BackupZoneQualificationMutationStatus.Applied,
                "replayed" => BackupZoneQualificationMutationStatus.Replayed,
                "not_found" => BackupZoneQualificationMutationStatus.NotFound,
                "inactive" => BackupZoneQualificationMutationStatus.Inactive,
                "revision_conflict" => BackupZoneQualificationMutationStatus.RevisionConflict,
                _ => throw new InvalidDataException("Qualification command returned an unknown status."),
            }, row.IsDBNull(1) ? null : row.GetInt64(1));
            if (await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Qualification command returned multiple statuses.");
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("backup zone qualification mutation", exception); }
    }

    public async ValueTask<BackupHistoricalZoneQualification?> GetAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CancellationToken cancellationToken) =>
        (await ReadAsync(targetId, targetRevision, cancellationToken).ConfigureAwait(false))?.Qualification;

    public async ValueTask<BackupZoneQualificationSnapshot?> ReadAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(revision);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(5));
        using var deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        try
        {
            await using var command = dataSource.CreateCommand("SELECT * FROM control.read_backup_zone_qualification(@target,@revision);");
            command.CommandTimeout = 5;
            command.Parameters.AddWithValue("target", targetId.Value); command.Parameters.AddWithValue("revision", revision.Value);
            await using NpgsqlDataReader row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await row.ReadAsync(deadline.Token).ConfigureAwait(false)) return null;
            long commandRevision = row.GetInt64(0);
            BackupHistoricalZoneQualification? qualification = null;
            string? evidence = null;
            string? rulesDigest = null;
            if (!row.IsDBNull(1))
            {
                string serialized = row.GetString(2);
                if (Encoding.UTF8.GetByteCount(serialized) > 65536 ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)), row.GetFieldValue<byte[]>(3)))
                    throw new InvalidDataException("Historical rules exceed their bound or do not match their checksum.");
                TimeZoneInfo zone;
                try { zone = TimeZoneInfo.FromSerializedString(serialized); }
                catch (Exception exception) when (exception is SerializationException or InvalidTimeZoneException or ArgumentException)
                { throw new InvalidDataException("Persisted historical zone rules are invalid.", exception); }
                qualification = new(targetId, revision, commandRevision, zone, row.GetString(1),
                    PostgreSqlRuntimeSupport.ReadUtcTimestamp(row, 4), PostgreSqlRuntimeSupport.ReadUtcTimestamp(row, 5));
                evidence = Convert.ToHexString(row.GetFieldValue<byte[]>(6)).ToLowerInvariant();
                rulesDigest = Convert.ToHexString(row.GetFieldValue<byte[]>(3)).ToLowerInvariant();
            }
            if (await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Qualification provider returned multiple rows.");
            return new(commandRevision, qualification, evidence, rulesDigest);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("backup zone qualification read", exception); }
    }
}
