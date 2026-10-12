using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed partial class PostgreSqlCollectorRuntimeRepositoryPort
{
    private const string CommitDeadlockReadSql = """
        SELECT result_status, inserted_count, duplicate_count, rejected_count, persisted_bytes, committed_at
        FROM control.commit_deadlock_read_collection_run(
            @run_id,@instance_id,@target_revision,@collector_id,@collector_version,@output_schema_version,
            @schedule_revision,@scheduled_at,@work_key,@owner_execution_id,@fencing_token,@request_digest,
            @outcome,@reason_code,@duration_ms,@attempt_count,@source_row_count,@output_item_count,
            @response_bytes,@output_bytes,@loss_kind,@minimum_lost_items,@loss_count_is_exact,@minimum_lost_bytes,
            @next_circuit_state,@next_consecutive_failures,@deadlock_occurred_ats,@deadlock_event_ids,
            @deadlock_fingerprints,@deadlock_participant_counts,@deadlock_relation_counts,@deadlock_parse_truncated,
            @deadlock_participant_json,@deadlock_relation_json,@deadlock_sizes,@source_proof);
        """;

    public async ValueTask<DeadlockReadCheckpointResult> ReadDeadlockCheckpointAsync(BeginCollectorRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDeadlockReadWork(request.Work);
        ValidateLeaseKey(request.Work, request.Lease);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
            await ConfigureDeadlockReadAsync(connection, transaction, request.Work, request.Timeout, timeout.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("""
                SELECT read_at,payload::text,source_digest FROM control.read_deadlock_read_checkpoint(
                    @instance_id,@target_revision,@schedule_revision,@run_id,@owner_execution_id,@fencing_token)
                """, connection, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout) };
            AddRunIdentity(command, request.Work, request.RunId, request.Lease, CreateRequestDigest(request.Work, request.RunId, request.Lease));
            DeadlockReadCheckpointResult result;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false)) throw new InvalidDataException("Deadlock checkpoint read returned no result.");
                result = DeadlockReadCheckpointCodec.Decode(request, PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 0), reader.GetString(1), reader.GetString(2));
                if (await reader.ReadAsync(timeout.Token).ConfigureAwait(false)) throw new InvalidDataException("Deadlock checkpoint read returned extra results.");
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("deadlock checkpoint read", exception);
        }
    }

    public async ValueTask<CollectorRunCommitResult> CommitDeadlockReadAsync(CommitCollectorRunRequest request, DeadlockReadSourceProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proof);
        ValidateDeadlockReadWork(request.Work);
        ValidateCommit(request);
        if (proof.TargetId != request.Work.TargetId || proof.TargetRevision != request.Work.TargetRevision
            || proof.ScheduleRevision != request.Work.ScheduleRevision.Value || proof.RunId != request.Summary.RunId
            || proof.State == DeadlockReadSourceState.Ready && (request.Summary.Accounting.SourceRowsRead != proof.Positions.Count
                || request.Summary.Accounting.ResponseBytes < 128 + 64 * proof.Positions.Count
                || request.Payload.Deadlocks.Items.Count > proof.Positions.Count
                || proof.SourceLoss && !request.Summary.Loss.HasLoss
                || request.Payload.Deadlocks.Items.Any(d => d.ParseTruncated) && !proof.SourceLoss))
            throw new ArgumentException("Deadlock source proof does not match the canonical commit.", nameof(proof));
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
            await ConfigureDeadlockReadAsync(connection, transaction, request.Work, request.Timeout, timeout.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(CommitDeadlockReadSql, connection, transaction)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout) };
            AddRunIdentity(command, request.Work, request.Summary.RunId, request.Lease, CreateRequestDigest(request.Work, request.Summary.RunId, request.Lease));
            AddCommitParameters(command, request, activityCollector: false, deadlockCollector: true, databaseInventoryV2: false);
            // PostgreSQL persists microseconds; the cache and canonical event vector must agree.
            command.Parameters["deadlock_occurred_ats"].Value = request.Payload.Deadlocks.Items
                .Select(d => DeadlockReadTime.ToRepositoryPrecision(d.OccurredAtUtc)).ToArray();
            command.Parameters.AddWithValue("source_proof", NpgsqlDbType.Jsonb, DeadlockReadCheckpointCodec.Encode(proof));
            CollectorRunCommitResult result;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false)) throw new InvalidDataException("Atomic deadlock commit returned no result.");
                CollectorRunCommitStatus status = reader.GetString(0) switch
                {
                    "committed" => CollectorRunCommitStatus.Committed,
                    "replayed" => CollectorRunCommitStatus.Replayed,
                    "lease_lost" => CollectorRunCommitStatus.LeaseLost,
                    _ => throw new InvalidDataException("Atomic deadlock commit returned an unknown status."),
                };
                result = new(status, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 5));
                if (await reader.ReadAsync(timeout.Token).ConfigureAwait(false)) throw new InvalidDataException("Atomic deadlock commit returned extra results.");
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return result;
        }
        catch (PostgresException exception) when (exception.SqlState == "55000" && exception.MessageText == "worker lease assertion failed")
        {
            return new(CollectorRunCommitStatus.LeaseLost, 0, 0, 0, 0, null);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("atomic deadlock commit", exception);
        }
    }

    private static void ValidateDeadlockReadWork(CollectorDueWorkItem work)
    {
        if (work.CollectorId.Value != "deadlocks.system-health" || work.CollectorManifestVersion != 2 || work.OutputSchemaVersion != 2)
            throw new ArgumentException("Deadlock checkpoint operations require the exact incremental collector contract.", nameof(work));
    }

    private static async Task ConfigureDeadlockReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CollectorDueWorkItem work, RepositoryCallTimeout timeout, CancellationToken token)
    {
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, token).ConfigureAwait(false);
        await using var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true)", connection, transaction)
        { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        scope.Parameters.AddWithValue("target", work.TargetId.Value.ToString("D"));
        await scope.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
