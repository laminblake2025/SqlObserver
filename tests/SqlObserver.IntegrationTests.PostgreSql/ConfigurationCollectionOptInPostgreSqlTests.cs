using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ConfigurationCollectionOptInPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ToggleIsAuditedRevisionFencedAndServerOnly()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.False(migrated.HasFailures);
        Guid target = Guid.NewGuid();
        Guid pendingTarget = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'Configuration toggle target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,statement_timestamp(),
              statement_timestamp(),statement_timestamp());
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@pending,@pending_key,'Configuration pending target','sql02',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','pending_discovery',1,statement_timestamp(),
              statement_timestamp(),statement_timestamp());
            """))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"config.toggle.{target:N}");
            seed.Parameters.AddWithValue("pending", pendingTarget);
            seed.Parameters.AddWithValue("pending_key", $"config.pending.{pendingTarget:N}");
            await seed.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var first = await ToggleAsync(server, target, 1, false, true);
        Assert.Equal(("applied", 2L, true), first);
        var conflict = await ToggleAsync(server, target, 1, false, false);
        Assert.Equal(("revision_conflict", 2L, true), conflict);
        var disabled = await ToggleAsync(server, target, 1, true, false);
        Assert.Equal(("applied", 3L, false), disabled);
        var staleTarget = await ToggleAsync(server, target, 2, false, true);
        Assert.Equal(("revision_conflict", null, null), staleTarget);
        var pending = await ToggleAsync(server, pendingTarget, 1, false, true);
        Assert.Equal(("inactive", null, null), pending);
        var port = new PostgreSqlConfigurationCollectionAdministrationPort(server);
        ConfigurationCollectionMutationResult enabledByPort = await port.SetAsync(
            new SetConfigurationCollectionRequest(new MonitoredInstanceId(target), 1,
                false, true, new AdministrativeAuditEnvelope(
                    new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001"),
                    new AuditCorrelationId(Guid.NewGuid()),
                    AdministrativeAuditAction.UpdateObservationTarget,
                    new MonitoredInstanceId(target)),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5))),
            CancellationToken.None);
        Assert.Equal(ConfigurationCollectionMutationStatus.Applied, enabledByPort.Status);
        Assert.Equal(4, enabledByPort.ScheduleRevision);
        Assert.True(enabledByPort.Enabled);
        await using (var evidence = database.DataSource.CreateCommand("""
            SELECT schedule_revision,enabled FROM control.collector_schedule
            WHERE instance_id=@target AND collector_id='engine.configurations';
            """))
        {
            evidence.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader row = await evidence.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(4, row.GetInt64(0));
            Assert.True(row.GetBoolean(1));
        }
        await using (var audit = database.DataSource.CreateCommand("""
            SELECT count(*) FROM audit.activity
            WHERE subject_identifier=@target::text
              AND action_name='observation_target.update';
            """))
        {
            audit.Parameters.AddWithValue("target", target);
            Assert.Equal(5L, await audit.ExecuteScalarAsync());
        }
        await using (var grants = database.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_server',
                     'control.set_configuration_collection_enabled(uuid,bigint,boolean,boolean,text,uuid)',
                     'EXECUTE')
               AND NOT has_function_privilege('sqlobserver_collector',
                     'control.set_configuration_collection_enabled(uuid,bigint,boolean,boolean,text,uuid)',
                     'EXECUTE')
               AND NOT has_table_privilege('sqlobserver_server',
                     'control.collector_schedule','UPDATE');
            """))
            Assert.Equal(true, await grants.ExecuteScalarAsync());
        await using (var pendingAudit = database.DataSource.CreateCommand("""
            SELECT safe_details->>'reason' FROM audit.activity
            WHERE subject_identifier=@target::text
              AND action_name='observation_target.update';
            """))
        {
            pendingAudit.Parameters.AddWithValue("target", pendingTarget);
            Assert.Equal("revision_conflict", await pendingAudit.ExecuteScalarAsync());
        }
    }

    private static async Task<(string Status, long? Revision, bool? Enabled)> ToggleAsync(
        NpgsqlDataSource server, Guid target, long expectedRevision,
        bool expectedEnabled, bool enabled)
    {
        await using var command = server.CreateCommand("""
            SELECT result_status,schedule_revision,collector_enabled
            FROM control.set_configuration_collection_enabled(
                @target,@revision,@expected,@enabled,@actor,@correlation);
            """);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("revision", expectedRevision);
        command.Parameters.AddWithValue("expected", expectedEnabled);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("actor", "S-1-5-21-1-2-3-1001");
        command.Parameters.AddWithValue("correlation", Guid.NewGuid());
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = (reader.GetString(0), reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1),
            reader.IsDBNull(2) ? (bool?)null : reader.GetBoolean(2));
        Assert.False(await reader.ReadAsync());
        return result;
    }
}
