using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class AlertDeliveryScalarBoundaryPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
    private static readonly WorkerLeaseIdentity Lease = new(new WorkerLeaseKey("alerts/scalar-boundary"), new WorkerExecutionId(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")), new FencingToken(1));
    private static readonly AlertDeliveryWork Work = new(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), Guid.NewGuid(), Guid.NewGuid(), "webhook", "test-reference", [], 0, new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero), Target, Lease);

    public enum DeliveryOperation { RecoverClaims, Complete, Renew, Cancel, Defer, RenewState, Recheck }

    [Theory]
    [InlineData(DeliveryOperation.RecoverClaims)]
    [InlineData(DeliveryOperation.Complete)]
    [InlineData(DeliveryOperation.Renew)]
    [InlineData(DeliveryOperation.Cancel)]
    [InlineData(DeliveryOperation.Defer)]
    [InlineData(DeliveryOperation.RenewState)]
    [InlineData(DeliveryOperation.Recheck)]
    public async Task InjectedSqlNullUsesTheExistingConservativeFallback(DeliveryOperation operation)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        // This is a defensive nullable-result boundary, not a claim that current
        // migrated functions naturally return NULL on an ordinary delivery path.
        await ReplaceFunctionBodyAsync(database, operation, operation is DeliveryOperation.RenewState or DeliveryOperation.Recheck ? "NULL::text" : "NULL::boolean");
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();

        await AssertPortResultAsync(new PostgreSqlAlertRepositoryPort(collector), operation, false);
    }

    [Theory]
    [InlineData(DeliveryOperation.RecoverClaims)]
    [InlineData(DeliveryOperation.Complete)]
    [InlineData(DeliveryOperation.Renew)]
    [InlineData(DeliveryOperation.Cancel)]
    [InlineData(DeliveryOperation.Defer)]
    public async Task TypedBooleanResultsKeepTheirExistingMeaning(DeliveryOperation operation)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collector);

        foreach (bool applied in new[] { false, true })
        {
            await ReplaceFunctionBodyAsync(database, operation, applied ? "TRUE" : "FALSE");
            await AssertPortResultAsync(repository, operation, applied);
        }
    }

    [Fact]
    public async Task TypedLeaseStatesKeepTheirExistingMeaning()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        foreach (AlertDeliveryLeaseOutcome expected in Enum.GetValues<AlertDeliveryLeaseOutcome>())
        {
            await ReplaceFunctionBodyAsync(database, DeliveryOperation.RenewState, $"'{expected}'::text");
            Assert.Equal(expected, await repository.RenewDeliveryStateAsync(Work, Lease, Timeout, CancellationToken.None));
        }
        await ReplaceFunctionBodyAsync(database, DeliveryOperation.RenewState, "'unrecognized_state'::text");
        Assert.Equal(AlertDeliveryLeaseOutcome.LostFence, await repository.RenewDeliveryStateAsync(Work, Lease, Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task TypedReadinessStatesKeepTheirExistingMeaning()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        foreach (AlertDeliveryReadiness expected in Enum.GetValues<AlertDeliveryReadiness>())
        {
            await ReplaceFunctionBodyAsync(database, DeliveryOperation.Recheck, $"'{expected}'::text");
            Assert.Equal(expected, await repository.RecheckDeliveryAsync(Work, Timeout, CancellationToken.None));
        }
        await ReplaceFunctionBodyAsync(database, DeliveryOperation.Recheck, "'unrecognized_state'::text");
        Assert.Equal(AlertDeliveryReadiness.LeaseLost, await repository.RecheckDeliveryAsync(Work, Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task NpgsqlScalarReturnsDbNullForTypedSqlNull()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        await using NpgsqlCommand boolean = database.DataSource.CreateCommand("SELECT NULL::boolean;");
        await using NpgsqlCommand text = database.DataSource.CreateCommand("SELECT NULL::text;");

        Assert.Same(DBNull.Value, await boolean.ExecuteScalarAsync());
        Assert.Same(DBNull.Value, await text.ExecuteScalarAsync());
    }

    private static async Task AssertPortResultAsync(PostgreSqlAlertRepositoryPort repository, DeliveryOperation operation, bool applied)
    {
        switch (operation)
        {
            case DeliveryOperation.RecoverClaims:
                Assert.Equal(applied, await repository.RecoverDeliveryClaimsAsync(Target, Lease, Timeout, CancellationToken.None));
                break;
            case DeliveryOperation.Complete:
                var completed = new AlertDeliveryResult(Work.DeliveryId, true, false, "delivered", Work.DueAtUtc, Target, 204, 0);
                AlertDeliveryResult expected = applied ? completed : completed with { Succeeded = false, PermanentFailure = false, Reason = "lease_lost" };
                Assert.Equal(expected, await repository.CompleteDeliveryAsync(completed, Lease, Timeout, CancellationToken.None));
                var permanentFailure = completed with { Succeeded = false, PermanentFailure = true, Reason = "rejected" };
                expected = applied ? permanentFailure : permanentFailure with { PermanentFailure = false, Reason = "lease_lost" };
                Assert.Equal(expected, await repository.CompleteDeliveryAsync(permanentFailure, Lease, Timeout, CancellationToken.None));
                break;
            case DeliveryOperation.Renew:
                Assert.Equal(applied, await repository.RenewDeliveryAsync(Work.DeliveryId, Target, Lease, Timeout, CancellationToken.None));
                break;
            case DeliveryOperation.Cancel:
                Assert.Equal(applied, await repository.CancelDeliveryAsync(new AlertDeliveryCancellation(Work.DeliveryId, "disabled"), Target, Lease, Timeout, CancellationToken.None));
                break;
            case DeliveryOperation.Defer:
                Assert.Equal(applied, await repository.DeferDeliveryAsync(Work, Lease, Timeout, CancellationToken.None));
                break;
            case DeliveryOperation.RenewState:
                Assert.Equal(AlertDeliveryLeaseOutcome.LostFence, await repository.RenewDeliveryStateAsync(Work, Lease, Timeout, CancellationToken.None));
                break;
            case DeliveryOperation.Recheck:
                Assert.Equal(AlertDeliveryReadiness.LeaseLost, await repository.RecheckDeliveryAsync(Work, Timeout, CancellationToken.None));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task ReplaceFunctionBodyAsync(RepositoryTestDatabase database, DeliveryOperation operation, string expression)
    {
        Assert.StartsWith("sqlobserver_", database.DatabaseName, StringComparison.Ordinal);
        string signature = operation switch
        {
            DeliveryOperation.RecoverClaims => "alerting.recover_delivery_claims(uuid,text,uuid,bigint)",
            DeliveryOperation.Complete => "alerting.complete_delivery(uuid,uuid,boolean,boolean,text,integer,integer,text,uuid,bigint)",
            DeliveryOperation.Renew => "alerting.renew_delivery(uuid,uuid,text,uuid,bigint)",
            DeliveryOperation.Cancel => "alerting.cancel_delivery(uuid,uuid,text,text,uuid,bigint)",
            DeliveryOperation.Defer => "alerting.defer_delivery(uuid,uuid,text,text,uuid,bigint)",
            DeliveryOperation.RenewState => "alerting.renew_delivery_with_outcome(uuid,uuid,text,uuid,bigint)",
            DeliveryOperation.Recheck => "alerting.recheck_delivery(uuid,uuid)",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var before = await ReadFunctionAsync(database, signature);
        Assert.Contains(before.Body, before.Definition, StringComparison.Ordinal);
        string replacement = before.Definition.Replace(before.Body, $"BEGIN RETURN {expression}; END;", StringComparison.Ordinal);
        await using NpgsqlCommand command = database.DataSource.CreateCommand(replacement);
        await command.ExecuteNonQueryAsync();

        var after = await ReadFunctionAsync(database, signature);
        // Preserve identity, named arguments, return type, owner, security mode,
        // search path and ACLs; only this disposable database's body is replaced.
        Assert.Equal(before.Contract, after.Contract);
    }

    private static async Task<(string Definition, string Body, string Contract)> ReadFunctionAsync(RepositoryTestDatabase database, string signature)
    {
        await using NpgsqlCommand command = database.DataSource.CreateCommand("SELECT pg_get_functiondef(p.oid),p.prosrc,(to_jsonb(p)-'prosrc')::text FROM pg_proc p WHERE p.oid=@signature::regprocedure;");
        command.Parameters.AddWithValue("signature", signature);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }
}
