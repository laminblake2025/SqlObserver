using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class BackupV2CollectorTests
{
    private static readonly SqlServerBackupV2AssetCatalog Assets = SqlServerBackupV2AssetCatalog.LoadEmbedded();
    private static readonly IdentityFingerprintKey Key = new(new byte[32]);
    private static readonly CollectorOutputValidator Validator = new(SqlServerBackupsV2Collector.OutputContract);

    [Fact]
    public async Task CompleteInventoryAndSlotsPublishOneBoundedV2EnvelopeWithoutAssumingATimeQualification()
    {
        var source = new Source([Header(), Sample()]); var port = new QualificationPort();
        var collector = Collector(source, port); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        var snapshot = Assert.IsType<BackupV2ReadSnapshot>(result.Payload.OperationalHealth!.Snapshot);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, result.Accounting.SourceRowsRead); Assert.Equal(2, result.Accounting.OutputItemsProduced);
        Assert.Equal(request.RunId, snapshot.RunId); Assert.Equal(M4TestData.RepositoryTime, snapshot.ReceivedAtUtc);
        Assert.Null(Assert.Single(Assert.Single(snapshot.Databases).Samples).Freshness.FinishUtc);
        Assert.Equal(request.TargetId, port.Target); Assert.Equal(request.TargetRevision, port.Revision);
        Assert.Equal(source.Token, port.Token); Assert.Equal(Assets.GetQuery(16), source.Query);
        Assert.Equal(request.Timeout.Value, source.Budget);
    }

    [Fact]
    public async Task QualifiedTimePassesThroughThePortAndRetainsItsRevision()
    {
        var port = new QualificationPort { Value = new(M4TestData.TargetId, M4TestData.TargetRevision, 9, TimeZoneInfo.Utc,
            "UTC", M4TestData.RepositoryTime.AddDays(-35), M4TestData.RepositoryTime.AddDays(1)) };
        var collector = Collector(new Source([Header(), Sample()]), port); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        var sample = Assert.Single(Assert.Single(((BackupV2ReadSnapshot)result.Payload.OperationalHealth!.Snapshot).Databases).Samples);
        Assert.Equal(9, sample.TimeResolution.QualificationRevision);
        Assert.Equal(BackupFinishTimeState.Resolved, sample.TimeResolution.State);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("rows")]
    [InlineData("bytes")]
    public async Task IncompleteScansAndWithheldLimitsCarryPreciseCoverageAndLoss(string kind)
    {
        object?[] unknown = Header(); unknown[2] = null; unknown[8] = false;
        var source = new Source(kind == "empty" ? [] : kind == "unknown" ? [unknown] : [Header(), Sample()])
        { RowLimit = kind == "rows" ? 1 : 3072, ByteLimit = kind == "bytes" ? 2048 : 4_194_304 };
        var collector = Collector(source); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        var snapshot = (BackupV2ReadSnapshot)result.Payload.OperationalHealth!.Snapshot;
        if (kind is "rows" or "bytes")
        {
            Assert.Empty(snapshot.Databases); Assert.Equal(snapshot.SourceRowsRead, result.Loss.MinimumLostItems);
            Assert.Equal(BackupFreshnessCoverage.Truncated, snapshot.Coverage);
        }
        else Assert.Equal(CollectorRunReason.VisibilityIncomplete, result.Reason);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public async Task EveryMajorRequiresTheCorrectServerAndMsdbScopeAndConditionalAgGrant(int major)
    {
        string server = major == 15 ? "server.view-state" : "server.view-performance-state";
        foreach (string? missing in new string?[] { null, server, "server.view-any-database", "msdb.backupset.select" })
        {
            var source = new Source([Header()]); var port = new QualificationPort();
            var collector = Collector(source, port); var request = Request(major, missingPermission: missing);
            var result = await collector.CollectAsync(request, CancellationToken.None);
            Validator.Validate(collector.Manifest, request, result);
            Assert.Equal(missing is null ? CollectorRunOutcome.Succeeded : CollectorRunOutcome.PermissionDenied, result.Outcome);
            Assert.Equal(missing is null ? 1 : 0, source.Calls); Assert.Equal(source.Calls, port.Calls);
        }
        var agSource = new Source([Header()]); var agCollector = Collector(agSource);
        var agRequest = Request(major, ag: true, missingPermission: "server.view-any-definition");
        var agResult = await agCollector.CollectAsync(agRequest, CancellationToken.None);
        Validator.Validate(agCollector.Manifest, agRequest, agResult);
        Assert.Equal(major == 15 ? CollectorRunOutcome.PermissionDenied : CollectorRunOutcome.Succeeded, agResult.Outcome);
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("sysadmin")]
    [InlineData("capability")]
    [InlineData("stale")]
    [InlineData("platform")]
    [InlineData("engine")]
    [InlineData("version")]
    [InlineData("msdb-scope")]
    public async Task UnsupportedOrUnqualifiedRequestsDoNotCallEitherPort(string defect)
    {
        var source = new Source([Header()]); var port = new QualificationPort(); var collector = Collector(source, port);
        var request = Request(defect: defect);
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.NotEqual(CollectorRunOutcome.Succeeded, result.Outcome); Assert.Null(result.Payload.OperationalHealth);
        Assert.Equal(0, source.Calls); Assert.Equal(0, port.Calls);
    }

    [Theory]
    [InlineData(51005, CollectorRunOutcome.PermissionDenied)]
    [InlineData(51006, CollectorRunOutcome.PermissionDenied)]
    [InlineData(51007, CollectorRunOutcome.OutputInvalid)]
    [InlineData(916, CollectorRunOutcome.PermissionDenied)]
    [InlineData(-2, CollectorRunOutcome.TimedOut)]
    [InlineData(11001, CollectorRunOutcome.TransientFailure)]
    [InlineData(18456, CollectorRunOutcome.PermanentFailure)]
    public async Task SqlFailuresPublishNoPrefixAndKeepTheSharedFailureClassifications(int number, CollectorRunOutcome outcome)
    {
        var collector = Collector(new Source([]) { Failure = TestSqlErrors.Create(number) }); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result);
        Assert.Equal(outcome, result.Outcome); Assert.Null(result.Payload.OperationalHealth);
        Assert.Equal(0, result.Accounting.SourceRowsRead);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneDeadlineCoversQualificationAndSourceAndExternalCancellationPropagates(bool inQualification)
    {
        var source = new Source([]) { Wait = !inQualification }; var port = new QualificationPort { Wait = inQualification };
        var collector = Collector(source, port); var request = Request(timeout: TimeSpan.FromSeconds(1));
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result); Assert.Equal(CollectorRunOutcome.TimedOut, result.Outcome);
        using var external = new CancellationTokenSource(); external.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await collector.CollectAsync(request, external.Token));
    }

    [Fact]
    public async Task WrongQualificationRevisionIsRejectedBeforeSqlAndMalformedRowsBecomeInvalidOutput()
    {
        var source = new Source([Header()]);
        var port = new QualificationPort { Value = new(M4TestData.TargetId, new(8), 1, TimeZoneInfo.Utc, "UTC",
            M4TestData.RepositoryTime.AddDays(-35), M4TestData.RepositoryTime.AddDays(1)) };
        var collector = Collector(source, port); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result); Assert.Equal(CollectorRunOutcome.OutputInvalid, result.Outcome);
        Assert.Equal(0, source.Calls);
        object?[] bad = Header(); bad[9] = "not-a-model";
        collector = Collector(new Source([bad])); result = await collector.CollectAsync(request, CancellationToken.None);
        Validator.Validate(collector.Manifest, request, result); Assert.Equal(CollectorRunOutcome.OutputInvalid, result.Outcome);
    }

    [Fact]
    public async Task ValidatorRejectsMissingEnvelopeAccountingDriftAndCrossVersionPayloads()
    {
        var collector = Collector(new Source([Header(), Sample()])); var request = Request();
        var result = await collector.CollectAsync(request, CancellationToken.None);
        var snapshot = (BackupV2ReadSnapshot)result.Payload.OperationalHealth!.Snapshot;
        foreach (var invalid in new[]
        {
            Copy(result, CollectorPayload.Empty, new(2, 0, 0, 0)),
            Copy(result, accounting: new(3, 2, result.Accounting.ResponseBytes, result.Accounting.OutputBytes)),
            Copy(result, new(operationalHealth: new(snapshot, 1, snapshot.EstimatedSizeBytes)),
                new(2, 1, result.Accounting.ResponseBytes, result.Accounting.OutputBytes)),
            Copy(result, new(operationalHealth: new(snapshot, 2, 0)), new(2, 2, 0, 0)),
        }) Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request, invalid));
        Assert.Throws<ArgumentException>(() => Copy(result, outcome: CollectorRunOutcome.Partial));
        var v1 = new SqlServerBackupsStatusCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        Assert.Throws<InvalidDataException>(() => new CollectorOutputValidator(M9Manifest.OutputContract(2048))
            .Validate(v1.Manifest, request, Copy(result, version: 1)));
        var legacy = new BackupStatusSnapshot(request.TargetId, request.TargetRevision, request.RunId,
            M4TestData.RepositoryTime, OperationalObservationState.Complete, [], 0, false);
        Assert.Throws<InvalidDataException>(() => Validator.Validate(collector.Manifest, request,
            Copy(result, new(operationalHealth: new(legacy, 0, 256)), new(0, 0, 256, 256))));
    }

    [Fact]
    public void DomainRejectsForgedCompleteCoverageCrossRunDataAndResolvedTimeWithoutProvenance()
    {
        var run = new BackupV2RunIdentity(M4TestData.TargetId, M4TestData.TargetRevision, new(Guid.NewGuid()));
        var context = new BackupV2DatabaseContext(new string('a', 64), new string('b', 64), new string('c', 64),
            null, null, null, true, BackupRecoveryModel.Full, "ONLINE", BackupFreshnessScope.Standalone, null, null,
            M4TestData.RepositoryTime, "UTC", M4TestData.RepositoryTime.DateTime.AddDays(-35), false);
        var database = new BackupV2DatabaseRead(context, [], run, BackupFreshnessCoverage.Complete);
        Assert.Throws<ArgumentException>(() => new BackupV2DatabaseRead(context with { IdentityKnown = false }, [], run, BackupFreshnessCoverage.Complete));
        Assert.Throws<ArgumentException>(() => new BackupV2ReadSnapshot(run with { RunId = new(Guid.NewGuid()) }, M4TestData.RepositoryTime,
            context.SourceObservedAtUtc, "UTC", context.HistoryLowerBoundLocal, [database], 1, BackupFreshnessCoverage.Complete));
        var unknown = new BackupV2DatabaseRead(context with { IdentityKnown = false }, [], run, BackupFreshnessCoverage.Partial);
        Assert.Throws<ArgumentException>(() => new BackupV2ReadSnapshot(run with { RunId = new(Guid.NewGuid()) }, M4TestData.RepositoryTime,
            context.SourceObservedAtUtc, "UTC", context.HistoryLowerBoundLocal, [unknown], 1, BackupFreshnessCoverage.Partial));
        var freshness = new BackupFreshnessSample(BackupKind.Full, false, M4TestData.RepositoryTime, M4TestData.RepositoryTime.DateTime, true, false, true);
        Assert.Throws<ArgumentException>(() => new BackupV2SampleRead(freshness, M4TestData.RepositoryTime.DateTime,
            0, 123, context.Fork, context.Fork!, new(BackupFinishTimeState.Resolved, M4TestData.RepositoryTime, null)));
    }

    [Fact]
    public void ManifestParityAndProductionCommandIncludeBothBoundedLookaheads()
    {
        var collector = Collector(new Source([])); using var manifest = JsonDocument.Parse(Assets.Manifest); var root = manifest.RootElement;
        Assert.Equal(root.GetProperty("collectorVersion").GetInt32(), collector.Manifest.ManifestVersion.Value);
        Assert.Equal(root.GetProperty("requiredCapabilities").EnumerateArray().Select(item => item.GetString()), collector.Manifest.RequiredCapabilities.Select(item => item.Value));
        foreach (int major in new[] { 15, 16, 17 }) Assert.Equal(root.GetProperty("requiredPermissionsByMajor").GetProperty(major.ToString(CultureInfo.InvariantCulture))
            .EnumerateArray().Select(item => item.GetString()), collector.Manifest.RequiredPermissions.Where(item => item.ApplicableVersions.Contains(major)).Select(item => item.PermissionId.Value));
        using var connection = new SqlConnection();
        using var command = BackupV2QuerySource.CreateCommand(connection, Assets.GetQuery(16), collector.Manifest.Limits, TimeSpan.FromMilliseconds(1250));
        Assert.Equal(2, command.CommandTimeout); Assert.Equal(2, command.Parameters.Count);
        Assert.Equal(3073, command.Parameters["maximum_rows"].Value); Assert.Equal(513, command.Parameters["maximum_databases"].Value);
        Assert.All(command.Parameters.Cast<SqlParameter>(), parameter => Assert.Equal(SqlDbType.Int, parameter.SqlDbType));
    }

    [Fact]
    public void AbsoluteRowLookaheadPassesValidationWhileEmptyScansCannotInventSourceContext()
    {
        var collector = Collector(new Source([])); var request = Request();
        var run = new BackupV2RunIdentity(request.TargetId, request.TargetRevision, request.RunId);
        var snapshot = new BackupV2ReadSnapshot(run, M4TestData.RepositoryTime, M4TestData.RepositoryTime, "UTC",
            M4TestData.RepositoryTime.DateTime.AddDays(-35), [], 3073, BackupFreshnessCoverage.Truncated);
        var payload = new CollectorPayload(operationalHealth: new(snapshot, 0, snapshot.EstimatedSizeBytes));
        var result = new CollectorExecutionResult(request.TargetId, request.TargetRevision, collector.Manifest.Id, 2, 2,
            CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit, payload, new(3073, 0, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            new(CollectorLossKind.SourceRowLimit, 3073, false));
        Validator.Validate(collector.Manifest, request, result);
        Assert.Throws<ArgumentException>(() => new BackupV2ReadSnapshot(run, M4TestData.RepositoryTime, M4TestData.RepositoryTime,
            null, null, [], 0, BackupFreshnessCoverage.Partial));
    }

    private static CollectorExecutionResult Copy(CollectorExecutionResult original, CollectorPayload? payload = null,
        CollectorRunAccounting? accounting = null, CollectorRunOutcome? outcome = null, int version = 2) =>
        new(original.TargetId, original.TargetRevision, original.CollectorId, version, version, outcome ?? original.Outcome,
            original.Reason, payload ?? original.Payload, accounting ?? original.Accounting, original.Loss);
    private static SqlServerBackupsV2Collector Collector(Source source, QualificationPort? port = null) => new(Assets, Key, source, port ?? new(), new Clock());
    private static object?[] Header() => [0, 5, Digest(1), Digest(2), Digest(3), null, null, null, true, "FULL", "ONLINE", (short)1,
        null, null, null, null, null, null, null, null, null, null, null, null, null, M4TestData.RepositoryTime.DateTime, "UTC", M4TestData.RepositoryTime.DateTime.AddDays(-35), false];
    private static object?[] Sample()
    {
        var row = Header(); row[0] = 1; row[14] = 1; row[15] = false; row[16] = M4TestData.RepositoryTime.DateTime.AddHours(-2);
        row[17] = M4TestData.RepositoryTime.DateTime.AddHours(-1); row[18] = (short)0; row[19] = 123L;
        row[20] = true; row[21] = false; row[22] = true; row[23] = Digest(3); row[24] = Digest(3); return row;
    }
    private static byte[] Digest(byte value) => Enumerable.Repeat(value, 32).ToArray();
    private static CollectorExecutionRequest Request(int major = 16, bool ag = false, string? missingPermission = null,
        string? defect = null, TimeSpan? timeout = null)
    {
        var template = M4TestData.CreateProfile(majorVersion: defect == "version" ? 14 : major,
            platform: defect == "platform" ? SqlServerPlatform.Linux : SqlServerPlatform.Windows,
            edition: defect == "engine" ? SqlServerEngineEdition.AzureSqlDatabase : SqlServerEngineEdition.Express);
        var permissions = new[] { major == 15 ? "server.view-state" : "server.view-performance-state", "server.view-any-database", "msdb.backupset.select" }
            .Concat(ag && major == 15 ? ["server.view-any-definition"] : Array.Empty<string>()).Where(item => item != missingPermission)
            .Select(item => new PermissionEvidence(new(item), item.StartsWith("msdb.", StringComparison.Ordinal) && defect != "msdb-scope"
                ? PermissionEvidenceScope.Database : PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted)).ToArray();
        var profile = new CapabilityProfile(template.TargetId, template.TargetRevision, template.CollectorId, 1, 1, template.ServerIdentity,
            defect is "tls" or "sysadmin" ? CapabilityDiscoveryOutcome.SecurityPolicyRejected : template.Outcome,
            defect == "tls" ? CapabilityDiscoveryReason.TransportNotEncrypted : defect == "sysadmin" ? CapabilityDiscoveryReason.ExcessivePrivilege : template.Reason,
            template.AuthenticationScheme, defect != "tls", defect == "sysadmin",
            Collector(new Source([])).Manifest.RequiredCapabilities.Where(item => defect != "capability" || item.Value != "transport.tls-validated")
                .Select(item => new CapabilityEvidence(item, CapabilityAvailability.Available, CapabilityEvidenceReason.Verified))
                .Concat(ag ? [new CapabilityEvidence(new("feature.availability-groups"), CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)] : Array.Empty<CapabilityEvidence>()).ToArray(),
            permissions, template.DiscoveryDuration, template.EvidenceBytes, template.CheckedAtUtc,
            defect == "stale" ? M4TestData.RepositoryTime : template.ValidUntilUtc);
        var original = M4TestData.CreateExecutionRequest(); return new(original.RunId, original.TargetId, original.TargetRevision,
            original.ConnectionPolicy, profile, original.Attempt, new(timeout ?? TimeSpan.FromSeconds(1)));
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => M4TestData.RepositoryTime.AddTicks(7); }
    private sealed class QualificationPort : IBackupHistoricalZoneQualificationPort
    {
        public int Calls; public MonitoredInstanceId? Target; public ObservationTargetRevision? Revision; public CancellationToken Token;
        public bool Wait; public BackupHistoricalZoneQualification? Value;
        public async ValueTask<BackupHistoricalZoneQualification?> GetAsync(MonitoredInstanceId target, ObservationTargetRevision revision, CancellationToken token)
        { Calls++; Target = target; Revision = revision; Token = token; if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, token); return Value; }
    }
    private sealed class Source(object?[][] rows) : IBackupV2QuerySource
    {
        public int Calls; public string? Query; public TimeSpan Budget; public CancellationToken Token;
        public int RowLimit = 3072, ByteLimit = 4_194_304; public bool Wait; public Exception? Failure;
        public async ValueTask<BackupV2ParseResult> ReadAsync(CollectorExecutionRequest request, string query, CollectorExecutionLimits limits,
            TimeSpan budget, IdentityFingerprintKey key, BackupHistoricalZoneQualification? qualification, TimeProvider clock, CancellationToken token)
        {
            Calls++; Query = query; Budget = budget; Token = token; if (Failure is not null) throw Failure;
            if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            var now = clock.GetUtcNow(); now = now.AddTicks(-now.Ticks % TimeSpan.TicksPerMicrosecond);
            return await SqlServerBackupV2Parser.ParseAsync(request, new Reader(rows), key, now, token, qualification, RowLimit, 512, ByteLimit);
        }
    }
    private sealed class Reader(object?[][] rows) : IOperationalHealthRowReader
    {
        private int index = -1; public int FieldCount => 29;
        public bool IsDBNull(int ordinal) => rows[index][ordinal] is null;
        public byte[] GetBinary(int ordinal) => (byte[])rows[index][ordinal]!;
        public bool GetBoolean(int ordinal) => (bool)rows[index][ordinal]!;
        public DateTime GetDateTime(int ordinal) => (DateTime)rows[index][ordinal]!;
        public Guid GetGuid(int ordinal) => (Guid)rows[index][ordinal]!;
        public short GetInt16(int ordinal) => (short)rows[index][ordinal]!;
        public int GetInt32(int ordinal) => (int)rows[index][ordinal]!;
        public long GetInt64(int ordinal) => (long)rows[index][ordinal]!;
        public string GetString(int ordinal) => (string)rows[index][ordinal]!;
        public ValueTask<bool> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(++index < rows.Length); }
    }
}
