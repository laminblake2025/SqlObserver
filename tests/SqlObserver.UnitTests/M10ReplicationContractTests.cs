using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.UnitTests;

public sealed class M10ReplicationContractTests
{
    private static readonly IdentityFingerprintKey Key = new(Enumerable.Repeat((byte)0xA5, IdentityFingerprintKey.RequiredLength).ToArray());
    private static readonly string[] ReplicationAssetNames = ["replication.health.v1.schema.json", "replication.health.v1.json", "replication.health.sqlserver15-windows.v1.sql", "replication.health.sqlserver16-windows.v1.sql", "replication.health.sqlserver17-windows.v1.sql"];

    [Fact]
    public void ReplicationAssetsAreFixedPassiveAndDoNotContainMutationSurface()
    {
        string root = FindRoot();
        string manifest = File.ReadAllText(Path.Combine(root, "collectors/manifests/replication.health.v1.json"));
        Assert.Contains("\"operationalMode\":\"passive\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"defaultIntervalSeconds\":60", manifest, StringComparison.Ordinal);
        Assert.Contains("\"minimumIntervalSeconds\":30", manifest, StringComparison.Ordinal);
        foreach (int major in new[] { 15, 16, 17 })
        {
            string sql = File.ReadAllText(Path.Combine(root, $"collectors/sql/replication.health.sqlserver{major}-windows.v1.sql"));
            Assert.DoesNotMatch(@"(?i)\bEXEC(?:UTE)?\b", sql);
            Assert.DoesNotContain("sp_", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dynamic", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LSN", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("@maximum_rows", sql, StringComparison.Ordinal);
            Assert.Contains("@distribution_database", sql, StringComparison.Ordinal);
            Assert.Contains("MSdistribution_agents", sql, StringComparison.Ordinal);
            Assert.Contains("MSdistribution_status", sql, StringComparison.Ordinal);
            Assert.Contains("MSdistribution_history", sql, StringComparison.Ordinal);
            Assert.Contains("HASHBYTES('SHA2_256'", sql, StringComparison.Ordinal);
            Assert.Contains("GETDATE()", sql, StringComparison.Ordinal);
            Assert.Contains("msdb.dbo.sysjobactivity", sql, StringComparison.Ordinal);
            Assert.Contains("j.stop_execution_date IS NOT NULL THEN 6", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReplicationDomainsRemainDistinctUnderTheSharedKey()
    {
        byte[] identity = Enumerable.Repeat((byte)0x7, 32).ToArray();
        string publication = ReplicationIdentityFingerprint.FromTypedIdentity("publication", identity, Key);
        string subscription = ReplicationIdentityFingerprint.FromTypedIdentity("subscription", identity, Key);
        string hostLike = ReplicationIdentityFingerprint.FromTypedIdentity("host", identity, Key);

        Assert.NotEqual(publication, subscription);
        Assert.NotEqual(publication, hostLike);
        Assert.NotEqual(subscription, hostLike);
    }

    [Fact]
    public async Task ParserPreservesBoundMonitorEvidenceAndRelativeAge()
    {
        var target = M4TestData.TargetId;
        var request = new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), target, M4TestData.TargetRevision, M4TestData.CreateConnectionPolicy(), M4TestData.CreateProfile(), new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(10)));
        ReplicationParseResult parsed = await SqlServerReplicationParser.ParseAsync(request, new FakeReader([new FakeRow(5, 4, Enumerable.Repeat((byte)7, 32).ToArray(), Enumerable.Repeat((byte)8, 32).ToArray(), 2, 42, 1500, 2300, 17, 0, 1)]), registeredDistributionDatabase: true, Key, CancellationToken.None);
        ReplicationObservation item = Assert.Single(parsed.Snapshot.Items);
        Assert.Equal(42, item.PendingCommands);
        Assert.Equal(1.5m, item.LatencySeconds);
        Assert.Equal(2.3m, item.RatePerSecond);
        Assert.Equal(TimeSpan.FromSeconds(17), item.LastSuccess.Age);
        Assert.Equal(ReplicationCoverage.Complete, item.Coverage);
        Assert.NotEqual(Convert.ToHexString(Enumerable.Repeat((byte)7, 32).ToArray()).ToLowerInvariant(), item.PublicationFingerprint);
        Assert.NotEqual(item.PublicationFingerprint, item.SubscriptionFingerprint);
    }

    [Fact]
    public async Task ParserSupportsClosedTopologyStatesAndUnboundVisibilityGap()
    {
        var target = M4TestData.TargetId;
        var request = new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), target, M4TestData.TargetRevision, M4TestData.CreateConnectionPolicy(), M4TestData.CreateProfile(), new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(10)));
        var rows = Enum.GetValues<ReplicationTopologyState>().Select(state => new FakeRow((int)state, 1, Enumerable.Repeat((byte)7, 32).ToArray(), null, 2, null, null, null, null, 1, 2)).ToArray();
        ReplicationParseResult parsed = await SqlServerReplicationParser.ParseAsync(request, new FakeReader(rows), registeredDistributionDatabase: false, Key, CancellationToken.None);
        Assert.Equal(Enum.GetValues<ReplicationTopologyState>().Length, parsed.Snapshot.Items.Count);
        Assert.All(parsed.Snapshot.Items, item => Assert.Equal(ReplicationCoverage.VisibilityGap, item.Coverage));
        Assert.All(parsed.Snapshot.Items, item => Assert.Null(item.PublicationFingerprint));
        Assert.All(parsed.Snapshot.Items, item => Assert.NotNull(item.VisibilityGapFingerprint));
        Assert.Equal(parsed.Snapshot.Items.Count, parsed.Snapshot.Items.Select(item => item.VisibilityGapFingerprint).Distinct().Count());
        Assert.Equal(OperationalObservationState.Degraded, parsed.Snapshot.State);
        Assert.Equal(CollectorLossKind.VisibilityIncomplete, parsed.Loss.Kind);

        // Exercise the persistence validator with the real parser envelope:
        // replication shares the typed operational payload but is not M9.
        var manifest = M4TestData.CreateManifest("replication.health", outputKind: CollectorOutputKind.ReplicationHealth);
        var work = M4TestData.CreateWork(manifest);
        var payload = new CollectorPayload([], operationalHealth: new OperationalHealthPayload(parsed.Snapshot, parsed.Items, parsed.Bytes));
        var summary = new CollectorRunSummary(request.RunId, target, M4TestData.TargetRevision, manifest.Id, 1, 1,
            CollectorRunOutcome.Partial, CollectorRunReason.VisibilityIncomplete, TimeSpan.FromMilliseconds(1), 1,
            new CollectorRunAccounting(parsed.Rows, payload.ItemCount, parsed.Bytes, payload.EstimatedSizeBytes), parsed.Loss);
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey($"collector/run/replication.health/{target.Value:N}"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        var commit = new CommitCollectorRunRequest(work, summary, payload, work.Circuit, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        typeof(PostgreSqlCollectorRuntimeRepositoryPort).GetMethod("ValidateCommit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [commit]);
    }

    [Fact]
    public async Task ParserMarksRowTruncationPartialAndKeepsRowLimitPrecedenceOverVisibilityGap()
    {
        var target = M4TestData.TargetId;
        var request = new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), target, M4TestData.TargetRevision, M4TestData.CreateConnectionPolicy(), M4TestData.CreateProfile(), new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(10)));
        var rows = Enumerable.Repeat(new FakeRow(5, 4, Enumerable.Repeat((byte)7, 32).ToArray(), null, 2, null, null, null, null, 1, 3), ReplicationBounds.MaximumRows + 1).ToArray();

        ReplicationParseResult parsed = await SqlServerReplicationParser.ParseAsync(request, new FakeReader(rows), registeredDistributionDatabase: false, Key, CancellationToken.None);

        Assert.Equal(ReplicationBounds.MaximumRows + 1, parsed.Rows);
        Assert.Equal(ReplicationBounds.MaximumRows, parsed.Items);
        Assert.True(parsed.Snapshot.Truncated);
        Assert.Equal(OperationalObservationState.Partial, parsed.Snapshot.State);
        Assert.Equal(ReplicationCoverage.VisibilityGap, parsed.Snapshot.Coverage);
        Assert.Equal(CollectorLossKind.SourceRowLimit, parsed.Loss.Kind);
    }

    [Fact]
    public async Task ParserAggregatesReducedCoverageInsteadOfUsingTheFirstRow()
    {
        var target = M4TestData.TargetId;
        var request = new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), target, M4TestData.TargetRevision, M4TestData.CreateConnectionPolicy(), M4TestData.CreateProfile(), new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(10)));
        var rows = new[]
        {
            new FakeRow(5, 4, Enumerable.Repeat((byte)7, 32).ToArray(), null, 2, null, null, null, null, 1, 1),
            new FakeRow(6, 4, Enumerable.Repeat((byte)8, 32).ToArray(), null, 2, null, null, null, null, 1, 2),
        };

        ReplicationParseResult parsed = await SqlServerReplicationParser.ParseAsync(request, new FakeReader(rows), registeredDistributionDatabase: true, Key, CancellationToken.None);

        Assert.Equal(ReplicationCoverage.LocalSummary, parsed.Snapshot.Coverage);
        Assert.Equal(OperationalObservationState.Degraded, parsed.Snapshot.State);
        Assert.Equal(CollectorLossKind.VisibilityIncomplete, parsed.Loss.Kind);
    }

    [Fact]
    public void ManifestDeclaresM10BoundsAndSeparateFeatureCapabilities()
    {
        CollectorManifest manifest = ReplicationManifest.Create();
        Assert.Equal(CollectorOutputKind.ReplicationHealth, manifest.OutputKind);
        Assert.Equal(TimeSpan.FromSeconds(5), manifest.Limits.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), manifest.Limits.CommandTimeout);
        Assert.Equal(2048, manifest.Limits.MaxRows);
        Assert.Contains(manifest.RequiredCapabilities, x => x.Value == "feature.replication");
        Assert.DoesNotContain(manifest.RequiredCapabilities, x => x.Value == "feature.host-binding");
        Assert.DoesNotContain(manifest.RequiredPermissions, x => x.PermissionId.Value.StartsWith("feature.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParserRequiresExactlyTheReviewedElevenFieldShape()
    {
        var target = M4TestData.TargetId;
        var request = new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), target, M4TestData.TargetRevision, M4TestData.CreateConnectionPolicy(), M4TestData.CreateProfile(), new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await SqlServerReplicationParser.ParseAsync(request, new FakeReader([new FakeRow(5, 4, Enumerable.Repeat((byte)7, 32).ToArray(), null, 2, null, null, null, null, 1, 2)], 12), false, Key, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await SqlServerReplicationParser.ParseAsync(request, new FakeReader([new FakeRow(5, 4, Enumerable.Repeat((byte)7, 32).ToArray(), null, 2, null, null, null, null, 1, 2)], 10), false, Key, CancellationToken.None));
    }

    [Fact]
    public void ReplicationEmbeddedBundleMatchesTheLFManifestDigest()
    {
        SqlServerReplicationAssetCatalog catalog = SqlServerReplicationAssetCatalog.LoadEmbedded();
        Assert.Equal("8fa9d8d4c8f3a8fdfb17ffe675f7642220ada826719136d5b7338372866c2b9a", catalog.BundleChecksum);
        Assert.Equal(ReplicationAssetNames, catalog.AssetNames);
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }

    private sealed record FakeRow(int Topology, int Role, byte[] Publication, byte[]? Subscription, int Status, long? Pending, int? Latency, int? Rate, long? Age, int Unknown, int Coverage);
    private sealed class FakeReader(FakeRow[] rows, int fieldCount = 11) : IReplicationRowReader
    {
        private int index = -1;
        private FakeRow Current => rows[index];
        public int FieldCount => fieldCount;
        public bool IsDBNull(int ordinal) => ordinal switch { 2 => Current.Publication is null, 3 => Current.Subscription is null, 5 => Current.Pending is null, 6 => Current.Latency is null, 7 => Current.Rate is null, 8 => Current.Age is null, _ => false };
        public byte[] GetBinary(int ordinal) => ordinal == 2 ? Current.Publication : Current.Subscription!;
        public int GetInt32(int ordinal) => ordinal switch { 0 => Current.Topology, 1 => Current.Role, 4 => Current.Status, 6 => Current.Latency!.Value, 7 => Current.Rate!.Value, 9 => Current.Unknown, 10 => Current.Coverage, _ => throw new InvalidOperationException() };
        public long GetInt64(int ordinal) => ordinal == 5 ? Current.Pending!.Value : ordinal == 8 ? Current.Age!.Value : throw new InvalidOperationException();
        public bool GetBoolean(int ordinal) => Current.Unknown != 0;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken) { index++; return ValueTask.FromResult(index < rows.Length); }
    }
}
