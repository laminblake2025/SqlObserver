using Npgsql;
using NpgsqlTypes;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class QueryPerformanceV2CommitPostgreSqlTests
{
    [Fact]
    public async Task CacheReferencesCanonicalRunIdentityWithIndexedValidatedNonCascadingForeignKeys()
    {
        await using Scene scene = await Prepare();
        await using var catalog = scene.Database.DataSource.CreateCommand("""
            SELECT count(*)=3,
                bool_and(fk.convalidated AND NOT fk.condeferrable AND fk.confdeltype='a' AND fk.confupdtype='a'),
                bool_and(child.attname=CASE WHEN fk.conrelid='events.plan_cache_runtime_watermark'::regclass
                    THEN 'last_run_id' ELSE 'run_id' END AND parent.attname='run_id'),
                bool_and(EXISTS(SELECT 1 FROM pg_index idx WHERE idx.indrelid=fk.conrelid
                    AND idx.indisvalid AND idx.indisready AND idx.indkey[0]=fk.conkey[1]))
            FROM pg_constraint fk
            JOIN pg_attribute child ON child.attrelid=fk.conrelid AND child.attnum=fk.conkey[1]
            JOIN pg_attribute parent ON parent.attrelid=fk.confrelid AND parent.attnum=fk.confkey[1]
            WHERE fk.contype='f' AND fk.conparentid=0 AND fk.confrelid='telemetry.collection_run_identity'::regclass
                AND fk.conrelid IN ('events.plan_cache_runtime_watermark'::regclass,
                    'events.plan_cache_runtime_delta'::regclass,'events.plan_cache_runtime_receipt'::regclass);
            """);
        await using var reader = await catalog.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 4; index++) Assert.True(reader.GetBoolean(index));
    }

    [Fact]
    public async Task UnknownRunCannotPublishPrivateCacheEvidenceAndCommittedRowsHoldTheirQueryGraph()
    {
        await using Scene scene = await Prepare(); Frame frame = await Start(scene);
        string before = await Snapshot(scene);
        await using (var connection = await scene.Database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, scene.Target);
            await using var apply = new NpgsqlCommand("SELECT events.apply_plan_cache_runtime_batch(@run,@target,1,@payload);", connection, transaction);
            apply.Parameters.AddWithValue("run", Guid.NewGuid()); apply.Parameters.AddWithValue("target", scene.Target);
            apply.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb,
                System.Text.Encoding.UTF8.GetString(PlanCacheRuntimeCommitPayload.Serialize(frame.Cache!)));
            Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(async () => await apply.ExecuteScalarAsync())).SqlState);
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await Snapshot(scene));
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(migrator);
        Assert.Equal(SqlObserver.Application.Ports.CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(PortRequest(frame, PortPayload(frame)), default)).Status);
        await using var scoped = await migrator.OpenConnectionAsync(); await using var scope = await scoped.BeginTransactionAsync();
        await Scope(scoped, scope, scene.Target);
        await using var holds = new NpgsqlCommand("SELECT hold_code FROM system.query_performance_run_retention_holds(@target,@run);", scoped, scope);
        holds.Parameters.AddWithValue("target", scene.Target); holds.Parameters.AddWithValue("run", frame.Run.Value);
        var codes = new List<string>();
        await using (var reader = await holds.ExecuteReaderAsync()) while (await reader.ReadAsync()) codes.Add(reader.GetString(0));
        Assert.Contains("cache_watermark", codes); Assert.Contains("cache_delta", codes); Assert.Contains("cache_receipt", codes);
        Assert.DoesNotContain("no_known_hold", codes);
    }
}
