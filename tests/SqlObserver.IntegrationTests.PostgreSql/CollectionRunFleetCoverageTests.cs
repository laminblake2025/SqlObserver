using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunFleetCoverageTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task FleetPagesEveryTargetCollectorAndRestoresCallerScope()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid copiedTarget = Guid.NewGuid(), pendingTarget = Guid.NewGuid();
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@copied,@copied::text,'Copied empty target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now()),
              (@pending,@pending::text,'Pending empty target','sql02',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            """, admin))
        {
            seed.Parameters.AddWithValue("copied", copiedTarget);
            seed.Parameters.AddWithValue("pending", pendingTarget);
            await seed.ExecuteNonQueryAsync();
        }
        await using (NpgsqlTransaction backfill = await admin.BeginTransactionAsync())
        {
            await using (var scope = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, backfill))
            {
                scope.Parameters.AddWithValue("target", copiedTarget);
                await scope.ExecuteNonQueryAsync();
            }
            await using var complete = new NpgsqlCommand(
                "SELECT complete FROM control.backfill_collection_run_v2(@target,10);",
                admin, backfill);
            complete.Parameters.AddWithValue("target", copiedTarget);
            Assert.True((bool)(await complete.ExecuteScalarAsync())!);
            await backfill.CommitAsync();
        }

        var expected = new List<(Guid Target, string Collector)>();
        await using (var inventory = new NpgsqlCommand("""
            SELECT target.instance_id,catalog.collector_id
            FROM control.observation_target AS target
            CROSS JOIN (SELECT DISTINCT collector_id
                        FROM control.collector_contract) AS catalog
            ORDER BY target.instance_id,catalog.collector_id;
            """, admin))
        await using (NpgsqlDataReader rows = await inventory.ExecuteReaderAsync())
            while (await rows.ReadAsync())
                expected.Add((rows.GetGuid(0), rows.GetString(1)));
        Assert.True(expected.Count > 3);

        var seen = new List<(Guid Target, string Collector)>();
        Guid sentinel = Guid.NewGuid();
        Guid? afterTarget = null;
        string? afterCollector = null;
        await using (NpgsqlTransaction paging = await admin.BeginTransactionAsync())
        {
            await using (var scope = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@sentinel::text,true);
                """, admin, paging))
            {
                scope.Parameters.AddWithValue("sentinel", sentinel);
                await scope.ExecuteNonQueryAsync();
            }
            while (true)
            {
                await using var page = new NpgsqlCommand("""
                    SELECT * FROM control.list_collection_run_v2_parity_coverage(
                      @after_target,@after_collector,3);
                    """, admin, paging);
                page.Parameters.Add(new NpgsqlParameter("after_target", NpgsqlDbType.Uuid)
                    { Value = (object?)afterTarget ?? DBNull.Value });
                page.Parameters.Add(new NpgsqlParameter("after_collector", NpgsqlDbType.Text)
                    { Value = (object?)afterCollector ?? DBNull.Value });
                int count = 0;
                await using (NpgsqlDataReader rows = await page.ExecuteReaderAsync())
                {
                    while (await rows.ReadAsync())
                    {
                        Guid target = rows.GetGuid(0);
                        string collector = rows.GetString(1);
                        seen.Add((target, collector));
                        afterTarget = target;
                        afterCollector = collector;
                        count++;
                        Assert.True(rows.IsDBNull(2));
                        Assert.True(rows.IsDBNull(3));
                        Assert.True(rows.IsDBNull(4));
                        if (target == copiedTarget) Assert.True(rows.GetBoolean(5));
                        if (target == pendingTarget) Assert.False(rows.GetBoolean(5));
                    }
                }
                await using var restored = new NpgsqlCommand(
                    "SELECT current_setting('sqlobserver.target_scope',true);",
                    admin, paging);
                Assert.Equal(sentinel.ToString(), await restored.ExecuteScalarAsync());
                if (count == 0) break;
            }
            await paging.CommitAsync();
        }
        Assert.Equal(expected, seen);
        Assert.Contains(seen, pair => pair.Target == copiedTarget);
        Assert.Contains(seen, pair => pair.Target == pendingTarget);

        await using (var privilege = new NpgsqlCommand("""
            SELECT has_function_privilege('sqlobserver_server',
                     'control.list_collection_run_v2_parity_coverage(uuid,text,integer)',
                     'EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                     'control.list_collection_run_v2_parity_coverage(uuid,text,integer)',
                     'EXECUTE');
            """, admin))
        await using (NpgsqlDataReader rows = await privilege.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync());
            Assert.False(rows.GetBoolean(0));
            Assert.False(rows.GetBoolean(1));
        }

        await using (NpgsqlTransaction invalid = await admin.BeginTransactionAsync())
        {
            await using var badCursor = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT * FROM control.list_collection_run_v2_parity_coverage(
                  @target,NULL::text,1);
                """, admin, invalid);
            badCursor.Parameters.AddWithValue("target", copiedTarget);
            PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
                () => badCursor.ExecuteNonQueryAsync());
            Assert.Equal("22023", rejected.SqlState);
            await invalid.RollbackAsync();
        }
    }
}
