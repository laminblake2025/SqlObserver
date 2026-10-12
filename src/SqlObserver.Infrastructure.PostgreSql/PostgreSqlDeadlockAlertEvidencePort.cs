using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlDeadlockAlertEvidencePort(NpgsqlDataSource dataSource) : IDeadlockAlertEvidencePort
{
    private static readonly string[] Properties = ["schemaVersion", "targetId", "targetRevision", "validUntilUtc",
        "scheduleRevision", "runId", "completedAtUtc", "windowFromUtc", "windowToUtc", "retainedEvents",
        "deadlockCount", "reason", "completionDigest"];

    public async ValueTask<DeadlockAlertEvidence> ReadAsync(MonitoredInstanceId targetId, long targetRevision,
        WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRevision);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeout);
        if (lease.Key.Value != "alerts/evaluation/reconcile")
            throw new ArgumentException("The alert coordinator lease is required.", nameof(lease));
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
                "SELECT read_at,payload,source_digest FROM alerting.read_deadlock_alert_evidence(@target,@revision,@owner,@fencing);",
                connection, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(bounded) };
            command.Parameters.AddWithValue("target", targetId.Value);
            command.Parameters.AddWithValue("revision", targetRevision);
            command.Parameters.AddWithValue("owner", lease.Owner.Value);
            command.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
            DeadlockAlertEvidence result;
            await using (NpgsqlDataReader row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
            {
                if (!await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
                DateTimeOffset readAt = row.GetFieldValue<DateTimeOffset>(0);
                string payload = row.GetString(1), digest = row.GetString(2);
                if (Encoding.UTF8.GetByteCount(payload) > 2048 ||
                    digest != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))) throw Invalid();
                result = Decode(payload, digest, readAt);
                if (result.TargetId != targetId || result.TargetRevision != targetRevision ||
                    await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
            }
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("deadlock alert evidence read", exception);
        }
    }

    private static DeadlockAlertEvidence Decode(string payload, string digest, DateTimeOffset readAt)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload, new() { MaxDepth = 3 });
            JsonElement root = document.RootElement;
            int schema = root.GetProperty("schemaVersion").GetInt32();
            string[] properties = schema == 2 ? [.. Properties, "collectorVersion", "outputSchemaVersion", "checkpointDigest"] : Properties;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(properties.Order(StringComparer.Ordinal)) || schema is not (1 or 2))
                throw Invalid();
            DeadlockAlertEvidenceReason reason = root.GetProperty("reason").GetString() switch
            {
                "current" => DeadlockAlertEvidenceReason.Current,
                "collector_disabled" => DeadlockAlertEvidenceReason.CollectorDisabled,
                "source_unavailable" => DeadlockAlertEvidenceReason.SourceUnavailable,
                "source_stale" => DeadlockAlertEvidenceReason.SourceStale,
                "source_incomplete" => DeadlockAlertEvidenceReason.SourceIncomplete,
                "source_inconsistent" => DeadlockAlertEvidenceReason.SourceInconsistent,
                _ => throw Invalid(),
            };
            var result = new DeadlockAlertEvidence(new(root.GetProperty("targetId").GetGuid()),
                root.GetProperty("targetRevision").GetInt64(), readAt, Utc(root, "validUntilUtc"),
                Number(root, "scheduleRevision"), root.GetProperty("runId").ValueKind == JsonValueKind.Null
                    ? null : root.GetProperty("runId").GetGuid(), Utc(root, "completedAtUtc"),
                Utc(root, "windowFromUtc"), Utc(root, "windowToUtc"),
                Number(root, "retainedEvents") is { } retained ? checked((int)retained) : null,
                Number(root, "deadlockCount") is { } count ? checked((int)count) : null,
                reason, root.GetProperty("completionDigest").GetString(), digest,
                schema == 2 ? root.GetProperty("collectorVersion").GetInt32() : 1,
                schema == 2 ? root.GetProperty("outputSchemaVersion").GetInt32() : 1,
                schema == 2 ? root.GetProperty("checkpointDigest").GetString() : null);
            if (schema == 2 && (result.CollectorVersion != 2 || result.OutputSchemaVersion != 2)) throw Invalid();
            result.Validate();
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException("Deadlock alert evidence returned an invalid envelope.", exception);
        }
    }

    private static DateTimeOffset? Utc(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetDateTimeOffset();
    private static long? Number(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetInt64();
    private static InvalidDataException Invalid() => new("Deadlock alert evidence returned an invalid envelope.");
}
