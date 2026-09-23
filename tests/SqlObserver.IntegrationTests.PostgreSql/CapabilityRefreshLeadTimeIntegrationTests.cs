using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CapabilityRefreshLeadTimeIntegrationTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task RefreshBecomesDueBeforeExpiryWithoutImmediatelyRepeatingFreshAttempts()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var migrated = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, PostgreSql18Fixture.MigrationSetupTimeout),CancellationToken.None);
        Assert.False(migrated.HasFailures);
        Guid fresh=Guid.NewGuid(),nearExpiry=Guid.NewGuid(),expired=Guid.NewGuid(),shortFresh=Guid.NewGuid();
        await using var seed=database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT id,id::text,'Refresh test','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,now()-interval '1 day',now(),now()-interval '1 day' FROM (VALUES(@fresh),(@near),(@expired),(@short)) t(id);
            INSERT INTO control.capability_discovery_attempt(attempt_id,instance_id,target_revision,collector_id,collector_manifest_version,output_schema_version,outcome,discovery_reason,authentication_scheme,transport_encrypted,is_sysadmin,duration_ms,response_bytes,checked_at,valid_until,recorded_at)
            SELECT gen_random_uuid(),id,1,'capability.connection',1,1,'unreachable','network_unreachable','unknown',false,false,0,0,now()-age,now()-age+ttl,now()-age
            FROM (VALUES(@fresh,interval '10 seconds',interval '5 minutes'),(@near,interval '4 minutes',interval '5 minutes'),(@expired,interval '6 minutes',interval '5 minutes'),(@short,interval '10 seconds',interval '1 minute')) a(id,age,ttl);
            """);
        seed.Parameters.AddWithValue("fresh",fresh);seed.Parameters.AddWithValue("near",nearExpiry);seed.Parameters.AddWithValue("expired",expired);seed.Parameters.AddWithValue("short",shortFresh);
        await seed.ExecuteNonQueryAsync();
        await using var query=database.DataSource.CreateCommand("SELECT target_instance_id,target_revision FROM control.list_due_capability_targets(16)");
        await using var reader=await query.ExecuteReaderAsync();
        var due=new List<Guid>();
        while(await reader.ReadAsync()){due.Add(reader.GetGuid(0));Assert.Equal(1,reader.GetInt64(1));}
        Assert.Equal(2,due.Count);Assert.Contains(nearExpiry,due);Assert.Contains(expired,due);
        Assert.DoesNotContain(fresh,due);Assert.DoesNotContain(shortFresh,due);
    }
}
