using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ObservationTargetGroupingPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task GroupingIsScopedAuditedAndIndependentOfCollectorRevision()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(244,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(5))), CancellationToken.None);
        Assert.False(migrations.HasFailures);

        Guid target = Guid.NewGuid(), other = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
                (instance_id, instance_key, display_name, host_name, tcp_port,
                 connect_timeout, authentication_mode, transport_security_mode,
                 lifecycle_state, revision, created_at, updated_at, discovery_requested_at)
            VALUES (@target, @key, 'Grouped SQL', 'test.invalid', 1433,
                    interval '5 seconds', 'windows_integrated_service_identity',
                    'mandatory_validated', 'active', 1,
                    statement_timestamp(), statement_timestamp(), statement_timestamp()),
                   (@other, @other_key, 'Other SQL', 'test.invalid', 1433,
                    interval '5 seconds', 'windows_integrated_service_identity',
                    'mandatory_validated', 'active', 1,
                    statement_timestamp(), statement_timestamp(), statement_timestamp());
            """))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("other", other);
            seed.Parameters.AddWithValue("key", $"grouping-{target:N}");
            seed.Parameters.AddWithValue("other_key", $"grouping-{other:N}");
            await seed.ExecuteNonQueryAsync();
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var port = new PostgreSqlObservationTargetGroupingPort(server);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(10));
        IReadOnlyDictionary<Guid, ObservationTargetGrouping> before = await port.ReadAsync([target], timeout, CancellationToken.None);
        Assert.Single(before);
        Assert.Null(before[target].Environment);
        Assert.Empty(before[target].Tags);
        Assert.Equal(0, before[target].Revision);

        var actor = new ActorSecurityIdentifier("S-1-5-21-100");
        Guid firstCorrelation = Guid.NewGuid(), secondCorrelation = Guid.NewGuid();
        var first = await port.SetAsync(new SetObservationTargetGroupingRequest(new MonitoredInstanceId(target),
            0, "production", ["critical", "east"],
            new AdministrativeAuditEnvelope(actor, new AuditCorrelationId(firstCorrelation),
                AdministrativeAuditAction.UpdateObservationTarget, new MonitoredInstanceId(target)), timeout), CancellationToken.None);
        Assert.Equal(ObservationTargetGroupingMutationStatus.Applied, first.Status);
        Assert.Equal(1, first.Revision);
        var conflict = await port.SetAsync(new SetObservationTargetGroupingRequest(new MonitoredInstanceId(target),
            0, "staging", [],
            new AdministrativeAuditEnvelope(actor, new AuditCorrelationId(secondCorrelation),
                AdministrativeAuditAction.UpdateObservationTarget, new MonitoredInstanceId(target)), timeout), CancellationToken.None);
        Assert.Equal(ObservationTargetGroupingMutationStatus.RevisionConflict, conflict.Status);
        Assert.Equal(1, conflict.Revision);

        IReadOnlyDictionary<Guid, ObservationTargetGrouping> after = await port.ReadAsync([target], timeout, CancellationToken.None);
        Assert.Single(after);
        Assert.Equal("production", after[target].Environment);
        Assert.Equal(["critical", "east"], after[target].Tags);
        Assert.Equal(1, after[target].Revision);
        await using var check = database.DataSource.CreateCommand("""
            SELECT (SELECT revision FROM control.observation_target WHERE instance_id = @target),
                   (SELECT count(*) FROM audit.activity
                    WHERE correlation_id IN (@first, @second)),
                   has_table_privilege('sqlobserver_server',
                    'control.observation_target_grouping', 'INSERT');
            """);
        check.Parameters.AddWithValue("target", target);
        check.Parameters.AddWithValue("first", firstCorrelation);
        check.Parameters.AddWithValue("second", secondCorrelation);
        await using NpgsqlDataReader row = await check.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(1, row.GetInt64(0));
        Assert.Equal(2, row.GetInt64(1));
        Assert.False(row.GetBoolean(2));
    }
}
