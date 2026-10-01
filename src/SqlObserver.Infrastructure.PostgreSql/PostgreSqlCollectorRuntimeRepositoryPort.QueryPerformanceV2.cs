using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed partial class PostgreSqlCollectorRuntimeRepositoryPort
{
    private const string CommitQueryPerformanceV2Sql = """
        SELECT * FROM control.commit_query_performance_v2(
            @run_id,@instance_id,@target_revision,@schedule_revision,@scheduled_at,@work_key,
            @owner_execution_id,@fencing_token,@request_digest,@outcome,@reason_code,@duration_ms,@attempt_count,
            @source_row_count,@output_item_count,@response_bytes,@output_bytes,@loss_kind,@minimum_lost_items,
            @loss_count_is_exact,@minimum_lost_bytes,@next_circuit_state,@next_consecutive_failures,@completion_digest,
            @window_start,@window_end,@query_source::events.query_performance_source,@query_source_state,
            @query_coverage,@query_freshness,@query_truncated,@query_payload,@cache_payload,@links,@plan_links,@snapshots);
        """;

    private static async ValueTask<CollectorRunCommitResult> CommitQueryPerformanceV2Async(
        NpgsqlConnection connection, CommitCollectorRunRequest request, byte[] requestDigest,
        CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
        {
            scope.Parameters.AddWithValue("scope", request.Work.TargetId.Value.ToString());
            await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var command = new NpgsqlCommand(CommitQueryPerformanceV2Sql, connection, transaction)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
        };
        AddRunIdentity(command, request.Work, request.Summary.RunId, request.Lease, requestDigest);
        AddQueryPerformanceCanonicalParameters(command, request);
        AddQueryPerformanceCommitParameters(command, request);
        // The engine keeps the rejected adapter's accounting. No rejected item
        // is persistable; the SQL contract records its count and bytes as loss.
        if (request.Summary.Outcome == CollectorRunOutcome.OutputInvalid)
        {
            command.Parameters["output_item_count"].Value = 0;
            command.Parameters["output_bytes"].Value = 0L;
        }
        command.Parameters.AddWithValue("cache_payload", NpgsqlDbType.Jsonb,
            request.Payload.PlanCacheRuntime is { } cache
                ? Encoding.UTF8.GetString(PlanCacheRuntimeCommitPayload.Serialize(cache)) : DBNull.Value);
        QueryWaitSnapshotLink[] snapshots = BuildQueryWaitSnapshots(request.Payload.QueryPerformance.Items);
        command.Parameters.AddWithValue("snapshots", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(snapshots.Select(static snapshot => new
            {
                database_id = snapshot.DatabaseId,
                query_fingerprint = snapshot.QueryFingerprint,
                plan_fingerprint = snapshot.PlanFingerprint,
                categories = snapshot.Categories.Select(static category => new
                {
                    category = category.Category,
                    waitMilliseconds = category.WaitMilliseconds,
                }),
            })));
        CollectorRunCommitResult result;
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Query performance v2 commit returned no result row.");
            CollectorRunCommitStatus status = reader.GetString(0) switch
            {
                "committed" => CollectorRunCommitStatus.Committed,
                "replayed" => CollectorRunCommitStatus.Replayed,
                _ => throw new InvalidDataException("Query performance v2 commit returned an unknown result status."),
            };
            result = new(status, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 5));
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Query performance v2 commit returned multiple result rows.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
