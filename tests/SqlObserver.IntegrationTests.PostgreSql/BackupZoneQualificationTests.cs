using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class BackupZoneQualificationTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task ServerCommandsAreAuditedReplayableRevocableAndBoundToTheExactTargetRevision()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        MonitoredInstanceId target = await SeedAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var commands = new PostgreSqlBackupZoneQualificationPort(server);
        var provider = new PostgreSqlBackupZoneQualificationPort(collector);
        Assert.Null(await provider.GetAsync(target, new(1), CancellationToken.None));
        SetBackupZoneQualificationRequest first = Request(target, 0);
        Assert.Equal(new(BackupZoneQualificationMutationStatus.Applied, 1), await commands.SetAsync(first, CancellationToken.None));
        Assert.Equal(new(BackupZoneQualificationMutationStatus.Replayed, 1), await commands.SetAsync(first, CancellationToken.None));
        BackupHistoricalZoneQualification qualification = Assert.IsType<BackupHistoricalZoneQualification>(
            await provider.GetAsync(target, new(1), CancellationToken.None));
        Assert.Equal(first.Change.Qualification!.Zone.ToSerializedString(), qualification.Zone.ToSerializedString());
        BackupFinishTimeResolution resolved = BackupHistoricalTimeResolver.Resolve(target, new(1),
            new DateTime(2026, 3, 8, 1, 55, 0), new DateTime(2026, 3, 8, 3, 5, 0), -300,
            qualification.SourceZoneId, qualification);
        Assert.Equal(BackupFinishTimeState.Resolved, resolved.State);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 5, 0, TimeSpan.Zero), resolved.FinishUtc);
        Assert.Equal(BackupZoneQualificationMutationStatus.RevisionConflict,
            (await commands.SetAsync(Request(target, 0), CancellationToken.None)).Status);
        var changedReplay = Request(target, 0, first.Change.OperationId, evidence: new string('b', 64));
        Assert.Equal(BackupZoneQualificationMutationStatus.RevisionConflict,
            (await commands.SetAsync(changedReplay, CancellationToken.None)).Status);
        Assert.Null(await provider.GetAsync(target, new(2), CancellationToken.None));
        var revoke = new SetBackupZoneQualificationRequest(new(target, new(1), 1, Guid.NewGuid(), null, null), Envelope(target), Timeout);
        Assert.Equal(new(BackupZoneQualificationMutationStatus.Applied, 2), await commands.SetAsync(revoke, CancellationToken.None));
        Assert.Null(await provider.GetAsync(target, new(1), CancellationToken.None));
        Assert.Equal(2, (await commands.ReadAsync(target, new(1), CancellationToken.None))!.Revision);
        Assert.Equal(BackupZoneQualificationMutationStatus.RevisionConflict,
            (await commands.SetAsync(first, CancellationToken.None)).Status);
        Assert.Equal(new(BackupZoneQualificationMutationStatus.Applied, 3), await commands.SetAsync(Request(target, 2), CancellationToken.None));
        await using (var audit = database.DataSource.CreateCommand("""
            SELECT count(*) FROM control.backup_zone_qualification_command command
            JOIN audit.activity activity ON activity.activity_id=command.audit_activity_id
              AND activity.occurred_at=command.audit_occurred_at
            WHERE command.instance_id=@target AND activity.subject_identifier=@target::text
              AND activity.authorization_result='allowed' AND activity.outcome='succeeded';
            """))
        {
            audit.Parameters.AddWithValue("target", target.Value); Assert.Equal(3L, await audit.ExecuteScalarAsync());
        }
        await using (var history = database.DataSource.CreateCommand("SELECT count(*) FROM control.backup_historical_zone_qualification WHERE instance_id=@target;"))
        {
            history.Parameters.AddWithValue("target", target.Value); Assert.Equal(2L, await history.ExecuteScalarAsync());
        }
        await using (var attempts = database.DataSource.CreateCommand("SELECT count(*) FROM audit.activity WHERE subject_identifier=@target::text AND action_name='observation_target.update';"))
        {
            attempts.Parameters.AddWithValue("target", target.Value); Assert.Equal(6L, await attempts.ExecuteScalarAsync());
        }
        await using (var revise = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=2 WHERE instance_id=@target;"))
        { revise.Parameters.AddWithValue("target", target.Value); await revise.ExecuteNonQueryAsync(); }
        Assert.Null(await provider.GetAsync(target, new(1), CancellationToken.None));
        Assert.Null(await provider.GetAsync(target, new(2), CancellationToken.None));
        Assert.Equal(BackupZoneQualificationMutationStatus.RevisionConflict,
            (await commands.SetAsync(Request(target, 3), CancellationToken.None)).Status);
        Assert.Equal(BackupZoneQualificationMutationStatus.NotFound,
            (await commands.SetAsync(Request(new(Guid.NewGuid()), 0), CancellationToken.None)).Status);
        await using (var retire = database.DataSource.CreateCommand("UPDATE control.observation_target SET lifecycle_state='retired',retired_at=clock_timestamp(),updated_at=clock_timestamp() WHERE instance_id=@target;"))
        { retire.Parameters.AddWithValue("target", target.Value); await retire.ExecuteNonQueryAsync(); }
        Assert.Equal(BackupZoneQualificationMutationStatus.Inactive,
            (await commands.SetAsync(Request(target, 0, targetRevision: 2), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ConcurrentCompareAndSwapHasOneWinnerAndPrivateHistoryCannotBeMutated()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        MonitoredInstanceId target = await SeedAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var port = new PostgreSqlBackupZoneQualificationPort(server);
        var results = await Task.WhenAll(port.SetAsync(Request(target, 0), CancellationToken.None).AsTask(),
            port.SetAsync(Request(target, 0), CancellationToken.None).AsTask());
        Assert.Single(results, x => x.Status == BackupZoneQualificationMutationStatus.Applied);
        Assert.Single(results, x => x.Status == BackupZoneQualificationMutationStatus.RevisionConflict);
        foreach (string table in new[] { "backup_zone_qualification_command", "backup_historical_zone_qualification" })
        {
            await using var mutate = database.DataSource.CreateCommand($"UPDATE control.{table} SET target_revision=target_revision;");
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await mutate.ExecuteNonQueryAsync())).SqlState);
            await using var direct = server.CreateCommand($"SELECT * FROM control.{table};");
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await direct.ExecuteNonQueryAsync())).SqlState);
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await new PostgreSqlBackupZoneQualificationPort(collector).SetAsync(Request(target, 1), CancellationToken.None))).SqlState);
        await using var security = database.DataSource.CreateCommand("""
            SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='control.backup_zone_qualification_command'::regclass;
            """);
        Assert.Equal(true, await security.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FailedCommandRollsBackCertificateAndAuditAndInvalidRulesCannotReachTheResolver()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        MonitoredInstanceId target = await SeedAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var port = new PostgreSqlBackupZoneQualificationPort(server);
        await using (var failure = database.DataSource.CreateCommand("""
            CREATE FUNCTION control.fixture_reject_zone_command() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'fixture final write failed' USING ERRCODE='23514'; END $$;
            CREATE TRIGGER fixture_final_zone_write BEFORE INSERT ON control.backup_zone_qualification_command
              FOR EACH ROW EXECUTE FUNCTION control.fixture_reject_zone_command();
            """)) await failure.ExecuteNonQueryAsync();
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await port.SetAsync(Request(target, 0), CancellationToken.None))).SqlState);
        await using (var rollback = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM control.backup_historical_zone_qualification),
              (SELECT count(*) FROM control.backup_zone_qualification_command),
              (SELECT count(*) FROM audit.activity WHERE subject_identifier=@target::text AND action_name='observation_target.update');
            """))
        {
            rollback.Parameters.AddWithValue("target", target.Value);
            await using NpgsqlDataReader row = await rollback.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(0L, row.GetInt64(0)); Assert.Equal(0L, row.GetInt64(1)); Assert.Equal(0L, row.GetInt64(2));
        }
        await using (var repair = database.DataSource.CreateCommand("DROP TRIGGER fixture_final_zone_write ON control.backup_zone_qualification_command;"))
            await repair.ExecuteNonQueryAsync();
        // Direct SQL bypasses the .NET command validation; the provider still refuses malformed serialization.
        await using (var invalid = server.CreateCommand("""
            SELECT * FROM control.set_backup_zone_qualification(@target,1,0,@op,'UTC','invalid serialized rules',
              timestamptz '2026-01-01Z',timestamptz '2027-01-01Z',decode(repeat('aa',32),'hex'),@actor,@correlation);
            """))
        {
            invalid.Parameters.AddWithValue("target", target.Value); invalid.Parameters.AddWithValue("op", Guid.NewGuid());
            invalid.Parameters.AddWithValue("actor", "S-1-5-21-1-2-3-1001"); invalid.Parameters.AddWithValue("correlation", Guid.NewGuid());
            await invalid.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new PostgreSqlBackupZoneQualificationPort(collector).GetAsync(target, new(1), CancellationToken.None));
    }

    [Fact]
    public async Task AnOwnerSeededCertificateDoesNotBecomeAnImplicitConfigurationAttestation()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        MonitoredInstanceId target = await SeedAsync(database);
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.backup_historical_zone_qualification(instance_id,target_revision,qualification_revision,
              source_zone_id,serialized_zone_rules,rules_sha256,qualified_from,qualified_to)
            VALUES(@target,1,50,'UTC',@rules,sha256(convert_to(@rules,'UTF8')),timestamptz '2026-01-01Z',timestamptz '2027-01-01Z');
            """))
        {
            seed.Parameters.AddWithValue("target", target.Value); seed.Parameters.AddWithValue("rules", TimeZoneInfo.Utc.ToSerializedString());
            await seed.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var provider = new PostgreSqlBackupZoneQualificationPort(collector);
        Assert.Null(await provider.GetAsync(target, new(1), CancellationToken.None));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var commands = new PostgreSqlBackupZoneQualificationPort(server);
        Assert.Equal(new(BackupZoneQualificationMutationStatus.Applied, 51), await commands.SetAsync(Request(target, 0), CancellationToken.None));
        Assert.Equal(51, (await provider.GetAsync(target, new(1), CancellationToken.None))!.Revision);
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(
            MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(result.HasFailures, string.Join(",", result.Results.Where(x => x.Outcome == MigrationOutcome.Failed)
            .Select(x => $"{x.Migration.Number.Value}:{x.FailureCode}")));
        return database;
    }

    private static async Task<MonitoredInstanceId> SeedAsync(RepositoryTestDatabase database)
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Historical zone fixture','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            """);
        command.Parameters.AddWithValue("target", target.Value); command.Parameters.AddWithValue("key", $"zone.{target.Value:N}");
        await command.ExecuteNonQueryAsync(); return target;
    }

    private static AdministrativeAuditEnvelope Envelope(MonitoredInstanceId target) => new(
        new("S-1-5-21-1-2-3-1001"), new(Guid.NewGuid()), AdministrativeAuditAction.UpdateObservationTarget, target);

    private static SetBackupZoneQualificationRequest Request(MonitoredInstanceId target, long expected,
        Guid? operation = null, string? evidence = null, long targetRevision = 1) => new(new(target, new(targetRevision), expected,
        operation ?? Guid.NewGuid(), new(target, new(targetRevision), expected + 1,
            FixtureEasternZone(), "Eastern Standard Time",
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        evidence ?? new string('a', 64)), Envelope(target), Timeout);

    // Use the qualification window's deterministic US rules. Linux tzdata's
    // historical rule expansion need not serialize like Windows registry rules.
    private static TimeZoneInfo FixtureEasternZone() => TimeZoneInfo.CreateCustomTimeZone(
        "Eastern Standard Time", TimeSpan.FromHours(-5), "Eastern", "Eastern Standard", "Eastern Daylight",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))]);
}
