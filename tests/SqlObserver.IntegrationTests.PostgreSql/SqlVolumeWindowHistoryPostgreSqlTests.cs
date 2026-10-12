using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlVolumeWindowHistoryPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task VolumeWindowHistoryPagesExactInt64EvidenceAndKeepsOldRevisionIdentitySeparate()
    {
        await using RepositoryTestDatabase database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await SqlVolumeWindowHistoryFixture.TargetAsync(database, 2);
        DateTimeOffset at = SqlVolumeWindowHistoryFixture.Now().AddMinutes(-30);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at, 1, total: long.MaxValue, available: long.MaxValue - 1);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(5), 1, total: long.MaxValue, available: long.MaxValue - 2);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(10), 2);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = new PostgreSqlSqlVolumeWindowHistoryRepositoryPort(server);
        var request = new SqlVolumeWindowHistoryCatalogRequest(target, 2, at, at.AddMinutes(20), 1, null, SqlVolumeWindowHistoryFixture.Timeout);
        SqlVolumeWindowHistoryCatalogPage first = Assert.IsType<SqlVolumeWindowHistoryCatalogPage>(await repository.ReadCatalogAsync(request, CancellationToken.None));
        Assert.Equal(1, Assert.Single(first.Items).SampleTargetRevision); Assert.NotNull(first.NextAnchor);
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), first.Items[0].FirstTotalBytes);
        Assert.Null(first.Items[0].AvailableBytesDelta); Assert.Equal("visibility_gaps", first.Items[0].ComparisonState); Assert.Equal("revision_boundary", first.Reason);
        SqlVolumeWindowHistoryCatalogPage second = Assert.IsType<SqlVolumeWindowHistoryCatalogPage>(await repository.ReadCatalogAsync(request with { Cursor = first.NextAnchor }, CancellationToken.None));
        Assert.Equal(2, Assert.Single(second.Items).SampleTargetRevision); Assert.Null(second.NextAnchor); Assert.Equal(first.ReadEpoch, second.ReadEpoch);
        SqlVolumeWindowHistoryCatalogPage comparable = Assert.IsType<SqlVolumeWindowHistoryCatalogPage>(await repository.ReadCatalogAsync(request with { ToUtc = at.AddMinutes(10), Limit = 25 }, CancellationToken.None));
        Assert.Equal("-1", Assert.Single(comparable.Items).AvailableBytesDelta); Assert.Equal("eligible", comparable.Items[0].ComparisonState);
        var history = new SqlVolumeWindowHistoryExactRequest(target, 2, at, at.AddMinutes(20), 1, SqlVolumeWindowHistoryFixture.Key, 1, null, SqlVolumeWindowHistoryFixture.Timeout);
        SqlVolumeWindowHistoryExactPage sample = Assert.IsType<SqlVolumeWindowHistoryExactPage>(await repository.ReadHistoryAsync(history, CancellationToken.None));
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), Assert.Single(sample.Items).TotalBytes);
        Assert.Equal(at.AddSeconds(-2), sample.Items[0].ObservedAtUtc); Assert.Equal(at.AddSeconds(-1), sample.Items[0].CollectedAtUtc);
        SqlVolumeWindowHistoryExactPage tail = Assert.IsType<SqlVolumeWindowHistoryExactPage>(await repository.ReadHistoryAsync(history with { Cursor = sample.NextAnchor }, CancellationToken.None));
        Assert.Equal(at.AddMinutes(5), Assert.Single(tail.Items).CompletedAtUtc); Assert.Null(tail.NextAnchor);
    }

    [Fact]
    public async Task VolumeWindowHistoryFailuresAbsenceUnknownCapacityLossAndElapsedGapsRemainExplicit()
    {
        await using RepositoryTestDatabase database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await SqlVolumeWindowHistoryFixture.TargetAsync(database);
        DateTimeOffset at = SqlVolumeWindowHistoryFixture.Now().AddMinutes(-50);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(5), outcome: "permission_denied", retain: false);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(10), key: new string('b', 64));
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(15), total: null, available: null);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(20), outcome: "partial");
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(40));
        using JsonDocument page = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(45), 250, 1, SqlVolumeWindowHistoryFixture.Key);
        JsonElement root = page.RootElement; Assert.Equal("partial", root.GetProperty("state").GetString());
        string?[] reasons = root.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("reason").GetString()).ToArray();
        Assert.Contains("collection_failed", reasons); Assert.Contains("volume_absent", reasons); Assert.Contains("capacity_unknown", reasons); Assert.Contains("collection_loss", reasons); Assert.Contains("collection_gap", reasons);
        JsonElement failed = root.GetProperty("items")[1]; Assert.Equal(JsonValueKind.Null, failed.GetProperty("totalBytes").ValueKind); Assert.Equal(JsonValueKind.Null, failed.GetProperty("observedAtUtc").ValueKind);
        Assert.True(root.GetProperty("coverage").GetProperty("hasVisibilityGap").GetBoolean());
        Assert.Equal(6, root.GetProperty("coverage").GetProperty("runCount").GetInt32());
    }

    [Fact]
    public async Task VolumeWindowHistoryContinuationRejectsLateCommitRetentionRevisionAndContextDrift()
    {
        await using RepositoryTestDatabase database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await SqlVolumeWindowHistoryFixture.TargetAsync(database);
        DateTimeOffset at = SqlVolumeWindowHistoryFixture.Now().AddMinutes(-30);
        Guid run = await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(5), key: new string('b', 64));
        using JsonDocument first = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(20), 1);
        JsonElement cursor = first.RootElement.GetProperty("nextAnchor");
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at.AddSeconds(1), at.AddMinutes(20), 1, cursor: cursor))).SqlState);
        // A new completed run committed after the read, carrying completion before its cutoff.
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(10));
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(20), 1, cursor: cursor))).SqlState);
        using JsonDocument fresh = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(20), 1);
        JsonElement current = fresh.RootElement.GetProperty("nextAnchor");
        await using (var detach = database.DataSource.CreateCommand("""
            SELECT child.relname FROM pg_inherits link JOIN pg_class child ON child.oid=link.inhrelid
            WHERE link.inhparent='telemetry.sql_volume_snapshot'::regclass AND EXISTS
              (SELECT 1 FROM telemetry.sql_volume_snapshot sample WHERE sample.run_id=@run AND sample.tableoid=child.oid);
            """))
        {
            detach.Parameters.AddWithValue("run", run); string child = Assert.IsType<string>(await detach.ExecuteScalarAsync());
            using var quote = new NpgsqlCommandBuilder();
            await using var remove = database.DataSource.CreateCommand($"ALTER TABLE telemetry.sql_volume_snapshot DETACH PARTITION telemetry.{quote.QuoteIdentifier(child)};");
            await remove.ExecuteNonQueryAsync();
        }
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(20), 1, cursor: current))).SqlState);
        using JsonDocument expired = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(20), 25);
        Assert.Equal("unavailable", expired.RootElement.GetProperty("state").GetString()); Assert.Equal("retained_evidence_unavailable", expired.RootElement.GetProperty("reason").GetString());
        await using var revision = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=revision+1 WHERE instance_id=@id;"); revision.Parameters.AddWithValue("id", target); await revision.ExecuteNonQueryAsync();
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(20), 25))).SqlState);
    }

    [Fact]
    public async Task VolumeWindowHistoryCanonicalPostSealRunsDoNotRequireOriginalMirror()
    {
        await using RepositoryTestDatabase database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        await SqlVolumeWindowHistoryFixture.SealAsync(database);
        Guid target = await SqlVolumeWindowHistoryFixture.TargetAsync(database);
        DateTimeOffset at = SqlVolumeWindowHistoryFixture.Now().AddMinutes(-10);
        Guid run = await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at);
        await using var original = database.DataSource.CreateCommand("SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run;"); original.Parameters.AddWithValue("run", run); Assert.Equal(0L, await original.ExecuteScalarAsync());
        using JsonDocument page = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(5), 25);
        Assert.Single(page.RootElement.GetProperty("items").EnumerateArray()); Assert.Equal("available", page.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task VolumeWindowHistoryNativeGrantsScopeAndRunAccountingAreClosed()
    {
        await using RepositoryTestDatabase database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await SqlVolumeWindowHistoryFixture.TargetAsync(database);
        DateTimeOffset at = SqlVolumeWindowHistoryFixture.Now().AddMinutes(-10);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at, retain: false);
        using JsonDocument incomplete = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(5), 250, 1, SqlVolumeWindowHistoryFixture.Key);
        Assert.Equal("stored_count_mismatch", incomplete.RootElement.GetProperty("items")[0].GetProperty("reason").GetString());
        Assert.Equal(0, incomplete.RootElement.GetProperty("coverage").GetProperty("completeRunCount").GetInt32());
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(5), 25, scope: Guid.NewGuid()))).SqlState);
        await using var grants = database.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_server','reporting.read_sql_volume_window_history(uuid,bigint,timestamptz,timestamptz,integer,bigint,bytea,jsonb)','EXECUTE')
              AND NOT has_function_privilege('sqlobserver_collector','reporting.read_sql_volume_window_history(uuid,bigint,timestamptz,timestamptz,integer,bigint,bytea,jsonb)','EXECUTE')
              AND NOT has_function_privilege('sqlobserver_auditor','reporting.read_sql_volume_window_history(uuid,bigint,timestamptz,timestamptz,integer,bigint,bytea,jsonb)','EXECUTE')
              AND NOT has_table_privilege('sqlobserver_server','telemetry.sql_volume_snapshot','SELECT')
              AND (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='telemetry.sql_volume_snapshot'::regclass)
              AND NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace,
                LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) acl
                WHERE n.nspname='reporting' AND p.proname IN('read_sql_volume_window_history','sql_volume_window_manifest') AND acl.grantee=0 AND acl.privilege_type='EXECUTE');
            """); Assert.True((bool)(await grants.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData("mount_point", 1000, "identity_not_comparable")]
    [InlineData("file_scoped_unknown", 1000, "identity_not_comparable")]
    [InlineData("volume_id", 2000, "capacity_changed")]
    public async Task VolumeWindowHistoryDoesNotInferPhysicalContinuityOrCompareChangedCapacity(string identity, long lastTotal, string comparison)
    {
        await using RepositoryTestDatabase database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await SqlVolumeWindowHistoryFixture.TargetAsync(database); DateTimeOffset at = SqlVolumeWindowHistoryFixture.Now().AddMinutes(-15);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at, identity: identity);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(5), identity: identity, total: lastTotal, available: 400);
        using JsonDocument page = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddMinutes(10), 25);
        JsonElement item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(comparison, item.GetProperty("comparisonState").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("availableBytesDelta").ValueKind); Assert.Equal(JsonValueKind.Null, item.GetProperty("totalBytesDelta").ValueKind);
    }

    [Fact]
    public async Task VolumeWindowHistoryPreservesFutureSqlObservationAsSkewAndExpiresOriginalCursorReceipt()
    {
        await using RepositoryTestDatabase database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await SqlVolumeWindowHistoryFixture.TargetAsync(database); DateTimeOffset now = SqlVolumeWindowHistoryFixture.Now();
        DateTimeOffset futureSql = now.AddMinutes(1);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, now.AddSeconds(-10), observed: futureSql);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, now.AddSeconds(-5), key: new string('b', 64));
        using JsonDocument history = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, now.AddMinutes(-1), now.AddMinutes(2), 250, 1, SqlVolumeWindowHistoryFixture.Key);
        JsonElement skewed = history.RootElement.GetProperty("items")[0]; Assert.Equal("source_clock_skew", skewed.GetProperty("reason").GetString());
        Assert.Equal(futureSql, skewed.GetProperty("observedAtUtc").GetDateTimeOffset()); Assert.Equal("partial", skewed.GetProperty("state").GetString());
        using JsonDocument catalog = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, now.AddMinutes(-1), now.AddMinutes(2), 1);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        SqlVolumeWindowHistoryCursor cursor = JsonSerializer.Deserialize<SqlVolumeWindowHistoryCursor>(catalog.RootElement.GetProperty("nextAnchor"), options)!;
        using JsonDocument expired = JsonDocument.Parse(JsonSerializer.Serialize(cursor with { IssuedAtUtc = now.AddMinutes(-16), CutoffUtc = now.AddMinutes(-16) }, options));
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, now.AddMinutes(-1), now.AddMinutes(2), 1, cursor: expired.RootElement))).SqlState);
        using JsonDocument ending = await SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, now.AddMinutes(-1), now.AddSeconds(-5), 25);
        Assert.Equal(1, ending.RootElement.GetProperty("coverage").GetProperty("runCount").GetInt32());
    }

    [Theory]
    [InlineData(10001, 0, false)]
    [InlineData(2, 501, true)]
    [InlineData(251, 1000, false)]
    public async Task VolumeWindowHistoryRefusesWholeWindowOverflow(int runs, int volumes, bool distinctPerRun)
    {
        await using RepositoryTestDatabase database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await SqlVolumeWindowHistoryFixture.TargetAsync(database);
        DateTimeOffset at = SqlVolumeWindowHistoryFixture.Now().AddHours(-4);
        await SqlVolumeWindowHistoryFixture.BulkAsync(database, target, at, runs, volumes, distinctPerRun);
        Assert.Equal("54000", (await Assert.ThrowsAsync<PostgresException>(() => SqlVolumeWindowHistoryFixture.ReadAsync(database, target, 1, at, at.AddHours(4), 25))).SqlState);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = new PostgreSqlSqlVolumeWindowHistoryRepositoryPort(server);
        SqlVolumeWindowHistoryReadException refusal = await Assert.ThrowsAsync<SqlVolumeWindowHistoryReadException>(() => repository.ReadCatalogAsync(
            new(target, 1, at, at.AddHours(4), 25, null, SqlVolumeWindowHistoryFixture.Timeout), CancellationToken.None).AsTask());
        Assert.True(refusal.StatusCode == 422, $"Expected bounded overflow 422; received {refusal.StatusCode}/{refusal.Code}: {refusal.InnerException}");
        Assert.Equal("history_window_overflow", refusal.Code);
    }
}

internal static class SqlVolumeWindowHistoryFixture
{
    internal static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    internal const string Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal static DateTimeOffset Now() => new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
    internal static async Task<RepositoryTestDatabase> CreateAsync(PostgreSql18Fixture fixture)
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(result.HasFailures, string.Join(",", result.Results.Where(item => item.Outcome == MigrationOutcome.Failed).Select(item => $"{item.Migration.Number.Value}:{item.FailureCode}")));
        await CutoverCanonicalForeignKeysAsync(database);
        return database;
    }
    internal static async Task<Guid> TargetAsync(RepositoryTestDatabase database, long revision = 1)
    {
        Guid target = Guid.NewGuid(); await using var query = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Volume history target','test.invalid',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',@revision,statement_timestamp(),statement_timestamp(),statement_timestamp());
            """); query.Parameters.AddWithValue("target", target); query.Parameters.AddWithValue("key", "volume-history-" + target.ToString("N")); query.Parameters.AddWithValue("revision", revision); await query.ExecuteNonQueryAsync(); return target;
    }
    internal static async Task<Guid> RunAsync(RepositoryTestDatabase database, Guid target, DateTimeOffset at, long revision = 1, string key = Key, string identity = "volume_id", long? total = 1000, long? available = 500, string outcome = "succeeded", bool retain = true, DateTimeOffset? observed = null)
    {
        Guid run = Guid.NewGuid(); string reason = outcome switch { "permission_denied" => "required_permission_missing", "partial" => "source_row_limit", _ => "completed" };
        bool loss = outcome == "partial", success = outcome is "succeeded" or "partial";
        await using var query = database.DataSource.CreateCommand("""
            SELECT control.ensure_collection_run_v2_day(@at::date);
            INSERT INTO telemetry.collection_run_v2(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'storage.volume',1,1,@revision,1,@work,@owner,1,sha256(@run::text::bytea),@at-interval '3 seconds',@at-interval '3 seconds');
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES(@run,@outcome,@reason,1,0,10,@count,@count,@count,0,0,128,128,128,@loss,@loss,@lossKind,true,@lost,0,sha256(@run::text::bytea),@at);
            INSERT INTO telemetry.sql_volume_snapshot(observed_at,run_id,instance_id,target_revision,volume_key,identity_kind,mapped_file_count,total_bytes,available_bytes,collected_at)
            SELECT @observed,@run,@target,@revision,decode(@key,'hex'),@identity,1,@total,@available,@at-interval '1 second' WHERE @retain AND @count=1;
            """);
        query.Parameters.AddWithValue("run", run); query.Parameters.AddWithValue("target", target); query.Parameters.AddWithValue("revision", revision); query.Parameters.AddWithValue("at", at);
        query.Parameters.AddWithValue("observed", observed ?? at.AddSeconds(-2));
        query.Parameters.AddWithValue("work", "collector/storage.volume/" + target.ToString("N")); query.Parameters.AddWithValue("owner", Guid.NewGuid());
        query.Parameters.AddWithValue("key", key); query.Parameters.AddWithValue("identity", identity); query.Parameters.Add("total", NpgsqlDbType.Bigint).Value = (object?)total ?? DBNull.Value; query.Parameters.Add("available", NpgsqlDbType.Bigint).Value = (object?)available ?? DBNull.Value;
        query.Parameters.AddWithValue("outcome", outcome); query.Parameters.AddWithValue("reason", reason); query.Parameters.AddWithValue("count", success ? 1 : 0); query.Parameters.AddWithValue("loss", loss); query.Parameters.AddWithValue("lossKind", loss ? "source_row_limit" : "none"); query.Parameters.AddWithValue("lost", loss ? 1 : 0); query.Parameters.AddWithValue("retain", retain);
        await query.ExecuteNonQueryAsync(); return run;
    }
    internal static async Task<JsonDocument> ReadAsync(RepositoryTestDatabase database, Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit, long? sampleRevision = null, string? key = null, JsonElement? cursor = null, Guid? scope = null)
    {
        await using NpgsqlDataSource server = database.CreateServerDataSource(); await using NpgsqlConnection connection = await server.OpenConnectionAsync(); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var query = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true); SELECT reporting.read_sql_volume_window_history(@target,@revision,@from,@to,@limit,@sample,@key,@cursor);", connection, transaction);
        query.Parameters.AddWithValue("scope", (scope ?? target).ToString("D")); query.Parameters.AddWithValue("target", target); query.Parameters.AddWithValue("revision", revision); query.Parameters.AddWithValue("from", from); query.Parameters.AddWithValue("to", to); query.Parameters.AddWithValue("limit", limit);
        query.Parameters.Add("sample", NpgsqlDbType.Bigint).Value = (object?)sampleRevision ?? DBNull.Value; query.Parameters.Add("key", NpgsqlDbType.Bytea).Value = key is null ? DBNull.Value : Convert.FromHexString(key); query.Parameters.Add("cursor", NpgsqlDbType.Jsonb).Value = cursor is null ? DBNull.Value : cursor.Value.GetRawText();
        await using NpgsqlDataReader rows = await query.ExecuteReaderAsync(); Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); JsonDocument result = JsonDocument.Parse(rows.GetString(0)); await rows.DisposeAsync(); await transaction.CommitAsync(); return result;
    }
    internal static async Task CutoverCanonicalForeignKeysAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync(); var children = new List<string>();
        await using (var inventory = new NpgsqlCommand("SELECT child_relation::text FROM control.collection_run_identity_fk_targets();", connection)) await using (NpgsqlDataReader rows = await inventory.ExecuteReaderAsync()) while (await rows.ReadAsync()) children.Add(rows.GetString(0));
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(); await using (var role = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;", connection, transaction)) await role.ExecuteNonQueryAsync();
        foreach (string child in children) { await using var stage = new NpgsqlCommand("SELECT control.stage_collection_run_identity_fk(@child::regclass,10000); SELECT control.validate_collection_run_identity_fk(@child::regclass);", connection, transaction); stage.Parameters.AddWithValue("child", child); await stage.ExecuteNonQueryAsync(); }
        await using (var cutover = new NpgsqlCommand("SELECT control.cutover_collection_run_identity_fks(10000);", connection, transaction)) await cutover.ExecuteScalarAsync();
        await transaction.CommitAsync();
    }
    internal static async Task SealAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;", connection, transaction)) await role.ExecuteNonQueryAsync();
        foreach (string route in new[] { "activity", "health", "m9", "m10", "overview", "ple", "query_catalog", "scheduler", "sql_volume", "wait", "writer" }) { await using var activate = new NpgsqlCommand(route == "writer" ? "SELECT control.set_collection_run_v2_writer_route(true,10000);" : $"SELECT control.set_collection_run_v2_{route}_read_route(true,10000);", connection, transaction); await activate.ExecuteScalarAsync(); }
        await using (var seal = new NpgsqlCommand("SELECT control.seal_collection_run_original_rollback(10000);", connection, transaction)) await seal.ExecuteScalarAsync(); await transaction.CommitAsync();
    }
    internal static async Task BulkAsync(RepositoryTestDatabase database, Guid target, DateTimeOffset at, int runs, int volumes, bool distinctPerRun)
    {
        await using var query = database.DataSource.CreateCommand("""
            SELECT control.ensure_collection_run_v2_day(@at::date);
            INSERT INTO telemetry.collection_run_v2(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
              SELECT gen_random_uuid(),@target,'storage.volume',1,1,1,1,@work,gen_random_uuid(),1,decode(repeat('aa',32),'hex'),@at+i*interval '1 second',@at+i*interval '1 second' FROM generate_series(1,@runs) i;
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
              SELECT r.run_id,'succeeded','completed',1,0,1,@volumes,@volumes,@volumes,0,0,0,0,0,false,false,'none',true,0,0,sha256(convert_to(r.run_id::text,'UTF8')),r.started_at FROM telemetry.collection_run_v2 r WHERE r.instance_id=@target AND r.collector_id='storage.volume';
            INSERT INTO telemetry.sql_volume_snapshot(observed_at,run_id,instance_id,target_revision,volume_key,identity_kind,mapped_file_count,total_bytes,available_bytes,collected_at)
              SELECT r.started_at,r.run_id,@target,1,sha256(convert_to(CASE WHEN @distinct THEN r.run_id::text||'|' ELSE '' END||v::text,'UTF8')),'volume_id',1,1000,500,r.started_at
              FROM telemetry.collection_run_v2 r CROSS JOIN generate_series(1,@volumes) v WHERE r.instance_id=@target AND r.collector_id='storage.volume';
            """); query.Parameters.AddWithValue("target", target); query.Parameters.AddWithValue("at", at); query.Parameters.AddWithValue("runs", runs); query.Parameters.AddWithValue("volumes", volumes); query.Parameters.AddWithValue("distinct", distinctPerRun); query.Parameters.AddWithValue("work", "collector/storage.volume/" + target.ToString("N")); await query.ExecuteNonQueryAsync();
    }
}
