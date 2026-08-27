using Npgsql;
using SqlObserver.Reporting;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlReportAuditPort(NpgsqlDataSource dataSource) : IReportAuditPort
{
    public async ValueTask AppendAsync(ReportAuditEvent audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit); if (audit.TargetId == Guid.Empty || string.IsNullOrWhiteSpace(audit.ActorSid)) throw new ArgumentException("Audit identity is required.");
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new("SELECT audit.append_report_activity(@run,@target,@actor,@kind,@outcome,@detail);", connection) { CommandTimeout = 2 };
        command.Parameters.AddWithValue("run", (object?)audit.RunId ?? DBNull.Value); command.Parameters.AddWithValue("target", audit.TargetId); command.Parameters.AddWithValue("actor", audit.ActorSid); command.Parameters.AddWithValue("kind", audit.ActivityKind); command.Parameters.AddWithValue("outcome", audit.Outcome); command.Parameters.AddWithValue("detail", audit.SafeDetail);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AppendExportAsync(ReportExportAudit exportRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exportRequest);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (NpgsqlCommand scope = new("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection)) { scope.Parameters.AddWithValue("scope", exportRequest.TargetId.ToString("D")); await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using NpgsqlCommand command = new("SELECT reporting.record_report_export(@event,@target,@run,@actor,@section,@format,@rows);", connection) { CommandTimeout = 2 };
        command.Parameters.AddWithValue("event", Guid.NewGuid()); command.Parameters.AddWithValue("target", exportRequest.TargetId); command.Parameters.AddWithValue("run", exportRequest.RunId); command.Parameters.AddWithValue("actor", exportRequest.ActorSid); command.Parameters.AddWithValue("section", exportRequest.Section); command.Parameters.AddWithValue("format", exportRequest.Format); command.Parameters.AddWithValue("rows", exportRequest.RowCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
