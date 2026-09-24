using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

// Use migrated storage and the actual collector/server ports for hour identity.
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class BaselineHourPersistencePostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly DateTimeOffset Cutoff = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
    private const string BaselineRepairMigrationFileName = "0081_analytics_identity_reclaim_paging.sql";

    [Fact]
    public async Task ActualPortPersistsAll168ComputedHoursAndIdenticalReplayPreservesThem()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAnalyticsRepositoryPort(collector, new IdentityFingerprintKey(new byte[32]));
        AnalyticsJobRequest request = await ClaimBaselineAsync(database, collector, repository, target);
        IReadOnlyList<BaselineResult> expected = ComputeAllHours();

        await repository.StoreBaselineAsync(request, expected, CancellationToken.None);

        // Before migration 0081, the six-column identity retained only one hour
        // from this complete 168-hour baseline result.
        Assert.Equal(168L, await CountAsync(database, "SELECT count(*) FROM analytics.metric_baseline WHERE instance_id=@target;", target));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var reader = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[32]));
        IReadOnlyList<BaselineResult> actual = await reader.ReadBaselinesAsync(
            new AnalyticsQueryRequest(new MonitoredInstanceId(target), "host.cpu.percent", Cutoff.AddDays(-28), Cutoff,
                200, Timeout, new ObservationTargetRevision(1)), CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, 168), actual.Select(row => row.HourOfWeek).Order());
        foreach (BaselineResult wanted in expected)
        {
            BaselineResult stored = Assert.Single(actual, row => row.HourOfWeek == wanted.HourOfWeek);
            Assert.Equal(wanted.SampleCount, stored.SampleCount);
            Assert.Equal(wanted.Median, stored.Median);
            Assert.Equal(wanted.WindowStartUtc, stored.WindowStartUtc);
            Assert.Equal(wanted.WindowEndUtc, stored.WindowEndUtc);
            Assert.Equal(wanted.DimensionsSha256, stored.DimensionsSha256);
        }
        string beforeReplay = await ReadRowsAsync(database, target);

        await repository.StoreBaselineAsync(request, expected, CancellationToken.None);

        Assert.Equal(beforeReplay, await ReadRowsAsync(database, target));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM control.analytics_replay WHERE instance_id=@target AND operation_kind='baseline';", target));
    }

    [Fact]
    public async Task ForwardUpgradePreservesUnknownLegacyHourAndAllowsConcreteHoursAlongsideIt()
    {
        PostgreSqlMigrationResource repair = Assert.Single(PostgreSqlMigrationCatalog.LoadEmbedded().Migrations,
            migration => migration.FileName == BaselineRepairMigrationFileName);
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(repair.Descriptor.Number.Value - 1);
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target);
        await ExecuteAsync(database, """
            INSERT INTO analytics.metric_baseline(instance_id,target_revision,metric_key,hour_of_week,
                complete_days,window_start,window_end,sample_count,dimensions,dimension_hash,visibility_state,generation)
            VALUES(@target,1,'host.cpu.percent',NULL,NULL,@from,@to,0,'{}'::jsonb,
                sha256(convert_to('{}','UTF8')),'unavailable',1);
            """, ("target", target), ("from", Cutoff.AddDays(-28)), ("to", Cutoff));
        string legacyBefore = await ReadUnknownRowsAsync(database, target);
        string functionsBefore = await ReadFunctionMetadataAsync(database);

        MigrationBatchResult upgrade = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);

        Assert.False(upgrade.HasFailures);
        Assert.Equal(repair.Descriptor.Number.Value, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(legacyBefore, await ReadUnknownRowsAsync(database, target));
        Assert.Equal(functionsBefore, await ReadFunctionMetadataAsync(database));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAnalyticsRepositoryPort(collector, new IdentityFingerprintKey(new byte[32]));
        AnalyticsJobRequest request = await ClaimBaselineAsync(database, collector, repository, target);
        await repository.StoreBaselineAsync(request, ComputeAllHours(), CancellationToken.None);
        Assert.Equal(169L, await CountAsync(database, "SELECT count(*) FROM analytics.metric_baseline WHERE instance_id=@target;", target));
        Assert.Equal(legacyBefore, await ReadUnknownRowsAsync(database, target));

        PostgresException duplicateUnknown = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, """
            INSERT INTO analytics.metric_baseline SELECT b.* FROM analytics.metric_baseline b
            WHERE b.instance_id=@target AND b.hour_of_week IS NULL;
            """, ("target", target)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateUnknown.SqlState);
        await using var trigger = database.DataSource.CreateCommand("SELECT tgenabled='O' FROM pg_trigger WHERE tgrelid='analytics.metric_baseline'::regclass AND tgname='m10_baseline_append_only';");
        Assert.Equal(true, await trigger.ExecuteScalarAsync());
        PostgresException immutable = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database,
            "UPDATE analytics.metric_baseline SET sample_count=sample_count WHERE instance_id=@target;", ("target", target)));
        Assert.Equal("55000", immutable.SqlState);
        MigrationBatchResult repeat = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
        Assert.False(repeat.HasFailures);
        Assert.DoesNotContain(repeat.Results, result => result.Migration.Number.Value == repair.Descriptor.Number.Value);
        Assert.Equal(legacyBefore, await ReadUnknownRowsAsync(database, target));
        // Legacy NULL -> 0 read-model behavior is separate; inspect NULL preservation
        // in PostgreSQL, without asserting the preexisting read-side mislabel is fixed.
    }

    [Theory]
    [InlineData("foreign_key")]
    [InlineData("replica_identity")]
    [InlineData("publication")]
    [InlineData("unexpected_key")]
    public async Task UnexpectedBaselineDependenciesRollBackMigrationWithoutChangingFunctionsOrHistory(string drift)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(80);
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target);
        await ExecuteAsync(database, """
            INSERT INTO analytics.metric_baseline(instance_id,target_revision,metric_key,hour_of_week,
                complete_days,window_start,window_end,sample_count,dimensions,dimension_hash,visibility_state,generation)
            VALUES(@target,1,'host.cpu.percent',NULL,NULL,@from,@to,0,'{}'::jsonb,
                sha256(convert_to('{}','UTF8')),'unavailable',1);
            """, ("target", target), ("from", Cutoff.AddDays(-28)), ("to", Cutoff));
        string setup = drift switch
        {
            "foreign_key" => """
                CREATE TABLE analytics.baseline_dependency(instance_id uuid,target_revision bigint,metric_key text,
                    window_start timestamptz,dimension_hash bytea,generation bigint,
                    FOREIGN KEY(instance_id,target_revision,metric_key,window_start,dimension_hash,generation)
                    REFERENCES analytics.metric_baseline(instance_id,target_revision,metric_key,window_start,dimension_hash,generation));
                """,
            "replica_identity" => "ALTER TABLE analytics.metric_baseline REPLICA IDENTITY USING INDEX metric_baseline_pkey;",
            "publication" => "CREATE PUBLICATION baseline_dependency FOR TABLE analytics.metric_baseline;",
            "unexpected_key" => "ALTER TABLE analytics.metric_baseline RENAME CONSTRAINT metric_baseline_pkey TO unexpected_baseline_key;",
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await ExecuteAsync(database, setup);
        string functionsBefore = await ReadFunctionMetadataAsync(database, includeBody: true);
        string schemaBefore = await ReadBaselineSchemaAsync(database);

        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);

        Assert.True(result.HasFailures);
        Assert.Equal(81, Assert.Single(result.Results).Migration.Number.Value);
        Assert.Equal("postgres_55000", Assert.Single(result.Results).FailureCode);
        Assert.Equal(functionsBefore, await ReadFunctionMetadataAsync(database, includeBody: true));
        Assert.Equal(schemaBefore, await ReadBaselineSchemaAsync(database));
        await using var ledger = database.DataSource.CreateCommand("SELECT max(migration_number) FROM system.schema_migration;");
        Assert.Equal(80, Convert.ToInt32(await ledger.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        await using var trigger = database.DataSource.CreateCommand("SELECT tgenabled='O' FROM pg_trigger WHERE tgrelid='analytics.metric_baseline'::regclass AND tgname='m10_baseline_append_only';");
        Assert.Equal(true, await trigger.ExecuteScalarAsync());
    }

    private static IReadOnlyList<BaselineResult> ComputeAllHours()
    {
        RollupResult[] inputs = Enumerable.Range(0, 28 * 24).Select(hour => new RollupResult
        {
            Interval = RollupInterval.Hour,
            BucketStartUtc = Cutoff.AddDays(-28).AddHours(hour),
            BucketEndUtc = Cutoff.AddDays(-28).AddHours(hour + 1),
            MetricKey = "host.cpu.percent",
            DimensionsSha256 = CanonicalDimensions.Sha256(null),
            Count = 1,
            Expected = 1,
            Mean = hour % 168,
        }).ToArray();
        IReadOnlyList<BaselineResult> rows = BaselineV1.Compute(inputs, Cutoff);
        Assert.Equal(Enumerable.Range(0, 168), rows.Select(row => row.HourOfWeek));
        Assert.All(rows, row => Assert.Equal(4, row.SampleCount));
        return rows;
    }

    private static async Task<AnalyticsJobRequest> ClaimBaselineAsync(RepositoryTestDatabase database,
        NpgsqlDataSource collector, PostgreSqlAnalyticsRepositoryPort repository, Guid target)
    {
        Guid jobId = Guid.NewGuid();
        await ExecuteAsync(database, """
            INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,
                source_cutoff_utc,metric_key,status,work_key,generation)
            VALUES(@job,'baseline',@target,1,@from,@to,@to,'host.cpu.percent','queued','analytics/derivation',1);
            """, ("job", jobId), ("target", target), ("from", Cutoff.AddDays(-28)), ("to", Cutoff));
        Guid owner = Guid.NewGuid();
        await using var leaseCommand = collector.CreateCommand("SELECT acquired,fencing_token FROM control.acquire_worker_lease('analytics/derivation',@owner,interval '5 minutes');");
        leaseCommand.Parameters.AddWithValue("owner", owner);
        long token;
        await using (NpgsqlDataReader leaseReader = await leaseCommand.ExecuteReaderAsync())
        {
            Assert.True(await leaseReader.ReadAsync());
            Assert.True(leaseReader.GetBoolean(0));
            token = leaseReader.GetInt64(1);
        }
        var lease = new WorkerLeaseIdentity(new("analytics/derivation"), new(owner), new(token));
        AnalyticsDerivationJob claimed = Assert.Single(await repository.ClaimAsync(lease, 1, CancellationToken.None));
        Assert.Equal(jobId, claimed.JobId);
        return new AnalyticsJobRequest(claimed.JobId, claimed.TargetId, lease.Key.Value, lease, Timeout, claimed.TargetRevision);
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync(int maximumMigrations = MigrationBatchResult.MaximumResults)
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
                .ApplyPendingAsync(new MigrationApplyRequest(maximumMigrations, Timeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static Task InsertTargetAsync(RepositoryTestDatabase database, Guid target) => ExecuteAsync(database, """
        INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,
            connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
        VALUES(@target,@key,'Baseline regression','sql01',1433,interval '5 seconds','windows_integrated_service_identity',
            'mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
        """, ("target", target), ("key", "baseline-" + target.ToString("N")));

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(RepositoryTestDatabase database, string sql, Guid target)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("target", target);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task<string> ReadRowsAsync(RepositoryTestDatabase database, Guid target) => ReadJsonAsync(database,
        "SELECT jsonb_agg(to_jsonb(b) ORDER BY b.hour_of_week NULLS FIRST,b.generation)::text FROM analytics.metric_baseline b WHERE b.instance_id=@target;", target);

    private static Task<string> ReadUnknownRowsAsync(RepositoryTestDatabase database, Guid target) => ReadJsonAsync(database,
        "SELECT jsonb_agg(to_jsonb(b) ORDER BY b.generation)::text FROM analytics.metric_baseline b WHERE b.instance_id=@target AND b.hour_of_week IS NULL;", target);

    private static async Task<string> ReadJsonAsync(RepositoryTestDatabase database, string sql, Guid target)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("target", target);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadFunctionMetadataAsync(RepositoryTestDatabase database, bool includeBody = false)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_agg(CASE WHEN @include_body THEN to_jsonb(p) ELSE to_jsonb(p)-'prosrc' END ORDER BY p.oid)::text FROM pg_proc p
            WHERE p.oid IN (
                'analytics.commit_metric_baselines(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)'::regprocedure,
                'control.claim_m10_analytics_jobs(text,uuid,bigint,integer)'::regprocedure,
                'control.claim_m10_derivation_jobs(text,uuid,bigint,integer)'::regprocedure,
                'analytics.list_metric_rollups_scoped(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz,text,timestamptz,text,bytea,bigint,bytea)'::regprocedure);
            """);
        command.Parameters.AddWithValue("include_body", includeBody);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadBaselineSchemaAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'constraints',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.oid) FROM pg_constraint c WHERE c.conrelid='analytics.metric_baseline'::regclass),
                'replicaIdentity',(SELECT relreplident FROM pg_class WHERE oid='analytics.metric_baseline'::regclass),
                'history',(SELECT coalesce(jsonb_agg(to_jsonb(b) ORDER BY b.instance_id,b.window_start),'[]'::jsonb) FROM analytics.metric_baseline b))::text;
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
