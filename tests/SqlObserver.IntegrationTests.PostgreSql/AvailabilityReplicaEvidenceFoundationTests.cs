using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class AvailabilityReplicaEvidenceFoundationTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ReplicaDatabaseRowsRemainDistinctPrivateImmutableAndTargetScoped()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrated.HasFailures, string.Join(", ", migrated.Results
            .Where(static item => item.Outcome == MigrationOutcome.Failed)
            .Select(static item => $"{item.Migration.Number.Value}:{item.FailureCode}")));

        Guid targetA = Guid.NewGuid(), targetB = Guid.NewGuid(), runA = Guid.NewGuid(), runB = Guid.NewGuid();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var catalog = new NpgsqlCommand("""
            SELECT count(*) FROM pg_class AS relation
            WHERE relation.oid IN ('telemetry.availability_replica_evidence'::regclass,
                'telemetry.availability_database_replica_evidence'::regclass)
              AND relation.relkind='p' AND relation.relrowsecurity AND relation.relforcerowsecurity
              AND pg_get_userbyid(relation.relowner)='sqlobserver_migrator'
              AND NOT has_table_privilege('sqlobserver_server',relation.oid,'SELECT')
              AND NOT has_table_privilege('sqlobserver_collector',relation.oid,'INSERT')
              AND NOT has_table_privilege('sqlobserver_auditor',relation.oid,'SELECT')
              AND NOT EXISTS (SELECT 1 FROM aclexplode(
                  coalesce(relation.relacl,acldefault('r',relation.relowner))) AS acl
                  WHERE acl.grantee=0);
            """, connection))
            Assert.Equal(2L, await catalog.ExecuteScalarAsync());

        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
               authentication_mode,transport_security_mode,lifecycle_state,revision,
               created_at,updated_at,discovery_requested_at)
            SELECT id,'ag.foundation.'||id::text,'AG foundation','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity','mandatory_validated',
              'active',1,statement_timestamp(),statement_timestamp(),statement_timestamp()
            FROM (VALUES (@target_a),(@target_b)) AS targets(id);
            SET ROLE sqlobserver_migrator;
            DO $partitions$ BEGIN
              EXECUTE format('CREATE TABLE telemetry.ag_replica_foundation_test PARTITION OF telemetry.availability_replica_evidence FOR VALUES FROM (%L) TO (%L)',current_date::timestamptz,(current_date+1)::timestamptz);
              EXECUTE format('CREATE TABLE telemetry.ag_database_foundation_test PARTITION OF telemetry.availability_database_replica_evidence FOR VALUES FROM (%L) TO (%L)',current_date::timestamptz,(current_date+1)::timestamptz);
            END $partitions$;
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,output_schema_version,
               target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,
               request_digest,scheduled_for,started_at)
            SELECT run,target,'availability-groups.health',1,1,1,1,
              'collector/run/availability-groups.health/'||target::text,@owner,1,
              decode(repeat('aa',32),'hex'),statement_timestamp(),statement_timestamp()
            FROM (VALUES (@run_a,@target_a),(@run_b,@target_b)) AS runs(run,target);
            INSERT INTO telemetry.availability_replica_evidence
              (instance_id,run_id,target_revision,observed_at,group_fingerprint,
               replica_fingerprint,observer_replica_fingerprint,replica_is_local,
               visibility_scope,state_observed,role,operational_state,connected_state)
            SELECT @target_a,@run_a,1,statement_timestamp(),decode(repeat('aa',32),'hex'),
              decode(repeat(replica,32),'hex'),decode(repeat('11',32),'hex'),local,
              1,true,role,operational,connected
            FROM (VALUES ('11',true,'PRIMARY','ONLINE','CONNECTED'),
                         ('22',false,'SECONDARY',NULL,'DISCONNECTED'))
              AS replicas(replica,local,role,operational,connected);
            INSERT INTO telemetry.availability_database_replica_evidence
              (instance_id,run_id,target_revision,observed_at,group_fingerprint,
               replica_fingerprint,database_fingerprint,observer_replica_fingerprint,
               replica_is_local,visibility_scope,state_observed,synchronization_state,
               synchronization_health,database_state,is_suspended,log_send_queue_kb,
               source_last_commit_local)
            SELECT target,run,1,statement_timestamp(),decode(repeat('aa',32),'hex'),
              decode(repeat(replica,32),'hex'),decode(repeat('dd',32),'hex'),
              decode(repeat('11',32),'hex'),replica='11',1,observed,
              CASE WHEN observed THEN 'SYNCHRONIZED' END,
              CASE WHEN observed THEN 'HEALTHY' END,
              CASE WHEN observed THEN 'ONLINE' END,
              CASE WHEN observed THEN false END,queue,
              CASE WHEN observed THEN timestamp '2026-09-29 08:15:00' END
            FROM (VALUES (@target_a,@run_a,'11',true,NULL::bigint),
                         (@target_a,@run_a,'22',true,0::bigint),
                         (@target_a,@run_a,'33',false,NULL::bigint),
                         (@target_b,@run_b,'22',true,1024::bigint))
              AS databases(target,run,replica,observed,queue);
            """, connection))
        {
            seed.Parameters.AddWithValue("target_a", targetA);
            seed.Parameters.AddWithValue("target_b", targetB);
            seed.Parameters.AddWithValue("run_a", runA);
            seed.Parameters.AddWithValue("run_b", runB);
            seed.Parameters.AddWithValue("owner", Guid.NewGuid());
            await seed.ExecuteNonQueryAsync();
        }

        await using (var read = new NpgsqlCommand("""
            SELECT count(*),count(DISTINCT replica_fingerprint),
              count(*) FILTER (WHERE log_send_queue_kb IS NULL),
              count(*) FILTER (WHERE log_send_queue_kb=0),
              count(*) FILTER (WHERE NOT state_observed),
              min(source_last_commit_local)
            FROM telemetry.availability_database_replica_evidence WHERE instance_id=@target;
            """, connection))
        {
            read.Parameters.AddWithValue("target", targetA);
            await using NpgsqlDataReader rows = await read.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(3L, rows.GetInt64(0));
            Assert.Equal(3L, rows.GetInt64(1));
            Assert.Equal(2L, rows.GetInt64(2));
            Assert.Equal(1L, rows.GetInt64(3));
            Assert.Equal(1L, rows.GetInt64(4));
            DateTime localCommit = rows.GetDateTime(5);
            Assert.Equal(DateTimeKind.Unspecified, localCommit.Kind);
            Assert.Equal(new DateTime(2026, 9, 29, 8, 15, 0), localCommit);
        }
        foreach (string table in new[] { "availability_replica_evidence", "availability_database_replica_evidence" })
        {
            await using var mutation = new NpgsqlCommand($"DELETE FROM telemetry.{table};", connection);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => mutation.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var invalid = new NpgsqlCommand("""
            INSERT INTO telemetry.availability_database_replica_evidence
              (instance_id,run_id,target_revision,observed_at,group_fingerprint,
               replica_fingerprint,database_fingerprint,observer_replica_fingerprint,
               replica_is_local,visibility_scope,state_observed,log_send_queue_kb)
            VALUES (@target,@run,1,statement_timestamp(),decode(repeat('aa',32),'hex'),
              decode(repeat('44',32),'hex'),decode(repeat('dd',32),'hex'),
              decode(repeat('11',32),'hex'),false,1,false,1);
            """, connection))
        {
            invalid.Parameters.AddWithValue("target", targetA);
            invalid.Parameters.AddWithValue("run", runA);
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(
                () => invalid.ExecuteNonQueryAsync())).SqlState);
        }

        // Temporary test-only grants exercise forced target RLS through the
        // actual server role while the deployed tables remain private.
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var grant = new NpgsqlCommand("""
            GRANT SELECT ON telemetry.availability_database_replica_evidence TO sqlobserver_server;
            SET LOCAL ROLE sqlobserver_server;
            """, connection, transaction))
            await grant.ExecuteNonQueryAsync();
        await using (var scoped = new NpgsqlCommand("""
            SELECT set_config('sqlobserver.target_scope',@scope,true);
            SELECT count(*) FROM telemetry.availability_database_replica_evidence;
            """, connection, transaction))
        {
            scoped.Parameters.AddWithValue("scope", targetA.ToString());
            await using NpgsqlDataReader rows = await scoped.ExecuteReaderAsync();
            Assert.True(await rows.NextResultAsync());
            Assert.True(await rows.ReadAsync());
            Assert.Equal(3L, rows.GetInt64(0));
        }
        await transaction.RollbackAsync();
    }
}
