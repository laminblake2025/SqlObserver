using System.Text.Json;
using System.Text;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Target-scoped bounded M6 projection reader. Raw event XML is never selected.</summary>
public sealed class PostgreSqlDeadlockProjectionPort : IDeadlockProjectionRepositoryPort
{
    private const string ListSql = "SELECT * FROM control.list_deadlocks(@instance_id,@from_utc,@to_utc,@max_results,@after_utc,@after_event_id,@snapshot_collected_at);";
    private const string DetailSql = "SELECT * FROM control.get_deadlock(@instance_id,@event_id);";
    private readonly NpgsqlDataSource dataSource;
    public PostgreSqlDeadlockProjectionPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    public async ValueTask<DeadlockPage?> ListDeadlocksAsync(ListDeadlocksRepositoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); using CancellationTokenSource scope = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(scope.Token); await using var command = new NpgsqlCommand(ListSql, connection) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout) };
        command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("from_utc", request.FromUtc); command.Parameters.AddWithValue("to_utc", request.ToUtc); command.Parameters.AddWithValue("max_results", request.MaxResults); command.Parameters.AddWithValue("after_utc", (object?)request.Cursor?.OccurredAtUtc ?? DBNull.Value); command.Parameters.AddWithValue("after_event_id", (object?)request.Cursor?.EventId ?? DBNull.Value); command.Parameters.AddWithValue("snapshot_collected_at", (object?)request.Cursor?.SnapshotCollectedAtUtc ?? DBNull.Value);
        var rows = new List<(DeadlockSummaryDto Item, DateTimeOffset Snapshot)>(request.MaxResults + 1); DateTimeOffset repositoryTime = request.Cursor?.SnapshotCollectedAtUtc ?? DateTimeOffset.UtcNow;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(scope.Token); while (await reader.ReadAsync(scope.Token)) { DateTimeOffset snapshot = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 7); repositoryTime = snapshot; if (reader.IsDBNull(1)) continue; DateTimeOffset occurred = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 0); DateTimeOffset collected = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 6); byte[] fingerprint = reader.GetFieldValue<byte[]>(2); int participantCount = reader.GetInt32(3); int relationCount = reader.GetInt32(4); if (fingerprint.Length != 32 || participantCount is < 0 or > 128 || relationCount is < 0 or > 256) throw new InvalidDataException("PostgreSQL returned an invalid bounded deadlock summary."); var item = new DeadlockSummaryDto(request.TargetId, reader.GetGuid(1), occurred, Convert.ToHexString(fingerprint).ToLowerInvariant(), participantCount, relationCount, reader.GetBoolean(5), collected); rows.Add((item, snapshot)); }
        bool hasMore = rows.Count > request.MaxResults; if (hasMore) rows.RemoveAt(rows.Count - 1); DeadlockPageCursor? next = rows.Count == 0 ? null : new DeadlockPageCursor(request.TargetId, rows[^1].Item.OccurredAtUtc, rows[^1].Item.EventId, rows[^1].Snapshot, request.FromUtc, request.ToUtc);
        return new DeadlockPage(request.TargetId, repositoryTime, rows.Select(static row => row.Item).ToArray(), hasMore ? next : null);
    }
    public async ValueTask<DeadlockDetailDto?> GetDeadlockAsync(MonitoredInstanceId targetId, Guid eventId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource scope = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken); await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(scope.Token); await using var command = new NpgsqlCommand(DetailSql, connection) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) }; command.Parameters.AddWithValue("instance_id", targetId.Value); command.Parameters.AddWithValue("event_id", eventId); await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(scope.Token); if (!await reader.ReadAsync(scope.Token)) return null;
        DateTimeOffset occurred = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 0); byte[] fingerprint = reader.GetFieldValue<byte[]>(2); int participantCount = reader.GetInt32(3); int relationCount = reader.GetInt32(4); if (fingerprint.Length != 32 || participantCount is < 0 or > 128 || relationCount is < 0 or > 256) throw new InvalidDataException("PostgreSQL returned an invalid bounded deadlock detail.");
        var summary = new DeadlockSummaryDto(targetId, reader.GetGuid(1), occurred, Convert.ToHexString(fingerprint).ToLowerInvariant(), participantCount, relationCount, reader.GetBoolean(5), PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 6));
        DeadlockParticipantDto[] participants = ReadParticipants(reader.GetFieldValue<JsonDocument>(7)); DeadlockRelationDto[] relations = ReadRelations(reader.GetFieldValue<JsonDocument>(8)); if (participants.Length != participantCount || relations.Length != relationCount) throw new InvalidDataException("PostgreSQL deadlock detail counts do not match its summary."); return new DeadlockDetailDto(summary, participants, relations);
    }
    private static DeadlockParticipantDto[] ReadParticipants(JsonDocument json)
    {
        JsonElement root = RequireArray(json, 128, 262144, "participant"); var result = new List<DeadlockParticipantDto>(root.GetArrayLength()); var ids = new HashSet<int>();
        foreach (JsonElement value in root.EnumerateArray()) { RequireObject(value, ["sessionId", "victim"], "participant"); int session = ReadSession(value, "sessionId"); if (!ids.Add(session)) throw new InvalidDataException("PostgreSQL returned duplicate deadlock participants."); bool victim = value.GetProperty("victim").ValueKind == JsonValueKind.True ? true : value.GetProperty("victim").ValueKind == JsonValueKind.False ? false : throw new InvalidDataException("PostgreSQL returned an invalid deadlock victim flag."); result.Add(new DeadlockParticipantDto(session, victim)); }
        return result.ToArray();
    }
    private static DeadlockRelationDto[] ReadRelations(JsonDocument json)
    {
        JsonElement root = RequireArray(json, 256, 524288, "relation"); var result = new List<DeadlockRelationDto>(root.GetArrayLength()); var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement value in root.EnumerateArray()) { RequireObject(value, ["blockerSessionId", "waiterSessionId", "resourceCategory", "lockMode"], "relation"); int blocker = ReadSession(value, "blockerSessionId"); int waiter = ReadSession(value, "waiterSessionId"); string category = ReadToken(value, "resourceCategory"); string mode = ReadToken(value, "lockMode"); if (category is not ("key" or "page" or "object_lock" or "metadata" or "exchange" or "other") || DeadlockLockModes.Normalize(mode) != mode) throw new InvalidDataException("PostgreSQL returned an invalid deadlock relation token."); if (!identities.Add($"{blocker}:{waiter}:{category}:{mode}")) throw new InvalidDataException("PostgreSQL returned duplicate deadlock relations."); result.Add(new DeadlockRelationDto(blocker, waiter, category, mode)); }
        return result.ToArray();
    }
    private static JsonElement RequireArray(JsonDocument json, int maxItems, int maxBytes, string kind) { JsonElement root = json.RootElement; if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > maxItems || Encoding.UTF8.GetByteCount(root.GetRawText()) > maxBytes) throw new InvalidDataException($"PostgreSQL returned an invalid bounded deadlock {kind} array."); return root; }
    private static void RequireObject(JsonElement value, string[] keys, string kind) { if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != keys.Length || keys.Any(key => !value.TryGetProperty(key, out _))) throw new InvalidDataException($"PostgreSQL returned an invalid deadlock {kind} object."); }
    private static int ReadSession(JsonElement value, string key) { JsonElement session = value.GetProperty(key); if (session.ValueKind != JsonValueKind.Number || !session.TryGetInt32(out int result) || result is < 1 or > 32767) throw new InvalidDataException("PostgreSQL returned an invalid deadlock session id."); return result; }
    private static string ReadToken(JsonElement value, string key) { JsonElement token = value.GetProperty(key); string? result = token.ValueKind == JsonValueKind.String ? token.GetString() : null; if (result is null || result.Length > 32 || result.Any(char.IsControl)) throw new InvalidDataException("PostgreSQL returned an invalid deadlock token."); return result; }
}
