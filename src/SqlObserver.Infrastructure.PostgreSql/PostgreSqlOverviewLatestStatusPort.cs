using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Reads the staged latest-status row through the target-scoped repository function.</summary>
public sealed class PostgreSqlOverviewLatestStatusPort(NpgsqlDataSource dataSource)
    : IOverviewLatestStatusRepositoryPort
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<OverviewLatestStatus?> ReadAsync(
        MonitoredInstanceId targetId, long targetRevision, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRevision);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.role','Viewer',true)," +
            "set_config('sqlobserver.target_scope',@scope,true);", connection, transaction)
        { CommandTimeout = 5 })
        {
            scope.Parameters.AddWithValue("scope", targetId.Value.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand(
            "SELECT target_revision,generation,published_at,collection_state,last_success_at," +
            "active_alert_count,active_alert_state,blocked_session_count,blocked_session_state," +
            "source_states,issues,resources,gaps " +
            "FROM reporting.get_overview_latest_status(@id,@revision);",
            connection, transaction)
        { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", targetId.Value);
        command.Parameters.AddWithValue("revision", targetRevision);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        long rowRevision = reader.GetInt64(0);
        long generation = reader.GetInt64(1);
        DateTimeOffset publishedAt = Utc(reader.GetDateTime(2));
        string collectionState = reader.GetString(3);
        DateTimeOffset? lastSuccessAt = reader.IsDBNull(4) ? null : Utc(reader.GetDateTime(4));
        int? alertCount = reader.IsDBNull(5) ? null : reader.GetInt32(5);
        string alertState = reader.GetString(6);
        int? blockedCount = reader.IsDBNull(7) ? null : reader.GetInt32(7);
        string blockedState = reader.GetString(8);
        Dictionary<string, OverviewLatestSource> sources = ParseSources(reader.GetString(9));
        OverviewIssue[] issues = ParseArray<OverviewIssue>(reader.GetString(10), 10);
        OverviewResource[] resources = ParseArray<OverviewResource>(reader.GetString(11), 32);
        string[] gaps = ParseArray<string>(reader.GetString(12), 32);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Overview latest status returned multiple target rows.");
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);

        if (rowRevision != targetRevision || generation <= 0 ||
            !sources.TryGetValue("alerts", out OverviewLatestSource? alertSource) ||
            !sources.TryGetValue("blocking", out OverviewLatestSource? blockingSource) ||
            alertSource.State != alertState || blockingSource.State != blockedState ||
            issues.Any(x => x.TargetId != targetId.Value || x.Title.Length is < 1 or > 256 ||
                            x.Detail.Length > 1024 || x.Priority is < 0 or > 10) ||
            resources.Any(x => x.TargetId != targetId.Value || x.Label.Length is < 1 or > 256 ||
                               x.Value is { } value && !double.IsFinite(value)) ||
            gaps.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256))
            throw new InvalidDataException("Overview latest status violates its target or payload contract.");
        return new OverviewLatestStatus(
            targetId.Value, rowRevision, generation, publishedAt, collectionState, lastSuccessAt,
            new OverviewValue(alertCount, alertState, alertSource.ObservedAtUtc),
            new OverviewValue(blockedCount, blockedState, blockingSource.ObservedAtUtc),
            sources, issues, resources, gaps);
    }

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeSpan.Zero);

    private static Dictionary<string, OverviewLatestSource> ParseSources(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out JsonElement version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) ||
            number != 1)
            throw new InvalidDataException("Overview latest source schema is unsupported.");

        var sources = new Dictionary<string, OverviewLatestSource>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.NameEquals("schemaVersion")) continue;
            if (property.Name.Length is < 1 or > 64 || property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("state", out JsonElement stateElement) ||
                stateElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Overview latest source entry is invalid.");
            string? state = stateElement.GetString();
            if (string.IsNullOrWhiteSpace(state) || state.Length > 64)
                throw new InvalidDataException("Overview latest source state is invalid.");
            DateTimeOffset? at = null;
            if (property.Value.TryGetProperty("observedAtUtc", out JsonElement time) &&
                time.ValueKind != JsonValueKind.Null)
            {
                if (time.ValueKind != JsonValueKind.String ||
                    !time.TryGetDateTimeOffset(out DateTimeOffset parsed) ||
                    parsed.Offset != TimeSpan.Zero)
                    throw new InvalidDataException("Overview latest source timestamp must be UTC.");
                at = parsed;
            }
            if (!sources.TryAdd(property.Name, new OverviewLatestSource(state, at)))
                throw new InvalidDataException("Overview latest source name is duplicated.");
        }
        return sources;
    }

    private static T[] ParseArray<T>(string json, int limit)
    {
        T[]? values = JsonSerializer.Deserialize<T[]>(json, JsonOptions);
        if (values is null || values.Length > limit || values.Any(static x => x is null))
            throw new InvalidDataException("Overview latest status array is invalid.");
        return values;
    }
}
