using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;
using Testcontainers.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class LeaseResilienceIntegrationTests
{
    private static readonly RepositoryCallTimeout DefaultTimeout = new(TimeSpan.FromSeconds(30));
    private static readonly WorkerLeaseDuration MinimumLeaseDuration = new(WorkerLeaseDuration.Minimum);
    private static readonly WorkerLeaseDuration LongLeaseDuration = new(TimeSpan.FromSeconds(30));

    private readonly PostgreSql18Fixture _fixture;

    public LeaseResilienceIntegrationTests(PostgreSql18Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExpiredLeaseSurvivesOwnerCrashAndTakeoverAdvancesFence()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var key = NewKey("crash-expiry");
        var crashedOwner = new WorkerExecutionId(Guid.NewGuid());
        var replacementOwner = new WorkerExecutionId(Guid.NewGuid());
        WorkerLease crashedLease;

        await using (NpgsqlDataSource crashedDataSource = database.CreateCollectorDataSource())
        {
            var crashedWorker = new PostgreSqlWorkerLeasePort(crashedDataSource);
            LeaseAcquisitionResult acquired = await crashedWorker.AcquireAsync(
                new AcquireWorkerLeaseRequest(
                    key,
                    crashedOwner,
                    MinimumLeaseDuration,
                    DefaultTimeout),
                CancellationToken.None);
            crashedLease = Assert.IsType<WorkerLease>(acquired.Lease);

            LeaseAcquisitionResult contended = await crashedWorker.AcquireAsync(
                new AcquireWorkerLeaseRequest(
                    key,
                    replacementOwner,
                    MinimumLeaseDuration,
                    DefaultTimeout),
                CancellationToken.None);
            Assert.Equal(LeaseAcquisitionStatus.Contended, contended.Status);
            Assert.Null(contended.Lease);
        }

        await WaitForRepositoryExpiryAsync(database.DataSource, key);

        await using NpgsqlDataSource replacementDataSource = database.CreateCollectorDataSource();
        var replacementWorker = new PostgreSqlWorkerLeasePort(replacementDataSource);
        LeaseAcquisitionResult takeover = await replacementWorker.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                key,
                replacementOwner,
                MinimumLeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        WorkerLease replacementLease = Assert.IsType<WorkerLease>(takeover.Lease);

        Assert.Equal(
            checked(crashedLease.Identity.FencingToken.Value + 1),
            replacementLease.Identity.FencingToken.Value);
        Assert.Equal(
            LeaseRenewalStatus.OwnershipLost,
            (await replacementWorker.RenewAsync(
                new RenewWorkerLeaseRequest(
                    crashedLease.Identity,
                    MinimumLeaseDuration,
                    DefaultTimeout),
                CancellationToken.None)).Status);
        Assert.Equal(
            LeaseReleaseStatus.NotOwned,
            await replacementWorker.ReleaseAsync(
                new ReleaseWorkerLeaseRequest(crashedLease.Identity, DefaultTimeout),
                CancellationToken.None));
    }

    [Fact]
    public async Task RepositoryClockRemainsAuthoritativeAcrossSessionTimezoneAndCallerSkew()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await SetDatabaseTimeZoneAsync(database);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();

        await using (NpgsqlConnection connection = await collectorDataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("SHOW TimeZone;", connection))
        {
            Assert.Equal("Pacific/Kiritimati", await command.ExecuteScalarAsync());
        }

        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        var key = NewKey("repository-clock");
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                key,
                new WorkerExecutionId(Guid.NewGuid()),
                LongLeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        WorkerLease lease = Assert.IsType<WorkerLease>(acquired.Lease);

        Assert.Equal(TimeSpan.Zero, acquired.RepositoryTimeUtc.Offset);
        Assert.Equal(TimeSpan.Zero, lease.AcquiredAtUtc.Offset);
        Assert.Equal(LongLeaseDuration.Value, lease.ExpiresAtUtc - lease.RenewedAtUtc);

        DateTimeOffset callerClockFarBehind = acquired.RepositoryTimeUtc.AddYears(-50);
        DateTimeOffset callerClockFarAhead = acquired.RepositoryTimeUtc.AddYears(50);
        Assert.True(callerClockFarBehind < lease.AcquiredAtUtc);
        Assert.True(callerClockFarAhead > lease.ExpiresAtUtc);

        LeaseAcquisitionResult contender = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                key,
                new WorkerExecutionId(Guid.NewGuid()),
                LongLeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Contended, contender.Status);
        Assert.Equal(
            LeaseOwnershipStatus.Current,
            await leases.AssertOwnershipAsync(
                new AssertWorkerLeaseRequest(lease.Identity, DefaultTimeout),
                CancellationToken.None));
    }

    [Fact]
    public async Task RenewalsKeepLongWorkFencedBeyondTheOriginalExpiry()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        var key = NewKey("long-work");
        WorkerExecutionId owner = new(Guid.NewGuid());
        WorkerExecutionId contender = new(Guid.NewGuid());
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                key,
                owner,
                MinimumLeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        WorkerLease original = Assert.IsType<WorkerLease>(acquired.Lease);
        WorkerLease current = original;
        LeaseRenewalResult? lastRenewal = null;

        for (int renewal = 0; renewal < 3; renewal++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1900));
            lastRenewal = await leases.RenewAsync(
                new RenewWorkerLeaseRequest(
                    current.Identity,
                    MinimumLeaseDuration,
                    DefaultTimeout),
                CancellationToken.None);
            Assert.Equal(LeaseRenewalStatus.Renewed, lastRenewal.Status);
            current = Assert.IsType<WorkerLease>(lastRenewal.Lease);
            Assert.Equal(original.Identity.FencingToken, current.Identity.FencingToken);
        }

        Assert.NotNull(lastRenewal);
        Assert.True(lastRenewal.RepositoryTimeUtc > original.ExpiresAtUtc);
        Assert.Equal(
            LeaseAcquisitionStatus.Contended,
            (await leases.AcquireAsync(
                new AcquireWorkerLeaseRequest(
                    key,
                    contender,
                    MinimumLeaseDuration,
                    DefaultTimeout),
                CancellationToken.None)).Status);

        var forgedIdentity = new WorkerLeaseIdentity(
            key,
            owner,
            new FencingToken(checked(current.Identity.FencingToken.Value + 1)));
        Assert.Equal(
            LeaseOwnershipStatus.NotCurrent,
            await leases.AssertOwnershipAsync(
                new AssertWorkerLeaseRequest(forgedIdentity, DefaultTimeout),
                CancellationToken.None));
        Assert.Equal(
            LeaseOwnershipStatus.Current,
            await leases.AssertOwnershipAsync(
                new AssertWorkerLeaseRequest(current.Identity, DefaultTimeout),
                CancellationToken.None));
    }

    [Fact]
    public async Task LeaseAssertionBlocksReplacementUntilProtectedTransactionCompletes()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        WorkerLease lease = await AcquireLeaseAsync(leases, NewKey("assertion-lock"), LongLeaseDuration);

        await using NpgsqlConnection assertingConnection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction assertingTransaction = await assertingConnection.BeginTransactionAsync();
        await AssertLeaseInsideTransactionAsync(assertingConnection, assertingTransaction, lease.Identity);

        Task<LeaseReleaseStatus> release = leases.ReleaseAsync(
            new ReleaseWorkerLeaseRequest(lease.Identity, DefaultTimeout),
            CancellationToken.None).AsTask();
        await WaitForBlockedLeaseStatementAsync(database.DataSource, "release_worker_lease");
        Assert.False(release.IsCompleted);

        await assertingTransaction.CommitAsync();
        Assert.Equal(LeaseReleaseStatus.Released, await release);
    }

    [Fact]
    public async Task BlockedLeaseCallsHonorCallerCancellationAndRepositoryTimeout()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);

        WorkerLeaseKey cancelledKey = NewKey("blocked-cancel");
        await using (NpgsqlConnection blocker = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction blockerTransaction = await blocker.BeginTransactionAsync())
        {
            await HoldLeaseAdvisoryLockAsync(blocker, blockerTransaction, cancelledKey);
            using var cancellation = new CancellationTokenSource();
            Task<LeaseAcquisitionResult> cancelled = leases.AcquireAsync(
                new AcquireWorkerLeaseRequest(
                    cancelledKey,
                    new WorkerExecutionId(Guid.NewGuid()),
                    LongLeaseDuration,
                    DefaultTimeout),
                cancellation.Token).AsTask();
            await WaitForBlockedLeaseStatementAsync(database.DataSource, "acquire_worker_lease");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            await blockerTransaction.RollbackAsync();
        }

        _ = await AcquireLeaseAsync(leases, cancelledKey, LongLeaseDuration);

        WorkerLeaseKey timedOutKey = NewKey("blocked-timeout");
        await using (NpgsqlConnection blocker = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction blockerTransaction = await blocker.BeginTransactionAsync())
        {
            await HoldLeaseAdvisoryLockAsync(blocker, blockerTransaction, timedOutKey);
            var oneSecondTimeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(1));
            var stopwatch = Stopwatch.StartNew();
            Task<LeaseAcquisitionResult> timedOut = leases.AcquireAsync(
                new AcquireWorkerLeaseRequest(
                    timedOutKey,
                    new WorkerExecutionId(Guid.NewGuid()),
                    LongLeaseDuration,
                    oneSecondTimeout),
                CancellationToken.None).AsTask();
            await WaitForBlockedLeaseStatementAsync(database.DataSource, "acquire_worker_lease");
            await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
            stopwatch.Stop();
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
            await blockerTransaction.RollbackAsync();
        }

        _ = await AcquireLeaseAsync(leases, timedOutKey, LongLeaseDuration);
    }

    [Fact]
    public async Task LeaseFenceAndMigrationHistoryPersistAcrossDatabaseRestart()
    {
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using PostgreSqlContainer container = new PostgreSqlBuilder(PostgreSql18Fixture.Image)
            .WithDatabase("sqlobserver_lease_restart")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();

        WorkerLeaseKey key = NewKey("restart");
        WorkerLease beforeRestart;
        await using (NpgsqlDataSource initialDataSource = NpgsqlDataSource.Create(container.GetConnectionString()))
        {
            var migrations = new PostgreSqlMigrationPort(initialDataSource);
            MigrationBatchResult result = await migrations.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
                CancellationToken.None);
            Assert.False(result.HasFailures);

            var leases = new PostgreSqlWorkerLeasePort(initialDataSource);
            beforeRestart = await AcquireLeaseAsync(leases, key, MinimumLeaseDuration);
        }

        await container.StopAsync();
        await container.StartAsync();

        await using NpgsqlDataSource restartedDataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await WaitForRepositoryExpiryAsync(restartedDataSource, key);

        await using (NpgsqlConnection connection = await restartedDataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "SELECT count(*) FROM system.schema_migration;",
            connection))
        {
            Assert.Equal(
                PostgreSqlMigrationCatalog.LoadEmbedded().Migrations.Count,
                Convert.ToInt32(
                    await command.ExecuteScalarAsync(),
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        var restartedLeases = new PostgreSqlWorkerLeasePort(restartedDataSource);
        WorkerLease afterRestart = await AcquireLeaseAsync(
            restartedLeases,
            key,
            MinimumLeaseDuration);
        Assert.Equal(
            checked(beforeRestart.Identity.FencingToken.Value + 1),
            afterRestart.Identity.FencingToken.Value);
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
                CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static WorkerLeaseKey NewKey(string purpose) =>
        new($"integration:resilience:{purpose}:{Guid.NewGuid():N}");

    private static async Task<WorkerLease> AcquireLeaseAsync(
        PostgreSqlWorkerLeasePort leases,
        WorkerLeaseKey key,
        WorkerLeaseDuration duration)
    {
        LeaseAcquisitionResult result = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                key,
                new WorkerExecutionId(Guid.NewGuid()),
                duration,
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, result.Status);
        return Assert.IsType<WorkerLease>(result.Lease);
    }

    private static async Task SetDatabaseTimeZoneAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"ALTER DATABASE \"{database.DatabaseName}\" SET timezone TO 'Pacific/Kiritimati';",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForRepositoryExpiryAsync(
        NpgsqlDataSource dataSource,
        WorkerLeaseKey key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using NpgsqlConnection connection = await dataSource
                .OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand(
                """
                SELECT expires_at <= clock_timestamp()
                FROM control.worker_lease
                WHERE work_key = @work_key;
                """,
                connection);
            command.Parameters.AddWithValue("work_key", key.Value);
            object? expired = await command.ExecuteScalarAsync(timeout.Token);
            if (expired is true)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task AssertLeaseInsideTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkerLeaseIdentity identity)
    {
        await using var command = new NpgsqlCommand(
            "SELECT control.assert_worker_lease(@work_key, @owner_execution_id, @fencing_token);",
            connection,
            transaction);
        command.Parameters.AddWithValue("work_key", identity.Key.Value);
        command.Parameters.AddWithValue("owner_execution_id", identity.Owner.Value);
        command.Parameters.AddWithValue("fencing_token", identity.FencingToken.Value);
        Assert.Equal(identity.FencingToken.Value, await command.ExecuteScalarAsync());
    }

    private static async Task HoldLeaseAdvisoryLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkerLeaseKey key)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_catalog.pg_advisory_xact_lock(
                pg_catalog.hashtextextended('sqlobserver:lease:' || @work_key, 0));
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("work_key", key.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForBlockedLeaseStatementAsync(
        NpgsqlDataSource dataSource,
        string statementFragment)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using NpgsqlConnection connection = await dataSource
                .OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS
                (
                    SELECT 1
                    FROM pg_catalog.pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND state = 'active'
                      AND wait_event_type = 'Lock'
                      AND query LIKE @query_pattern
                );
                """,
                connection);
            command.Parameters.AddWithValue("query_pattern", $"%{statementFragment}%");
            if (await command.ExecuteScalarAsync(timeout.Token) is true)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }
}
