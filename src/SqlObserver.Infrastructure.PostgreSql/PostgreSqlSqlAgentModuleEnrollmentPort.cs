using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Explicit repository authorization, separate from SQL permissions and scheduling.</summary>
public sealed class PostgreSqlSqlAgentModuleEnrollmentPort(NpgsqlDataSource dataSource)
    : ISqlAgentModuleEnrollmentAdministrationPort, ISqlAgentModuleEnrollmentResolver
{
    public async ValueTask<SqlAgentModuleEnrollmentMutationResult> SetAsync(
        SetSqlAgentModuleEnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Change);
        ArgumentNullException.ThrowIfNull(request.Audit);
        ArgumentNullException.ThrowIfNull(request.Timeout);
        var change = request.Change;
        ValidateRevision(change.TargetRevision);
        if (request.Audit.Action != AdministrativeAuditAction.UpdateObservationTarget || request.Audit.TargetId != change.TargetId)
            throw new ArgumentException("Module enrollment audit must match its target and action.", nameof(request));
        using var deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT result_status,command_revision FROM control.set_sql_agent_module_enrollment(
                  @target,@target_revision,@expected,@operation,@enrollment,@actor,@correlation)
                """);
            command.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout);
            command.Parameters.AddWithValue("target", change.TargetId.Value);
            command.Parameters.AddWithValue("target_revision", change.TargetRevision.Value);
            command.Parameters.AddWithValue("expected", change.ExpectedRevision);
            command.Parameters.AddWithValue("operation", change.OperationId);
            command.Parameters.Add("enrollment", NpgsqlDbType.Jsonb).Value = change.Enrollment is { } enrollment ? Serialize(enrollment) : DBNull.Value;
            command.Parameters.AddWithValue("actor", request.Audit.ActorSid.Value);
            command.Parameters.AddWithValue("correlation", request.Audit.CorrelationId.Value);
            await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Module enrollment command returned no status.");
            var result = new SqlAgentModuleEnrollmentMutationResult(reader.GetString(0) switch
            {
                "applied" => SqlAgentModuleEnrollmentMutationStatus.Applied,
                "replayed" => SqlAgentModuleEnrollmentMutationStatus.Replayed,
                "not_found" => SqlAgentModuleEnrollmentMutationStatus.NotFound,
                "inactive" => SqlAgentModuleEnrollmentMutationStatus.Inactive,
                "revision_conflict" => SqlAgentModuleEnrollmentMutationStatus.RevisionConflict,
                _ => throw new InvalidDataException("Module enrollment command returned an unknown status."),
            }, reader.IsDBNull(1) ? null : reader.GetInt64(1));
            if (await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Module enrollment command returned multiple statuses.");
            return result;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("module enrollment mutation", error); }
    }

    public async ValueTask<SqlAgentModuleEnrollmentReadResult> ReadAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(targetRevision);
        ValidateRevision(targetRevision);
        using var deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(new(TimeSpan.FromSeconds(5)), cancellationToken);
        try
        {
            await using var command = dataSource.CreateCommand("SELECT * FROM control.read_sql_agent_module_enrollment(@target,@revision)");
            command.CommandTimeout = 5; command.Parameters.AddWithValue("target", targetId.Value); command.Parameters.AddWithValue("revision", targetRevision.Value);
            await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Module enrollment read returned no status.");
            var status = reader.GetString(0) switch
            {
                "found" => SqlAgentModuleEnrollmentReadStatus.Found,
                "not_found" => SqlAgentModuleEnrollmentReadStatus.NotFound,
                "inactive" => SqlAgentModuleEnrollmentReadStatus.Inactive,
                "revision_conflict" => SqlAgentModuleEnrollmentReadStatus.RevisionConflict,
                _ => throw new InvalidDataException("Module enrollment read returned an unknown status."),
            };
            SqlAgentModuleEnrollmentSnapshot? snapshot = status == SqlAgentModuleEnrollmentReadStatus.Found
                ? Snapshot(reader, 1, targetId, targetRevision) : null;
            if (status != SqlAgentModuleEnrollmentReadStatus.Found && (!reader.IsDBNull(1) || !reader.IsDBNull(2)))
                throw new InvalidDataException("Invalid-context module enrollment read disclosed a snapshot.");
            if (await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Module enrollment read returned multiple statuses.");
            return new(status, snapshot);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("module enrollment read", error); }
    }

    public async ValueTask<SqlAgentModuleEnrollmentSnapshot?> ResolveAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(targetRevision);
        ValidateRevision(targetRevision);
        using var deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(new(TimeSpan.FromSeconds(5)), cancellationToken);
        try
        {
            await using var command = dataSource.CreateCommand("SELECT * FROM control.resolve_sql_agent_module_enrollment(@target,@revision)");
            command.CommandTimeout = 5; command.Parameters.AddWithValue("target", targetId.Value); command.Parameters.AddWithValue("revision", targetRevision.Value);
            await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) return null;
            var snapshot = Snapshot(reader, 0, targetId, targetRevision);
            if (await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Module enrollment resolver returned multiple snapshots.");
            return snapshot;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("module enrollment resolution", error); }
    }

    private static void ValidateRevision(ObservationTargetRevision revision)
    {
        if (revision.Value > SqlAgentModuleEnrollment.MaximumRevision)
            throw new ArgumentOutOfRangeException(nameof(revision));
    }

    private static SqlAgentModuleEnrollmentSnapshot Snapshot(NpgsqlDataReader row, int ordinal,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision)
    {
        long revision = row.GetInt64(ordinal);
        try
        {
            return new(revision, row.IsDBNull(ordinal + 1) ? null : Parse(row.GetString(ordinal + 1), targetId, targetRevision, revision));
        }
        catch (Exception error) when (error is ArgumentException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new InvalidDataException("Persisted module enrollment is invalid.", error); }
    }

    private static SqlAgentModuleEnrollment Parse(string json, MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, long revision)
    {
        if (Encoding.UTF8.GetByteCount(json)>16384) throw new InvalidDataException("Persisted module enrollment exceeds its bound.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        var value = document.RootElement; var b = value.GetProperty("binding");
        RequireKeys(value, ["binding", "enrollmentId", "evidenceSha256", "expectedWindowsSid", "expiresAtUtc", "notBeforeUtc"]);
        RequireKeys(b, ["certificateHost", "certificateId", "definitionSha256", "endpointHost", "installationPolicyVersion", "instanceName", "machineName", "moduleVersion", "objectId", "probeVersion", "publicCertificateSha256", "serverName", "sourceVersion", "tcpPort", "thumbprintHex"]);
        if (b.GetProperty("moduleVersion").GetInt32()!=1 || b.GetProperty("sourceVersion").GetInt32()!=2 ||
            b.GetProperty("probeVersion").GetInt32()!=1 || b.GetProperty("installationPolicyVersion").GetInt32()!=2)
            throw new InvalidDataException("Persisted module enrollment version is unsupported.");
        var binding = new SqlAgentModuleBinding(Text(b,"endpointHost"), b.GetProperty("tcpPort").GetInt32(), Text(b,"certificateHost"),
            Text(b,"serverName"), Text(b,"machineName"), Text(b,"instanceName"), b.GetProperty("objectId").GetInt32(),
            b.GetProperty("certificateId").GetInt32(), Text(b,"definitionSha256"), Text(b,"publicCertificateSha256"), Text(b,"thumbprintHex"));
        return new(targetId, targetRevision, revision, value.GetProperty("enrollmentId").GetGuid(), Text(value,"expectedWindowsSid"), binding,
            DateTimeOffset.Parse(Text(value,"notBeforeUtc"), CultureInfo.InvariantCulture, DateTimeStyles.None),
            DateTimeOffset.Parse(Text(value,"expiresAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.None), Text(value,"evidenceSha256"));
    }

    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString() ?? throw new InvalidDataException("Persisted module enrollment contains a null field.");
    private static void RequireKeys(JsonElement value, string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(keys))
            throw new InvalidDataException("Persisted module enrollment fields differ from the contract.");
    }

    private static string Serialize(SqlAgentModuleEnrollment enrollment)
    {
        var b = enrollment.Binding;
        return JsonSerializer.Serialize(new
        {
            enrollmentId = enrollment.EnrollmentId, expectedWindowsSid = enrollment.ExpectedWindowsSid,
            notBeforeUtc = enrollment.NotBeforeUtc, expiresAtUtc = enrollment.ExpiresAtUtc, evidenceSha256 = enrollment.EvidenceSha256,
            binding = new { endpointHost=b.EndpointHost, tcpPort=b.TcpPort, certificateHost=b.CertificateHost, serverName=b.ServerName,
                machineName=b.MachineName, instanceName=b.InstanceName, objectId=b.ObjectId, certificateId=b.CertificateId,
                definitionSha256=b.DefinitionSha256, publicCertificateSha256=b.PublicCertificateSha256, thumbprintHex=b.ThumbprintHex,
                moduleVersion=b.ModuleVersion, sourceVersion=b.SourceVersion, probeVersion=b.ProbeVersion, installationPolicyVersion=b.InstallationPolicyVersion }
        });
    }
}
