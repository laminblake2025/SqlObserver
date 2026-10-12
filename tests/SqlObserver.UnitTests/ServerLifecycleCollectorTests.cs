using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class ServerLifecycleCollectorTests
{
    private static readonly IdentityFingerprintKey Key = new(new byte[32]);
    private static readonly SqlServerLifecycleAssetCatalog Assets = SqlServerLifecycleAssetCatalog.LoadEmbedded();
    private static readonly CollectorOutputValidator Validator = new(SqlServerLifecycleCollector.OutputContract);

    [Fact]
    public async Task StandaloneSourceReceiptPassesValidationAndUsesSourceUtc()
    {
        object?[] header = ServerLifecycleTestRows.Engine(hadr: false);
        header[1] = ServerLifecycleTestRows.At.AddMinutes(-1).AddTicks(20);
        var source = new Source([header]); var collector = Collector(source); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(CollectorRunReason.Completed, result.Reason);
        var snapshot = Assert.IsType<ServerLifecycleSnapshot>(result.Payload.OperationalHealth!.Snapshot);
        Assert.Equal(new DateTimeOffset((DateTime)header[1]!, TimeSpan.Zero), snapshot.SourceObservedAtUtc);
        Assert.Equal(request.RunId, snapshot.RunId);
        Assert.Equal(request.TargetRevision, snapshot.TargetRevision);
        Assert.Equal(1, result.Accounting.SourceRowsRead);
        Assert.Equal(1, result.Accounting.OutputItemsProduced);
        Assert.Equal(snapshot.EstimatedSizeBytes, result.Accounting.OutputBytes);
        Assert.InRange(result.Accounting.ResponseBytes, result.Accounting.OutputBytes, SqlServerLifecycleCollector.MaximumResponseBytes);
        Assert.Null(snapshot.Engine.OccurredAtUtc);
        Assert.Equal(Assets.GetQuery(16), source.Query);
        Assert.Equal(request.Timeout.Value, source.Budget);
    }

    [Fact]
    public async Task UnknownAvailabilityObserverRetainsEvidenceWithVisibilityLoss()
    {
        object?[] member = ServerLifecycleTestRows.Member();
        member[11] = null; member[12] = null; member[13] = null; member[14] = false;
        var collector = Collector(new Source([ServerLifecycleTestRows.Engine(), member]));
        var request = Request(); var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        var snapshot = Assert.IsType<ServerLifecycleSnapshot>(result.Payload.OperationalHealth!.Snapshot);
        Assert.Single(snapshot.Members);
        Assert.NotEqual(ServerLifecycleCoverage.Complete, snapshot.AvailabilityCoverage);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.Equal(CollectorRunReason.VisibilityIncomplete, result.Reason);
        Assert.Equal(CollectorLossKind.VisibilityIncomplete, result.Loss.Kind);
        Assert.Equal(2, result.Accounting.SourceRowsRead);
        Assert.Equal(2, result.Accounting.OutputItemsProduced);
    }

    [Theory]
    [InlineData(15, "server.view-state")]
    [InlineData(16, "server.view-performance-state")]
    [InlineData(17, "server.view-performance-state")]
    public async Task VersionSpecificAndMetadataPermissionsGateTheRealSource(int major, string permission)
    {
        foreach (string? missing in new string?[] { null, permission, "server.view-any-definition" })
        {
            var source = new Source([ServerLifecycleTestRows.Engine(hadr: false)]);
            var collector = Collector(source); var request = Request(major: major, missingPermission: missing);
            var result = await collector.CollectAsync(request, CancellationToken.None);
            Validator.Validate(collector.Manifest, request, result);
            Assert.Equal(missing is null ? CollectorRunOutcome.Succeeded : CollectorRunOutcome.PermissionDenied, result.Outcome);
            Assert.Equal(missing is null ? 1 : 0, source.Calls);
            if (missing is null) Assert.Equal(Assets.GetQuery(major), source.Query);
            else { Assert.Equal(CollectorRunReason.RequiredPermissionMissing, result.Reason); Assert.Empty(result.Payload.Metrics); Assert.Null(result.Payload.OperationalHealth); }
        }
    }

    [Theory]
    [InlineData(14, SqlServerPlatform.Windows, SqlServerEngineEdition.Enterprise)]
    [InlineData(18, SqlServerPlatform.Windows, SqlServerEngineEdition.Enterprise)]
    [InlineData(16, SqlServerPlatform.Linux, SqlServerEngineEdition.Enterprise)]
    [InlineData(16, SqlServerPlatform.Windows, SqlServerEngineEdition.Express)]
    public async Task UnsupportedVersionPlatformOrEditionNeverCallsTheSource(int major, SqlServerPlatform platform, SqlServerEngineEdition edition)
    {
        var source = new Source([]); var collector = Collector(source);
        var request = Request(major: major, platform: platform, edition: edition);
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.Unsupported, result.Outcome);
        Assert.Equal(CollectorRunReason.TargetUnsupported, result.Reason);
        Assert.Equal(0, source.Calls); Assert.Null(result.Payload.OperationalHealth);
    }

    [Fact]
    public async Task MissingCapabilityAndStaleProfileAreRejectedBeforeReading()
    {
        foreach (var request in new[] { Request(missingCapability: true), Request(missingIdentity: true) })
        {
            var source = new Source([]); var collector = Collector(source);
            var result = await collector.CollectAsync(request, CancellationToken.None);
            Assert.Equal(0, source.Calls); Assert.Null(result.Payload.OperationalHealth);
            Assert.Equal(0, result.Accounting.SourceRowsRead); Assert.Equal(0, result.Accounting.OutputItemsProduced);
            Assert.Equal(request.CapabilityProfile.ServerIdentity is null || request.CapabilityProfile.TargetRevision != request.TargetRevision
                ? CollectorRunOutcome.OutputInvalid : CollectorRunOutcome.Unsupported, result.Outcome);
        }
    }

    [Fact]
    public void ExecutionRequestRejectsMismatchedCapabilityRevision()
    {
        Assert.Throws<ArgumentException>(() => Request(staleRevision: true));
    }

    [Theory]
    [InlineData(-2, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded)]
    [InlineData(916, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing)]
    [InlineData(11001, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure)]
    [InlineData(1205, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure)]
    [InlineData(18456, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure)]
    public async Task SourceFailuresKeepSharedClassificationAndEmptyAccounting(int error, CollectorRunOutcome outcome, CollectorRunReason reason)
    {
        var collector = Collector(new Source([]) { Failure = TestSqlErrors.Create(error) }); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(reason, result.Reason);
        Assert.Null(result.Payload.OperationalHealth);
        Assert.Equal(0, result.Accounting.SourceRowsRead); Assert.Equal(0, result.Accounting.OutputItemsProduced);
        Assert.Equal(0, result.Accounting.ResponseBytes); Assert.Equal(0, result.Accounting.OutputBytes);
        Assert.False(result.Loss.HasLoss);
    }

    [Fact]
    public async Task MalformedOrMissingHeaderCannotPublishAValidPrefix()
    {
        object?[] invalid = ServerLifecycleTestRows.Member(); invalid[12] = "invented-role";
        foreach (object?[][] rows in new[] { Array.Empty<object?[]>(), new[] { ServerLifecycleTestRows.Engine(), invalid } })
        {
            var collector = Collector(new Source(rows)); var request = Request();
            var result = await collector.CollectAsync(request, CancellationToken.None);
            Validator.Validate(collector.Manifest, request, result);
            Assert.Equal(CollectorRunOutcome.OutputInvalid, result.Outcome);
            Assert.Equal(CollectorRunReason.OutputValidationFailed, result.Reason);
            Assert.Equal(CollectorLossKind.OutputValidationFailure, result.Loss.Kind);
            Assert.Null(result.Payload.OperationalHealth); Assert.Equal(0, result.Accounting.OutputItemsProduced);
        }
    }

    [Fact]
    public async Task RowAndByteCapsRetainOnlyExplicitlyIncompleteEvidence()
    {
        object?[][] rows = [ServerLifecycleTestRows.Engine(), .. Enumerable.Range(0, 1024).Select(index =>
        {
            Guid replica = Guid.Parse("00000000-0000-4000-8000-" + (index + 1).ToString("D12", CultureInfo.InvariantCulture));
            object?[] row = ServerLifecycleTestRows.Member(expected: 1024, replica: replica);
            row[11] = Guid.Parse("00000000-0000-4000-8000-000000000001");
            row[12] = index == 0 ? "PRIMARY" : "SECONDARY"; return row;
        })];
        int engineBytes = (await SqlServerLifecycleParser.ParseAsync(Request(),
            new ServerLifecycleTestRows.Reader([ServerLifecycleTestRows.Engine()]), Key, CancellationToken.None)).Snapshot.EstimatedSizeBytes;
        foreach (var source in new[] { new Source(rows), new Source([ServerLifecycleTestRows.Engine(), ServerLifecycleTestRows.Member()]) { ParserByteLimit = engineBytes } })
        {
            var collector = Collector(source); var request = Request();
            var result = await collector.CollectAsync(request, CancellationToken.None);
            Validator.Validate(collector.Manifest, request, result);
            var snapshot = Assert.IsType<ServerLifecycleSnapshot>(result.Payload.OperationalHealth!.Snapshot);
            Assert.Equal(CollectorRunOutcome.Partial, result.Outcome); Assert.True(snapshot.Truncated);
            Assert.Equal(source.ParserByteLimit.HasValue ? CollectorRunReason.ResponseByteLimit : CollectorRunReason.SourceRowLimit, result.Reason);
            Assert.Equal(source.ParserByteLimit.HasValue ? 2 : 1025, result.Accounting.SourceRowsRead);
            Assert.Equal(1 + snapshot.Members.Count, result.Accounting.OutputItemsProduced);
            if (source.ParserByteLimit.HasValue) Assert.Empty(snapshot.Members);
        }
    }

    [Fact]
    public async Task DeadlineCancellationAndCallerCancellationHaveDifferentResults()
    {
        var source = new Source([]) { WaitUntilCancelled = true }; var collector = Collector(source);
        var request = Request(timeout: TimeSpan.FromMilliseconds(100));
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.TimedOut, result.Outcome); Assert.Null(result.Payload.OperationalHealth);
        using var cancellation = new CancellationTokenSource();
        source.WaitUntilCancelled = false; source.OnRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await collector.CollectAsync(request, cancellation.Token));
        int calls = source.Calls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await collector.CollectAsync(request, cancellation.Token));
        Assert.Equal(calls, source.Calls);
    }

    [Fact]
    public async Task ManifestDeadlineAndCommandParametersRemainBounded()
    {
        var source = new Source([ServerLifecycleTestRows.Engine(hadr: false)]); var collector = Collector(source);
        var result = await collector.CollectAsync(Request(timeout: TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome); Assert.Equal(TimeSpan.FromSeconds(5), source.Budget);
        Assert.Equal(CollectorOutputKind.ServerLifecycle, collector.Manifest.OutputKind);
        Assert.Equal(1024, collector.Manifest.Limits.MaxRows); Assert.Equal(1_048_576, collector.Manifest.Limits.MaxResponseBytes);
        using var connection = new SqlConnection();
        using var command = ServerLifecycleQuerySource.CreateCommand(connection, Assets.GetQuery(16), collector.Manifest.Limits, TimeSpan.FromMilliseconds(1250));
        Assert.Equal(2, command.CommandTimeout); Assert.Equal(Assets.GetQuery(16), command.CommandText);
        var parameter = Assert.Single(command.Parameters.Cast<SqlParameter>());
        Assert.Equal("maximum_rows", parameter.ParameterName); Assert.Equal(SqlDbType.Int, parameter.SqlDbType); Assert.Equal(1025, parameter.Value);
    }

    [Fact]
    public async Task ValidatorRejectsForeignTargetRevisionRunSchemaAndSidecars()
    {
        var collector = Collector(new Source([ServerLifecycleTestRows.Engine(hadr: false)])); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        var snapshot = Assert.IsType<ServerLifecycleSnapshot>(result.Payload.OperationalHealth!.Snapshot);
        foreach (var altered in new[]
        {
            new ServerLifecycleSnapshot(new MonitoredInstanceId(Guid.NewGuid()), snapshot.TargetRevision, snapshot.RunId,
                snapshot.SourceObservedAtUtc, snapshot.Engine, false, [], 1, false),
            new ServerLifecycleSnapshot(snapshot.TargetId, new ObservationTargetRevision(snapshot.TargetRevision.Value + 1), snapshot.RunId,
                snapshot.SourceObservedAtUtc, snapshot.Engine, false, [], 1, false),
            new ServerLifecycleSnapshot(snapshot.TargetId, snapshot.TargetRevision, new CollectorRunId(Guid.NewGuid()),
                snapshot.SourceObservedAtUtc, snapshot.Engine, false, [], 1, false),
            new ServerLifecycleSnapshot(snapshot.TargetId, snapshot.TargetRevision, snapshot.RunId,
                snapshot.SourceObservedAtUtc, snapshot.Engine, false, [], 1, false, sourceSchemaVersion: 2),
        })
        {
            var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(altered, 1, altered.EstimatedSizeBytes));
            Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request, Copy(result, payload: payload)));
        }
        var foreign = new HostMetricsPayloadContext(HostIdentityFingerprint.FromOpaqueIdentity("private-foreign-host", Key),
            new HostObservationRevision(1), new HostObservationRevision(1), Key);
        var foreignPayload = new CollectorPayload(operationalHealth: result.Payload.OperationalHealth, hostMetricsContext: foreign);
        Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request, Copy(result, payload: foreignPayload)));
    }

    [Fact]
    public async Task ValidatorRejectsForgedCoverageCountsBytesAndFailedEvidence()
    {
        var collector = Collector(new Source([ServerLifecycleTestRows.Engine(hadr: false)])); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        foreach (var altered in new[]
        {
            Copy(result, payload: CollectorPayload.Empty, accounting: new CollectorRunAccounting(0, 0, 0, 0)),
            Copy(result, accounting: new CollectorRunAccounting(2, 1, result.Accounting.ResponseBytes, result.Accounting.OutputBytes)),
            Copy(result, accounting: new CollectorRunAccounting(1, 1, 0, result.Accounting.OutputBytes)),
            Copy(result, accounting: new CollectorRunAccounting(1, 1, SqlServerLifecycleCollector.MaximumResponseBytes + 1, result.Accounting.OutputBytes)),
        }) Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request, altered));
        var snapshot = Assert.IsType<ServerLifecycleSnapshot>(result.Payload.OperationalHealth!.Snapshot);
        var wrongCount = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, 2, snapshot.EstimatedSizeBytes));
        Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request, Copy(result, payload: wrongCount,
            accounting: new CollectorRunAccounting(1, 2, result.Accounting.ResponseBytes, snapshot.EstimatedSizeBytes))));
        var wrongBytes = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, 1, 0));
        Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request, Copy(result, payload: wrongBytes,
            accounting: new CollectorRunAccounting(1, 1, result.Accounting.ResponseBytes, 0))));
        Assert.Throws<ArgumentException>(() => Copy(result, outcome: CollectorRunOutcome.TransientFailure,
            reason: CollectorRunReason.TransientTargetFailure));
        object?[] partial = ServerLifecycleTestRows.Engine(hadr: false); partial[2] = null;
        var incomplete = await Collector(new Source([partial])).CollectAsync(request, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request,
            Copy(incomplete, outcome: CollectorRunOutcome.Succeeded, reason: CollectorRunReason.Completed, loss: CollectorLossEvidence.None)));
    }

    private static CollectorExecutionResult Copy(CollectorExecutionResult result, CollectorPayload? payload = null,
        CollectorRunAccounting? accounting = null, CollectorRunOutcome? outcome = null, CollectorRunReason? reason = null,
        CollectorLossEvidence? loss = null) => new(result.TargetId, result.TargetRevision, result.CollectorId,
            result.CollectorManifestVersion, result.OutputSchemaVersion, outcome ?? result.Outcome, reason ?? result.Reason,
            payload ?? result.Payload, accounting ?? result.Accounting, loss ?? result.Loss);

    private static SqlServerLifecycleCollector Collector(Source source) => new(Assets, Key, source);
    private static CollectorExecutionRequest Request(int major = 16, SqlServerPlatform platform = SqlServerPlatform.Windows,
        SqlServerEngineEdition edition = SqlServerEngineEdition.Enterprise, string? missingPermission = null,
        bool missingCapability = false, bool staleRevision = false, bool missingIdentity = false, TimeSpan? timeout = null)
    {
        var template = M4TestData.CreateProfile(majorVersion: major, platform: platform, edition: edition);
        var manifest = new SqlServerLifecycleCollector(Assets, Key).Manifest;
        var profile = new CapabilityProfile(template.TargetId, staleRevision ? new ObservationTargetRevision(template.TargetRevision.Value + 1) : template.TargetRevision,
            template.CollectorId, 1, 1, missingIdentity ? null : template.ServerIdentity,
            missingIdentity ? CapabilityDiscoveryOutcome.Unreachable : template.Outcome,
            missingIdentity ? CapabilityDiscoveryReason.NetworkUnreachable : template.Reason,
            missingIdentity ? SqlServerAuthenticationScheme.Unknown : template.AuthenticationScheme,
            !missingIdentity && template.TransportEncrypted, template.IsSysAdmin,
            manifest.RequiredCapabilities.Where(item => !missingCapability || item.Value != "connection.tds")
                .Select(item => new CapabilityEvidence(item, CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)).ToArray(),
            manifest.RequiredPermissions.Where(item => item.ApplicableVersions.Contains(major) && item.PermissionId.Value != missingPermission)
                .Select(item => new PermissionEvidence(item.PermissionId, item.Scope, PermissionEvidenceOutcome.Granted)).ToArray(),
            template.DiscoveryDuration, template.EvidenceBytes, template.CheckedAtUtc, template.ValidUntilUtc);
        var request = M4TestData.CreateExecutionRequest();
        return new CollectorExecutionRequest(request.RunId, request.TargetId, request.TargetRevision, request.ConnectionPolicy,
            profile, request.Attempt, new CollectorExecutionTimeout(timeout ?? request.Timeout.Value));
    }

    private sealed class Source(object?[][] rows) : IServerLifecycleQuerySource
    {
        internal int Calls { get; private set; }
        internal string? Query { get; private set; }
        internal TimeSpan Budget { get; private set; }
        internal int? ParserByteLimit { get; init; }
        internal Exception? Failure { get; init; }
        internal bool WaitUntilCancelled { get; set; }
        internal Action? OnRead { get; set; }
        public async ValueTask<ServerLifecycleParseResult> ReadAsync(CollectorExecutionRequest request, string query,
            CollectorExecutionLimits limits, TimeSpan budget, IdentityFingerprintKey key, CancellationToken cancellationToken)
        {
            Calls++; Query = query; Budget = budget;
            if (Failure is not null) throw Failure;
            if (WaitUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            OnRead?.Invoke();
            return await SqlServerLifecycleParser.ParseAsync(request, new ServerLifecycleTestRows.Reader(rows), key,
                cancellationToken, limits.MaxRows, ParserByteLimit ?? limits.MaxResponseBytes);
        }
    }
}
