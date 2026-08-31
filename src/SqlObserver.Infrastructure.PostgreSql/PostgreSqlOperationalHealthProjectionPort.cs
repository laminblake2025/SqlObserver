using Npgsql;
using System.Text;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Target-scoped latest-run M9 projections. Callers never choose a run identity.</summary>
public sealed class PostgreSqlOperationalHealthProjectionPort : IOperationalHealthRepositoryPort
{
    private const string HeaderSql = "SELECT run_id,target_revision,observed_at,state FROM reporting.get_latest_m9_run(@instance_id,@collector_id);";
    private readonly NpgsqlDataSource dataSource;
    public PostgreSqlOperationalHealthProjectionPort(NpgsqlDataSource dataSource) => this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<BackupStatusSnapshot?> GetBackupsAsync(OperationalHealthRequest request, CancellationToken cancellationToken)
    {
        Header header = await ReadHeaderAsync(request, "backups.status", cancellationToken).ConfigureAwait(false);
        var state = ParseState(header.State);
        PageCursor? cursor = ReadPageCursor(request, header, "backups", null, null);
        if (!header.HasValue) return new BackupStatusSnapshot(request.TargetId, header.Revision, header.RunId, header.ObservedAtUtc, state, [], 0, false);
        var items = new List<BackupStatusObservation>();
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, request.TargetId, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT * FROM reporting.list_backup_status(@instance_id,@run_id,@target_revision,@after_finish,@after_finish_is_null,@after_backup_set_id,@after_database,@after_kind,@limit);", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("run_id", header.RunId!.Value); command.Parameters.AddWithValue("target_revision", header.Revision.Value);
        bool afterFinishIsNull = cursor?.A == NullUtcCursor;
        command.Parameters.AddWithValue("after_finish", cursor?.A is null or "" or NullUtcCursor ? DBNull.Value : DateTimeOffset.Parse(cursor.A, null, System.Globalization.DateTimeStyles.RoundtripKind));
        command.Parameters.AddWithValue("after_finish_is_null", afterFinishIsNull);
        command.Parameters.AddWithValue("after_backup_set_id", cursor?.B is null or "" ? DBNull.Value : long.Parse(cursor.B, System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("after_database", cursor?.C is null or "" ? DBNull.Value : Convert.FromHexString(cursor.C));
        command.Parameters.AddWithValue("after_kind", cursor?.D is null or "" ? DBNull.Value : short.Parse(cursor.D, System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("limit", request.Limit + 1);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(new BackupStatusObservation(request.TargetId, header.Revision, Convert.ToHexString((byte[])reader[0]).ToLowerInvariant(), (BackupKind)reader.GetInt16(1), reader.IsDBNull(3)?null:reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4)?null:reader.GetFieldValue<DateTime>(4), reader.GetBoolean(5), reader.IsDBNull(6)?null:reader.GetInt64(6), reader.IsDBNull(7)?null:reader.GetBoolean(7), reader.IsDBNull(8)?null:reader.GetBoolean(8), reader.IsDBNull(9)?null:reader.GetBoolean(9), (BackupCoverage)reader.GetInt16(10)) { BackupSetId = reader.IsDBNull(2)?null:reader.GetInt64(2) });
        bool more = items.Count > request.Limit; if (more) items.RemoveAt(items.Count - 1);
        string? next = more && items.Count > 0 ? EncodePageCursor(request.TargetId, header, new PageCursor("backups", header.RunId!.Value, header.Revision.Value, null, null, items[^1].LastFinishUtc?.ToString("O") ?? NullUtcCursor, items[^1].BackupSetId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", items[^1].DatabaseFingerprint, ((int)items[^1].Kind).ToString(System.Globalization.CultureInfo.InvariantCulture))) : null;
        return new BackupStatusSnapshot(request.TargetId, header.Revision, header.RunId, header.ObservedAtUtc, state, items, items.Count, state is OperationalObservationState.Partial) { NextCursor = next };
    }
    public async ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(OperationalHealthRequest request, CancellationToken cancellationToken)
    {
        Header header = await ReadHeaderAsync(request, "sql-agent.failures", cancellationToken).ConfigureAwait(false);
        DateTimeOffset to = request.ToUtc ?? DateTimeOffset.UtcNow, from = request.FromUtc ?? to.AddHours(-24);
        if (to <= from || to - from > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(request), "Agent detection window must be 0-7 days and UTC.");
        PageCursor? cursor = ReadPageCursor(request, header, "agent", from, to);
        if (!header.HasValue) return new SqlAgentFailureSnapshot(request.TargetId, header.Revision, header.RunId, header.ObservedAtUtc, ParseState(header.State), [], 0, false, from, to);
        var items = new List<SqlAgentFailureObservation>();
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, request.TargetId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset? afterDetected = cursor?.A is null or "" ? null : DateTimeOffset.Parse(cursor.A, null, System.Globalization.DateTimeStyles.RoundtripKind);
        byte[]? afterFingerprint = cursor?.B is null or "" ? null : Convert.FromHexString(cursor.B);
        await using var command = new NpgsqlCommand("SELECT * FROM reporting.list_sql_agent_failures(@instance_id,@run_id,@target_revision,@from_utc,@to_utc,@after_detected,@after_fingerprint,@limit);", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("run_id", header.RunId!.Value); command.Parameters.AddWithValue("target_revision", header.Revision.Value); command.Parameters.AddWithValue("from_utc", from); command.Parameters.AddWithValue("to_utc", to); command.Parameters.AddWithValue("after_detected", (object?)afterDetected ?? DBNull.Value); command.Parameters.AddWithValue("after_fingerprint", (object?)afterFingerprint ?? DBNull.Value); command.Parameters.AddWithValue("limit", request.Limit + 1);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(new SqlAgentFailureObservation(request.TargetId, header.Revision, reader.GetGuid(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3), (AgentFailureKind)reader.GetInt16(4), reader.IsDBNull(5)?null:reader.GetInt32(5), reader.IsDBNull(6)?null:reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetFieldValue<DateTimeOffset>(9), Convert.ToHexString((byte[])reader[10]).ToLowerInvariant()));
        bool more = items.Count > request.Limit; if (more) items.RemoveAt(items.Count - 1);
        string? next = more && items.Count > 0 ? EncodePageCursor(request.TargetId, header, new PageCursor("agent", header.RunId!.Value, header.Revision.Value, from.ToString("O"), to.ToString("O"), items[^1].DetectedAtUtc.ToString("O"), items[^1].FailureFingerprint, "", "")) : null;
        return new SqlAgentFailureSnapshot(request.TargetId, header.Revision, header.RunId, header.ObservedAtUtc, ParseState(header.State), items, items.Count, false, from, to) { NextCursor = next };
    }
    public async ValueTask<TempDbSnapshot?> GetTempDbAsync(OperationalHealthRequest request, CancellationToken cancellationToken)
    {
        Header header = await ReadHeaderAsync(request, "tempdb.health", cancellationToken).ConfigureAwait(false);
        PageCursor? cursor = ReadPageCursor(request, header, "tempdb", null, null);
        if (!header.HasValue) return new TempDbSnapshot(request.TargetId, header.Revision, header.RunId, header.ObservedAtUtc, ParseState(header.State), null, null, null, null, [], false);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, request.TargetId, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT * FROM reporting.get_tempdb_snapshot(@instance_id,@run_id,@target_revision);", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("run_id", header.RunId!.Value); command.Parameters.AddWithValue("target_revision", header.Revision.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new TempDbSnapshot(request.TargetId, header.Revision, header.RunId, header.ObservedAtUtc, OperationalObservationState.NoData, null, null, null, null, [], false);
        long? total=reader.IsDBNull(2)?null:reader.GetInt64(2), used=reader.IsDBNull(3)?null:reader.GetInt64(3), logTotal=reader.IsDBNull(4)?null:reader.GetInt64(4), logUsed=reader.IsDBNull(5)?null:reader.GetInt64(5); bool truncated=reader.GetBoolean(6); await reader.DisposeAsync().ConfigureAwait(false); await command.DisposeAsync().ConfigureAwait(false);
        var files = new List<TempDbFileObservation>();
        await using var fileCommand = new NpgsqlCommand("SELECT * FROM reporting.list_tempdb_files(@instance_id,@run_id,@target_revision,@after_file_id,@limit);", connection) { CommandTimeout = 5 };
        fileCommand.Parameters.AddWithValue("instance_id", request.TargetId.Value); fileCommand.Parameters.AddWithValue("run_id", header.RunId!.Value); fileCommand.Parameters.AddWithValue("target_revision", header.Revision.Value); fileCommand.Parameters.AddWithValue("after_file_id", cursor?.A is null or "" ? DBNull.Value : int.Parse(cursor.A, System.Globalization.CultureInfo.InvariantCulture)); fileCommand.Parameters.AddWithValue("limit", request.Limit + 1);
        await using NpgsqlDataReader fileReader = await fileCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await fileReader.ReadAsync(cancellationToken).ConfigureAwait(false)) files.Add(new TempDbFileObservation(request.TargetId, header.Revision, fileReader.GetInt32(0), fileReader.GetInt64(1), fileReader.GetInt64(2), fileReader.GetInt64(3), (TempDbComponentState)fileReader.GetInt16(4)));
        bool more = files.Count > request.Limit; if (more) files.RemoveAt(files.Count - 1);
        string? next = more && files.Count > 0 ? EncodePageCursor(request.TargetId, header, new PageCursor("tempdb", header.RunId!.Value, header.Revision.Value, null, null, files[^1].FileId.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "", "")) : null;
        return new TempDbSnapshot(request.TargetId, header.Revision, header.RunId, header.ObservedAtUtc, ParseState(header.State), total, used, logTotal, logUsed, files, truncated) { NextCursor = next };
    }
    public async ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(OperationalHealthRequest request, CancellationToken cancellationToken)
    {
        TempDbSnapshot? snapshot = await GetTempDbAsync(request, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : snapshot with { TotalBytes = null, UsedBytes = null, LogTotalBytes = null, LogUsedBytes = null };
    }
    public async ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(OperationalHealthRequest request, CancellationToken cancellationToken)
        => await GetAvailabilityGroupStreamsAsync(request, includeReplicas: true, includeDatabases: true, cancellationToken).ConfigureAwait(false);

    private async ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupStreamsAsync(OperationalHealthRequest request, bool includeReplicas, bool includeDatabases, CancellationToken cancellationToken)
    {
        Header header = await ReadHeaderAsync(request, "availability-groups.health", cancellationToken).ConfigureAwait(false);
        string cursorKind = includeReplicas && !includeDatabases ? "ag.replicas" : !includeReplicas && includeDatabases ? "ag.databases" : "ag.summary";
        // The summary is intentionally an unpaged view.  Its two child streams
        // return independent cursors below; a cursor from either list route is
        // never accepted by the combined route.
        if (includeReplicas && includeDatabases && request.Cursor is not null)
            throw new ArgumentException("Combined availability-group summaries use child cursors.", nameof(request));
        PageCursor? cursor = includeReplicas && includeDatabases ? null : ReadPageCursor(request, header, cursorKind, null, null);
        // The combined MCP view has no single ordering across the two child
        // streams. Split the bounded page budget between them and mark any
        // omitted tail as truncation; child routes retain independent cursors.
        bool combined = includeReplicas && includeDatabases;
        int streamLimit = combined ? Math.Max(1, request.Limit / 2) : request.Limit;
        if (!header.HasValue) return new AvailabilityGroupsSnapshot(request.TargetId, header.Revision, header.RunId, header.ObservedAtUtc, ParseState(header.State), AvailabilityVisibilityScope.ResolvingLocalOnly, [], [], false);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, request.TargetId, cancellationToken).ConfigureAwait(false);
        var replicas = new List<AvailabilityReplicaObservation>();
        if (includeReplicas) await using (var replicaCommand = new NpgsqlCommand("SELECT * FROM reporting.list_availability_group_replicas(@instance_id,@run_id,@target_revision,@after_group,@after_key,@limit);", connection) { CommandTimeout = 5 })
        {
            replicaCommand.Parameters.AddWithValue("instance_id", request.TargetId.Value); replicaCommand.Parameters.AddWithValue("run_id", header.RunId!.Value); replicaCommand.Parameters.AddWithValue("target_revision", header.Revision.Value); replicaCommand.Parameters.AddWithValue("after_group", cursor?.A is null or "" ? DBNull.Value : Convert.FromHexString(cursor.A)); replicaCommand.Parameters.AddWithValue("after_key", cursor?.B is null or "" ? DBNull.Value : Convert.FromHexString(cursor.B)); replicaCommand.Parameters.AddWithValue("limit", streamLimit + 1);
            await using NpgsqlDataReader reader = await replicaCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) replicas.Add(new AvailabilityReplicaObservation(request.TargetId, header.Revision, Convert.ToHexString((byte[])reader[0]).ToLowerInvariant(), Convert.ToHexString((byte[])reader[1]).ToLowerInvariant(), reader.GetString(2), reader.GetString(3), reader.GetString(4), (AvailabilityVisibilityScope)reader.GetInt16(5), reader.GetBoolean(6)));
        }
        var databases = new List<AvailabilityDatabaseObservation>();
        if (includeDatabases) await using (var databaseCommand = new NpgsqlCommand("SELECT * FROM reporting.list_availability_group_databases(@instance_id,@run_id,@target_revision,@after_group,@after_key,@limit);", connection) { CommandTimeout = 5 })
        {
            databaseCommand.Parameters.AddWithValue("instance_id", request.TargetId.Value); databaseCommand.Parameters.AddWithValue("run_id", header.RunId!.Value); databaseCommand.Parameters.AddWithValue("target_revision", header.Revision.Value); databaseCommand.Parameters.AddWithValue("after_group", cursor?.A is null or "" ? DBNull.Value : Convert.FromHexString(cursor.A)); databaseCommand.Parameters.AddWithValue("after_key", cursor?.B is null or "" ? DBNull.Value : Convert.FromHexString(cursor.B)); databaseCommand.Parameters.AddWithValue("limit", streamLimit + 1);
            await using NpgsqlDataReader reader = await databaseCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) databases.Add(new AvailabilityDatabaseObservation(request.TargetId, header.Revision, Convert.ToHexString((byte[])reader[0]).ToLowerInvariant(), Convert.ToHexString((byte[])reader[1]).ToLowerInvariant(), reader.GetString(2), reader.GetString(3), (AvailabilityVisibilityScope)reader.GetInt16(4), reader.GetBoolean(5)));
        }
        // Both SQL functions use streamLimit + 1 so the adapter can distinguish
        // an exact page from a page with a lookahead row.  The old comparison
        // against request.Limit left the lookahead in combined pages because
        // each child is intentionally capped at half the total budget.
        bool replicasMore = replicas.Count > streamLimit;
        bool databasesMore = databases.Count > streamLimit;
        if (replicasMore) replicas.RemoveAt(replicas.Count - 1);
        if (databasesMore) databases.RemoveAt(databases.Count - 1);
        bool combinedTrimmed = false;
        if (combined && replicas.Count + databases.Count > request.Limit)
        {
            combinedTrimmed = true;
            int excess = replicas.Count + databases.Count - request.Limit;
            while (excess > 0 && databases.Count > 0) { databases.RemoveAt(databases.Count - 1); excess--; }
            while (excess > 0 && replicas.Count > 0) { replicas.RemoveAt(replicas.Count - 1); excess--; }
        }
        string? replicasNext = replicasMore && replicas.Count > 0
            ? EncodePageCursor(request.TargetId, header, new PageCursor("ag.replicas", header.RunId!.Value, header.Revision.Value, null, null, replicas[^1].GroupFingerprint, replicas[^1].ReplicaFingerprint, "", ""))
            : null;
        string? databasesNext = databasesMore && databases.Count > 0
            ? EncodePageCursor(request.TargetId, header, new PageCursor("ag.databases", header.RunId!.Value, header.Revision.Value, null, null, databases[^1].GroupFingerprint, databases[^1].DatabaseFingerprint, "", ""))
            : null;
        bool more = combined ? replicasMore || databasesMore || combinedTrimmed : includeReplicas ? replicasMore : databasesMore;
        string? next = includeReplicas && !includeDatabases ? replicasNext : includeDatabases && !includeReplicas ? databasesNext : null;
        AvailabilityVisibilityScope visibility = replicas.Select(static x => x.VisibilityScope).Concat(databases.Select(static x => x.VisibilityScope)).DefaultIfEmpty(AvailabilityVisibilityScope.ResolvingLocalOnly).First();
        return new AvailabilityGroupsSnapshot(request.TargetId, header.Revision, header.RunId, header.ObservedAtUtc, ParseState(header.State), visibility, replicas, databases, more)
        {
            NextCursor = next,
            ReplicasNextCursor = replicasNext,
            DatabasesNextCursor = databasesNext,
        };
    }
    public async ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(OperationalHealthRequest request, CancellationToken cancellationToken)
    {
        return await GetAvailabilityGroupStreamsAsync(request, includeReplicas: true, includeDatabases: false, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(OperationalHealthRequest request, CancellationToken cancellationToken)
    {
        return await GetAvailabilityGroupStreamsAsync(request, includeReplicas: false, includeDatabases: true, cancellationToken).ConfigureAwait(false);
    }
    private async ValueTask<Header> ReadHeaderAsync(OperationalHealthRequest request, string collectorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > OperationalHealthBounds.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(request));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection)) { scope.Parameters.AddWithValue("scope", request.TargetId.Value.ToString()); await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await using var command = new NpgsqlCommand(HeaderSql, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("instance_id", request.TargetId.Value);
        command.Parameters.AddWithValue("collector_id", collectorId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new Header(null, new ObservationTargetRevision(1), DateTimeOffset.UtcNow, "NoData", false);
        Header header = new(new CollectorRunId(reader.GetGuid(0)), new ObservationTargetRevision(reader.GetInt64(1)), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3), true);
        if (request.Cursor is not null)
        {
            OperationalHealthCursor cursor = OperationalHealthCursor.Decode(request.Cursor);
            if (cursor.TargetId != request.TargetId || cursor.SnapshotUtc != header.ObservedAtUtc) throw new ArgumentException("Cursor is not bound to this target/latest snapshot.", nameof(request));
        }
        return header;
    }

    private static PageCursor? ReadPageCursor(OperationalHealthRequest request, Header header, string kind, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (request.Cursor is null) return null;
        if (!header.HasValue) throw new ArgumentException("Cursor is not bound to an existing latest run.", nameof(request));
        OperationalHealthCursor outer = OperationalHealthCursor.Decode(request.Cursor);
        try
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(outer.TieKey));
            PageCursor cursor = JsonSerializer.Deserialize<PageCursor>(json) ?? throw new ArgumentException("Cursor payload is empty.", nameof(request));
            if (!string.Equals(cursor.Kind, kind, StringComparison.Ordinal) || cursor.RunId != header.RunId!.Value || cursor.Revision != header.Revision.Value || (from is not null && cursor.From != from.Value.ToString("O")) || (to is not null && cursor.To != to.Value.ToString("O"))) throw new ArgumentException("Cursor is not bound to this run, revision, or filter window.", nameof(request));
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("Cursor payload is invalid.", nameof(request), exception);
        }
    }

    private static string EncodePageCursor(MonitoredInstanceId target, Header header, PageCursor cursor)
    {
        string tie = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor)));
        return new OperationalHealthCursor(target, header.ObservedAtUtc, tie).Encode();
    }

    private const string NullUtcCursor = "<null-utc>";
    private sealed record PageCursor(string Kind, Guid RunId, long Revision, string? From, string? To, string A, string B, string C, string D);
    private static OperationalObservationState ParseState(string value) => value switch
    {
        "Complete" => OperationalObservationState.Complete,
        "Partial" => OperationalObservationState.Partial,
        "Degraded" => OperationalObservationState.Degraded,
        "Unsupported" => OperationalObservationState.Unsupported,
        "PermissionDenied" => OperationalObservationState.PermissionDenied,
        _ => OperationalObservationState.NoData,
    };
    private static async ValueTask SetScopeAsync(NpgsqlConnection connection, MonitoredInstanceId target, CancellationToken cancellationToken)
    {
        await using var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection);
        scope.Parameters.AddWithValue("scope", target.Value.ToString());
        await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private readonly record struct Header(CollectorRunId? RunId, ObservationTargetRevision Revision, DateTimeOffset ObservedAtUtc, string State, bool HasValue);
}
