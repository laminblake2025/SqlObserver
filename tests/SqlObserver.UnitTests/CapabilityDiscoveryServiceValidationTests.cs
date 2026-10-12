using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class CapabilityDiscoveryServiceValidationTests
{
    private static readonly DateTimeOffset CheckedAtUtc =
        new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public async Task FrozenSupportedProfilesAreRecorded(int majorVersion)
    {
        TestHarness harness = CreateHarness(
            request => CreateConnectedProfile(request, majorVersion: majorVersion));

        CapabilityDiscoveryRunResult result = await harness.RunAsync();

        Assert.Equal(1, result.RecordedCount);
        Assert.Equal(1, harness.Repository.RecordCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V3ProfilesPreserveOptionalHistoryPermissions(bool includeHistory)
    {
        var permissions = new List<PermissionEvidence>
        {
            new(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted),
            new(new SqlServerPermissionId("server.view-any-database"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Denied),
            new(new SqlServerPermissionId("replication.replmonitor"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.NotApplicable),
        };
        if (includeHistory)
        {
            permissions.Add(new(new SqlServerPermissionId("msdb.backupset.select"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.Granted));
            permissions.Add(new(new SqlServerPermissionId("msdb.sysjobhistory.select"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.Denied));
        }
        CapabilityEvidence[] capabilities = [.. CreateCapabilities(),
            new(new CapabilityId("feature.replication"), CapabilityAvailability.Unavailable, CapabilityEvidenceReason.FeatureDisabled),
            new(new CapabilityId("feature.host-binding"), CapabilityAvailability.Unavailable, CapabilityEvidenceReason.FeatureDisabled)];
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(request,
            manifestVersion: 3, outputSchemaVersion: 3, capabilities: capabilities, permissions: permissions));
        Assert.Equal(1, (await harness.RunAsync()).RecordedCount);
        Assert.Equal(1, harness.Repository.RecordCalls);
    }

    [Fact]
    public async Task V3ProfileWithoutDatabaseVisibilityEvidenceIsRejected()
    {
        PermissionEvidence[] permissions =
        [
            new(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted),
            new(new SqlServerPermissionId("replication.replmonitor"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.NotApplicable),
        ];
        CapabilityEvidence[] capabilities = [.. CreateCapabilities(),
            new(new CapabilityId("feature.replication"), CapabilityAvailability.Unavailable, CapabilityEvidenceReason.FeatureDisabled),
            new(new CapabilityId("feature.host-binding"), CapabilityAvailability.Unavailable, CapabilityEvidenceReason.FeatureDisabled)];
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(request,
            manifestVersion: 3, outputSchemaVersion: 3, capabilities: capabilities, permissions: permissions));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Theory]
    [InlineData(PermissionEvidenceOutcome.Granted)]
    [InlineData(PermissionEvidenceOutcome.Denied)]
    public async Task V4RecordsMetadataPermissionWithoutChangingOverallSupport(PermissionEvidenceOutcome metadataOutcome)
    {
        PermissionEvidence[] permissions =
        [
            new(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted),
            new(new SqlServerPermissionId("server.view-any-database"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted),
            new(new SqlServerPermissionId("server.view-any-definition"), PermissionEvidenceScope.Server, metadataOutcome),
            new(new SqlServerPermissionId("replication.replmonitor"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.NotApplicable),
        ];
        CapabilityEvidence[] capabilities = [.. CreateCapabilities(),
            new(new CapabilityId("feature.replication"), CapabilityAvailability.Unavailable, CapabilityEvidenceReason.FeatureDisabled),
            new(new CapabilityId("feature.host-binding"), CapabilityAvailability.Unavailable, CapabilityEvidenceReason.FeatureDisabled)];
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(request,
            manifestVersion: 4, outputSchemaVersion: 4, capabilities: capabilities, permissions: permissions));

        Assert.Equal(1, (await harness.RunAsync()).RecordedCount);
        Assert.Equal(1, harness.Repository.RecordCalls);
    }

    [Fact]
    public async Task V4WithoutMetadataPermissionEvidenceIsRejected()
    {
        PermissionEvidence[] permissions =
        [
            new(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted),
            new(new SqlServerPermissionId("server.view-any-database"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted),
            new(new SqlServerPermissionId("replication.replmonitor"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.NotApplicable),
        ];
        CapabilityEvidence[] capabilities = [.. CreateCapabilities(),
            new(new CapabilityId("feature.replication"), CapabilityAvailability.Unavailable, CapabilityEvidenceReason.FeatureDisabled),
            new(new CapabilityId("feature.host-binding"), CapabilityAvailability.Unavailable, CapabilityEvidenceReason.FeatureDisabled)];
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(request,
            manifestVersion: 4, outputSchemaVersion: 4, capabilities: capabilities, permissions: permissions));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public async Task ContractVersionDriftIsRejectedBeforePersistence(
        int manifestVersion,
        int outputSchemaVersion)
    {
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            manifestVersion: manifestVersion,
            outputSchemaVersion: outputSchemaVersion));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Fact]
    public async Task MissingRequiredCapabilityIsRejectedBeforePersistence()
    {
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            capabilities: CreateCapabilities()
                .Where(static evidence => evidence.CapabilityId.Value != "platform.windows")
                .ToArray()));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Fact]
    public async Task UnknownCapabilityIsRejectedBeforePersistence()
    {
        CapabilityEvidence[] capabilities =
        [
            .. CreateCapabilities(),
            new CapabilityEvidence(
                new CapabilityId("unexpected.capability"),
                CapabilityAvailability.Available,
                CapabilityEvidenceReason.Verified),
        ];
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            capabilities: capabilities));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Fact]
    public async Task CapabilityEvidenceMustAgreeWithTheSecurityEnvelope()
    {
        CapabilityEvidence[] capabilities = CreateCapabilities();
        capabilities[2] = new CapabilityEvidence(
            new CapabilityId("transport.tls-validated"),
            CapabilityAvailability.Unavailable,
            CapabilityEvidenceReason.FeatureDisabled);
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            capabilities: capabilities));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Theory]
    [InlineData(15, "server.view-performance-state")]
    [InlineData(16, "server.view-state")]
    [InlineData(17, "server.view-state")]
    public async Task SupportedMajorRequiresItsVersionSpecificPermission(
        int majorVersion,
        string wrongPermissionId)
    {
        PermissionEvidence[] permissions =
        [
            new PermissionEvidence(
                new SqlServerPermissionId(wrongPermissionId),
                PermissionEvidenceScope.Server,
                PermissionEvidenceOutcome.Granted),
            CreateRoleMembershipEvidence(majorVersion),
        ];
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            majorVersion: majorVersion,
            permissions: permissions));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Fact]
    public async Task SupportedProfileRequiresGrantedPermissionEvidence()
    {
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            permissions: CreatePermissions(16, PermissionEvidenceOutcome.Denied)));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Fact]
    public async Task UnsupportedVersionMayRecordOnlyFrozenAllowlistedPermissionEvidence()
    {
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            majorVersion: 18,
            outcome: CapabilityDiscoveryOutcome.Unsupported,
            reason: CapabilityDiscoveryReason.UnsupportedVersion,
            permissions:
            [
                new PermissionEvidence(
                    new SqlServerPermissionId("server.view-performance-state"),
                    PermissionEvidenceScope.Server,
                    PermissionEvidenceOutcome.Denied),
                new PermissionEvidence(
                    new SqlServerPermissionId("server.performance-reader-role-membership"),
                    PermissionEvidenceScope.Server,
                    PermissionEvidenceOutcome.Denied),
            ]));

        CapabilityDiscoveryRunResult result = await harness.RunAsync();

        Assert.Equal(1, result.RecordedCount);
        Assert.Equal(1, harness.Repository.RecordCalls);
    }

    [Fact]
    public async Task UnsupportedVersionRejectsUnknownPermissionEvidence()
    {
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            majorVersion: 18,
            outcome: CapabilityDiscoveryOutcome.Unsupported,
            reason: CapabilityDiscoveryReason.UnsupportedVersion,
            permissions:
            [
                new PermissionEvidence(
                    new SqlServerPermissionId("server.arbitrary"),
                    PermissionEvidenceScope.Server,
                    PermissionEvidenceOutcome.Denied),
            ]));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Fact]
    public async Task UnsupportedVersionStillRequiresTheFrozenPermissionShape()
    {
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            majorVersion: 18,
            outcome: CapabilityDiscoveryOutcome.Unsupported,
            reason: CapabilityDiscoveryReason.UnsupportedVersion,
            permissions:
            [
                new PermissionEvidence(
                    new SqlServerPermissionId("server.view-performance-state"),
                    PermissionEvidenceScope.Server,
                    PermissionEvidenceOutcome.Denied),
            ]));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Fact]
    public async Task DiscoveryDurationCannotExceedRequestedTimeout()
    {
        TestHarness harness = CreateHarness(
            request => CreateConnectedProfile(
                request,
                discoveryDuration: TimeSpan.FromSeconds(3)),
            discoveryTimeout: TimeSpan.FromSeconds(2));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Fact]
    public async Task ConnectedEvidenceCannotExceedTheFrozenResponseBudget()
    {
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            evidenceBytes: 16_385));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public async Task ValidityIntervalMustMatchTheRequestedRefresh(int actualMinutes)
    {
        TestHarness harness = CreateHarness(request => CreateConnectedProfile(
            request,
            validityInterval: TimeSpan.FromMinutes(actualMinutes)));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    [Fact]
    public async Task SubMicrosecondRefreshIsValidatedAtPersistencePrecision()
    {
        TimeSpan requestedRefresh = TimeSpan.FromMinutes(5).Add(TimeSpan.FromTicks(5));
        TestHarness harness = CreateHarness(
            request => CreateConnectedProfile(request),
            refreshInterval: requestedRefresh);

        CapabilityDiscoveryRunResult result = await harness.RunAsync();

        Assert.Equal(1, result.RecordedCount);
    }

    [Fact]
    public async Task ConnectionFailureWithNoInventedEvidenceIsRecorded()
    {
        TestHarness harness = CreateHarness(request => CreateConnectionFailureProfile(request));

        CapabilityDiscoveryRunResult result = await harness.RunAsync();

        Assert.Equal(1, result.RecordedCount);
        Assert.Equal(1, harness.Repository.RecordCalls);
    }

    [Fact]
    public async Task ConnectionFailureWithEvidenceIsRejectedBeforePersistence()
    {
        TestHarness harness = CreateHarness(request => CreateConnectionFailureProfile(
            request,
            evidenceBytes: 1));

        await AssertInvalidBeforePersistenceAsync(harness);
    }

    private static async Task AssertInvalidBeforePersistenceAsync(TestHarness harness)
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            _ = await harness.RunAsync();
        });
        Assert.Equal(0, harness.Repository.RecordCalls);
    }

    private static TestHarness CreateHarness(
        Func<CapabilityDiscoveryRequest, CapabilityProfile> profileFactory,
        TimeSpan? discoveryTimeout = null,
        TimeSpan? refreshInterval = null)
    {
        var target = new CapabilityDiscoveryDueTarget(
            new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            new ObservationTargetRevision(3),
            new SqlServerConnectionPolicy(
                new SqlServerEndpoint(new SqlServerHostName("sql.example.test"), tcpPort: 1433),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))));
        var repository = new FakeProfileRepository(target);
        var service = new CapabilityDiscoveryService(
            repository,
            new FakeDiscoveryPort(profileFactory));
        var request = new CapabilityDiscoveryRunRequest(
            maxTargets: 1,
            new WorkerLeaseIdentity(
                new WorkerLeaseKey("capability/discovery"),
                new WorkerExecutionId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
                new FencingToken(7)),
            new ActorSecurityIdentifier("S-1-5-18"),
            new AuditCorrelationId(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
            new CapabilityDiscoveryTimeout(discoveryTimeout ?? TimeSpan.FromSeconds(5)),
            new CapabilityProfileRefreshInterval(refreshInterval ?? TimeSpan.FromMinutes(5)),
            new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        return new TestHarness(service, repository, request);
    }

    private static CapabilityProfile CreateConnectedProfile(
        CapabilityDiscoveryRequest request,
        int manifestVersion = 1,
        int outputSchemaVersion = 1,
        int majorVersion = 16,
        CapabilityDiscoveryOutcome outcome = CapabilityDiscoveryOutcome.Supported,
        CapabilityDiscoveryReason reason = CapabilityDiscoveryReason.Verified,
        IReadOnlyList<CapabilityEvidence>? capabilities = null,
        IReadOnlyList<PermissionEvidence>? permissions = null,
        TimeSpan? discoveryDuration = null,
        TimeSpan? validityInterval = null,
        int evidenceBytes = 256)
    {
        TimeSpan requestedValidity = validityInterval ?? request.RefreshInterval.Value;
        long validityTicks = requestedValidity.Ticks -
            (requestedValidity.Ticks % TimeSpan.TicksPerMicrosecond);
        return new CapabilityProfile(
            request.TargetId,
            request.TargetRevision,
            new CollectorId("capability.connection"),
            manifestVersion,
            outputSchemaVersion,
            new SqlServerIdentity(
                new SqlServerVersion(majorVersion, 0, 1000, 1),
                new SqlServerEditionName("Enterprise Edition"),
                SqlServerEngineEdition.Enterprise,
                SqlServerPlatform.Windows),
            outcome,
            reason,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false,
            capabilities ?? CreateCapabilities(),
            permissions ?? CreatePermissions(majorVersion, PermissionEvidenceOutcome.Granted),
            discoveryDuration ?? TimeSpan.FromMilliseconds(25),
            evidenceBytes,
            CheckedAtUtc,
            CheckedAtUtc.AddTicks(validityTicks));
    }

    private static CapabilityProfile CreateConnectionFailureProfile(
        CapabilityDiscoveryRequest request,
        int evidenceBytes = 0) =>
        new(
            request.TargetId,
            request.TargetRevision,
            new CollectorId("capability.connection"),
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            serverIdentity: null,
            CapabilityDiscoveryOutcome.Unreachable,
            CapabilityDiscoveryReason.NetworkUnreachable,
            SqlServerAuthenticationScheme.Unknown,
            transportEncrypted: false,
            isSysAdmin: false,
            capabilities: [],
            permissions: [],
            TimeSpan.FromMilliseconds(25),
            evidenceBytes,
            CheckedAtUtc,
            AddAtPersistencePrecision(CheckedAtUtc, request.RefreshInterval.Value));

    private static CapabilityEvidence[] CreateCapabilities() =>
    [
        new CapabilityEvidence(
            new CapabilityId("connection.tds"),
            CapabilityAvailability.Available,
            CapabilityEvidenceReason.Verified),
        new CapabilityEvidence(
            new CapabilityId("authentication.windows-integrated"),
            CapabilityAvailability.Available,
            CapabilityEvidenceReason.Verified),
        new CapabilityEvidence(
            new CapabilityId("transport.tls-validated"),
            CapabilityAvailability.Available,
            CapabilityEvidenceReason.Verified),
        new CapabilityEvidence(
            new CapabilityId("privilege.non-sysadmin"),
            CapabilityAvailability.Available,
            CapabilityEvidenceReason.Verified),
        new CapabilityEvidence(
            new CapabilityId("platform.windows"),
            CapabilityAvailability.Available,
            CapabilityEvidenceReason.Verified),
        new CapabilityEvidence(
            new CapabilityId("feature.availability-groups"),
            CapabilityAvailability.Unavailable,
            CapabilityEvidenceReason.FeatureDisabled),
    ];

    private static PermissionEvidence[] CreatePermissions(
        int majorVersion,
        PermissionEvidenceOutcome requiredOutcome) =>
    [
        new PermissionEvidence(
            new SqlServerPermissionId(
                majorVersion == 15
                    ? "server.view-state"
                    : "server.view-performance-state"),
            PermissionEvidenceScope.Server,
            requiredOutcome),
        CreateRoleMembershipEvidence(majorVersion),
    ];

    private static PermissionEvidence CreateRoleMembershipEvidence(int majorVersion) =>
        new(
            new SqlServerPermissionId("server.performance-reader-role-membership"),
            PermissionEvidenceScope.Server,
            majorVersion == 15
                ? PermissionEvidenceOutcome.NotApplicable
                : PermissionEvidenceOutcome.Denied);

    private static DateTimeOffset AddAtPersistencePrecision(
        DateTimeOffset value,
        TimeSpan interval)
    {
        DateTimeOffset result = value.Add(interval);
        return result.AddTicks(-(result.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private sealed class TestHarness
    {
        private readonly CapabilityDiscoveryService _service;
        private readonly CapabilityDiscoveryRunRequest _request;

        internal TestHarness(
            CapabilityDiscoveryService service,
            FakeProfileRepository repository,
            CapabilityDiscoveryRunRequest request)
        {
            _service = service;
            Repository = repository;
            _request = request;
        }

        internal FakeProfileRepository Repository { get; }

        internal ValueTask<CapabilityDiscoveryRunResult> RunAsync() =>
            _service.DiscoverDueAsync(_request, CancellationToken.None);
    }

    private sealed class FakeDiscoveryPort : ISqlServerCapabilityDiscoveryPort
    {
        private readonly Func<CapabilityDiscoveryRequest, CapabilityProfile> _profileFactory;

        internal FakeDiscoveryPort(Func<CapabilityDiscoveryRequest, CapabilityProfile> profileFactory)
        {
            _profileFactory = profileFactory;
        }

        public ValueTask<CapabilityProfile> DiscoverAsync(
            CapabilityDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_profileFactory(request));
        }
    }

    private sealed class FakeProfileRepository : ICapabilityProfileRepositoryPort
    {
        private readonly CapabilityDiscoveryDueTarget _target;

        internal FakeProfileRepository(CapabilityDiscoveryDueTarget target)
        {
            _target = target;
        }

        internal int RecordCalls { get; private set; }

        public ValueTask<CapabilityDiscoveryDueBatch> ListDueAsync(
            CapabilityDiscoveryDueRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CapabilityDiscoveryDueBatch([_target], hasMore: false));
        }

        public ValueTask<CapabilityProfileRecordResult> RecordAsync(
            RecordCapabilityProfileRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordCalls++;
            return ValueTask.FromResult(new CapabilityProfileRecordResult(
                CapabilityProfileRecordStatus.Recorded,
                CheckedAtUtc));
        }

        public ValueTask<CapabilityProfile?> GetLatestAsync(
            GetLatestCapabilityProfileRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CapabilityProfileBatch> GetLatestForTargetsAsync(
            GetLatestCapabilityProfilesRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
