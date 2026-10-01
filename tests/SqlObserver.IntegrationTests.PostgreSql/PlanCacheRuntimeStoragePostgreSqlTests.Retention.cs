using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class PlanCacheRuntimeStoragePostgreSqlTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RunDependencyUpgradeValidatesExistingEvidenceWithoutDiscardingOrFabricatingRuns(bool orphan, bool detached)
    {
        await using var database = await Migrate(318);
        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        DateTimeOffset at = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, target);
            await SeedCanonicalRun(connection, transaction, target, orphan ? Guid.NewGuid() : run, at);
            await using var apply = new NpgsqlCommand("SELECT events.apply_plan_cache_runtime_batch(@run,@target,1,@payload);", connection, transaction);
            apply.Parameters.AddWithValue("run", run); apply.Parameters.AddWithValue("target", target);
            var payload = orphan
                ? new PlanCacheRuntimeOutputBatch(new(target), new(1), Digest(54), at, at.AddMilliseconds(20),
                    2, 0, 0, 0, 0, 0, 0, [])
                : TypedBatch(target, at, [1, 2]);
            apply.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb,
                System.Text.Encoding.UTF8.GetString(PlanCacheRuntimeCommitPayload.Serialize(payload)));
            Assert.Equal(orphan ? 0 : 2, await apply.ExecuteScalarAsync());
            await transaction.CommitAsync();
        }
        if (detached)
        {
            // Model an already detached pre-upgrade grace partition, without deleting its evidence.
            await using var detach = database.DataSource.CreateCommand("""
                SET ROLE sqlobserver_migrator;
                DO $fixture$ DECLARE child record; BEGIN
                    SELECT registry.* INTO STRICT child FROM system.partition_registry registry
                    WHERE parent_schema='events' AND parent_table='plan_cache_runtime_receipt'
                      AND lifecycle_state='attached' AND range_start<=now() AND range_end>now();
                    EXECUTE format('ALTER TABLE events.plan_cache_runtime_receipt DETACH PARTITION %I.%I',child.partition_schema,child.partition_name);
                    UPDATE system.partition_registry SET lifecycle_state='detached',detached_at=clock_timestamp()
                    WHERE partition_schema=child.partition_schema AND partition_name=child.partition_name;
                END $fixture$;
                RESET ROLE;
                """);
            await detach.ExecuteNonQueryAsync();
        }
        string before = await Evidence();
        var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(1, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), default);
        Assert.Equal(orphan, result.HasFailures);
        Assert.Single(result.Results);
        Assert.Equal(319, result.Results[0].Migration.Number.Value);
        Assert.Equal(before, await Evidence());
        await using var catalog = database.DataSource.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM system.schema_migration WHERE migration_number=319),
                to_regclass('events.ix_plan_cache_watermark_canonical_run') IS NOT NULL,
                EXISTS(SELECT 1 FROM telemetry.collection_run_identity WHERE run_id=@run),
                EXISTS(SELECT 1 FROM pg_constraint WHERE conname='fk_plan_cache_watermark_run'),
                NOT EXISTS(SELECT 1 FROM pg_policy WHERE polname='cache_run_upgrade_validation'),
                (SELECT count(*) FROM pg_constraint WHERE conname='fk_plan_cache_detached_run');
            """);
        catalog.Parameters.AddWithValue("run", run);
        await using var reader = await catalog.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 4; index++) Assert.Equal(!orphan, reader.GetBoolean(index));
        Assert.True(reader.GetBoolean(4));
        Assert.Equal(detached && !orphan ? 1L : 0L, reader.GetInt64(5));

        async Task<string> Evidence()
        {
            string attached = await CacheContents(database);
            if (!detached) return attached;
            await using var name = database.DataSource.CreateCommand("SELECT partition_name FROM system.partition_registry WHERE parent_table='plan_cache_runtime_receipt' AND lifecycle_state='detached';");
            string child = Assert.IsType<string>(await name.ExecuteScalarAsync());
            await using var contents = database.DataSource.CreateCommand($"SELECT jsonb_agg(to_jsonb(receipt) ORDER BY run_id)::text FROM events.\"{child.Replace("\"", "\"\"")}\" receipt;");
            return attached + Assert.IsType<string>(await contents.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task CacheRunRetentionPreviewRequiresExactTargetScopeAndPrivateMigrationRole()
    {
        await using var database = await Migrate();
        Guid target = Guid.NewGuid();
        foreach (string role in new[] { "sqlobserver_collector", "sqlobserver_server", "sqlobserver_auditor", "sqlobserver_migrator" })
        {
            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            // Runtime roles have no EXECUTE even with the correct target; the migrator has the wrong scope.
            await using var setup = new NpgsqlCommand($"SET LOCAL ROLE {role}; SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction);
            setup.Parameters.AddWithValue("scope", (role == "sqlobserver_migrator" ? Guid.NewGuid() : target).ToString());
            await setup.ExecuteNonQueryAsync();
            await using var holds = new NpgsqlCommand("SELECT hold_code FROM system.plan_cache_runtime_run_retention_holds(@target,@run);", connection, transaction);
            holds.Parameters.AddWithValue("target", target); holds.Parameters.AddWithValue("run", Guid.NewGuid());
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await holds.ExecuteScalarAsync())).SqlState);
            await transaction.RollbackAsync();
        }
    }
}
