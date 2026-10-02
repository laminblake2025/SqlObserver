using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed partial class CanonicalRunReferenceGateTests(PostgreSql18Fixture fixture, Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task SealedEmptyGraphHasNoReferenceAndReaderHasNoRuntimeMembershipOrWriteRights()
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedRunAsync(database);
        Assert.Equal("no_known_reference:", Assert.Single(await PreviewAsync(database, target, run)));
        await using (var indexes = database.DataSource.CreateCommand("""
            SELECT reviewed.child_relation::text,relation.relkind::text
            FROM system.collection_run_reviewed_references() reviewed
            JOIN pg_class relation ON relation.oid=reviewed.child_relation
            JOIN pg_attribute column_row ON column_row.attrelid=reviewed.child_relation
              AND column_row.attname=reviewed.column_name
            WHERE NOT EXISTS(SELECT 1 FROM pg_index index_row
              JOIN pg_class index_class ON index_class.oid=index_row.indexrelid
              JOIN pg_am method ON method.oid=index_class.relam
              WHERE index_row.indrelid=reviewed.child_relation AND index_row.indisvalid AND index_row.indisready
                AND index_row.indpred IS NULL AND index_row.indexprs IS NULL
                AND index_row.indkey[0]=column_row.attnum AND method.amname='btree')
            ORDER BY 1;
            """))
        await using (var rows = await indexes.ExecuteReaderAsync())
        {
            var missing = new List<string>();
            while (await rows.ReadAsync()) missing.Add(rows.GetString(0) + ":" + rows.GetString(1));
            output.WriteLine("Run-reference parents without a leading usable B-tree index: " + string.Join(", ", missing));
            Assert.Empty(missing);
        }
        await using var security = database.DataSource.CreateCommand("""
            SELECT NOT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
                     AND NOT rolinherit AND NOT rolreplication AND rolbypassrls,
              NOT EXISTS(SELECT 1 FROM pg_auth_members
                WHERE roleid=role.oid OR member=role.oid),
              NOT EXISTS(SELECT 1 FROM system.collection_run_reviewed_references() reviewed
                WHERE has_table_privilege(role.oid,reviewed.child_relation,'INSERT,UPDATE,DELETE,TRUNCATE')),
              NOT has_schema_privilege(role.oid,'system','CREATE'),
              (SELECT pg_get_userbyid(proowner) FROM pg_proc
                WHERE oid='system.collection_run_reference_holds(uuid,uuid)'::regprocedure)
            FROM pg_roles role WHERE rolname='sqlobserver_run_dependency_reader';
            """);
        await using var row = await security.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        for (int index = 0; index < 4; index++) Assert.True(row.GetBoolean(index));
        Assert.Equal("sqlobserver_run_dependency_reader", row.GetString(4));
    }

    [Fact]
    public async Task PrivateCrossTargetCacheReferenceIsVisibleToTheGateAndPreserved()
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedRunAsync(database);
        var (otherTarget, _) = await SeedRunAsync(database);
        await SeedCacheReceiptAsync(database, otherTarget, run);
        Assert.Equal("run_missing:", Assert.Single(await PreviewAsync(database, otherTarget, run)));
        await using (var scoped = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await scoped.BeginTransactionAsync())
        {
            await SetScopeAsync(scoped, transaction, target);
            await using var invisible = new NpgsqlCommand("SELECT count(*) FROM events.plan_cache_runtime_receipt WHERE run_id=@run;", scoped, transaction);
            invisible.Parameters.AddWithValue("run", run);
            Assert.Equal(0L, await invisible.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }
        Assert.Contains("retained_reference:events.plan_cache_runtime_receipt", await PreviewAsync(database, target, run));
        await using var preserved = database.DataSource.CreateCommand("SELECT count(*) FROM events.plan_cache_runtime_receipt WHERE run_id=@run;");
        preserved.Parameters.AddWithValue("run", run);
        Assert.Equal(1L, await preserved.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CascadeSnapshotsAndOutcomeReceiptsHoldTheRunWithoutDeletingEvidence()
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedRunAsync(database);
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.deadlock_read_checkpoint
              (instance_id,target_revision,schedule_revision,run_id,completed_at,completion_digest,source_state)
            VALUES(@target,1,1,@run,statement_timestamp(),decode(repeat('aa',32),'hex'),'missing');
            INSERT INTO alerting.deadlock_source_snapshot
              (instance_id,target_revision,schedule_revision,run_id,completed_at,completion_digest,event_ids,occurred_ats,parse_truncated)
            VALUES(@target,1,1,@run,statement_timestamp(),decode(repeat('aa',32),'hex'),ARRAY[]::uuid[],ARRAY[]::timestamptz[],ARRAY[]::boolean[]);
            INSERT INTO telemetry.collection_run_outcome
              (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
               source_row_count,output_item_count,inserted_item_count,duplicate_item_count,
               rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,
               loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES(@run,'succeeded','completed',1,0,10,0,0,0,0,0,0,0,0,
              false,false,'none',true,0,0,decode(repeat('bb',32),'hex'),statement_timestamp());
            SELECT control.ensure_deadlock_read_receipt_partition(statement_timestamp());
            INSERT INTO events.deadlock_read_receipt
              (completed_at,run_id,instance_id,completion_digest,read_digest,checkpoint_digest)
            VALUES(statement_timestamp(),@run,@target,decode(repeat('bb',32),'hex'),decode(repeat('cc',32),'hex'),decode(repeat('dd',32),'hex'));
            """);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("run", run);
        await seed.ExecuteNonQueryAsync();
        var holds = await PreviewAsync(database, target, run);
        Assert.Contains("retained_reference:control.deadlock_read_checkpoint", holds);
        Assert.Contains("retained_reference:alerting.deadlock_source_snapshot", holds);
        Assert.Contains("retained_reference:events.deadlock_read_receipt", holds);
        Assert.DoesNotContain("no_known_reference:", holds);
        await using var count = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM control.deadlock_read_checkpoint WHERE run_id=@run)
              +(SELECT count(*) FROM alerting.deadlock_source_snapshot WHERE run_id=@run)
              +(SELECT count(*) FROM events.deadlock_read_receipt WHERE run_id=@run);
            """);
        count.Parameters.AddWithValue("run", run);
        Assert.Equal(3L, await count.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EmptyUnknownReferencesAlteredCascadeKeysAndMissingIndexesFailClosed(int driftKind)
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedRunAsync(database);
        await using var drift = database.DataSource.CreateCommand(driftKind == 3 ? """
            ALTER TABLE telemetry.collection_run_identity ADD CONSTRAINT unreviewed_bridge_cascade
              FOREIGN KEY(run_id,started_at) REFERENCES telemetry.collection_run_v2(run_id,started_at) ON DELETE CASCADE;
            """ : driftKind == 2
            ? "DROP INDEX events.ix_plan_cache_receipt_canonical_run;"
            : driftKind == 1 ? """
            ALTER TABLE events.plan_cache_runtime_receipt DROP CONSTRAINT fk_plan_cache_receipt_run;
            ALTER TABLE events.plan_cache_runtime_receipt ADD CONSTRAINT fk_plan_cache_receipt_run
              FOREIGN KEY(run_id) REFERENCES telemetry.collection_run_identity(run_id) ON DELETE CASCADE;
            """ : """
            CREATE TABLE events.unreviewed_run_dependency
              (run_id uuid REFERENCES telemetry.collection_run_identity(run_id) ON DELETE CASCADE);
            """);
        await drift.ExecuteNonQueryAsync();
        var holds = await PreviewAsync(database, target, run);
        Assert.Contains(driftKind == 2 ? "reference_index_unverified:events.plan_cache_runtime_receipt"
            : driftKind == 1 ? "reference_catalog_unverified:events.plan_cache_runtime_receipt" : "unreviewed_foreign_key:", holds);
        Assert.DoesNotContain("no_known_reference:", holds);
    }

    [Fact]
    public async Task ReferenceScanBoundStopsBeforeProbingAnUnreviewedCatalog()
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedRunAsync(database);
        await using var add = database.DataSource.CreateCommand("""
            DO $fixture$ BEGIN
              FOR number IN 1..100 LOOP
                EXECUTE format('CREATE TABLE events.%I (run_id uuid REFERENCES telemetry.collection_run_identity(run_id))',
                  'unreviewed_run_dependency_'||number);
              END LOOP;
            END $fixture$;
            """);
        await add.ExecuteNonQueryAsync();
        Assert.Equal("reference_scan_bound:", Assert.Single(await PreviewAsync(database, target, run)));
    }

    [Fact]
    public async Task DetachedGraceReferenceRequiresVerifiedVisibilityAndRemainsAHold()
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedRunAsync(database);
        await SeedCacheReceiptAsync(database, target, run);
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
        Assert.Contains("reference_visibility_unverified:events.plan_cache_runtime_receipt", await PreviewAsync(database, target, run));
        await using var grant = database.DataSource.CreateCommand("""
            DO $fixture$ DECLARE child record; BEGIN
              SELECT * INTO STRICT child FROM system.partition_registry WHERE parent_table='plan_cache_runtime_receipt' AND lifecycle_state='detached';
              EXECUTE format('GRANT SELECT ON %I.%I TO sqlobserver_run_dependency_reader',child.partition_schema,child.partition_name);
            END $fixture$;
            """);
        await grant.ExecuteNonQueryAsync();
        var holds = await PreviewAsync(database, target, run);
        Assert.Contains("detached_reference:events.plan_cache_runtime_receipt", holds);
        Assert.DoesNotContain("no_known_reference:", holds);
    }

    [Theory]
    [InlineData("sqlobserver_server")]
    [InlineData("sqlobserver_collector")]
    [InlineData("sqlobserver_auditor")]
    [InlineData("sqlobserver_migrator")]
    public async Task RuntimeRolesAndWrongMigrationScopeCannotInspectDependencies(string role)
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedRunAsync(database);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // Only fixed test-role constants are interpolated; target/run remain parameters.
        await using var setup = new NpgsqlCommand($"SET LOCAL ROLE {role}; SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction);
        setup.Parameters.AddWithValue("scope", (role == "sqlobserver_migrator" ? Guid.NewGuid() : target).ToString());
        await setup.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT * FROM system.collection_run_reference_holds(@target,@run);", connection, transaction);
        read.Parameters.AddWithValue("target", target); read.Parameters.AddWithValue("run", run);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await read.ExecuteScalarAsync())).SqlState);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonthlyPartialIndexBuildResumesAndUnexpectedChildNamesAreRefused(bool unexpectedChild)
    {
        await using var database = await PrepareAsync(328);
        await using (var setup = await database.DataSource.OpenConnectionAsync())
        {
            await using (var role = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", setup)) await role.ExecuteNonQueryAsync();
            if (unexpectedChild)
            {
                // Create before the parent index so this child needs a build.
                // A new partition created after the index inherits it automatically.
                await using var spoof = new NpgsqlCommand("""
                    CREATE TABLE events.blocking_edge_unreviewed PARTITION OF events.blocking_edge
                    FOR VALUES FROM ('2100-01-01') TO ('2100-02-01');
                    """, setup);
                await spoof.ExecuteNonQueryAsync();
            }
            await using (var parent = new NpgsqlCommand("""
                CREATE INDEX ix_run_ref_blocking_edge ON ONLY events.blocking_edge(collection_run_id);
                """, setup)) await parent.ExecuteNonQueryAsync();
            await using var name = new NpgsqlCommand("""
                SELECT child.relname FROM pg_inherits link JOIN pg_class child ON child.oid=link.inhrelid
                WHERE link.inhparent='events.blocking_edge'::regclass ORDER BY child.relname LIMIT 1;
                """, setup);
            string child = Assert.IsType<string>(await name.ExecuteScalarAsync());
            string suffix = child[^6..];
            Assert.All(suffix, character => Assert.True(char.IsAsciiDigit(character)));
            using var quote = new NpgsqlCommandBuilder();
            string childIndex = quote.QuoteIdentifier("ix_run_ref_blocking_edge_" + suffix);
            await using var first = new NpgsqlCommand($"CREATE INDEX CONCURRENTLY {childIndex} ON events.{quote.QuoteIdentifier(child)}(collection_run_id);", setup);
            // CREATE INDEX CONCURRENTLY must be the sole statement in its command.
            await first.ExecuteNonQueryAsync();
            first.CommandText = $"ALTER INDEX events.ix_run_ref_blocking_edge ATTACH PARTITION events.{childIndex};";
            await first.ExecuteNonQueryAsync();
        }
        var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new(1, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.Equal(unexpectedChild, result.HasFailures);
        Assert.Equal(329, Assert.Single(result.Results).Migration.Number.Value);
        await using var check = database.DataSource.CreateCommand("""
            SELECT (SELECT indisvalid AND indisready FROM pg_index
                      WHERE indexrelid='events.ix_run_ref_blocking_edge'::regclass),
                   EXISTS(SELECT 1 FROM system.schema_migration WHERE migration_number=329),
                   to_regclass('events.blocking_edge_unreviewed') IS NOT NULL;
            """);
        await using var row = await check.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(!unexpectedChild, row.GetBoolean(0));
        Assert.Equal(!unexpectedChild, row.GetBoolean(1));
        Assert.Equal(unexpectedChild, row.GetBoolean(2));
    }

    private async Task<RepositoryTestDatabase> PrepareAsync(int? maximumMigrations = null)
    {
        var database = await fixture.CreateDatabaseAsync();
        try
        {
            var applied = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new(maximumMigrations ?? MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
            Assert.False(applied.HasFailures, string.Join(", ", applied.Results.Where(result => result.Outcome == MigrationOutcome.Failed)
                .Select(result => $"{result.Migration.Number.Value}:{result.FailureCode}")));
            await using var connection = await database.DataSource.OpenConnectionAsync();
            var children = new List<string>();
            await using (var inventory = new NpgsqlCommand("SELECT child_relation::text FROM control.collection_run_identity_fk_targets() ORDER BY child_relation::oid;", connection))
            await using (var rows = await inventory.ExecuteReaderAsync())
                while (await rows.ReadAsync()) children.Add(rows.GetString(0));
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;", connection, transaction)) await role.ExecuteNonQueryAsync();
            foreach (string child in children)
            {
                await using var stage = new NpgsqlCommand("SELECT control.stage_collection_run_identity_fk(@child::regclass,10000); SELECT control.validate_collection_run_identity_fk(@child::regclass);", connection, transaction);
                stage.Parameters.AddWithValue("child", child); await stage.ExecuteNonQueryAsync();
            }
            await using (var cutover = new NpgsqlCommand("SELECT control.cutover_collection_run_identity_fks(10000);", connection, transaction)) Assert.Equal(20, await cutover.ExecuteScalarAsync());
            foreach (string route in new[] { "activity", "health", "m9", "m10", "overview", "ple", "query_catalog", "scheduler", "sql_volume", "wait", "writer" })
            {
                await using var activate = new NpgsqlCommand(route == "writer"
                    ? "SELECT control.set_collection_run_v2_writer_route(true,10000);"
                    : $"SELECT control.set_collection_run_v2_{route}_read_route(true,10000);", connection, transaction);
                await activate.ExecuteScalarAsync();
            }
            await using (var seal = new NpgsqlCommand("SELECT control.seal_collection_run_original_rollback(10000);", connection, transaction)) await seal.ExecuteScalarAsync();
            await transaction.CommitAsync();
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static async Task<(Guid Target, Guid Run)> SeedRunAsync(RepositoryTestDatabase database, DateTimeOffset? startedAt = null, Guid? instanceId = null)
    {
        Guid target = instanceId ?? Guid.NewGuid(), run = Guid.NewGuid();
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,
               transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'run retention target','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,
              statement_timestamp(),statement_timestamp(),statement_timestamp()) ON CONFLICT(instance_id) DO NOTHING;
            SELECT control.ensure_collection_run_v2_day(@at::date);
            INSERT INTO telemetry.collection_run_v2
              (run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,
               work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'engine.core',1,1,1,1,@work,@owner,1,decode(repeat('aa',32),'hex'),@at,@at);
            """);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("run", run);
        seed.Parameters.AddWithValue("key", $"run-retention.{target:N}");
        seed.Parameters.AddWithValue("work", $"collector/run/engine.core/{target:N}");
        seed.Parameters.AddWithValue("owner", Guid.NewGuid());
        seed.Parameters.AddWithValue("at", startedAt ?? DateTimeOffset.UtcNow);
        await seed.ExecuteNonQueryAsync();
        return (target, run);
    }

    private static async Task SeedCacheReceiptAsync(RepositoryTestDatabase database, Guid target, Guid run)
    {
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO events.plan_cache_runtime_receipt
              (committed_at,run_id,instance_id,target_revision,payload_digest,source_epoch,
               source_read_started_at,source_read_completed_at,candidate_limit,candidate_rows,
               unresolved_rows,invisible_database_rows,excluded_rows,emitted_rows,unqualified_rows,
               statement_rows,source_exhaustive,qualified_exhaustive)
            VALUES(statement_timestamp(),@run,@target,1,decode(repeat('aa',32),'hex'),decode(repeat('bb',32),'hex'),
              statement_timestamp(),statement_timestamp(),2000,0,0,0,0,0,0,0,true,true);
            """);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("run", run);
        await seed.ExecuteNonQueryAsync();
    }

    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid target)
    {
        await using var scope = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator; SELECT set_config('sqlobserver.target_scope',@target,true);", connection, transaction);
        scope.Parameters.AddWithValue("target", target.ToString()); await scope.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> PreviewAsync(RepositoryTestDatabase database, Guid target, Guid run)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, target);
        await using var read = new NpgsqlCommand("SELECT hold_code||':'||coalesce(relation_name,'') FROM system.collection_run_reference_holds(@target,@run);", connection, transaction);
        read.Parameters.AddWithValue("target", target); read.Parameters.AddWithValue("run", run);
        var results = new List<string>();
        await using (var rows = await read.ExecuteReaderAsync()) while (await rows.ReadAsync()) results.Add(rows.GetString(0));
        await transaction.RollbackAsync();
        return results;
    }
}
