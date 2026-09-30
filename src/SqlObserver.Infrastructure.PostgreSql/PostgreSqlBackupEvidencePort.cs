using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlBackupEvidencePort(NpgsqlDataSource dataSource) : IBackupEvidencePort
{
    public async ValueTask<BackupEvidencePage> ReadAsync(BackupEvidenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, timeout.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, request.Timeout, timeout.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope", request.TargetId.Value.ToString());
                await scope.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
            }
            BackupEvidenceHeader header;
            await using (var command = new NpgsqlCommand("SELECT * FROM reporting.get_backups_v2_header(@target,@at,@revision);", connection, transaction))
            {
                Bind(command, request);
                command.Parameters.Add("revision", NpgsqlDbType.Bigint).Value = (object?)request.TargetRevision ?? DBNull.Value;
                await using NpgsqlDataReader row = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
                if (!await row.ReadAsync(timeout.Token).ConfigureAwait(false)) throw Invalid();
                header = ReadHeader(row, request.TargetId, request.AtUtc);
                if (await row.ReadAsync(timeout.Token).ConfigureAwait(false)) throw Invalid();
            }
            if (request.Cursor is { } cursor && (header.RunId != cursor.RunId || header.TargetRevision != cursor.TargetRevision ||
                header.EvidenceStatus != BackupEvidenceStatus.Retained || header.PolicyHistoryCount != cursor.PolicyHistoryCount ||
                header.QualificationCommandRevision != cursor.QualificationCommandRevision)) throw new BackupEvidenceCursorStaleException();
            var items = new List<BackupDatabaseEvidence>(request.Limit);
            bool hasMore = false;
            if (header.EvidenceStatus == BackupEvidenceStatus.Retained)
            {
                await using var command = new NpgsqlCommand("SELECT * FROM reporting.list_backups_v2_databases(@target,@at,@revision,@run,@limit,@after);", connection, transaction);
                Bind(command, request);
                command.Parameters.AddWithValue("revision", header.TargetRevision);
                command.Parameters.AddWithValue("run", header.RunId!.Value);
                command.Parameters.AddWithValue("limit", request.Limit);
                command.Parameters.AddWithValue("after", request.Cursor?.DatabaseOrdinal ?? 0);
                await using NpgsqlDataReader row = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
                while (await row.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    if (hasMore) throw Invalid();
                    BackupDatabaseEvidence item = ReadDatabase(row, header);
                    if (items.Count == request.Limit) hasMore = true; else items.Add(item);
                }
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return new(header, items.AsReadOnly(), hasMore ? new(request.TargetId, request.AtUtc, header.TargetRevision,
                header.RunId!.Value, items[^1].DatabaseOrdinal, header.PolicyHistoryCount, header.QualificationCommandRevision) : null);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        { throw new UnauthorizedAccessException("Backup evidence target scope was rejected.", exception); }
        catch (PostgresException exception) when (exception.SqlState == "40001")
        { throw new BackupEvidenceCursorStaleException(exception); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("Backup evidence", exception); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Backup evidence could not be decoded.", exception); }
    }

    private static void Bind(NpgsqlCommand command, BackupEvidenceRequest request)
    {
        command.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout);
        command.Parameters.AddWithValue("target", request.TargetId.Value); command.Parameters.AddWithValue("at", request.AtUtc);
    }
    internal static BackupEvidenceHeader ReadHeader(NpgsqlDataReader row, MonitoredInstanceId target,
        DateTimeOffset atUtc, int offset = 0) =>
        new(target, atUtc, row.GetInt64(offset + 1), Nullable<Guid>(row, offset), Utc(row, offset + 2), Utc(row, offset + 3),
            Utc(row, offset + 4), Text(row, offset + 5), Nullable<DateTime>(row, offset + 6), (BackupFreshnessCoverage)row.GetInt16(offset + 7),
            Status(row.GetString(offset + 8)), row.GetInt32(offset + 9), row.GetInt32(offset + 10), row.GetInt64(offset + 11),
            row.GetInt32(offset + 12), row.GetBoolean(offset + 13), row.GetInt64(offset + 14), row.GetInt64(offset + 15));

    internal static BackupDatabaseEvidence ReadDatabase(NpgsqlDataReader row, BackupEvidenceHeader header)
    {
        if (!header.SourceObservedAtUtc.HasValue || !header.HistoryLowerBoundLocal.HasValue) throw Invalid();
        var identity = new BackupV2RunIdentity(header.TargetId, new(header.TargetRevision), new(header.RunId!.Value));
        var context = new BackupV2DatabaseContext(Fingerprint(row, 1), Fingerprint(row, 2), Fingerprint(row, 3),
            Fingerprint(row, 4), Fingerprint(row, 5), Fingerprint(row, 6), !row.IsDBNull(1),
            (BackupRecoveryModel)row.GetInt16(7), Text(row, 8), (BackupFreshnessScope)row.GetInt16(9),
            Text(row, 10), Nullable<bool>(row, 11), header.SourceObservedAtUtc.Value, header.SourceZoneId,
            header.HistoryLowerBoundLocal.Value, row.GetBoolean(12));
        using JsonDocument samplesJson = JsonDocument.Parse(row.GetString(14));
        JsonElement array = samplesJson.RootElement;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 5) throw Invalid();
        var samples = new List<BackupV2SampleRead>(5); var trusted = new List<bool>(5);
        foreach (JsonElement sample in array.EnumerateArray())
        {
            DateTimeOffset? finish = Value(sample, "finishUtc", e => e.GetDateTimeOffset());
            var freshness = new BackupFreshnessSample((BackupKind)sample.GetProperty("kind").GetInt16(),
                sample.GetProperty("copyOnly").GetBoolean(), finish, Value(sample, "sourceLocalFinish", e => e.GetDateTime()),
                Value(sample, "hasChecksum", e => e.GetBoolean()), Value(sample, "isDamaged", e => e.GetBoolean()),
                Value(sample, "metadataComplete", e => e.GetBoolean()));
            samples.Add(new(freshness, Value(sample, "sourceLocalStart", e => e.GetDateTime()),
                Value(sample, "recordedStartOffsetMinutes", e => e.GetInt16()), Value(sample, "sizeBytes", e => e.GetInt64()),
                sample.GetProperty("firstFork").GetString(), sample.GetProperty("lastFork").GetString()!,
                new((BackupFinishTimeState)sample.GetProperty("timeState").GetInt16(), finish,
                    Value(sample, "qualificationRevision", e => e.GetInt64()))));
            trusted.Add(sample.GetProperty("qualificationTrusted").GetBoolean());
        }
        BackupFreshnessPolicy? policy = null;
        if (!row.IsDBNull(15))
        {
            using JsonDocument policyJson = JsonDocument.Parse(row.GetString(15)); JsonElement p = policyJson.RootElement;
            policy = new(header.TargetId, new(header.TargetRevision), context.Database!, p.GetProperty("policy_revision").GetInt64(),
                p.GetProperty("enabled").GetBoolean(), TimeSpan.FromSeconds(p.GetProperty("maximum_full_age_seconds").GetInt32()),
                Value(p, "maximum_log_age_seconds", e => TimeSpan.FromSeconds(e.GetInt32())),
                TimeSpan.FromSeconds(p.GetProperty("maximum_observation_age_seconds").GetInt32()),
                (FullBackupRequirement)p.GetProperty("full_requirement").GetInt16(), p.GetProperty("require_checksums").GetBoolean());
        }
        return new(row.GetInt16(0), new(context, samples, identity, (BackupFreshnessCoverage)row.GetInt16(13)), policy, trusted.AsReadOnly());
    }
    private static T? Value<T>(JsonElement element, string name, Func<JsonElement, T> read) where T : struct =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : read(element.GetProperty(name));
    private static T? Nullable<T>(NpgsqlDataReader row, int ordinal) where T : struct => row.IsDBNull(ordinal) ? null : row.GetFieldValue<T>(ordinal);
    private static DateTimeOffset? Utc(NpgsqlDataReader row, int ordinal) => row.IsDBNull(ordinal) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(row, ordinal);
    private static string? Text(NpgsqlDataReader row, int ordinal) => row.IsDBNull(ordinal) ? null : row.GetString(ordinal);
    private static string? Fingerprint(NpgsqlDataReader row, int ordinal) => row.IsDBNull(ordinal) ? null : Convert.ToHexStringLower(row.GetFieldValue<byte[]>(ordinal));
    private static InvalidDataException Invalid() => new("The backup evidence repository contract is invalid.");
    private static BackupEvidenceStatus Status(string value) => value switch
    {
        "not_collected" => BackupEvidenceStatus.NotCollected, "collection_failed" => BackupEvidenceStatus.CollectionFailed,
        "evidence_not_retained" => BackupEvidenceStatus.EvidenceNotRetained, "withheld_by_limit" => BackupEvidenceStatus.WithheldByLimit,
        "no_data" => BackupEvidenceStatus.NoData, "retained" => BackupEvidenceStatus.Retained, _ => throw Invalid(),
    };
}
