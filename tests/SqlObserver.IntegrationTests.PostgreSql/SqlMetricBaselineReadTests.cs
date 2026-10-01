using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Security;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class SqlMetricAnalyticsSourceTests
{
    [Fact]
    public async Task SqlBaselineServerReadPreservesAllHoursAndRefusesMixedScopeAndRevision()
    {
        await using RepositoryTestDatabase database = await Database(); Guid target = await Target(database);
        Guid other = await Target(database); await SeedBaselineHours(database, target); await SqlBaselineJob(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); var writer = RollupRepository(collector);
        var lease = await RollupLease(collector); AnalyticsDerivationJob job = Assert.Single(await writer.ClaimAsync(lease, 1, default));
        var result = SqlMetricBaselineV2.Compute(await writer.ReadSqlBaselineInputsAsync(job, default), BatchMetric, job.ToUtc, job.SourceCutoffUtc);
        await writer.StoreSqlBaselinesAsync(RollupRequest(job, lease), result, default);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var reader = RollupRepository(server);
        var service = new SqlMetricBaselineQueryService(reader);
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001"), AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(target)])),
             new RoleAuthorizationGrant(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([new(other)]))]);
        var request = new AnalyticsQueryRequest(new(target), BatchMetric, BaselineEnd.AddDays(-28), BaselineEnd, 168, RollupTimeout, new(1), DateTimeOffset.UtcNow);
        SqlMetricBaselinePage page = await service.GetAsync(authorization, request, 2, 1, default);
        Assert.Equal(target, page.TargetId); Assert.Equal(1, page.TargetRevision); Assert.Equal("batches_per_second", page.Unit);
        Assert.Equal("complete", page.State); Assert.Equal(168, page.Items.Count);
        Assert.All(page.Items, row => { Assert.Equal(32.5, row.Statistics.Median); Assert.Equal(4, row.Statistics.SampleCount); });
        Assert.Equal(Enumerable.Range(0, 168), page.Items.Select(row => row.Statistics.HourOfWeek));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(authorization, request with { TargetId = new(other) }, 2, 1, default).AsTask());
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => reader.ReadSqlBaselinesAsync(request with { TargetRevision = new(2) }, 1, default).AsTask())).SqlState);
        Assert.Empty((await service.GetAsync(authorization, request, 2, 2, default)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(authorization, request, 1, 1, default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(authorization, request with { MetricKey = "host.cpu.percent" }, 2, 1, default).AsTask());
        await using var permissions = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner)='sqlobserver_migrator'
                AND has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
                AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
                AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
                AND NOT EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid='reporting.read_sql_metric_baselines(uuid,bigint,text,timestamptz,timestamptz,timestamptz,bigint)'::regprocedure;
            """);
        Assert.Equal(true, await permissions.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SqlBaselineReadSnapshotBeforeBundleCompletionDoesNotReturnPartialHours()
    {
        await using RepositoryTestDatabase database = await Database(); Guid target = await Target(database); await SqlBaselineJob(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); var writer = RollupRepository(collector);
        var lease = await RollupLease(collector); AnalyticsDerivationJob job = Assert.Single(await writer.ClaimAsync(lease, 1, default));
        await writer.StoreSqlBaselinesAsync(RollupRequest(job, lease), SqlMetricBaselineV2.Compute([], BatchMetric, job.ToUtc, job.SourceCutoffUtc), default);
        await using var timestamps = database.DataSource.CreateCommand("SELECT min(computed_at),max(computed_at) FROM analytics.sql_metric_baseline WHERE instance_id=@target;");
        timestamps.Parameters.AddWithValue("target", target); DateTimeOffset first, last;
        await using (NpgsqlDataReader rows = await timestamps.ExecuteReaderAsync())
        { Assert.True(await rows.ReadAsync()); first = rows.GetFieldValue<DateTimeOffset>(0); last = rows.GetFieldValue<DateTimeOffset>(1); }
        Assert.True(last > first);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var reader = RollupRepository(server);
        var request = new AnalyticsQueryRequest(new(target), BatchMetric, job.FromUtc, job.ToUtc, 168, RollupTimeout, new(1), first);
        Assert.Empty(await reader.ReadSqlBaselinesAsync(request, 1, default));
        Assert.Equal(168, (await reader.ReadSqlBaselinesAsync(request with { SnapshotUtc = last }, 1, default)).Count);
        Assert.Empty(await reader.ReadSqlBaselinesAsync(request with { SnapshotUtc = job.ToUtc }, 1, default));
    }
}
