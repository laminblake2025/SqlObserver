using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(IsolatedClockPostgreSqlCollectionDefinition.Name)]
[Trait("Category","RequiresPostgreSql")]
public sealed class ReplicationWindowHistoryClockPostgreSqlTests(IsolatedClockPostgreSqlFixture fixture)
{
    [Fact]
    public async Task GenuineNativeClockAdvanceExpiresOriginalListAndSnapshotContinuations()
    {
        await using var db=await fixture.CreateDatabaseAsync();var migrated=await new PostgreSqlMigrationPort(db.DataSource).ApplyPendingAsync(new(MigrationBatchResult.MaximumResults,ReplicationWindowFixture.Timeout),CancellationToken.None);Assert.False(migrated.HasFailures);
        await SqlVolumeWindowHistoryFixture.CutoverCanonicalForeignKeysAsync(db);await SqlVolumeWindowHistoryFixture.SealAsync(db);
        Guid target=await ReplicationWindowFixture.TargetAsync(db);var window=await DatabaseFileWindowFixture.WindowAsync(db);var selected=await ReplicationWindowFixture.CommitAsync(db,target,2);await ReplicationWindowFixture.CommitAsync(db,target,1);
        await using var server=db.CreateServerDataSource();var reader=new PostgreSqlReplicationWindowHistoryRepositoryPort(server);var list=ReplicationWindowFixture.List(target,window,1);var exact=ReplicationWindowFixture.Exact(target,window,selected.Id.Value,1);
        var first=Assert.IsType<ReplicationWindowHistoryPage>(await reader.ListAsync(list,CancellationToken.None));var snapshot=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(exact,CancellationToken.None));Assert.NotNull(first.NextAnchor);Assert.NotNull(snapshot.NextAnchor);
        // Advances only the dedicated container clock. No production/source timestamps are edited.
        await fixture.AdvanceAsync(TimeSpan.FromDays(1),CancellationToken.None);
        Assert.Equal(409,(await Assert.ThrowsAsync<ReplicationWindowHistoryReadException>(()=>reader.ListAsync(list with {Cursor=first.NextAnchor},CancellationToken.None).AsTask())).StatusCode);
        Assert.Equal(409,(await Assert.ThrowsAsync<ReplicationWindowHistoryReadException>(()=>reader.GetAsync(exact with {Cursor=snapshot.NextAnchor},CancellationToken.None).AsTask())).StatusCode);
        var retained=Assert.IsType<ReplicationWindowHistorySnapshotPage>(await reader.GetAsync(exact,CancellationToken.None));Assert.Equal(snapshot.Receipt,retained.Receipt);Assert.Equal(Assert.Single(snapshot.Items),Assert.Single(retained.Items));Assert.NotNull(retained.NextAnchor);
    }
}
