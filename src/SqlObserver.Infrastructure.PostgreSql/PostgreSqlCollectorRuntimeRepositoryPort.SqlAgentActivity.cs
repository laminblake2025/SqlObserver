using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed partial class PostgreSqlCollectorRuntimeRepositoryPort
{
    private const string CommitSqlAgentActivitySql = """
        SELECT * FROM control.commit_sql_agent_activity_collection_run(
          @run_id,@instance_id,@target_revision,@collector_id,@collector_version,@output_schema_version,
          @schedule_revision,@scheduled_at,@work_key,@owner_execution_id,@fencing_token,@request_digest,
          @outcome,@reason_code,@duration_ms,@attempt_count,@source_row_count,@output_item_count,
          @response_bytes,@output_bytes,@loss_kind,@minimum_lost_items,@loss_count_is_exact,@minimum_lost_bytes,
          @next_circuit_state,@next_consecutive_failures,@agent_activity_payload,
          sha256(convert_to(CAST(@agent_activity_payload AS jsonb)::text,'UTF8')));
        """;

    private static async ValueTask<CollectorRunCommitResult> CommitSqlAgentActivityAsync(
        NpgsqlConnection connection, CommitCollectorRunRequest request, byte[] requestDigest,
        CancellationToken cancellationToken)
    {
        if (request.Payload.SqlAgentModuleCapture is { } capture &&
            SqlAgentActivityPersistencePayload.SerializeObservation(capture).Length > 524_288)
            throw new InvalidDataException("Agent activity private projection exceeds its accepted bound.");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(SqlAgentActivityPersistencePayload.Build(request,
            MapOutcome(request.Summary.Outcome), MapReason(request.Summary.Reason)), M9JsonOptions);
        if (json.Length > 530_000)
            throw new InvalidDataException("Agent activity persistence envelope exceeds its accepted bound.");
        await using var command = new NpgsqlCommand(CommitSqlAgentActivitySql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
        };
        AddRunIdentity(command, request.Work, request.Summary.RunId, request.Lease, requestDigest);
        AddSummaryCommitParameters(command, request);
        command.Parameters.AddWithValue("agent_activity_payload", NpgsqlDbType.Jsonb, Encoding.UTF8.GetString(json));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Agent activity commit returned no result row.");
        CollectorRunCommitStatus status = reader.GetString(0) switch
        {
            "committed" => CollectorRunCommitStatus.Committed,
            "replayed" => CollectorRunCommitStatus.Replayed,
            _ => throw new InvalidDataException("Agent activity commit returned an unknown result status."),
        };
        var result = new CollectorRunCommitResult(status, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.IsDBNull(5) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 5));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Agent activity commit returned multiple result rows.");
        return result;
    }
}
