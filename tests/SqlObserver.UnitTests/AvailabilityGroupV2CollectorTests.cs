using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class AvailabilityGroupV2CollectorTests
{
    private static readonly IdentityFingerprintKey Key = new(new byte[32]);
    private static readonly SqlServerAvailabilityGroupV2AssetCatalog Assets =
        SqlServerAvailabilityGroupV2AssetCatalog.LoadEmbedded();
    private static readonly CollectorOutputValidator Validator = new(SqlServerAvailabilityGroupsV2Collector.OutputContract);

    [Fact]
    public async Task PrimaryFaultEvidencePassesV2ValidationWithoutInventingRemoteState()
    {
        object?[] remote = Replica(0x22);
        remote[8] = null;
        remote[10] = "DISCONNECTED";
        object?[] remoteDatabase = Database(0x22);
        remoteDatabase[12] = "NOT_HEALTHY";
        remoteDatabase[13] = "SUSPECT";
        remoteDatabase[14] = true;
        remoteDatabase[15] = 0L;
        remoteDatabase[16] = 123L;
        remoteDatabase[19] = new DateTime(2026, 9, 29, 8, 15, 0);
        var source = new FixtureSource([Replica(0x11), remote, Database(0x11), remoteDatabase]);
        var collector = Collector(source);
        var request = Request();
        CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        var snapshot = Assert.IsType<AvailabilityGroupsV2Snapshot>(result.Payload.OperationalHealth!.Snapshot);
        Assert.Equal(OperationalObservationState.Complete, snapshot.State);
        Assert.Equal(request.RunId, snapshot.RunId);
        Assert.Equal(M4TestData.RepositoryTime, snapshot.ObservedAtUtc);
        Assert.Equal(4, result.Accounting.SourceRowsRead);
        Assert.Equal(4, result.Accounting.OutputItemsProduced);
        Assert.Null(snapshot.Replicas.Single(item => item.Identity.ReplicaIsLocal == false).OperationalState);
        var database = snapshot.Databases.Single(item => item.Identity.ReplicaIsLocal == false);
        Assert.Equal("SUSPECT", database.DatabaseState);
        Assert.Equal(0, database.LogSendQueueKb);
        Assert.Equal(123, database.LogSendRateKbPerSecond);
        Assert.Equal(DateTimeKind.Unspecified, database.SourceLastCommitLocal!.Value.Kind);
        Assert.Equal(Assets.GetQuery(16), source.Query);
        Assert.Equal(request.Timeout.Value, source.Budget);
        Assert.InRange(result.Accounting.ResponseBytes, snapshot.EstimatedSizeBytes, 2_097_152);
    }

    [Fact]
    public async Task SecondaryVisibilityIsPartialAndEmptyScanIsNoData()
    {
        object?[] local = Replica(0x11, 2);
        local[8] = "SECONDARY";
        foreach (object?[][] rows in new[] { new[] { local }, Array.Empty<object?[]>() })
        {
            var collector = Collector(new FixtureSource(rows));
            var request = Request();
            CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
            Validator.Validate(collector.Manifest, request, result);
            var snapshot = Assert.IsType<AvailabilityGroupsV2Snapshot>(result.Payload.OperationalHealth!.Snapshot);
            Assert.Equal(rows.Length == 0 ? OperationalObservationState.NoData : OperationalObservationState.Degraded, snapshot.State);
            Assert.Equal(rows.Length == 0 ? CollectorRunOutcome.Succeeded : CollectorRunOutcome.Partial, result.Outcome);
            Assert.Equal(rows.Length == 0 ? CollectorRunReason.Completed : CollectorRunReason.VisibilityIncomplete, result.Reason);
        }
    }

    [Fact]
    public async Task RowAndByteLimitsKeepDistinctLossAndAccounting()
    {
        object?[][] rows = Enumerable.Range(0, 2049).Select(index =>
        {
            object?[] row = Replica(0x11);
            row[1] = Convert.FromHexString(index.ToString("x64", CultureInfo.InvariantCulture));
            return row;
        }).ToArray();
        foreach (var source in new[] { new FixtureSource(rows), new FixtureSource([Replica(0x11)]) { ParserByteLimit = 256 } })
        {
            var collector = Collector(source);
            var request = Request();
            CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
            Validator.Validate(collector.Manifest, request, result);
            Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
            var snapshot = Assert.IsType<AvailabilityGroupsV2Snapshot>(result.Payload.OperationalHealth!.Snapshot);
            Assert.True(snapshot.Truncated);
            if (source.ParserByteLimit == 256)
            {
                Assert.Equal(CollectorRunReason.ResponseByteLimit, result.Reason);
                Assert.Empty(snapshot.Replicas);
                Assert.Equal(1, result.Accounting.SourceRowsRead);
                Assert.Equal(1, result.Loss.MinimumLostItems);
            }
            else
            {
                Assert.Equal(CollectorRunReason.SourceRowLimit, result.Reason);
                Assert.Equal(2049, result.Accounting.SourceRowsRead);
                Assert.Equal(2048, snapshot.Replicas.Count);
                Assert.Equal(1, result.Loss.MinimumLostItems);
            }
        }
    }

    [Theory]
    [InlineData(15, "server.view-state")]
    [InlineData(16, "server.view-performance-state")]
    [InlineData(17, "server.view-performance-state")]
    public async Task MajorSpecificServerPermissionsAndDatabaseVisibilityAreRequired(int major, string permission)
    {
        foreach (string? missing in new string?[] { null, permission, "server.view-any-database" })
        {
            var source = new FixtureSource([]);
            var collector = Collector(source);
            var request = Request(major, omittedPermission: missing);
            CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
            Validator.Validate(collector.Manifest, request, result);
            Assert.Equal(missing is null ? CollectorRunOutcome.Succeeded : CollectorRunOutcome.PermissionDenied, result.Outcome);
            Assert.Equal(missing is null ? 1 : 0, source.Calls);
            if (missing is null) Assert.Equal(Assets.GetQuery(major), source.Query);
        }
    }

    [Fact]
    public async Task SqlServer2019RequiresAgMetadataVisibilityBeforeReadingSql()
    {
        var source = new FixtureSource([]);
        var collector = Collector(source);
        var request = Request(15, omittedPermission: "server.view-any-definition");
        CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.PermissionDenied, result.Outcome);
        Assert.Equal(CollectorRunReason.RequiredPermissionMissing, result.Reason);
        Assert.Equal(0, source.Calls);
        Assert.Null(result.Payload.OperationalHealth);
    }

    [Theory]
    [InlineData(14, SqlServerPlatform.Windows, SqlServerEngineEdition.Enterprise, true)]
    [InlineData(16, SqlServerPlatform.Linux, SqlServerEngineEdition.Enterprise, true)]
    [InlineData(16, SqlServerPlatform.Windows, SqlServerEngineEdition.Express, true)]
    [InlineData(16, SqlServerPlatform.Windows, SqlServerEngineEdition.Enterprise, false)]
    public async Task UnsupportedTargetOrMissingFeatureDoesNotReadSql(int major,
        SqlServerPlatform platform, SqlServerEngineEdition edition, bool feature)
    {
        var source = new FixtureSource([]);
        var collector = Collector(source);
        var request = Request(major, platform, edition, feature);
        CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.Unsupported, result.Outcome);
        Assert.Equal(0, source.Calls);
        Assert.Null(result.Payload.OperationalHealth);
    }

    [Theory]
    [InlineData(-2, CollectorRunOutcome.TimedOut)]
    [InlineData(916, CollectorRunOutcome.PermissionDenied)]
    [InlineData(11001, CollectorRunOutcome.TransientFailure)]
    [InlineData(1205, CollectorRunOutcome.TransientFailure)]
    [InlineData(18456, CollectorRunOutcome.PermanentFailure)]
    public async Task SqlFailuresUseSharedClassificationAndPublishNoEvidence(int number, CollectorRunOutcome expected)
    {
        var collector = Collector(new FixtureSource([]) { Failure = TestSqlErrors.Create(number) });
        var request = Request();
        CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Payload.OperationalHealth);
        Assert.Equal(0, result.Accounting.OutputBytes);
    }

    [Fact]
    public async Task MalformedSourceCannotPublishItsPreviouslyReadPrefix()
    {
        object?[] malformed = Database(0x11);
        malformed[15] = -1L;
        var collector = Collector(new FixtureSource([Replica(0x11), malformed]));
        var request = Request();
        CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.OutputInvalid, result.Outcome);
        Assert.Equal(CollectorLossKind.OutputValidationFailure, result.Loss.Kind);
        Assert.Null(result.Payload.OperationalHealth);
        Assert.Equal(0, result.Accounting.OutputItemsProduced);
    }

    [Fact]
    public async Task DeadlineIsBoundedAndCallerCancellationStillPropagates()
    {
        var source = new FixtureSource([]) { WaitUntilCancelled = true };
        var collector = Collector(source);
        var request = Request(timeout: TimeSpan.FromMilliseconds(100));
        CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.TimedOut, result.Outcome);
        Assert.Equal(request.Timeout.Value, source.Budget);
        Validator.Validate(collector.Manifest, request, result);
        using var cancellation = new CancellationTokenSource();
        source.WaitUntilCancelled = false;
        source.OnRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await collector.CollectAsync(request, cancellation.Token));
        cancellation.Cancel();
        int calls = source.Calls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await collector.CollectAsync(request, cancellation.Token));
        Assert.Equal(calls, source.Calls);
    }

    [Fact]
    public async Task MissingServerIdentityIsRejectedBeforeSqlAndLongRequestUsesManifestDeadline()
    {
        var source = new FixtureSource([]);
        var collector = Collector(source);
        var missingIdentity = Request(missingIdentity: true);
        CollectorExecutionResult rejected = await collector.CollectAsync(missingIdentity, CancellationToken.None);
        Validator.Validate(collector.Manifest, missingIdentity, rejected);
        Assert.Equal(CollectorRunOutcome.OutputInvalid, rejected.Outcome);
        Assert.Equal(CollectorRunReason.CapabilityProfileStale, rejected.Reason);
        Assert.Equal(CollectorLossKind.OutputValidationFailure, rejected.Loss.Kind);
        Assert.Equal(0, source.Calls);
        var longRequest = Request(timeout: TimeSpan.FromSeconds(30));
        CollectorExecutionResult result = await collector.CollectAsync(longRequest, CancellationToken.None);
        Validator.Validate(collector.Manifest, longRequest, result);
        Assert.Equal(TimeSpan.FromSeconds(5), source.Budget);
    }

    [Fact]
    public async Task ValidatorRejectsMixedVersionsMissingEvidenceAndForgedRunCoverageOrAccounting()
    {
        var collector = Collector(new FixtureSource([Replica(0x11)]));
        var request = Request();
        CollectorExecutionResult result = await collector.CollectAsync(request, CancellationToken.None);
        var snapshot = Assert.IsType<AvailabilityGroupsV2Snapshot>(result.Payload.OperationalHealth!.Snapshot);
        var otherRun = new AvailabilityGroupsV2Snapshot(snapshot.TargetId, snapshot.TargetRevision,
            new CollectorRunId(Guid.NewGuid()), snapshot.ObservedAtUtc, snapshot.Replicas, snapshot.Databases, 1, false);
        CollectorPayload wrongRun = new(operationalHealth: new OperationalHealthPayload(otherRun, 1, otherRun.EstimatedSizeBytes));
        CollectorPayload wrongCount = new(operationalHealth: new OperationalHealthPayload(snapshot, 0, snapshot.EstimatedSizeBytes));
        CollectorPayload wrongBytes = new(operationalHealth: new OperationalHealthPayload(snapshot, 1, 0));
        var emptySnapshot = new AvailabilityGroupsV2Snapshot(snapshot.TargetId, snapshot.TargetRevision,
            snapshot.RunId, snapshot.ObservedAtUtc, [], [], 0, false);
        CollectorPayload failedEnvelope = new(operationalHealth: new OperationalHealthPayload(emptySnapshot, 0, 256));
        foreach (CollectorExecutionResult invalid in new[]
        {
            Copy(result, payload: wrongRun),
            Copy(result, payload: CollectorPayload.Empty, accounting: new CollectorRunAccounting(0, 0, 0, 0)),
            Copy(result, accounting: new CollectorRunAccounting(2, 1, result.Accounting.ResponseBytes, result.Accounting.OutputBytes)),
            Copy(result, payload: wrongCount, accounting: new CollectorRunAccounting(1, 0, result.Accounting.ResponseBytes, result.Accounting.OutputBytes)),
            Copy(result, payload: wrongBytes, accounting: new CollectorRunAccounting(1, 1, 0, 0)),
            Copy(result, payload: failedEnvelope, accounting: new CollectorRunAccounting(0, 0, 256, 256),
                outcome: CollectorRunOutcome.TransientFailure, reason: CollectorRunReason.TransientTargetFailure),
            Copy(result, payload: CollectorPayload.Empty, accounting: new CollectorRunAccounting(0, 0, 0, 0),
                outcome: CollectorRunOutcome.OutputInvalid, reason: CollectorRunReason.OutputValidationFailed),
        })
            Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request, invalid));

        var v1 = new SqlServerAvailabilityGroupsHealthCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var v1Validator = new CollectorOutputValidator(M9Manifest.OutputContract(2048));
        Assert.Throws<InvalidDataException>(() => v1Validator.Validate(v1.Manifest, request, Copy(result, version: 1)));
        var oldSnapshot = new AvailabilityGroupsSnapshot(request.TargetId, request.TargetRevision, request.RunId,
            snapshot.ObservedAtUtc, OperationalObservationState.NoData, AvailabilityVisibilityScope.PrimaryAllKnown, [], [], false);
        var oldPayload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(oldSnapshot, 0, 256));
        Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request,
            Copy(result, payload: oldPayload, accounting: new CollectorRunAccounting(0, 0, 256, 256))));
    }

    [Fact]
    public void PinnedManifestAndProductionCommandKeepReviewedBoundsAndPermissions()
    {
        var collector = Collector(new FixtureSource([]));
        using JsonDocument manifest = JsonDocument.Parse(Assets.Manifest);
        JsonElement root = manifest.RootElement;
        Assert.Equal(root.GetProperty("collectorVersion").GetInt32(), collector.Manifest.ManifestVersion.Value);
        Assert.Equal(root.GetProperty("outputSchemaVersion").GetInt32(), collector.Manifest.OutputSchemaVersion.Value);
        Assert.Equal(root.GetProperty("collectorId").GetString(), collector.Manifest.Id.Value);
        Assert.Equal(root.GetProperty("requiredCapabilities").EnumerateArray().Select(item => item.GetString()),
            collector.Manifest.RequiredCapabilities.Select(item => item.Value));
        foreach (int major in new[] { 15, 16, 17 })
            Assert.Equal(root.GetProperty("requiredPermissionsByMajor").GetProperty(major.ToString(CultureInfo.InvariantCulture)).EnumerateArray()
                    .Select(item => item.GetString()), collector.Manifest.RequiredPermissions
                    .Where(item => item.ApplicableVersions.Contains(major)).Select(item => item.PermissionId.Value));
        var limits = collector.Manifest.Limits;
        Assert.Equal(root.GetProperty("executionBounds").GetProperty("maximumRows").GetInt32(), limits.MaxRows);
        Assert.Equal(root.GetProperty("executionBounds").GetProperty("maximumResponseBytes").GetInt32(), limits.MaxResponseBytes);
        using var connection = new SqlConnection();
        using var command = AvailabilityGroupV2QuerySource.CreateCommand(connection, Assets.GetQuery(16), limits, TimeSpan.FromMilliseconds(1250));
        Assert.Equal(Assets.GetQuery(16), command.CommandText);
        Assert.Equal(2, command.CommandTimeout);
        var parameter = Assert.Single(command.Parameters.Cast<SqlParameter>());
        Assert.Equal("maximum_rows", parameter.ParameterName);
        Assert.Equal(SqlDbType.Int, parameter.SqlDbType);
        Assert.Equal(2049, parameter.Value);
    }

    private static CollectorExecutionResult Copy(CollectorExecutionResult original, CollectorPayload? payload = null,
        CollectorRunAccounting? accounting = null, CollectorRunOutcome? outcome = null,
        CollectorRunReason? reason = null, int version = 2) => new(original.TargetId, original.TargetRevision,
        original.CollectorId, version, version, outcome ?? original.Outcome, reason ?? original.Reason,
        payload ?? original.Payload, accounting ?? original.Accounting, original.Loss);

    private static SqlServerAvailabilityGroupsV2Collector Collector(FixtureSource source) =>
        new(Assets, Key, source, new FixtureClock());

    private static CollectorExecutionRequest Request(int major = 16,
        SqlServerPlatform platform = SqlServerPlatform.Windows, SqlServerEngineEdition edition = SqlServerEngineEdition.Enterprise,
        bool feature = true, string? omittedPermission = null, TimeSpan? timeout = null, bool missingIdentity = false)
    {
        var template = M4TestData.CreateProfile(majorVersion: major, platform: platform, edition: edition);
        string permission = major == 15 ? "server.view-state" : "server.view-performance-state";
        var profile = new CapabilityProfile(template.TargetId, template.TargetRevision, template.CollectorId, 1, 1,
            missingIdentity ? null : template.ServerIdentity,
            missingIdentity ? CapabilityDiscoveryOutcome.Unreachable : template.Outcome,
            missingIdentity ? CapabilityDiscoveryReason.NetworkUnreachable : template.Reason,
            missingIdentity ? SqlServerAuthenticationScheme.Unknown : template.AuthenticationScheme,
            !missingIdentity && template.TransportEncrypted, template.IsSysAdmin,
            [new CapabilityEvidence(new CapabilityId("feature.availability-groups"),
                feature ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable,
                feature ? CapabilityEvidenceReason.Verified : CapabilityEvidenceReason.FeatureDisabled)],
            new[] { permission, "server.view-any-database" }.Concat(major == 15 ? ["server.view-any-definition"] : Array.Empty<string>())
                .Where(item => item != omittedPermission)
                .Select(item => new PermissionEvidence(new SqlServerPermissionId(item), PermissionEvidenceScope.Server,
                    PermissionEvidenceOutcome.Granted)).ToArray(),
            template.DiscoveryDuration, template.EvidenceBytes, template.CheckedAtUtc, template.ValidUntilUtc);
        var request = M4TestData.CreateExecutionRequest();
        return new CollectorExecutionRequest(request.RunId, request.TargetId, request.TargetRevision,
            request.ConnectionPolicy, profile, request.Attempt, new CollectorExecutionTimeout(timeout ?? request.Timeout.Value));
    }

    private static byte[] Digest(byte value) => Enumerable.Repeat(value, 32).ToArray();
    private static object?[] Replica(byte replica, short scope = 1) =>
    [0, Digest(0xaa), Digest(replica), null, Digest(0x11), replica == 0x11, scope, true,
     replica == 0x11 ? "PRIMARY" : "SECONDARY", replica == 0x11 ? "ONLINE" : null, "CONNECTED",
     null, null, null, null, null, null, null, null, null];
    private static object?[] Database(byte replica) =>
    [1, Digest(0xaa), Digest(replica), Digest(0xdd), Digest(0x11), replica == 0x11, (short)1, true,
     null, null, null, "SYNCHRONIZED", "HEALTHY", "ONLINE", false, null, null, null, null, null];

    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => M4TestData.RepositoryTime.AddTicks(7);
    }

    private sealed class FixtureSource(object?[][] rows) : IAvailabilityGroupV2QuerySource
    {
        public int Calls { get; private set; }
        public string? Query { get; private set; }
        public TimeSpan Budget { get; private set; }
        public int? ParserByteLimit { get; init; }
        public Exception? Failure { get; init; }
        public bool WaitUntilCancelled { get; set; }
        public Action? OnRead { get; set; }
        public async ValueTask<AvailabilityGroupV2ParseResult> ReadAsync(CollectorExecutionRequest request,
            string query, CollectorExecutionLimits limits, TimeSpan budget, IdentityFingerprintKey key,
            DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
        {
            Calls++;
            Query = query;
            Budget = budget;
            if (Failure is not null) throw Failure;
            if (WaitUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            OnRead?.Invoke();
            return await SqlServerAvailabilityGroupV2Parser.ParseAsync(request, new FixtureReader(rows), key,
                observedAtUtc, cancellationToken, limits.MaxRows, ParserByteLimit ?? limits.MaxResponseBytes);
        }
    }

    private sealed class FixtureReader(object?[][] rows) : IOperationalHealthRowReader
    {
        private int index = -1;
        public int FieldCount => 20;
        public bool IsDBNull(int ordinal) => rows[index][ordinal] is null;
        public byte[] GetBinary(int ordinal) => (byte[])rows[index][ordinal]!;
        public bool GetBoolean(int ordinal) => (bool)rows[index][ordinal]!;
        public DateTime GetDateTime(int ordinal) => (DateTime)rows[index][ordinal]!;
        public Guid GetGuid(int ordinal) => (Guid)rows[index][ordinal]!;
        public short GetInt16(int ordinal) => (short)rows[index][ordinal]!;
        public int GetInt32(int ordinal) => (int)rows[index][ordinal]!;
        public long GetInt64(int ordinal) => (long)rows[index][ordinal]!;
        public string GetString(int ordinal) => (string)rows[index][ordinal]!;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(++index < rows.Length);
        }
    }
}
