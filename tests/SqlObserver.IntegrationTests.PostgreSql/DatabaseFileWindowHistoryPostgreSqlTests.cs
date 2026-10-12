using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class DatabaseFileWindowHistoryPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task CanonicalPostSealCommitsPageExactFactsWithoutOriginalRunOrFileMirrors()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture, seal: true);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database);
        var window = await DatabaseFileWindowFixture.WindowAsync(database);
        var first = await DatabaseFileWindowFixture.CommitAsync(database, target, 105);
        var second = await DatabaseFileWindowFixture.CommitAsync(database, target, 1);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window, 1), CancellationToken.None));
        Assert.Equal(second.Id.Value, Assert.Single(page.Items).RunId); Assert.Equal(2, page.Coverage.RunCount);
        Assert.Equal(106, page.Coverage.RetainedFileCount); Assert.Null(page.Coverage.RetentionDays);
        var tail = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window, 1, page.NextAnchor), CancellationToken.None));
        Assert.Equal(first.Id.Value, Assert.Single(tail.Items).RunId); Assert.Null(tail.NextAnchor);
        Assert.Equal(page.CutoffUtc, tail.CutoffUtc); Assert.Equal(page.ReadEpoch, tail.ReadEpoch);
        var exact = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, first.Id.Value, 100), CancellationToken.None));
        Assert.Equal(100, exact.Items.Count); Assert.Equal(105, exact.Receipt!.StoredFileCount);
        var rest = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, first.Id.Value, 100, exact.NextAnchor), CancellationToken.None));
        Assert.Equal(5, rest.Items.Count); Assert.Null(rest.NextAnchor);
        Assert.Equal(Enumerable.Range(1, 105).OrderBy(static index => index % 2 == 0 ? 6 : 5).ThenBy(static index => index),
            exact.Items.Concat(rest.Items).Select(static file => file.FileId));
        Assert.Equal(105, exact.Items.Concat(rest.Items).Select(static file => (file.DatabaseId, file.FileId)).Distinct().Count());
        Assert.Equal(exact.ReadEpoch, rest.ReadEpoch); Assert.Equal(exact.Receipt, rest.Receipt);
        Assert.Equal(0L, await DatabaseFileWindowFixture.CountAsync(database, "SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target;", target));
        Assert.Equal(0L, await DatabaseFileWindowFixture.CountAsync(database, "SELECT count(*) FROM telemetry.database_file_snapshot_v2 WHERE instance_id=@target;", target));
        Assert.Equal(106L, await DatabaseFileWindowFixture.CountAsync(database, "SELECT count(*) FROM telemetry.database_file_snapshot WHERE instance_id=@target;", target));
    }

    [Fact]
    public async Task LatestCompletedFailureIsNotSkippedAndSuccessfulEmptyIsAnExplicitReceipt()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        await DatabaseFileWindowFixture.CommitAsync(database, target, 1);
        var empty = await DatabaseFileWindowFixture.CommitAsync(database, target, 0);
        var failed = await DatabaseFileWindowFixture.CommitAsync(database, target, 0, CollectorRunOutcome.PermissionDenied);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window), CancellationToken.None));
        Assert.Equal(failed.Id.Value, page.Items[0].RunId); Assert.Equal("collection_failed", page.Items[0].Reason);
        Assert.Equal("unavailable", page.Items[0].State); Assert.Equal("permission_denied", page.Items[0].Outcome);
        Assert.Equal("partial", page.State); Assert.Equal(1, page.Coverage.FailedRunCount); Assert.Equal(1, page.Coverage.GapRunCount);
        Assert.Equal(1, page.Coverage.EmptyRunCount); Assert.Equal(2, page.Coverage.CompleteRunCount);
        var exact = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, empty.Id.Value), CancellationToken.None));
        Assert.Equal("available", exact.State); Assert.Equal("observed_empty", exact.Reason); Assert.Empty(exact.Items);
        Assert.Equal(0, exact.Receipt!.ExpectedFileCount); Assert.Null(exact.Receipt.FirstObservedAtUtc);
        var refused = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, failed.Id.Value), CancellationToken.None));
        Assert.Equal("collection_failed", refused.Reason); Assert.Empty(refused.Items); Assert.NotNull(refused.Receipt);
    }

    [Fact]
    public async Task PartialLossPreservesReceivedFilesAndDoesNotCallThemComplete()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        var source = await DatabaseFileWindowFixture.CommitAsync(database, target, 2, CollectorRunOutcome.Partial);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window), CancellationToken.None));
        Assert.Equal(1, page.Coverage.LostRunCount); Assert.Equal(0, page.Coverage.CompleteRunCount); Assert.Equal(2, page.Coverage.RetainedFileCount);
        var exact = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, source.Id.Value), CancellationToken.None));
        Assert.Equal("partial", exact.State); Assert.Equal("collection_loss", exact.Reason); Assert.Equal(2, exact.Items.Count);
        Assert.Equal("source_row_limit", exact.Receipt!.LossKind); Assert.Equal(1, exact.Receipt.MinimumLostItems);
    }

    [Theory]
    [InlineData(CollectorRunOutcome.TimedOut, "timed_out")]
    [InlineData(CollectorRunOutcome.TransientFailure, "transient_failure")]
    [InlineData(CollectorRunOutcome.PermanentFailure, "permanent_failure")]
    [InlineData(CollectorRunOutcome.Unsupported, "unsupported")]
    [InlineData(CollectorRunOutcome.OutputInvalid, "output_invalid")]
    public async Task FailedNativeAttemptsRemainReceiptEvidenceWithoutInventedFileRows(CollectorRunOutcome outcome, string wire)
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        var failed = await DatabaseFileWindowFixture.CommitAsync(database, target, 0, outcome);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window), CancellationToken.None));
        Assert.Equal(wire, Assert.Single(page.Items).Outcome); Assert.Equal(1, page.Coverage.FailedRunCount);
        Assert.Equal(0, page.Coverage.RetainedFileCount); Assert.Equal(0, page.Coverage.CompleteRunCount);
        var exact = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, failed.Id.Value), CancellationToken.None));
        Assert.NotNull(exact.Receipt); Assert.Equal("collection_failed", exact.Reason); Assert.Equal("unavailable", exact.State); Assert.Empty(exact.Items);
    }

    [Fact]
    public async Task NativeAbandonmentRemainsInTheCompletedWindowWithoutInventedFilePayload()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        // The due-work inventory deliberately hides a currently active run.
        // Exercise the supported native begin/reclaim boundary directly, as
        // the bounded canonical-overflow producer does, without forging rows.
        await DatabaseFileWindowFixture.AbandonManyAsync(database, target, 1);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window), CancellationToken.None));
        var abandoned = Assert.Single(page.Items);
        Assert.Equal("lease_lost", abandoned.Outcome); Assert.Equal("collection_failed", abandoned.Reason); Assert.Equal(0, abandoned.StoredFileCount);
        Assert.Equal(1, page.Coverage.RunCount); Assert.Equal(1, page.Coverage.FailedRunCount);
    }

    [Theory]
    [InlineData(0L, 0, "disabled")]
    [InlineData(512L, 0, "fixed_bytes")]
    [InlineData(0L, 10, "percent")]
    [InlineData(512L, 10, "ambiguous")]
    public async Task GrowthModesAndInt64FactsRemainExactWithoutInferringGrowthEvents(long growth, int percent, string mode)
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        var source = await DatabaseFileWindowFixture.CommitAsync(database, target, 1, growth: growth, percent: percent);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, source.Id.Value), CancellationToken.None));
        var file = Assert.Single(page.Items); Assert.Equal(mode, file.GrowthMode);
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), file.SizeBytes); Assert.Null(file.MaximumSizeBytes);
        Assert.Equal((long.MaxValue - 1).ToString(CultureInfo.InvariantCulture), file.ReadCount);
        Assert.Equal("15", file.IoStallMilliseconds); Assert.Equal("10", file.ReadStallMilliseconds); Assert.Equal("5", file.WriteStallMilliseconds);
        Assert.Equal(growth.ToString(CultureInfo.InvariantCulture), file.GrowthBytes); Assert.Equal(percent, file.GrowthPercent);
    }

    [Fact]
    public async Task SourceObservationClockSkewRemainsVisibleSeparatelyFromCompletionAndIngestion()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        var source = await DatabaseFileWindowFixture.CommitAsync(database, target, 1, observedOffset: TimeSpan.FromMinutes(1));
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, source.Id.Value), CancellationToken.None));
        Assert.Equal("source_clock_skew", page.Reason); Assert.Equal("partial", page.State);
        var file = Assert.Single(page.Items); Assert.True(file.ObservedAtUtc > page.CutoffUtc); Assert.True(file.CollectedAtUtc <= page.CutoffUtc);
        Assert.Equal(file.ObservedAtUtc, page.Receipt!.FirstObservedAtUtc); Assert.True(page.Receipt.CompletedAtUtc < file.ObservedAtUtc);
    }

    [Fact]
    public async Task OriginalCutoffIgnoresLaterCommitsAndSnapshotEpochDoesNotDependOnOtherRuns()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        var one = await DatabaseFileWindowFixture.CommitAsync(database, target, 2); await DatabaseFileWindowFixture.CommitAsync(database, target, 1);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var first = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window, 1), CancellationToken.None));
        var selected = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, one.Id.Value, 1), CancellationToken.None));
        await DatabaseFileWindowFixture.CommitAsync(database, target, 1);
        var oldTail = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window, 1, first.NextAnchor), CancellationToken.None));
        Assert.Equal(first.ReadEpoch, oldTail.ReadEpoch); Assert.Equal(2, oldTail.Coverage.RunCount); Assert.Null(oldTail.NextAnchor);
        var now = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window), CancellationToken.None));
        Assert.Equal(3, now.Coverage.RunCount); Assert.NotEqual(first.ReadEpoch, now.ReadEpoch);
        var same = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, one.Id.Value, 1), CancellationToken.None));
        Assert.Equal(selected.ReadEpoch, same.ReadEpoch); Assert.Equal(selected.Receipt, same.Receipt);
        var oldFileTail = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, one.Id.Value, 1, selected.NextAnchor), CancellationToken.None));
        Assert.Equal(selected.ReadEpoch, oldFileTail.ReadEpoch); Assert.Single(oldFileTail.Items);
    }

    [Fact]
    public async Task CadenceChangesFenceAnUnreadManifestAndUnrelatedPoliciesDoNotAdvertiseRetention()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        await DatabaseFileWindowFixture.CommitAsync(database, target, 1); await DatabaseFileWindowFixture.CommitAsync(database, target, 1);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window, 1), CancellationToken.None));
        await using (var change = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET schedule_revision=schedule_revision+1,collection_interval=interval '2 minutes',updated_at=statement_timestamp() WHERE instance_id=@target AND collector_id='database.files';"))
        { change.Parameters.AddWithValue("target", target); await change.ExecuteNonQueryAsync(); }
        var error = await Assert.ThrowsAsync<DatabaseFileWindowHistoryReadException>(() => reader.ListAsync(DatabaseFileWindowFixture.List(target, window, 1, page.NextAnchor), CancellationToken.None).AsTask());
        Assert.Equal(409, error.StatusCode);
        var fresh = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window), CancellationToken.None));
        Assert.Equal("partial", fresh.State); Assert.All(fresh.Items, item => Assert.Equal("collection_gap", item.Reason));
        Assert.Equal(0L, await DatabaseFileWindowFixture.CountAsync(database,
            "SELECT count(*) FROM system.retention_policy WHERE parent_table IN('database_file_snapshot','database_file_snapshot_v2','collection_run_v2') AND @target IS NOT NULL;", target));
        await using (var change = database.DataSource.CreateCommand("SET ROLE sqlobserver_migrator; UPDATE system.retention_policy SET policy_revision=policy_revision+1 WHERE data_class='raw_metric_samples';"))
        { Assert.Equal(1, await change.ExecuteNonQueryAsync()); }
        var afterPolicy = Assert.IsType<DatabaseFileWindowHistoryPage>(await reader.ListAsync(DatabaseFileWindowFixture.List(target, window), CancellationToken.None));
        Assert.Equal(fresh.ReadEpoch, afterPolicy.ReadEpoch); Assert.Null(afterPolicy.Coverage.RetentionDays);
    }

    [Fact]
    public async Task ExactOutsideWindowForeignRunWrongRevisionAndExpiredContextNeverSubstituteLatest()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database), other = await DatabaseFileWindowFixture.TargetAsync(database);
        var window = await DatabaseFileWindowFixture.WindowAsync(database); var one = await DatabaseFileWindowFixture.CommitAsync(database, target, 2);
        var foreign = await DatabaseFileWindowFixture.CommitAsync(database, other, 1);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        var exact = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, one.Id.Value, 1), CancellationToken.None));
        var outside = window with { FromUtc = exact.Receipt!.CompletedAtUtc.AddTicks(10) };
        var absent = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, outside, one.Id.Value), CancellationToken.None));
        Assert.Equal("snapshot_outside_window", absent.Reason); Assert.Null(absent.Receipt); Assert.Empty(absent.Items);
        var wrong = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, foreign.Id.Value), CancellationToken.None));
        Assert.Equal("snapshot_not_retained", wrong.Reason); Assert.Null(wrong.Receipt);
        var anchor = Assert.IsType<DatabaseFileWindowHistoryCursor>(exact.NextAnchor);
        Assert.Equal(400, (await Assert.ThrowsAsync<DatabaseFileWindowHistoryReadException>(() => DatabaseFileWindowFixture.RawAsync(database, target, window, 1, one.Id.Value,
            anchor with { AfterDatabaseId = 32767, AfterFileId = 65535 }).AsTask())).StatusCode);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => DatabaseFileWindowFixture.NativeAsync(database, target, window, 1, one.Id.Value,
            anchor with { IssuedAtUtc = anchor.IssuedAtUtc.AddMinutes(-16), CutoffUtc = anchor.CutoffUtc.AddMinutes(-16) }))).SqlState);
        await using (var revision = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=revision+1,updated_at=statement_timestamp() WHERE instance_id=@target;"))
        { revision.Parameters.AddWithValue("target", target); await revision.ExecuteNonQueryAsync(); }
        Assert.Equal(409, (await Assert.ThrowsAsync<DatabaseFileWindowHistoryReadException>(() => reader.ListAsync(DatabaseFileWindowFixture.List(target, window), CancellationToken.None).AsTask())).StatusCode);
        var old = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, one.Id.Value) with { TargetRevision = 2 }, CancellationToken.None));
        Assert.Equal(1, old.SampleTargetRevision); Assert.Equal(2, old.TargetRevision); Assert.NotNull(old.Receipt);
    }

    [Fact]
    public async Task NativePrivilegesRequireScopeAndDoNotExposeProtectedTablesOrManifest()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        await DatabaseFileWindowFixture.CommitAsync(database, target, 1);
        await using var server = database.CreateServerDataSource(); await using var connection = await server.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var denied = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@other,true); SELECT reporting.read_database_file_window_history(@target,1,@from,@to,100);", connection, tx);
        denied.Parameters.AddWithValue("other", Guid.NewGuid().ToString("D")); denied.Parameters.AddWithValue("target", target);
        denied.Parameters.AddWithValue("from", window.FromUtc); denied.Parameters.AddWithValue("to", window.ToUtc);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync())).SqlState);
        await using var acl = database.DataSource.CreateCommand("""
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='reporting'
              AND p.proname='read_database_file_window_history' AND p.prosecdef AND p.proowner='sqlobserver_migrator'::regrole
              AND has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
              AND NOT has_table_privilege('sqlobserver_server','telemetry.database_file_snapshot','SELECT')
              AND (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='telemetry.database_file_snapshot'::regclass)
              AND NOT EXISTS(SELECT 1 FROM pg_proc m JOIN pg_namespace s ON s.oid=m.pronamespace,
                LATERAL aclexplode(coalesce(m.proacl,acldefault('f',m.proowner))) a
                WHERE s.nspname='reporting' AND m.proname IN('database_file_window_manifest','read_database_file_window_history') AND a.grantee=0);
            """); Assert.Equal(1L, await acl.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CancellationPropagatesAndNativeWholeWindowRunOverflowCannotHideExactSelectedProof()
    {
        await using var database = await DatabaseFileWindowFixture.CreateAsync(fixture);
        Guid target = await DatabaseFileWindowFixture.TargetAsync(database); var window = await DatabaseFileWindowFixture.WindowAsync(database);
        var retained = await DatabaseFileWindowFixture.CommitAsync(database, target, 1);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server);
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ListAsync(DatabaseFileWindowFixture.List(target, window), cancelled.Token).AsTask());
        var before = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, retained.Id.Value), CancellationToken.None));
        await DatabaseFileWindowFixture.AbandonManyAsync(database, target, DatabaseFileWindowHistoryValidation.MaximumRuns + 1);
        var overflow = await Assert.ThrowsAsync<DatabaseFileWindowHistoryReadException>(() => reader.ListAsync(DatabaseFileWindowFixture.List(target, window), CancellationToken.None).AsTask());
        Assert.Equal(422, overflow.StatusCode); Assert.Equal("history_window_overflow", overflow.Code);
        var after = Assert.IsType<DatabaseFileWindowHistorySnapshotPage>(await reader.GetAsync(DatabaseFileWindowFixture.Exact(target, window, retained.Id.Value), CancellationToken.None));
        Assert.Equal(before.ReadEpoch, after.ReadEpoch); Assert.Equal(before.Receipt, after.Receipt); Assert.Equal(before.Items, after.Items);
    }
}

