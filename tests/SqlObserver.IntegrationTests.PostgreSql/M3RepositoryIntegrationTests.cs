using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M3RepositoryIntegrationTests
{
    private static readonly RepositoryCallTimeout DefaultTimeout = new(TimeSpan.FromSeconds(30));
    private static readonly WorkerLeaseDuration LeaseDuration = new(TimeSpan.FromSeconds(30));
    private readonly PostgreSql18Fixture _fixture;

    public M3RepositoryIntegrationTests(PostgreSql18Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TargetMutationsAreRevisionFencedScopedAndAtomicWithAudit()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource serverDataSource = database.CreateServerDataSource();
        var targets = new PostgreSqlObservationTargetPort(serverDataSource);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        ObservationTargetRegistration registration = CreateRegistration(targetId, "m3.atomic.target", "Atomic target");

        ObservationTargetRegistrationResult registered = await targets.RegisterAsync(
            new RegisterObservationTargetRequest(
                registration,
                CreateAudit(targetId, AdministrativeAuditAction.RegisterObservationTarget),
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(ObservationTargetRegistrationStatus.Registered, registered.Status);
        ObservationTarget initial = Assert.IsType<ObservationTarget>(registered.Target);
        Assert.Equal(1, initial.Revision.Value);
        Assert.Equal(ObservationTargetLifecycle.PendingDiscovery, initial.Lifecycle);
        Assert.InRange(initial.DiscoveryRequestedAtUtc, initial.CreatedAtUtc, initial.UpdatedAtUtc);

        ObservationTargetRegistrationResult replay = await targets.RegisterAsync(
            new RegisterObservationTargetRequest(
                registration,
                CreateAudit(targetId, AdministrativeAuditAction.RegisterObservationTarget),
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(ObservationTargetRegistrationStatus.AlreadyExists, replay.Status);

        ObservationTargetPage scopedOut = await targets.ListObservationTargetsAsync(
            new ListObservationTargetsRequest(
                TargetAuthorizationScope.None(),
                10,
                null,
                includeRetired: false,
                DefaultTimeout),
            CancellationToken.None);
        Assert.Empty(scopedOut.Targets);

        ObservationTargetPage scopedIn = await targets.ListObservationTargetsAsync(
            new ListObservationTargetsRequest(
                TargetAuthorizationScope.ForTargets([targetId]),
                10,
                null,
                includeRetired: false,
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(targetId, Assert.Single(scopedIn.Targets).TargetId);

        await InstallFailingAuditTriggerAsync(database);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(async () =>
                await targets.UpdateAsync(
                    new UpdateObservationTargetRequest(
                        targetId,
                        initial.Revision,
                        new ObservationTargetDisplayName("Must roll back"),
                        CreatePolicy(port: 1544),
                        CreateAudit(targetId, AdministrativeAuditAction.UpdateObservationTarget),
                        DefaultTimeout),
                    CancellationToken.None));
        }
        finally
        {
            await RemoveFailingAuditTriggerAsync(database);
        }

        ObservationTarget afterAuditFailure = Assert.IsType<ObservationTarget>(await targets.GetAsync(
            new GetObservationTargetRequest(targetId, DefaultTimeout),
            CancellationToken.None));
        Assert.Equal("Atomic target", afterAuditFailure.DisplayName.Value);
        Assert.Equal(1, afterAuditFailure.Revision.Value);
        Assert.Equal(1433, afterAuditFailure.ConnectionPolicy.Endpoint.TcpPort);

        ObservationTargetMutationResult stale = await targets.UpdateAsync(
            new UpdateObservationTargetRequest(
                targetId,
                new ObservationTargetRevision(99),
                new ObservationTargetDisplayName("Stale"),
                CreatePolicy(port: 1544),
                CreateAudit(targetId, AdministrativeAuditAction.UpdateObservationTarget),
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(ObservationTargetMutationStatus.RevisionConflict, stale.Status);
        Assert.Null(stale.Target);

        ObservationTargetMutationResult updated = await targets.UpdateAsync(
            new UpdateObservationTargetRequest(
                targetId,
                initial.Revision,
                new ObservationTargetDisplayName("Updated target"),
                CreatePolicy(port: 1544),
                CreateAudit(targetId, AdministrativeAuditAction.UpdateObservationTarget),
                DefaultTimeout),
            CancellationToken.None);
        ObservationTarget updatedTarget = Assert.IsType<ObservationTarget>(updated.Target);
        Assert.Equal(2, updatedTarget.Revision.Value);
        Assert.Equal(1544, updatedTarget.ConnectionPolicy.Endpoint.TcpPort);

        ObservationTargetMutationResult retired = await targets.RetireAsync(
            new RetireObservationTargetRequest(
                targetId,
                updatedTarget.Revision,
                CreateAudit(targetId, AdministrativeAuditAction.RetireObservationTarget),
                DefaultTimeout),
            CancellationToken.None);
        ObservationTarget retiredTarget = Assert.IsType<ObservationTarget>(retired.Target);
        Assert.Equal(ObservationTargetLifecycle.Retired, retiredTarget.Lifecycle);
        Assert.Equal(3, retiredTarget.Revision.Value);
        Assert.NotNull(retiredTarget.RetiredAtUtc);

        ObservationTargetPage excludesRetired = await targets.ListObservationTargetsAsync(
            new ListObservationTargetsRequest(
                TargetAuthorizationScope.ForAllTargets(),
                10,
                null,
                includeRetired: false,
                DefaultTimeout),
            CancellationToken.None);
        Assert.Empty(excludesRetired.Targets);
    }

    [Fact]
    public async Task CapabilityProfilesAreRepositoryClockedFencedAppendOnlyAndBulkReadable()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource serverDataSource = database.CreateServerDataSource();
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var targets = new PostgreSqlObservationTargetPort(serverDataSource);
        var capabilities = new PostgreSqlCapabilityProfilePort(collectorDataSource);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        ObservationTarget target = Assert.IsType<ObservationTarget>((await targets.RegisterAsync(
            new RegisterObservationTargetRequest(
                CreateRegistration(targetId, "m3.profile.target", "Profile target"),
                CreateAudit(targetId, AdministrativeAuditAction.RegisterObservationTarget),
                DefaultTimeout),
            CancellationToken.None)).Target);

        CapabilityDiscoveryDueBatch due = await capabilities.ListDueAsync(
            new CapabilityDiscoveryDueRequest(1, DefaultTimeout),
            CancellationToken.None);
        CapabilityDiscoveryDueTarget dueTarget = Assert.Single(due.Targets);
        Assert.Equal(targetId, dueTarget.TargetId);
        Assert.Equal(target.Revision, dueTarget.TargetRevision);

        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        WorkerLeaseKey leaseKey = new($"capability:{targetId.Value:N}");
        LeaseAcquisitionResult acquisition = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                leaseKey,
                new WorkerExecutionId(Guid.NewGuid()),
                LeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        WorkerLease lease = Assert.IsType<WorkerLease>(acquisition.Lease);
        CapabilityProfile profile = CreateProfile(targetId, target.Revision);

        CapabilityProfileRecordResult recorded = await capabilities.RecordAsync(
            new RecordCapabilityProfileRequest(
                profile,
                lease.Identity,
                CreateAudit(targetId, AdministrativeAuditAction.RecordCapabilityProfile),
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(CapabilityProfileRecordStatus.Recorded, recorded.Status);
        Assert.NotNull(recorded.RecordedAtUtc);

        CapabilityProfile latest = Assert.IsType<CapabilityProfile>(await capabilities.GetLatestAsync(
            new GetLatestCapabilityProfileRequest(targetId, DefaultTimeout),
            CancellationToken.None));
        Assert.Equal("collector.primary", latest.CollectorId.Value);
        Assert.Equal(7, latest.CollectorManifestVersion);
        Assert.Equal(3, latest.OutputSchemaVersion);
        Assert.Equal(SqlServerEngineEdition.Enterprise, latest.ServerIdentity?.EngineEdition);
        Assert.Equal(CapabilityEvidenceReason.EditionUnsupported, latest.Capabilities[1].Reason);
        Assert.Equal(2, latest.Permissions.Count);

        ObservationTargetRegistrationResult operationalReplay = await targets.RegisterAsync(
            new RegisterObservationTargetRequest(
                CreateRegistration(targetId, "m3.profile.target", "Profile target"),
                CreateAudit(targetId, AdministrativeAuditAction.RegisterObservationTarget),
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(ObservationTargetRegistrationStatus.AlreadyExists, operationalReplay.Status);
        Assert.Equal(ObservationTargetLifecycle.Active, operationalReplay.Target?.Lifecycle);

        CapabilityProfileBatch bulk = await capabilities.GetLatestForTargetsAsync(
            new GetLatestCapabilityProfilesRequest([targetId], DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(targetId, Assert.Single(bulk.Profiles).TargetId);

        ObservationTargetMutationResult rediscovery = await targets.RequestRediscoveryAsync(
            new RequestCapabilityRediscoveryRequest(
                targetId,
                target.Revision,
                CreateAudit(targetId, AdministrativeAuditAction.RequestCapabilityRediscovery),
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(2, Assert.IsType<ObservationTarget>(rediscovery.Target).Revision.Value);

        CapabilityProfileRecordResult revisionConflict = await capabilities.RecordAsync(
            new RecordCapabilityProfileRequest(
                profile,
                lease.Identity,
                CreateAudit(targetId, AdministrativeAuditAction.RecordCapabilityProfile),
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(CapabilityProfileRecordStatus.RevisionConflict, revisionConflict.Status);

        Assert.Equal(
            LeaseReleaseStatus.Released,
            await leases.ReleaseAsync(
                new ReleaseWorkerLeaseRequest(lease.Identity, DefaultTimeout),
                CancellationToken.None));
        LeaseAcquisitionResult replacement = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                leaseKey,
                new WorkerExecutionId(Guid.NewGuid()),
                LeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        _ = Assert.IsType<WorkerLease>(replacement.Lease);

        PostgresException staleFence = await Assert.ThrowsAsync<PostgresException>(async () =>
            await capabilities.RecordAsync(
                new RecordCapabilityProfileRequest(
                    profile,
                    lease.Identity,
                    CreateAudit(targetId, AdministrativeAuditAction.RecordCapabilityProfile),
                    DefaultTimeout),
                CancellationToken.None));
        Assert.Equal("55000", staleFence.SqlState);

        await AssertSqlStateAsync(
            database,
            "UPDATE control.capability_discovery_attempt SET outcome = 'unsupported';",
            "55000");
        await AssertSqlStateAsync(
            database,
            "DELETE FROM control.capability_profile;",
            "55000");
    }

    [Fact]
    public async Task M3FunctionsAndSanitizedViewDenyPublicAndCrossRoleCalls()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        string probeRole = $"m3_public_probe_{Guid.NewGuid():N}";
        await ExecuteAdminAsync(database, $"CREATE ROLE \"{probeRole}\" NOLOGIN;");

        try
        {
            await ExecuteAllowedAsRoleAsync(
                database,
                "sqlobserver_server",
                "SELECT instance_id FROM reporting.observation_target_status LIMIT 0;");
            await ExecuteAllowedAsRoleAsync(
                database,
                "sqlobserver_collector",
                "SELECT * FROM control.list_due_capability_targets(1);");
            await ExecuteAllowedAsRoleAsync(
                database,
                "sqlobserver_auditor",
                "SELECT activity_id FROM audit.activity LIMIT 0;");
            await ExecuteAllowedAsRoleAsync(
                database,
                "sqlobserver_server",
                "SELECT * FROM audit.append_denied_administrative_activity('S-1-5-21-1', gen_random_uuid(), 'register_observation_target', gen_random_uuid(), 'required_role_missing');");

            await AssertDeniedAsRoleAsync(
                database,
                "sqlobserver_collector",
                "SELECT instance_id FROM reporting.observation_target_status LIMIT 0;");
            await AssertDeniedAsRoleAsync(
                database,
                "sqlobserver_server",
                "SELECT * FROM control.list_due_capability_targets(1);");
            await AssertDeniedAsRoleAsync(
                database,
                "sqlobserver_auditor",
                "SELECT * FROM control.get_latest_capability_profile(gen_random_uuid());");
            await AssertDeniedAsRoleAsync(
                database,
                "sqlobserver_server",
                "SELECT * FROM audit.append_administrative_activity('S-1-5-21-1', gen_random_uuid(), 'register_observation_target', gen_random_uuid(), 'denied', 'denied', 'required_role_missing');");
            await AssertDeniedAsRoleAsync(
                database,
                "sqlobserver_collector",
                "SELECT * FROM audit.append_administrative_activity('S-1-5-21-1', gen_random_uuid(), 'record_capability_profile', gen_random_uuid(), 'granted', 'succeeded', 'completed');");
            await AssertDeniedAsRoleAsync(
                database,
                "sqlobserver_collector",
                "SELECT * FROM audit.append_denied_administrative_activity('S-1-5-21-1', gen_random_uuid(), 'register_observation_target', gen_random_uuid(), 'required_role_missing');");
            await AssertDeniedAsRoleAsync(
                database,
                probeRole,
                "SELECT instance_id FROM reporting.observation_target_status LIMIT 0;");
            await AssertDeniedAsRoleAsync(
                database,
                probeRole,
                "SELECT * FROM control.list_due_capability_targets(1);");
            await AssertDeniedAsRoleAsync(
                database,
                probeRole,
                "SELECT * FROM audit.append_administrative_activity('S-1-5-21-1', gen_random_uuid(), 'register_observation_target', gen_random_uuid(), 'granted', 'succeeded', 'completed');");
            await AssertDeniedAsRoleAsync(
                database,
                probeRole,
                "SELECT * FROM audit.append_denied_administrative_activity('S-1-5-21-1', gen_random_uuid(), 'register_observation_target', gen_random_uuid(), 'required_role_missing');");
        }
        finally
        {
            await ExecuteAdminAsync(database, $"DROP ROLE \"{probeRole}\";");
        }
    }

    [Fact]
    public async Task CapabilityDueSchedulingUsesRepositoryAnchoredRefreshAcrossCallerClockSkew()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource serverDataSource = database.CreateServerDataSource();
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var targets = new PostgreSqlObservationTargetPort(serverDataSource);
        var capabilities = new PostgreSqlCapabilityProfilePort(collectorDataSource);
        MonitoredInstanceId backwardClockTargetId = new(Guid.NewGuid());
        MonitoredInstanceId forwardClockTargetId = new(Guid.NewGuid());
        ObservationTarget backwardClockTarget = Assert.IsType<ObservationTarget>((await targets.RegisterAsync(
            new RegisterObservationTargetRequest(
                CreateRegistration(backwardClockTargetId, "m3.clock.backward", "Backward clock"),
                CreateAudit(backwardClockTargetId, AdministrativeAuditAction.RegisterObservationTarget),
                DefaultTimeout),
            CancellationToken.None)).Target);
        ObservationTarget forwardClockTarget = Assert.IsType<ObservationTarget>((await targets.RegisterAsync(
            new RegisterObservationTargetRequest(
                CreateRegistration(forwardClockTargetId, "m3.clock.forward", "Forward clock"),
                CreateAudit(forwardClockTargetId, AdministrativeAuditAction.RegisterObservationTarget),
                DefaultTimeout),
            CancellationToken.None)).Target);

        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        LeaseAcquisitionResult acquisition = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"capability:clock-skew:{Guid.NewGuid():N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                LeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        WorkerLease lease = Assert.IsType<WorkerLease>(acquisition.Lease);
        DateTimeOffset backwardCheckedAt = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset forwardCheckedAt = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await AssertSqlStateAsync(
            database,
            $"""
            INSERT INTO control.capability_discovery_attempt
            (
                attempt_id, instance_id, target_revision, collector_id,
                collector_manifest_version, output_schema_version, outcome,
                discovery_reason, authentication_scheme, transport_encrypted,
                is_sysadmin, duration_ms, response_bytes, checked_at, valid_until,
                recorded_at
            )
            VALUES
            (
                gen_random_uuid(), '{backwardClockTargetId.Value:D}'::uuid,
                {backwardClockTarget.Revision.Value}, 'collector.primary', 7, 3,
                'unreachable', 'network_unreachable', 'unknown', false, false,
                0, 0, TIMESTAMPTZ '2001-01-01 00:00:00+00',
                TIMESTAMPTZ '2001-01-01 00:00:30+00', clock_timestamp()
            );
            """,
            "23514");

        await AssertSqlStateAsync(
            database,
            $"""
            INSERT INTO control.capability_discovery_attempt
            (
                attempt_id, instance_id, target_revision, collector_id,
                collector_manifest_version, output_schema_version, outcome,
                discovery_reason, authentication_scheme, transport_encrypted,
                is_sysadmin, duration_ms, response_bytes, checked_at, valid_until,
                recorded_at
            )
            VALUES
            (
                gen_random_uuid(), '{backwardClockTargetId.Value:D}'::uuid,
                {backwardClockTarget.Revision.Value}, 'collector.primary', 7, 3,
                'unreachable', 'network_unreachable', 'unknown', false, false,
                0, 0, TIMESTAMPTZ 'infinity', TIMESTAMPTZ 'infinity',
                clock_timestamp()
            );
            """,
            "23514");

        foreach ((ObservationTarget target, DateTimeOffset checkedAt) in new[]
        {
            (backwardClockTarget, backwardCheckedAt),
            (forwardClockTarget, forwardCheckedAt),
        })
        {
            CapabilityProfileRecordResult result = await capabilities.RecordAsync(
                new RecordCapabilityProfileRequest(
                    CreateProfile(target.TargetId, target.Revision, checkedAt, TimeSpan.FromMinutes(5)),
                    lease.Identity,
                    CreateAudit(target.TargetId, AdministrativeAuditAction.RecordCapabilityProfile),
                    DefaultTimeout),
                CancellationToken.None);
            Assert.Equal(CapabilityProfileRecordStatus.Recorded, result.Status);
        }

        CapabilityDiscoveryDueBatch immediatelyDue = await capabilities.ListDueAsync(
            new CapabilityDiscoveryDueRequest(16, DefaultTimeout),
            CancellationToken.None);
        Assert.DoesNotContain(immediatelyDue.Targets, item => item.TargetId == backwardClockTargetId);
        Assert.DoesNotContain(immediatelyDue.Targets, item => item.TargetId == forwardClockTargetId);

        await BackdateCapabilitySchedulingAnchorsAsync(
            database,
            [backwardClockTargetId.Value, forwardClockTargetId.Value]);

        CapabilityDiscoveryDueBatch expiredByRepositoryClock = await capabilities.ListDueAsync(
            new CapabilityDiscoveryDueRequest(16, DefaultTimeout),
            CancellationToken.None);
        Assert.Contains(expiredByRepositoryClock.Targets, item => item.TargetId == backwardClockTargetId);
        Assert.Contains(expiredByRepositoryClock.Targets, item => item.TargetId == forwardClockTargetId);
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var migrations = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await migrations.ApplyPendingAsync(
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

    private static ObservationTargetRegistration CreateRegistration(
        MonitoredInstanceId targetId,
        string key,
        string displayName) =>
        new(
            targetId,
            new ObservationTargetKey(key),
            new ObservationTargetDisplayName(displayName),
            CreatePolicy(port: 1433));

    private static SqlServerConnectionPolicy CreatePolicy(int port) =>
        new(
            new SqlServerEndpoint(new SqlServerHostName("sql01.example.test"), tcpPort: port),
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)),
            new SqlServerCertificateHostName("sql01.example.test"));

    private static AdministrativeAuditEnvelope CreateAudit(
        MonitoredInstanceId targetId,
        AdministrativeAuditAction action) =>
        new(
            new ActorSecurityIdentifier("S-1-5-21-1000"),
            new AuditCorrelationId(Guid.NewGuid()),
            action,
            targetId);

    private static CapabilityProfile CreateProfile(
        MonitoredInstanceId targetId,
        ObservationTargetRevision revision,
        DateTimeOffset? checkedAtOverride = null,
        TimeSpan? refreshInterval = null)
    {
        DateTimeOffset checkedAt = checkedAtOverride ?? DateTimeOffset.UtcNow;
        checkedAt = new DateTimeOffset(checkedAt.Ticks - (checkedAt.Ticks % 10), TimeSpan.Zero);
        return new CapabilityProfile(
            targetId,
            revision,
            new CollectorId("collector.primary"),
            7,
            3,
            new SqlServerIdentity(
                new SqlServerVersion(16, 0, 4125, 3),
                new SqlServerEditionName("Enterprise Edition"),
                SqlServerEngineEdition.Enterprise,
                SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false,
            [
                new CapabilityEvidence(
                    new CapabilityId("connection"),
                    CapabilityAvailability.Available,
                    CapabilityEvidenceReason.Verified),
                new CapabilityEvidence(
                    new CapabilityId("query.store"),
                    CapabilityAvailability.Unavailable,
                    CapabilityEvidenceReason.EditionUnsupported),
            ],
            [
                new PermissionEvidence(
                    new SqlServerPermissionId("view.server.state"),
                    PermissionEvidenceScope.Server,
                    PermissionEvidenceOutcome.Granted),
                new PermissionEvidence(
                    new SqlServerPermissionId("view.database.state"),
                    PermissionEvidenceScope.Database,
                    PermissionEvidenceOutcome.Denied),
            ],
            TimeSpan.FromMilliseconds(125),
            2048,
            checkedAt,
            checkedAt.Add(refreshInterval ?? TimeSpan.FromHours(1)));
    }

    private static async Task BackdateCapabilitySchedulingAnchorsAsync(
        RepositoryTestDatabase database,
        Guid[] targetIds)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            ALTER TABLE control.capability_discovery_attempt
                DISABLE TRIGGER capability_attempt_append_only;
            ALTER TABLE control.capability_profile
                DISABLE TRIGGER capability_profile_append_only;

            UPDATE control.capability_discovery_attempt
            SET recorded_at = clock_timestamp() - interval '10 minutes'
            WHERE instance_id = ANY(@target_ids);

            UPDATE control.capability_profile
            SET recorded_at = clock_timestamp() - interval '10 minutes'
            WHERE instance_id = ANY(@target_ids);

            UPDATE control.observation_target
            SET
                created_at = clock_timestamp() - interval '20 minutes',
                discovery_requested_at = clock_timestamp() - interval '19 minutes'
            WHERE instance_id = ANY(@target_ids);

            ALTER TABLE control.capability_discovery_attempt
                ENABLE TRIGGER capability_attempt_append_only;
            ALTER TABLE control.capability_profile
                ENABLE TRIGGER capability_profile_append_only;
            """,
            connection);
        command.Parameters.AddWithValue("target_ids", targetIds);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InstallFailingAuditTriggerAsync(RepositoryTestDatabase database)
    {
        await ExecuteAdminAsync(
            database,
            """
            CREATE FUNCTION audit.fail_m3_audit_probe()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $probe$
            BEGIN
                RAISE EXCEPTION 'forced M3 audit failure';
            END
            $probe$;
            CREATE TRIGGER fail_m3_audit_probe
            BEFORE INSERT ON audit.activity
            FOR EACH ROW EXECUTE FUNCTION audit.fail_m3_audit_probe();
            """);
    }

    private static async Task RemoveFailingAuditTriggerAsync(RepositoryTestDatabase database)
    {
        await ExecuteAdminAsync(
            database,
            """
            DROP TRIGGER fail_m3_audit_probe ON audit.activity;
            DROP FUNCTION audit.fail_m3_audit_probe();
            """);
    }

    private static async Task ExecuteAdminAsync(RepositoryTestDatabase database, string sql)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertSqlStateAsync(
        RepositoryTestDatabase database,
        string sql,
        string expectedSqlState)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync());
        Assert.Equal(expectedSqlState, exception.SqlState);
    }

    private static async Task ExecuteAllowedAsRoleAsync(
        RepositoryTestDatabase database,
        string role,
        string sql)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE \"{role}\";", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        await transaction.RollbackAsync();
    }

    private static async Task AssertDeniedAsRoleAsync(
        RepositoryTestDatabase database,
        string role,
        string sql)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE \"{role}\";", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync());
        Assert.Equal("42501", exception.SqlState);
    }
}
