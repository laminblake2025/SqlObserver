using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class BackupV2ParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly IdentityFingerprintKey Key = new(new byte[32]);

    [Fact]
    public async Task InventoryFiveSlotsAndExplicitTimeQualificationFeedTheRealAssessment()
    {
        object?[][] rows = [Header(), Sample(1, false), Sample(1, true), Sample(2, false), Sample(3, false), Sample(3, true), Header(6)];
        var read = await Parse(rows, qualification: Qualification());
        Assert.Equal(7, read.Snapshot.SourceRowsRead);
        Assert.Equal(BackupFreshnessCoverage.Complete, read.Snapshot.Coverage);
        Assert.False(read.Loss.HasLoss);
        var database = read.Snapshot.Databases[0];
        Assert.Equal(5, database.Samples.Count);
        Assert.Empty(read.Snapshot.Databases[1].Samples);
        Assert.All(database.Samples, sample =>
        {
            Assert.Equal(BackupFinishTimeState.Resolved, sample.TimeResolution.State);
            Assert.Equal(7, sample.TimeResolution.QualificationRevision);
            Assert.Equal(123L, sample.SizeBytes);
            Assert.Equal(database.RecoveryForkFingerprint, sample.LastRecoveryForkFingerprint);
        });
        var policy = Policy(database.DatabaseFingerprint!);
        var assessment = BackupFreshnessEvaluator.Assess(policy, database.FreshnessEvidence!, Now);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, assessment.Full.Status);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, assessment.Log.Status);
        Assert.Equal(BackupFreshnessReason.BackupNotObserved,
            BackupFreshnessEvaluator.Assess(Policy(read.Snapshot.Databases[1].DatabaseFingerprint!),
                read.Snapshot.Databases[1].FreshnessEvidence!, Now).Full.Reason);
        Assert.Throws<NotSupportedException>(() => ((IList<BackupV2SampleRead>)database.Samples).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<BackupV2DatabaseRead>)read.Snapshot.Databases).Clear());
    }

    [Fact]
    public async Task CurrentZoneAndStartOffsetDoNotProduceUnqualifiedUtcOrAHealthyVerdict()
    {
        var database = Assert.Single((await Parse([Header(), Sample(1, false)])).Snapshot.Databases);
        var sample = Assert.Single(database.Samples);
        Assert.Null(sample.Freshness.FinishUtc);
        Assert.Equal(BackupFinishTimeState.QualificationMissing, sample.TimeResolution.State);
        Assert.Equal(DateTimeKind.Unspecified, sample.Freshness.SourceLocalFinish!.Value.Kind);
        Assert.Equal(BackupFreshnessReason.FinishTimeUnknown,
            BackupFreshnessEvaluator.Assess(Policy(database.DatabaseFingerprint!), database.FreshnessEvidence!, Now).Full.Reason);
    }

    [Fact]
    public async Task PartialAgIdentityAndLocalSecondaryCannotClaimFleetBackupCoverage()
    {
        object?[] partial = Header(); partial[7] = Digest(7); partial[11] = (short)2;
        var incomplete = await Parse([partial]);
        Assert.Equal(BackupFreshnessCoverage.Partial, incomplete.Snapshot.Coverage);
        Assert.Equal(CollectorLossKind.VisibilityIncomplete, incomplete.Loss.Kind);
        object?[] ag = Header(); ag[5] = Digest(5); ag[6] = Digest(6); ag[7] = Digest(7);
        ag[11] = (short)2; ag[12] = "SECONDARY"; ag[13] = false;
        object?[] sample = Sample(1, true);
        foreach (int ordinal in new[] { 5, 6, 7, 11, 12, 13 }) sample[ordinal] = ag[ordinal];
        var database = Assert.Single((await Parse([ag, sample], qualification: Qualification())).Snapshot.Databases);
        Assert.Equal(BackupFreshnessCoverage.Complete, database.Coverage);
        Assert.Equal(BackupFreshnessScope.AvailabilityGroupLocalOnly, database.Scope);
        Assert.False(database.LocalReplicaPreferred);
        Assert.Equal(BackupFreshnessReason.OtherReplicaEvidenceRequired,
            BackupFreshnessEvaluator.Assess(Policy(database.DatabaseFingerprint!), database.FreshnessEvidence!, Now).Full.Reason);
    }

    [Theory]
    [InlineData("unknown-guid")]
    [InlineData("duplicate-guid")]
    [InlineData("missing-family")]
    [InlineData("offline")]
    [InlineData("unknown-model")]
    [InlineData("unclassified")]
    public async Task IncompleteInventoryIsRetainedWithoutInventingAStablePolicyIdentity(string defect)
    {
        object?[] header = Header();
        switch (defect)
        {
            case "unknown-guid": header[2] = null; header[8] = false; break;
            case "duplicate-guid": header[8] = false; break;
            case "missing-family": header[3] = null; break;
            case "offline": header[10] = "OFFLINE"; break;
            case "unknown-model": header[9] = null; break;
            case "unclassified": header[28] = true; break;
        }
        var read = await Parse([header]);
        var database = Assert.Single(read.Snapshot.Databases);
        Assert.Equal(BackupFreshnessCoverage.Partial, database.Coverage);
        if (defect is "unknown-guid" or "duplicate-guid")
        { Assert.Null(database.DatabaseFingerprint); Assert.Null(database.FreshnessEvidence); }
        else Assert.Equal(BackupFreshnessReason.SourceIncomplete,
            BackupFreshnessEvaluator.Assess(Policy(database.DatabaseFingerprint!), database.FreshnessEvidence!, Now).Full.Reason);
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("databases")]
    [InlineData("bytes")]
    public async Task LimitLookaheadWithholdsTheWholePrefixAndDisclosesLoss(string limit)
    {
        var reader = new SequentialReader([Header(), Sample(1, false), Header(6), Header(7)]);
        var result = await SqlServerBackupV2Parser.ParseAsync(M4TestData.CreateExecutionRequest(), reader, Key, Now,
            CancellationToken.None, maximumRows: limit == "rows" ? 1 : 3072,
            maximumDatabases: limit == "databases" ? 1 : 512, maximumResponseBytes: limit == "bytes" ? 2048 : 4_194_304);
        Assert.Empty(result.Snapshot.Databases);
        Assert.Equal(BackupFreshnessCoverage.Truncated, result.Snapshot.Coverage);
        Assert.Equal(limit == "bytes" ? CollectorLossKind.ResponseByteLimit : CollectorLossKind.SourceRowLimit, result.Loss.Kind);
        Assert.Equal(reader.RowsRead, result.Snapshot.SourceRowsRead);
        Assert.Equal(reader.RowsRead, result.Loss.MinimumLostItems);
        Assert.InRange(reader.RowsRead, 1, 3);
    }

    [Theory]
    [InlineData("orphan")]
    [InlineData("duplicate-slot")]
    [InlineData("unordered")]
    [InlineData("header-sample")]
    [InlineData("context-drift")]
    [InlineData("clock-drift")]
    [InlineData("zone-drift")]
    [InlineData("lookback-drift")]
    [InlineData("fork")]
    [InlineData("missing-first-fork")]
    [InlineData("copy-diff")]
    [InlineData("unknown-guid-sample")]
    [InlineData("short-digest")]
    [InlineData("invalid-kind")]
    [InlineData("invalid-state")]
    [InlineData("invalid-model")]
    [InlineData("invalid-offset")]
    [InlineData("invalid-size")]
    [InlineData("ag-standalone")]
    [InlineData("preference-without-ag")]
    [InlineData("duplicate-known-guid")]
    [InlineData("out-of-history")]
    [InlineData("missing-clock")]
    [InlineData("offset-bearing-time")]
    [InlineData("oversized-token")]
    [InlineData("non-microsecond-clock")]
    public async Task MalformedStreamsCannotPublishAPrefix(string defect)
    {
        object?[] header = Header(), sample = Sample(1, false);
        object?[][] rows = [header, sample];
        switch (defect)
        {
            case "orphan": rows = [sample]; break;
            case "duplicate-slot": rows = [header, sample, (object?[])sample.Clone()]; break;
            case "unordered": rows = [Header(6), header]; break;
            case "header-sample": header[14] = 1; break;
            case "context-drift": sample[10] = "OFFLINE"; break;
            case "clock-drift": sample[25] = Now.DateTime.AddSeconds(1); break;
            case "zone-drift": sample[26] = "Other Zone"; break;
            case "lookback-drift": sample[27] = Now.DateTime.AddDays(-34); break;
            case "fork": sample[24] = Digest(9); break;
            case "missing-first-fork": sample[23] = null; break;
            case "copy-diff": sample[14] = 2; sample[15] = true; break;
            case "unknown-guid-sample": header[8] = false; sample[8] = false; break;
            case "short-digest": header[2] = new byte[31]; break;
            case "invalid-kind": sample[14] = 4; break;
            case "invalid-state": header[10] = "UNAVAILABLE"; break;
            case "invalid-model": header[9] = "CUSTOM"; break;
            case "invalid-offset": sample[18] = (short)1; break;
            case "invalid-size": sample[19] = -1L; break;
            case "ag-standalone": header[5] = Digest(5); break;
            case "preference-without-ag": header[13] = true; break;
            case "duplicate-known-guid": object?[] other = Header(6); other[2] = header[2]; rows = [header, other]; break;
            case "out-of-history": sample[17] = Now.DateTime.AddDays(-36); break;
            case "missing-clock": header[25] = null; break;
            case "offset-bearing-time": sample[17] = Now.UtcDateTime; break;
            case "oversized-token": header[26] = new string('x', 129); break;
            case "non-microsecond-clock": header[25] = Now.DateTime.AddTicks(1); break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(rows));
    }

    [Fact]
    public async Task TypedKeyedIdentitiesBindTheTargetRevisionAndIdentityClass()
    {
        object?[] header = Header(); header[3] = header[2]; header[4] = header[2];
        var first = Assert.Single((await Parse([header])).Snapshot.Databases);
        var changedKey = Assert.Single((await Parse([header], key: new IdentityFingerprintKey(Enumerable.Repeat((byte)1, 32).ToArray()))).Snapshot.Databases);
        var changedRevision = Assert.Single((await Parse([header], request: Request(M4TestData.TargetId, new(8)))).Snapshot.Databases);
        var changedTarget = Assert.Single((await Parse([header], request: Request(new MonitoredInstanceId(Guid.NewGuid()), M4TestData.TargetRevision))).Snapshot.Databases);
        Assert.Equal(64, first.DatabaseFingerprint!.Length);
        Assert.NotEqual(Convert.ToHexString((byte[])header[2]!).ToLowerInvariant(), first.DatabaseFingerprint);
        Assert.NotEqual(first.DatabaseFingerprint, first.FamilyFingerprint);
        Assert.NotEqual(first.DatabaseFingerprint, first.RecoveryForkFingerprint);
        Assert.NotEqual(first.DatabaseFingerprint, changedKey.DatabaseFingerprint);
        Assert.NotEqual(first.DatabaseFingerprint, changedRevision.DatabaseFingerprint);
        Assert.NotEqual(first.DatabaseFingerprint, changedTarget.DatabaseFingerprint);
    }

    [Fact]
    public async Task SourceClockAndReceiptTimeRemainDistinctAndFutureBackupsStayVisible()
    {
        object?[] sample = Sample(1, false); sample[16] = Now.DateTime.AddMinutes(1); sample[17] = Now.DateTime.AddMinutes(2);
        var read = await Parse([Header(), sample], qualification: Qualification(), receipt: Now.AddSeconds(3));
        Assert.Equal(Now, read.Snapshot.SourceObservedAtUtc); Assert.Equal(Now.AddSeconds(3), read.Snapshot.ReceivedAtUtc);
        var database = Assert.Single(read.Snapshot.Databases);
        Assert.Equal(BackupFreshnessReason.FutureSourceTime,
                BackupFreshnessEvaluator.Assess(Policy(database.DatabaseFingerprint!), database.FreshnessEvidence!, Now.AddSeconds(3)).Full.Reason);
    }

    [Fact]
    public async Task NullableMetadataUnknownOffsetAndKnownFaultsSurviveTheReadBoundary()
    {
        object?[] sample = Sample(1, false);
        sample[18] = null; sample[19] = null; sample[20] = false; sample[21] = true; sample[22] = null; sample[23] = null;
        var database = Assert.Single((await Parse([Header(), sample], qualification: Qualification())).Snapshot.Databases);
        var recorded = Assert.Single(database.Samples);
        Assert.Equal(BackupFinishTimeState.RecordedOffsetUnknown, recorded.TimeResolution.State);
        Assert.Null(recorded.SizeBytes); Assert.Null(recorded.FirstRecoveryForkFingerprint);
        Assert.Null(recorded.Freshness.MetadataComplete);
        var assessment = BackupFreshnessEvaluator.Assess(Policy(database.DatabaseFingerprint!), database.FreshnessEvidence!, Now);
        Assert.Equal(BackupFreshnessReason.FinishTimeUnknown, assessment.Full.Reason);
        Assert.Contains(assessment.ObservedIssues, issue => issue.Reason == BackupFreshnessReason.BackupDamaged);
        Assert.Contains(assessment.ObservedIssues, issue => issue.Reason == BackupFreshnessReason.ChecksumMissing);
    }

    [Fact]
    public async Task SimpleRecoveryAndMissingFinishKeepTheirIndependentAssessmentMeanings()
    {
        object?[] header = Header(), sample = Sample(1, false);
        header[9] = "SIMPLE"; sample[9] = "SIMPLE"; sample[17] = null;
        var database = Assert.Single((await Parse([header, sample], qualification: Qualification())).Snapshot.Databases);
        Assert.Equal(BackupFinishTimeState.FinishMissing, Assert.Single(database.Samples).TimeResolution.State);
        var assessment = BackupFreshnessEvaluator.Assess(Policy(database.DatabaseFingerprint!), database.FreshnessEvidence!, Now);
        Assert.Equal(BackupFreshnessReason.FinishTimeUnknown, assessment.Full.Reason);
        Assert.Equal(BackupFreshnessStatus.NotRequired, assessment.Log.Status);
    }

    [Fact]
    public async Task ExplicitLimitsAndHistoricalQualificationMustMatchTheRequest()
    {
        var request = M4TestData.CreateExecutionRequest();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await SqlServerBackupV2Parser.ParseAsync(
            request, new SequentialReader([]), Key, Now, CancellationToken.None, maximumRows: 3073));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await SqlServerBackupV2Parser.ParseAsync(
            request, new SequentialReader([]), Key, Now, CancellationToken.None, maximumDatabases: 513));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await SqlServerBackupV2Parser.ParseAsync(
            request, new SequentialReader([]), Key, Now, CancellationToken.None, maximumResponseBytes: 4_194_305));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Parse([], qualification: Qualification(),
            request: Request(M4TestData.TargetId, new(8))));
    }

    [Fact]
    public async Task EmptyScanIsIncompleteAndCancellationAndWrongShapeAreRejected()
    {
        var empty = await Parse([]);
        Assert.Equal(BackupFreshnessCoverage.Partial, empty.Snapshot.Coverage);
        Assert.Equal(CollectorLossKind.VisibilityIncomplete, empty.Loss.Kind);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await SqlServerBackupV2Parser.ParseAsync(
            M4TestData.CreateExecutionRequest(), new SequentialReader([Header()]), Key, Now, cancelled.Token));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await SqlServerBackupV2Parser.ParseAsync(
            M4TestData.CreateExecutionRequest(), new SequentialReader([Header()], 28), Key, Now, CancellationToken.None));
    }

    [Fact]
    public async Task ResponseAccountingCoversEscapedContextAndDerivedEvidenceWithoutKeepingSourceIdentities()
    {
        object?[] header = Header(), sample = Sample(1, false);
        header[26] = new string('\u2603', 128); sample[26] = header[26];
        var result = await Parse([header, sample]);
        byte[] serialized = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result.Snapshot);
        Assert.InRange(serialized.Length, 1, result.ResponseBytes);
        string json = System.Text.Encoding.UTF8.GetString(serialized);
        Assert.DoesNotContain("catalog_database_id", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString((byte[])header[2]!).ToLowerInvariant(), json, StringComparison.OrdinalIgnoreCase);
    }

    private static ValueTask<BackupV2ParseResult> Parse(object?[][] rows, BackupHistoricalZoneQualification? qualification = null,
        IdentityFingerprintKey? key = null, CollectorExecutionRequest? request = null, DateTimeOffset? receipt = null) =>
        SqlServerBackupV2Parser.ParseAsync(request ?? M4TestData.CreateExecutionRequest(), new SequentialReader(rows),
            key ?? Key, receipt ?? Now, CancellationToken.None, qualification);

    private static BackupHistoricalZoneQualification Qualification() => new(M4TestData.TargetId, M4TestData.TargetRevision,
        7, TimeZoneInfo.Utc, "UTC", Now.AddDays(-40), Now.AddDays(1));
    private static BackupFreshnessPolicy Policy(string database) => new(M4TestData.TargetId, M4TestData.TargetRevision,
        database, 1, true, TimeSpan.FromDays(2), TimeSpan.FromDays(2), TimeSpan.FromMinutes(5), FullBackupRequirement.FullImage, true);
    private static byte[] Digest(byte value) => Enumerable.Repeat(value, 32).ToArray();
    private static object?[] Header(int id = 5) =>
    [
        0, id, Digest((byte)id), Digest(3), Digest(4), null, null, null, true, "FULL", "ONLINE", (short)1,
        null, null, null, null, null, null, null, null, null, null, null, null, null,
        Now.DateTime, "UTC", Now.DateTime.AddDays(-35), false,
    ];
    private static object?[] Sample(int kind, bool copyOnly)
    {
        object?[] row = Header(); row[0] = 1; row[14] = kind; row[15] = copyOnly;
        row[16] = Now.DateTime.AddDays(-1).AddHours(-1); row[17] = Now.DateTime.AddDays(-1);
        row[18] = (short)0; row[19] = 123L; row[20] = true; row[21] = false; row[22] = true;
        row[23] = Digest(4); row[24] = Digest(4); return row;
    }
    private static CollectorExecutionRequest Request(MonitoredInstanceId target, ObservationTargetRevision revision)
    {
        var profile = new CapabilityProfile(target, revision, new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(16, 0, 1, 0), new SqlServerEditionName("test"),
                SqlServerEngineEdition.Express, SqlServerPlatform.Windows), CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos, true, false, [], [],
            TimeSpan.FromSeconds(1), 64, Now, Now.AddMinutes(5));
        return new(new CollectorRunId(Guid.NewGuid()), target, revision, M4TestData.CreateConnectionPolicy(), profile,
            new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(5)));
    }

    private sealed class SequentialReader(object?[][] rows, int fieldCount = 29) : IOperationalHealthRowReader
    {
        private int index = -1, ordinal = -1;
        public int RowsRead { get; private set; }
        public int FieldCount => fieldCount;
        private object? Value(int next)
        {
            if (next < ordinal) throw new InvalidOperationException("Parser moved backwards in a sequential row.");
            ordinal = next; return rows[index][next];
        }
        public bool IsDBNull(int next) => Value(next) is null;
        public byte[] GetBinary(int next) => (byte[])Value(next)!;
        public bool GetBoolean(int next) => (bool)Value(next)!;
        public DateTime GetDateTime(int next) => (DateTime)Value(next)!;
        public Guid GetGuid(int next) => (Guid)Value(next)!;
        public short GetInt16(int next) => (short)Value(next)!;
        public int GetInt32(int next) => (int)Value(next)!;
        public long GetInt64(int next) => (long)Value(next)!;
        public string GetString(int next) => (string)Value(next)!;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); ordinal = -1;
            bool read = ++index < rows.Length; if (read) RowsRead++; return ValueTask.FromResult(read);
        }
    }
}