internal static class DatabaseFileWindowFixture
{
    internal static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Window(DateTimeOffset FromUtc, DateTimeOffset ToUtc);
    internal sealed record Run(CollectorDueWorkItem Work, CollectorRunId Id, WorkerLeaseIdentity Lease);
    internal static async Task<RepositoryTestDatabase> CreateAsync(PostgreSql18Fixture fixture, bool seal = false)
    {
        var database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        if (seal) await SqlVolumeWindowHistoryFixture.SealAsync(database);
        return database;
    }
    internal static async Task<Guid> TargetAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid(); await using var command = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Native file window','source.invalid',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
            INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,
              enabled,collection_interval,next_due_at,created_at,updated_at)
            SELECT @target,c.collector_id,c.collector_version,1,1,true,c.default_interval,statement_timestamp(),statement_timestamp(),statement_timestamp()
            FROM control.collector_contract c WHERE c.collector_id IN('database.inventory','database.files') AND c.collector_version=1
            ON CONFLICT(instance_id,collector_id) DO NOTHING;
            UPDATE control.collector_schedule SET next_due_at=statement_timestamp()+interval '1 day',
              last_outcome=CASE WHEN collector_id IN('capability.connection','engine.core','database.inventory') THEN 'succeeded' ELSE last_outcome END,
              last_succeeded_at=CASE WHEN collector_id IN('capability.connection','engine.core','database.inventory') THEN statement_timestamp() ELSE last_succeeded_at END
            WHERE instance_id=@target;
            """); command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("key", "file-window-" + target.ToString("N"));
        await command.ExecuteNonQueryAsync(); return target;
    }
    internal static async Task<Window> WindowAsync(RepositoryTestDatabase database)
    {
        await using var query = database.DataSource.CreateCommand("SELECT clock_timestamp();");
        var now = Assert.IsType<DateTime>(await query.ExecuteScalarAsync());
        return new(new DateTimeOffset(now, TimeSpan.Zero).AddSeconds(-1), new DateTimeOffset(now, TimeSpan.Zero).AddHours(1));
    }
    internal static DatabaseFileWindowHistoryListRequest List(Guid target, Window window, int limit = 100, DatabaseFileWindowHistoryCursor? cursor = null) =>
        new(target, 1, window.FromUtc, window.ToUtc, limit, cursor, Timeout);
    internal static DatabaseFileWindowHistorySnapshotRequest Exact(Guid target, Window window, Guid run, int limit = 100, DatabaseFileWindowHistoryCursor? cursor = null) =>
        new(target, 1, window.FromUtc, window.ToUtc, run, 1, limit, cursor, Timeout);
    internal static async Task<Run> BeginAsync(RepositoryTestDatabase database, NpgsqlDataSource collector,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target, WorkerLeaseIdentity? lease = null)
    {
        await using (var due = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='database.files';"))
        { due.Parameters.AddWithValue("target", target); await due.ExecuteNonQueryAsync(); }
        var work = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target && item.CollectorId.Value == "database.files");
        if (lease is null)
        {
            var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new WorkerLeaseKey($"collector/run/database.files/{target:N}"),
                new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(WorkerLeaseDuration.Maximum), Timeout), CancellationToken.None);
            Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status); lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        }
        var id = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, id, lease, Timeout), CancellationToken.None)).Status);
        return new(work, id, lease);
    }
    internal static async Task<Run> CommitAsync(RepositoryTestDatabase database, Guid target, int files,
        CollectorRunOutcome outcome = CollectorRunOutcome.Succeeded, long growth = 512, int percent = 0, TimeSpan? observedOffset = null)
    {
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var items = Enumerable.Range(1, files).Select(index => new DatabaseFileObservation(run.Work.TargetId, run.Work.TargetRevision,
            index % 2 == 0 ? 6 : 5, index, new SqlServerObjectName("native_file_" + index.ToString(CultureInfo.InvariantCulture)),
            DatabaseFileType.Rows, DatabaseFileState.Online, long.MaxValue, null, growth, percent, long.MaxValue - 1, 1, long.MaxValue - 2, 3, 15,
            run.Work.RepositoryTimeUtc + (observedOffset ?? TimeSpan.Zero), 10, 5)).ToArray();
        await CommitAsync(runtime, run, items, outcome);
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(run.Lease, Timeout), CancellationToken.None);
        return run;
    }
    internal static async Task CommitAsync(PostgreSqlCollectorRuntimeRepositoryPort runtime, Run run, DatabaseFileObservation[] files,
        CollectorRunOutcome outcome = CollectorRunOutcome.Succeeded)
    {
        var payload = new CollectorPayload(databaseFiles: new(files));
        CollectorLossEvidence loss = outcome switch { CollectorRunOutcome.Partial => new(CollectorLossKind.SourceRowLimit, 1, false),
            CollectorRunOutcome.OutputInvalid => new(CollectorLossKind.OutputValidationFailure, 1, false), _ => CollectorLossEvidence.None };
        CollectorRunReason reason = outcome switch { CollectorRunOutcome.Partial => CollectorRunReason.SourceRowLimit,
            CollectorRunOutcome.PermissionDenied => CollectorRunReason.RequiredPermissionMissing,
            CollectorRunOutcome.TimedOut => CollectorRunReason.DeadlineExceeded, CollectorRunOutcome.TransientFailure => CollectorRunReason.TransientTargetFailure,
            CollectorRunOutcome.PermanentFailure => CollectorRunReason.PermanentTargetFailure, CollectorRunOutcome.Unsupported => CollectorRunReason.TargetUnsupported,
            CollectorRunOutcome.OutputInvalid => CollectorRunReason.OutputValidationFailed, _ => CollectorRunReason.Completed };
        var summary = new CollectorRunSummary(run.Id, run.Work.TargetId, run.Work.TargetRevision, run.Work.CollectorId,
            run.Work.CollectorManifestVersion, run.Work.OutputSchemaVersion, outcome, reason, TimeSpan.FromMilliseconds(5), 1,
            new(files.Length + (loss.HasLoss ? 1 : 0), payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), loss);
        var circuit = outcome is CollectorRunOutcome.TimedOut or CollectorRunOutcome.TransientFailure
            ? new CollectorCircuitSnapshot(CollectorCircuitState.Closed, 1, run.Work.RepositoryTimeUtc)
            : CollectorCircuitSnapshot.Closed(run.Work.RepositoryTimeUtc);
        var commit = new CommitCollectorRunRequest(run.Work, summary, payload, circuit, run.Lease, Timeout);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
    }
    internal static async Task<long> CountAsync(RepositoryTestDatabase database, string sql, Guid target)
    { await using var query = database.DataSource.CreateCommand(sql); query.Parameters.AddWithValue("target", target); return Assert.IsType<long>(await query.ExecuteScalarAsync()); }
    internal static async ValueTask<DatabaseFileWindowHistorySnapshotPage?> RawAsync(RepositoryTestDatabase database, Guid target, Window window,
        int limit, Guid run, DatabaseFileWindowHistoryCursor cursor)
    {
        await using var server = database.CreateServerDataSource();
        return await new PostgreSqlDatabaseFileWindowHistoryRepositoryPort(server).GetAsync(Exact(target, window, run, limit, cursor), CancellationToken.None);
    }
    internal static async Task<JsonDocument> NativeAsync(RepositoryTestDatabase database, Guid target, Window window,
        int limit, Guid run, DatabaseFileWindowHistoryCursor cursor)
    {
        await using var server = database.CreateServerDataSource(); await using var connection = await server.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
        { scope.Parameters.AddWithValue("scope", target.ToString("D")); await scope.ExecuteNonQueryAsync(); }
        await using var query = new NpgsqlCommand("SELECT reporting.read_database_file_window_history(@target,1,@from,@to,@limit,@run,1,@cursor);", connection, transaction);
        query.Parameters.AddWithValue("target", target); query.Parameters.AddWithValue("from", window.FromUtc); query.Parameters.AddWithValue("to", window.ToUtc);
        query.Parameters.AddWithValue("limit", limit); query.Parameters.AddWithValue("run", run);
        query.Parameters.AddWithValue("cursor", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(cursor, Json));
        string payload = Assert.IsType<string>(await query.ExecuteScalarAsync()); await transaction.CommitAsync(); return JsonDocument.Parse(payload);
    }
    internal static async Task AbandonManyAsync(RepositoryTestDatabase database, Guid target, int count)
    {
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var pending = await BeginAsync(database, collector, runtime, target);
        var ids = new Guid[count]; var digests = new byte[count][];
        var digest = typeof(PostgreSqlCollectorRuntimeRepositoryPort).GetMethod("CreateRequestDigest", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (int index = 0; index < count; index++)
        { ids[index] = Guid.NewGuid(); digests[index] = Assert.IsType<byte[]>(digest.Invoke(null, [pending.Work, new CollectorRunId(ids[index]), pending.Lease])); }
        await using var batch = collector.CreateCommand("""
            SELECT begun.result_status,count(*) FROM unnest(@runs,@digests) input(run_id,request_digest)
            CROSS JOIN LATERAL control.begin_collection_run(input.run_id,@target,1,'database.files',1,1,@schedule,@scheduled,@work,@owner,@fence,input.request_digest) begun
            GROUP BY begun.result_status;
            """); batch.CommandTimeout = 180;
        batch.Parameters.AddWithValue("runs", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids); batch.Parameters.AddWithValue("digests", NpgsqlDbType.Array | NpgsqlDbType.Bytea, digests);
        batch.Parameters.AddWithValue("target", target); batch.Parameters.AddWithValue("schedule", pending.Work.ScheduleRevision.Value);
        batch.Parameters.AddWithValue("scheduled", pending.Work.ScheduledAtUtc); batch.Parameters.AddWithValue("work", pending.Lease.Key.Value);
        batch.Parameters.AddWithValue("owner", pending.Lease.Owner.Value); batch.Parameters.AddWithValue("fence", pending.Lease.FencingToken.Value);
        await using var rows = await batch.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync()); Assert.Equal("started", rows.GetString(0));
        Assert.Equal((long)count, rows.GetInt64(1)); Assert.False(await rows.ReadAsync());
    }
}
