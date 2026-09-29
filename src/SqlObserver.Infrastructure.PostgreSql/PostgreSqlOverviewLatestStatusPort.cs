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
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);
    private const string ExpiredGap = "Latest status projection is stale";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<OverviewLatestStatus?> ReadAsync(
        MonitoredInstanceId targetId, long targetRevision, DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRevision);
        if (cutoffUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Overview cutoff must be UTC.", nameof(cutoffUtc));
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
        var status = new OverviewLatestStatus(
            targetId.Value, rowRevision, generation, publishedAt, collectionState, lastSuccessAt,
            new OverviewValue(alertCount, alertState, alertSource.ObservedAtUtc),
            new OverviewValue(blockedCount, blockedState, blockingSource.ObservedAtUtc),
            sources, issues, resources, gaps);
        return ApplyFreshness(status, cutoffUtc);
    }

    private static OverviewLatestStatus ApplyFreshness(OverviewLatestStatus status, DateTimeOffset cutoffUtc)
    {
        bool publicationExpired = cutoffUtc - status.PublishedAtUtc > MaximumAge;
        HashSet<string> expiredSources = status.Sources
            .Where(pair => pair.Key is ("alerts" or "blocking" or "waits" or "tempDb" or
                "backups" or "availabilityGroups") && SourceExpired(pair.Value, cutoffUtc))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        bool alertsExpired = publicationExpired || expiredSources.Contains("alerts");
        bool blockingExpired = publicationExpired || expiredSources.Contains("blocking");
        if (!publicationExpired && expiredSources.Count == 0 &&
            IsActionable(status.ActiveAlerts.State) && IsActionable(status.BlockedSessions.State))
            return status;

        Dictionary<string, OverviewLatestSource> sources = status.Sources.ToDictionary(
            pair => pair.Key,
            pair => (publicationExpired || expiredSources.Contains(pair.Key))
                ? pair.Value with { State = StaleIfCurrent(pair.Value.State) }
                : pair.Value,
            StringComparer.Ordinal);
        string alertState = sources["alerts"].State;
        string blockingState = sources["blocking"].State;
        IEnumerable<string> freshnessGaps = new[]
        {
            publicationExpired ? ExpiredGap : null,
            alertsExpired || alertState == "stale" ? "Active alert evidence stale" : null,
            blockingExpired || blockingState == "stale" ? "Current blocking evidence stale" : null,
            expiredSources.Contains("waits") ? "Comparable wait evidence stale" : null,
            expiredSources.Contains("tempDb") ? "TempDB evidence stale" : null,
            expiredSources.Contains("backups") ? "Backup evidence stale" : null,
            expiredSources.Contains("availabilityGroups") ? "AG evidence stale" : null,
        }.OfType<string>();
        string[] gaps = freshnessGaps.Concat(status.Gaps)
            .Distinct(StringComparer.Ordinal).Take(32).ToArray();
        return status with
        {
            CollectionState = publicationExpired
                ? StaleIfCurrent(status.CollectionState) : status.CollectionState,
            ActiveAlerts = status.ActiveAlerts with { State = alertState },
            BlockedSessions = status.BlockedSessions with { State = blockingState },
            Sources = sources,
            Issues = status.Issues.Where(issue =>
                (issue.Destination != "alerts" || IsActionable(alertState)) &&
                (issue.Destination != "activity" || IsActionable(blockingState)) &&
                (issue.Title != "Damaged backup" || SourceActionable(sources, "backups")) &&
                (issue.Title != "Availability replica disconnected" ||
                    SourceActionable(sources, "availabilityGroups")) &&
                !publicationExpired).ToArray(),
            Resources = status.Resources.Select(resource =>
            {
                string? source = ResourceSource(resource.Label);
                return publicationExpired || source is not null && expiredSources.Contains(source)
                    ? resource with { State = StaleIfCurrent(resource.State) }
                    : resource;
            }).ToArray(),
            Gaps = gaps,
        };
    }

    private static string? ResourceSource(string label) => label switch
    {
        "TempDB used" => "tempDb",
        "Backup evidence" => "backups",
        "Availability replicas" => "availabilityGroups",
        _ when label.StartsWith("Wait · ", StringComparison.Ordinal) => "waits",
        _ => null,
    };

    private static bool SourceActionable(
        Dictionary<string, OverviewLatestSource> sources, string key) =>
        sources.TryGetValue(key, out OverviewLatestSource? source) && IsActionable(source.State);

    private static bool SourceExpired(OverviewLatestSource source, DateTimeOffset cutoffUtc) =>
        source.State is ("current" or "partial" or "degraded") &&
        (source.ObservedAtUtc is null || cutoffUtc - source.ObservedAtUtc.Value > MaximumAge);

    private static string StaleIfCurrent(string state) =>
        state is "current" or "partial" or "degraded" ? "stale" : state;

    private static bool IsActionable(string state) => state is "current" or "partial";

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
