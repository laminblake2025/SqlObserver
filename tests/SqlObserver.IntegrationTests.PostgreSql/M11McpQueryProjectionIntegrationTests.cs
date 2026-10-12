using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M11McpQueryProjectionIntegrationTests
{
    private readonly PostgreSql18Fixture _fixture;
    public M11McpQueryProjectionIntegrationTests(PostgreSql18Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FixedMcpProjectionFunctionsArePresentAndServerOnly()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string sql = """
            SELECT to_regprocedure('reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz)') IS NOT NULL,
                   to_regprocedure('reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb)') IS NOT NULL,
                   to_regprocedure('reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz)') IS NOT NULL,
                   to_regprocedure('reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer)') IS NOT NULL,
                   to_regprocedure('reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer,timestamptz,uuid)') IS NOT NULL,
                   to_regprocedure('reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz)') IS NOT NULL,
                   to_regprocedure('reporting.get_m10_incident_evidence(uuid,bigint,uuid,integer,timestamptz)') IS NOT NULL,
                   to_regprocedure('reporting.get_m10_incident_generations(uuid,bigint,uuid,integer,timestamptz)') IS NOT NULL,
                   to_regprocedure('reporting.get_m10_incident_evidence(uuid,bigint,uuid,integer,timestamptz,timestamptz,uuid)') IS NOT NULL,
                   to_regprocedure('reporting.get_m10_incident_generations(uuid,bigint,uuid,integer,timestamptz,bigint)') IS NOT NULL;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 10; index++) Assert.True(reader.GetBoolean(index), $"M11 projection {index} is missing.");
        await reader.CloseAsync();

        string[] functions =
        [
            "reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz)",
            "reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb)",
            "reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz)",
            "reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer)",
            "reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer,timestamptz,uuid)",
            "reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz)",
            "reporting.get_m10_incident_evidence(uuid,bigint,uuid,integer,timestamptz)",
            "reporting.get_m10_incident_generations(uuid,bigint,uuid,integer,timestamptz)",
            "reporting.get_m10_incident_evidence(uuid,bigint,uuid,integer,timestamptz,timestamptz,uuid)",
            "reporting.get_m10_incident_generations(uuid,bigint,uuid,integer,timestamptz,bigint)",
        ];
        foreach (string signature in functions)
        {
            await using var privileges = new NpgsqlCommand("SELECT has_function_privilege('sqlobserver_server',@signature,'EXECUTE'),has_function_privilege('sqlobserver_collector',@signature,'EXECUTE'),has_function_privilege('sqlobserver_auditor',@signature,'EXECUTE'),has_function_privilege('public',@signature,'EXECUTE');", connection);
            privileges.Parameters.AddWithValue("signature", signature);
            await using NpgsqlDataReader result = await privileges.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            Assert.True(result.GetBoolean(0), $"Server cannot execute {signature}.");
            Assert.False(result.GetBoolean(1), $"Collector can execute server projection {signature}.");
            Assert.False(result.GetBoolean(2), $"Auditor can execute server projection {signature}.");
            Assert.False(result.GetBoolean(3), $"PUBLIC can execute server projection {signature}.");
            await result.CloseAsync();

            await using var owner = new NpgsqlCommand("SELECT pg_get_userbyid(proowner) FROM pg_catalog.pg_proc WHERE oid=to_regprocedure(@signature);", connection);
            owner.Parameters.AddWithValue("signature", signature);
            Assert.Equal("sqlobserver_migrator", (string?)await owner.ExecuteScalarAsync());
        }

        await using var definition = new NpgsqlCommand("SELECT pg_get_functiondef(to_regprocedure('reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb)'));", connection);
        string functionText = (string?)await definition.ExecuteScalarAsync() ?? string.Empty;
        Assert.Contains("h.run_id", functionText, StringComparison.Ordinal);
        Assert.Contains("p_cursor_dimensions", functionText, StringComparison.Ordinal);
        Assert.Contains("ORDER BY s.observed_at", functionText, StringComparison.Ordinal);

        await using var forecastDefinition = new NpgsqlCommand("SELECT pg_get_functiondef(to_regprocedure('reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer,timestamptz,uuid)'));", connection);
        string forecastText = (string?)await forecastDefinition.ExecuteScalarAsync() ?? string.Empty;
        Assert.Contains("p_limit BETWEEN 1 AND 201", forecastText, StringComparison.Ordinal);
        Assert.Contains("(f.horizon_start,f.forecast_id)", forecastText, StringComparison.Ordinal);
        Assert.Contains("ORDER BY f.horizon_start,f.forecast_id", forecastText, StringComparison.Ordinal);
        Assert.Contains("f.computed_at<=p_snapshot_utc", forecastText, StringComparison.Ordinal);

        await using var metricSnapshotDefinition = new NpgsqlCommand("SELECT pg_get_functiondef(to_regprocedure('reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,timestamptz,uuid,text,jsonb)'));", connection);
        string metricSnapshotText = (string?)await metricSnapshotDefinition.ExecuteScalarAsync() ?? string.Empty;
        Assert.Contains("h.collected_at<=p_snapshot_utc", metricSnapshotText, StringComparison.Ordinal);
        Assert.Contains("h.observed_at<=p_snapshot_utc", metricSnapshotText, StringComparison.Ordinal);
        await using var incidentDefinition = new NpgsqlCommand("SELECT pg_get_functiondef(to_regprocedure('reporting.get_m10_incident_evidence(uuid,bigint,uuid,integer,timestamptz,timestamptz,uuid)')),pg_get_functiondef(to_regprocedure('reporting.get_m10_incident_generations(uuid,bigint,uuid,integer,timestamptz,bigint)'));", connection);
        await using NpgsqlDataReader incidentReader = await incidentDefinition.ExecuteReaderAsync();
        Assert.True(await incidentReader.ReadAsync());
        Assert.Contains("p_cursor_occurred_at", incidentReader.GetString(0), StringComparison.Ordinal);
        Assert.Contains("p_cursor_packet_id", incidentReader.GetString(0), StringComparison.Ordinal);
        Assert.Contains("p_cursor_generation", incidentReader.GetString(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricCursorUsesPostgreSqlJsonbTieOrdering()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid host = Guid.NewGuid();
        Guid run = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m11-jsonb-order", 1);
        await SeedHostAsync(database, target, host);
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.UtcDateTime.Date.AddHours(12);
        DateTimeOffset observed = snapshot.AddSeconds(-30);
        await ExecuteAsync(database,
            "INSERT INTO telemetry.host_metric_snapshot_v2(observed_at,run_id,instance_id,target_revision,host_id,binding_revision,profile_revision,metric_key,metric_value,dimensions,collected_at) VALUES(@observed,@run,@target,1,@host,1,1,'host.cpu.percent',1,'{}'::jsonb,@collected),(@observed,@run,@target,1,@host,1,1,'host.cpu.percent',2,'{\"volume\":\"C\"}'::jsonb,@collected);",
            ("observed", observed), ("run", run), ("target", target), ("host", host), ("collected", observed));

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[IdentityFingerprintKey.RequiredLength]));
        var targetId = new MonitoredInstanceId(target);
        var cursor = new MetricSeriesCursor(targetId, "host.cpu.percent", observed, run, "{}", snapshot, new ObservationTargetRevision(1));
        MetricSeriesPage page = await repository.ReadMetricSeriesAsync(
            new MetricSeriesQuery(
                new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-711"), AuthorizationPrincipalState.Active, [ApplicationRole.Viewer], TargetAuthorizationScope.ForTargets([targetId])),
                targetId, "host.cpu.percent", observed.AddMinutes(-1), snapshot, 1,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), Cursor: cursor), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(2, page.Items[0].Value);
        Assert.Equal("C", page.Items[0].Dimensions["volume"]);
    }

    [Fact]
    public async Task IncidentEvidenceRequiresMembershipGenerationAtSnapshot()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid thread = Guid.NewGuid();
        Guid packet = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m11-incident-snapshot", 1);
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.UtcDateTime.Date.AddHours(12);
        DateTimeOffset occurred = snapshot.AddMinutes(-10);
        await ExecuteAsync(database,
            "INSERT INTO analytics.incident_thread(thread_id,instance_id,target_revision,opened_at,state,current_generation,summary) VALUES(@thread,@target,1,@opened,'open',1,'{}'::jsonb); INSERT INTO analytics.evidence_packet_v2(occurred_at,packet_id,instance_id,target_revision,evidence_kind,source_digest,identity_digest,source_cutoff_digest,evidence,confidence,visibility_state) VALUES(@occurred,@packet,@target,1,'metric',decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),decode(repeat('c',64),'hex'),'{}'::jsonb,.9,'complete'); INSERT INTO analytics.incident_generation(instance_id,target_revision,thread_id,generation,observed_at,state,evidence_packet_id,correlation_digest,supersedes_previous,details) VALUES(@target,1,@thread,1,@generation,'open',@packet,decode(repeat('d',64),'hex'),false,'{}'::jsonb);",
            ("thread", thread), ("target", target), ("opened", occurred), ("occurred", occurred), ("packet", packet), ("generation", snapshot.AddSeconds(1)));

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM reporting.get_m10_incident_evidence(@target,1,@thread,10,@snapshot,NULL,NULL);", connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("thread", thread);
        command.Parameters.AddWithValue("snapshot", snapshot);
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ProjectionFunctionsExecuteBoundedLookaheadAndDiagnosticTiePagination()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m11-pagination", 1);
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.UtcDateTime.Date.AddHours(12);

        // The server adapter asks metric and forecast projections for one
        // sentinel row.  Both functions must accept that bounded lookahead
        // even when the caller uses its maximum page size.
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await SetScopeAsync(serverConnection, target);
        Assert.Equal(0L, await ScalarLongAsync(serverConnection,
            "SELECT count(*) FROM reporting.list_metric_series(@target,1,@from,@to,'host.cpu.percent',1001,@snapshot,@cursor_at,@cursor_run_id,@cursor_metric_key,@cursor_dimensions);",
            ("target", target), ("from", snapshot.AddHours(-1)), ("to", snapshot), ("snapshot", snapshot),
            ("cursor_at", DBNull.Value), ("cursor_run_id", DBNull.Value), ("cursor_metric_key", DBNull.Value), ("cursor_dimensions", DBNull.Value)));
        Assert.Equal(0L, await ScalarLongAsync(serverConnection,
            "SELECT count(*) FROM reporting.get_m10_forecast_scoped(@target,1,'host.cpu.percent','{}'::jsonb,interval '1 day',@snapshot,201);",
            ("target", target), ("snapshot", snapshot)));

        // Equal timestamps force the event id to be part of the continuation
        // key.  There are 101 rows, so limit 100 must expose one sentinel and
        // the final continuation must be empty.
        DateTimeOffset occurred = snapshot.AddSeconds(-30);
        await ExecuteAsync(database,
            "INSERT INTO events.diagnostic_event(occurred_at,event_id,instance_id,event_kind,severity,safe_metadata,collected_at) SELECT @occurred,md5(format('%s-%s',@seed,g))::uuid,@target,'m11.pagination',g % 10,'{}'::jsonb,@collected FROM generate_series(1,101) AS values(g);",
            ("occurred", occurred), ("seed", target.ToString()), ("target", target), ("collected", occurred));
        await ExecuteAsync(database,
            "INSERT INTO events.diagnostic_event(occurred_at,event_id,instance_id,event_kind,severity,safe_metadata,collected_at) VALUES(@occurred,@event_id,@target,'m11.late',1,'{}'::jsonb,@collected);",
            ("occurred", occurred.AddSeconds(1)), ("event_id", Guid.NewGuid()), ("target", target), ("collected", snapshot.AddSeconds(1)));
        Assert.Equal(100L, await CountDiagnosticsAsync(serverConnection, target, snapshot, 100, null, null));
        Assert.Equal(101L, await CountDiagnosticsAsync(serverConnection, target, snapshot, 101, null, null));
        Assert.Equal(101L, await CountDiagnosticsAsync(serverConnection, target, snapshot, 1001, null, null));

        await using NpgsqlConnection adminConnection = await database.DataSource.OpenConnectionAsync();
        Guid lastFirstPage = await ScalarGuidAsync(adminConnection,
            "SELECT event_id FROM events.diagnostic_event WHERE instance_id=@target AND occurred_at=@occurred ORDER BY occurred_at,event_id OFFSET 99 LIMIT 1;",
            ("target", target), ("occurred", occurred));
        Assert.Equal(1L, await CountDiagnosticsAsync(serverConnection, target, snapshot, 101, occurred, lastFirstPage));
        Assert.Equal(0L, await CountDiagnosticsAsync(serverConnection, target, snapshot, 101, occurred, await ScalarGuidAsync(adminConnection,
            "SELECT event_id FROM events.diagnostic_event WHERE instance_id=@target AND occurred_at=@occurred ORDER BY occurred_at,event_id OFFSET 100 LIMIT 1;",
            ("target", target), ("occurred", occurred))));

        await using var definitions = new NpgsqlCommand("SELECT pg_get_functiondef(to_regprocedure('reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz,integer)')),pg_get_functiondef(to_regprocedure('reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz)'));", serverConnection);
        await using NpgsqlDataReader definitionReader = await definitions.ExecuteReaderAsync();
        Assert.True(await definitionReader.ReadAsync());
        Assert.Contains("p_limit", definitionReader.GetString(0), StringComparison.Ordinal);
        Assert.Contains("101", definitionReader.GetString(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricAndForecastProjectionPagesUseCompleteTieKeys()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid host = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m11-page-data", 1);
        await SeedHostAsync(database, target, host);
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.UtcDateTime.Date.AddHours(12);
        DateTimeOffset observed = snapshot.AddSeconds(-30);
        await SeedMetricRowsAsync(database, target, host, observed);
        await SeedForecastRowsAsync(database, target, snapshot.AddMinutes(-10), snapshot.AddHours(1));

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await SetScopeAsync(connection, target);

        List<(DateTimeOffset ObservedAt, Guid RunId)> metricLookahead = await ReadMetricRowsAsync(connection, target, snapshot, 1001, null, null);
        Assert.Equal(1001, metricLookahead.Count);
        Assert.Equal(1001, metricLookahead.Select(static row => row.RunId).Distinct().Count());
        Assert.All(metricLookahead, row => Assert.Equal(observed, row.ObservedAt));
        List<(DateTimeOffset ObservedAt, Guid RunId)> metricPage2 = await ReadMetricRowsAsync(connection, target, snapshot, 1001, metricLookahead[999].ObservedAt, metricLookahead[999].RunId);
        Assert.Single(metricPage2);
        Assert.Equal(metricLookahead[1000], metricPage2[0]);
        Assert.Empty(await ReadMetricRowsAsync(connection, target, snapshot, 1001, metricPage2[0].ObservedAt, metricPage2[0].RunId));
        Assert.Equal(1001, metricLookahead.Take(1000).Concat(metricPage2).Select(static row => row.RunId).Distinct().Count());

        List<(DateTimeOffset HorizonStart, Guid ForecastId)> forecastLookahead = await ReadForecastRowsAsync(connection, target, snapshot, 201, null, null);
        Assert.Equal(201, forecastLookahead.Count);
        Assert.Equal(201, forecastLookahead.Select(static row => row.ForecastId).Distinct().Count());
        List<(DateTimeOffset HorizonStart, Guid ForecastId)> forecastPage2 = await ReadForecastRowsAsync(connection, target, snapshot, 201, forecastLookahead[199].HorizonStart, forecastLookahead[199].ForecastId);
        Assert.Single(forecastPage2);
        Assert.Equal(forecastLookahead[200], forecastPage2[0]);
        Assert.Empty(await ReadForecastRowsAsync(connection, target, snapshot, 201, forecastPage2[0].HorizonStart, forecastPage2[0].ForecastId));
        Assert.Equal(201, forecastLookahead.Take(200).Concat(forecastPage2).Select(static row => row.ForecastId).Distinct().Count());
    }

    private static async Task<List<(DateTimeOffset ObservedAt, Guid RunId)>> ReadMetricRowsAsync(NpgsqlConnection connection, Guid target, DateTimeOffset snapshot, int limit, DateTimeOffset? cursorAt, Guid? cursorRunId)
    {
        await using var command = new NpgsqlCommand("SELECT observed_at,run_id FROM reporting.list_metric_series(@target,1,@from,@to,'host.cpu.percent',@limit,@snapshot,@cursor_at,@cursor_run_id,@cursor_metric_key,@cursor_dimensions);", connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("from", snapshot.AddHours(-1));
        command.Parameters.AddWithValue("to", snapshot);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("snapshot", snapshot);
        command.Parameters.AddWithValue("cursor_at", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)cursorAt ?? DBNull.Value);
        command.Parameters.AddWithValue("cursor_run_id", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)cursorRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("cursor_metric_key", NpgsqlTypes.NpgsqlDbType.Text, (object?)(cursorRunId is null ? null : "host.cpu.percent") ?? DBNull.Value);
        command.Parameters.AddWithValue("cursor_dimensions", NpgsqlTypes.NpgsqlDbType.Jsonb, (object?)(cursorRunId is null ? null : "{}") ?? DBNull.Value);
        var rows = new List<(DateTimeOffset ObservedAt, Guid RunId)>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetFieldValue<DateTimeOffset>(0), reader.GetGuid(1)));
        return rows;
    }

    private static async Task<List<(DateTimeOffset HorizonStart, Guid ForecastId)>> ReadForecastRowsAsync(NpgsqlConnection connection, Guid target, DateTimeOffset snapshot, int limit, DateTimeOffset? cursorStart, Guid? cursorId)
    {
        await using var command = new NpgsqlCommand("SELECT horizon_start,forecast_id FROM reporting.get_m10_forecast_scoped(@target,1,'host.cpu.percent','{}'::jsonb,interval '1 day',@snapshot,@limit,@cursor_start,@cursor_id);", connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("snapshot", snapshot);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("cursor_start", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)cursorStart ?? DBNull.Value);
        command.Parameters.AddWithValue("cursor_id", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)cursorId ?? DBNull.Value);
        var rows = new List<(DateTimeOffset HorizonStart, Guid ForecastId)>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetFieldValue<DateTimeOffset>(0), reader.GetGuid(1)));
        return rows;
    }

    private static Task SeedHostAsync(RepositoryTestDatabase database, Guid target, Guid host) =>
        ExecuteAsync(database, "INSERT INTO control.host_binding(instance_id,target_revision,host_id,binding_revision,host_name,identity_fingerprint,binding_state) VALUES(@target,1,@host,1,'sql01',decode(repeat('a',64),'hex'),'active'); INSERT INTO control.host_profile(instance_id,target_revision,host_id,binding_revision,profile_revision,os_family,os_version,cpu_count,memory_bytes,capability_state,profile) VALUES(@target,1,@host,1,1,'Windows','2026',4,4294967296,'available','{\"osFamily\":\"Windows\",\"osVersion\":\"2026\",\"cpuCount\":4,\"memoryBytes\":4294967296,\"capabilityState\":\"available\"}'::jsonb);", ("target", target), ("host", host));

    private static Task SeedMetricRowsAsync(RepositoryTestDatabase database, Guid target, Guid host, DateTimeOffset observed) =>
        ExecuteAsync(database, "INSERT INTO telemetry.host_metric_snapshot_v2(observed_at,run_id,instance_id,target_revision,host_id,binding_revision,profile_revision,metric_key,metric_value,dimensions,collected_at) SELECT @observed,md5(format('%s-%s',@seed,g))::uuid,@target,1,@host,1,1,'host.cpu.percent',g::double precision,'{}'::jsonb,@collected FROM generate_series(1,1001) AS values(g);", ("observed", observed), ("seed", target.ToString()), ("target", target), ("host", host), ("collected", observed));

    private static Task SeedForecastRowsAsync(RepositoryTestDatabase database, Guid target, DateTimeOffset horizonStart, DateTimeOffset horizonEnd) =>
        ExecuteAsync(database, "INSERT INTO analytics.metric_forecast(forecast_id,instance_id,target_revision,metric_key,horizon_start,horizon_end,model,predicted_value,lower_bound,upper_bound,confidence,residual,slope_per_day,source_generation,visibility_state,dimensions,dimension_hash,computed_at) SELECT md5(format('%s-%s',@seed,g))::uuid,@target,1,'host.cpu.percent',@horizon_start,@horizon_end,'m11-test',g::double precision,g::double precision-1,g::double precision+1,1,0,0,1,'complete','{}'::jsonb,sha256(convert_to('{}','UTF8')),@computed FROM generate_series(1,201) AS values(g);", ("seed", target.ToString()), ("target", target), ("horizon_start", horizonStart), ("horizon_end", horizonEnd), ("computed", horizonStart));

    private static async Task<long> CountDiagnosticsAsync(NpgsqlConnection connection, Guid target, DateTimeOffset snapshot, int limit, DateTimeOffset? cursorAt, Guid? cursorEventId)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM reporting.search_m10_diagnostics(@target,1,@from,@to,@limit,@cursor_at,@cursor_event_id,@snapshot);", connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("from", snapshot.AddHours(-1));
        command.Parameters.AddWithValue("to", snapshot);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("cursor_at", (object?)cursorAt ?? DBNull.Value);
        command.Parameters.AddWithValue("cursor_event_id", (object?)cursorEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("snapshot", snapshot);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<Guid> ScalarGuidAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidDataException("Expected event id."));
    }

    private static Task<int> SetScopeAsync(NpgsqlConnection connection, Guid target) =>
        new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection) { Parameters = { new NpgsqlParameter("scope", target.ToString()) } }.ExecuteNonQueryAsync();

    private static async Task InsertTargetAsync(RepositoryTestDatabase database, Guid target, string key, long revision) =>
        await ExecuteAsync(database, "INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at) VALUES(@target,@key,'M11 target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',@revision,statement_timestamp(),statement_timestamp(),statement_timestamp());", ("target", target), ("key", key), ("revision", revision));

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] values)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }
}
