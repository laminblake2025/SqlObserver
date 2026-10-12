using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class PlanCacheRuntimeTargetSourceTests
{
    [Fact]
    public async Task ConcurrentIdentityPhasesBracketOneCacheReadAndPreserveEveryCounter()
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request, 20) { IdentityDelay = TimeSpan.FromMilliseconds(5) };
        PlanCacheRuntimeTargetRead read = await new PlanCacheRuntimeTargetSource(port).ReadAsync(request, 2000, default);
        Assert.Equal(1, port.CacheCalls); Assert.Equal(40, port.IdentityCalls);
        Assert.InRange(port.Peak, 2, PlanCacheRuntimeTargetSource.MaximumConcurrentDatabases);
        Assert.True(read.Output.QualifiedExhaustive); Assert.Equal(2, read.Output.Items.Count);
        Assert.Equal(42, read.SourceRowsRead); Assert.Equal(40 * 256, read.IdentityResponseBytes);
        Assert.Equal(256 + 2 * 256 + 40 * 256, read.ResponseBytes);
        Assert.All(read.Output.Items, item => Assert.Equal(new(1001, 2002, 3, 4, 5, 6), item.Watermark.Counters));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("replacement")]
    [InlineData("epoch")]
    public async Task MissingOrChangedLiveEvidenceWithholdsOnlyItsDatabase(string failure)
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request) { Failure = failure };
        PlanCacheRuntimeTargetRead read = await new PlanCacheRuntimeTargetSource(port).ReadAsync(request, 2000, default);
        Assert.Single(read.Output.Items); Assert.Equal(5, read.Output.Items[0].Watermark.Key.Query.DatabaseId);
        Assert.Equal(1, read.Output.UnqualifiedRows); Assert.False(read.Output.QualifiedExhaustive);
        Assert.True(read.Output.SourceExhaustive); Assert.Equal(1, port.CacheCalls);
        Assert.Equal(failure == "missing" ? 2 : 4, read.IdentityRowsRead);
    }

    [Fact]
    public async Task IdentityPhaseDeadlinePreservesFinishedBracketsWithoutRetryingMissingDatabases()
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request) { Failure = "slow" };
        var source = new PlanCacheRuntimeTargetSource(port, TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        PlanCacheRuntimeTargetRead read = await source.ReadAsync(request, 2000, default);
        Assert.Single(read.Output.Items); Assert.Equal(1, read.Output.UnqualifiedRows);
        Assert.Equal(3, port.IdentityCalls); Assert.Equal(2, read.IdentityRowsRead); Assert.Equal(1, port.CacheCalls);
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-name")]
    [InlineData("system-db")]
    [InlineData("too-many")]
    public async Task InventoryShapeIsCheckedBeforeAnyCapture(string failure)
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request) { Failure = failure };
        await Assert.ThrowsAsync<InvalidDataException>(() => new PlanCacheRuntimeTargetSource(port).ReadAsync(request, 2000, default));
        Assert.Equal(0, port.CacheCalls); Assert.Equal(0, port.IdentityCalls);
    }

    [Theory]
    [InlineData("foreign-page")]
    [InlineData("wrong-limit")]
    [InlineData("foreign-identity")]
    [InlineData("wrong-db")]
    public async Task ForeignSourceFramesAreRejectedWithoutFabricatingIdentity(string failure)
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request) { Failure = failure };
        await Assert.ThrowsAsync<InvalidDataException>(() => new PlanCacheRuntimeTargetSource(port).ReadAsync(request, 2000, default));
    }

    [Fact]
    public async Task InventoryProbeIsLimitedTo256IdentityReadsPerPhase()
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request, 257);
        PlanCacheRuntimeTargetRead read = await new PlanCacheRuntimeTargetSource(port).ReadAsync(request, 2000, default);
        Assert.Equal(512, port.IdentityCalls); Assert.Equal(512, read.IdentityRowsRead); Assert.Equal(514, read.SourceRowsRead);
        Assert.DoesNotContain(261, port.ObservedDatabases); // The look-ahead inventory entry is not opened.
    }

    [Fact]
    public async Task CappedCacheProbeKeepsTheEffectiveLimitAndDoesNotTriggerASecondRead()
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request);
        PlanCacheRuntimeTargetRead read = await new PlanCacheRuntimeTargetSource(port).ReadAsync(request, 1, default);
        Assert.Equal(1, port.CacheCalls); Assert.Equal(1, read.Output.CandidateLimit);
        Assert.Equal(2, read.Output.CandidateRows); Assert.Single(read.Output.Items);
        Assert.False(read.Output.SourceExhaustive); Assert.Equal(0, read.Output.UnqualifiedRows);
    }

    [Fact]
    public async Task CaptureDeadlineStopsBeforeTheAfterPhase()
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request) { Failure = "cache-timeout" };
        var source = new PlanCacheRuntimeTargetSource(port, TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => source.ReadAsync(request, 2000, default));
        Assert.Equal(2, port.IdentityCalls); Assert.Equal(1, port.CacheCalls);
    }

    [Fact]
    public async Task CallerCancellationWinsOverAFaultedIdentitySibling()
    {
        CollectorExecutionRequest request = Request(); using var cancelled = new CancellationTokenSource();
        var port = new Port(request) { Failure = "cancel-fault", Cancel = cancelled };
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PlanCacheRuntimeTargetSource(port).ReadAsync(request, 2000, cancelled.Token));
        Assert.Equal(cancelled.Token, failure.CancellationToken); Assert.Equal(0, port.CacheCalls);
    }

    [Theory]
    [InlineData("inventory-timeout", 0)]
    [InlineData("cache-fault-after-timeout", 1)]
    public async Task PhaseExpirationWinsOverAProviderFaultAfterCancellation(string failure, int captures)
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request) { Failure = failure };
        var source = new PlanCacheRuntimeTargetSource(port, TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => source.ReadAsync(request, 2000, default));
        Assert.Equal(captures, port.CacheCalls);
    }

    [Theory]
    [InlineData(null, 2, 0)]
    [InlineData("replacement", 1, 1)]
    [InlineData("epoch", 1, 1)]
    [InlineData("unknown", 1, 1)]
    [InlineData("missing", 1, 1)]
    public async Task ContentSharesOneCounterCaptureAndOnlyStableLiveDatabaseBracketsSurvive(string? fault, int retained, int unqualified)
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request) { Failure = fault };
        PlanCacheRuntimeTargetRead read = await new PlanCacheRuntimeTargetSource(port).ReadWithContentAsync(request, 2000, 8,
            new UnavailableQuerySensitiveContentProtector(), default);
        Assert.Equal(1, port.CacheCalls); Assert.Equal(1, port.ContentCalls);
        var content = Assert.IsType<PlanCacheContentOutputBatch>(read.Content);
        Assert.Equal(retained, content.Items.Count); Assert.Equal(unqualified, content.UnqualifiedEntries);
        Assert.Equal(2, content.AttemptedEntries); Assert.Same(read.Output, content.Runtime);
        Assert.All(content.Items, item => Assert.Contains(item.Statement, read.Output.Items));
        Assert.Equal(read.Output.CandidateRows + read.IdentityRowsRead + 2, read.SourceRowsRead);
        Assert.Equal(PlanCacheRuntimeSourceReader.HeaderBytes + read.Output.EmittedRows * PlanCacheRuntimeSourceReader.RowBytes
            + read.IdentityResponseBytes + content.SourceContentBytes + 2 * 512, read.ResponseBytes);
    }

    [Theory]
    [InlineData("content-scope")]
    [InlineData("content-limit")]
    [InlineData("content-bytes")]
    [InlineData("content-unqualified-state")]
    public async Task MalformedContentCannotBeHiddenByAnUnqualifiedDatabase(string fault)
    {
        CollectorExecutionRequest request = Request(); var port = new Port(request) { Failure = fault };
        await Assert.ThrowsAsync<InvalidDataException>(() => new PlanCacheRuntimeTargetSource(port).ReadWithContentAsync(request, 2000, 8,
            new UnavailableQuerySensitiveContentProtector(), default));
    }

    [Fact]
    public async Task OptionalContentKeepsTheExistingCaptureDeadlineAndLegacyReadUnchanged()
    {
        CollectorExecutionRequest request = Request(); var slow = new Port(request) { Failure = "cache-timeout" };
        var source = new PlanCacheRuntimeTargetSource(slow, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => source.ReadWithContentAsync(request, 2000, 8,
            new UnavailableQuerySensitiveContentProtector(), default));
        Assert.Equal(2, slow.IdentityCalls); Assert.Equal(1, slow.CacheCalls); Assert.Equal(1, slow.ContentCalls);
        var port = new Port(request);
        var legacy = await new PlanCacheRuntimeTargetSource(port).ReadAsync(request, 2000, default);
        Assert.Null(legacy.Content); Assert.Equal(0, port.ContentCalls); Assert.Equal(1, port.CacheCalls);
    }

    internal static CollectorExecutionRequest Request()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
        var profile = new CapabilityProfile(target, revision, new("capability.connection"), 1, 1,
            new(new(16, 0, 1, 0), new("Express"), SqlServerEngineEdition.Express, SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos,
            true, false, [], [], TimeSpan.FromSeconds(1), 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));
        return new(new(Guid.NewGuid()), target, revision, new(new(new("test"), new SqlServerInstanceName("SQLEXPRESS")),
            new(TimeSpan.FromSeconds(1))), profile, new(1), new(TimeSpan.FromMinutes(2)));
    }

    private sealed class Port(CollectorExecutionRequest request, int databaseCount = 2) : IPlanCacheContentTargetExecutionPort
    {
        private readonly DateTimeOffset at = DateTimeOffset.UtcNow;
        private int active;
        internal string? Failure { get; init; }
        internal CancellationTokenSource? Cancel { get; init; }
        internal TimeSpan IdentityDelay { get; init; }
        internal int CacheCalls;
        internal int ContentCalls;
        internal int IdentityCalls;
        internal int Peak;
        internal System.Collections.Concurrent.ConcurrentBag<int> ObservedDatabases { get; } = [];

        public Task<IReadOnlyList<SqlServerDatabaseIdentity>> ReadInventoryAsync(CollectorExecutionRequest execution, CancellationToken token)
        {
            if (Failure == "inventory-timeout") return FaultAfterCancellation(token);
            int count = Failure == "too-many" ? 258 : databaseCount;
            SqlServerDatabaseIdentity[] databases = Enumerable.Range(0, count).Select(index =>
                new SqlServerDatabaseIdentity(index + 5, $"db{index + 5}")).ToArray();
            if (Failure == "duplicate-id") databases[1] = new(5, "other");
            if (Failure == "duplicate-name") databases[1] = new(6, "db5");
            if (Failure == "system-db") databases[1] = new(4, "system");
            return Task.FromResult<IReadOnlyList<SqlServerDatabaseIdentity>>(databases);

            static async Task<IReadOnlyList<SqlServerDatabaseIdentity>> FaultAfterCancellation(CancellationToken cancelled)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancelled); }
                catch (OperationCanceledException) { throw new InvalidDataException("provider fault after cancellation"); }
                return [];
            }
        }

        public async Task<PlanCacheDatabaseIdentitySnapshot?> ReadIdentityAsync(CollectorExecutionRequest execution,
            SqlServerDatabaseIdentity database, CancellationToken token)
        {
            Interlocked.Increment(ref IdentityCalls); ObservedDatabases.Add(database.DatabaseId);
            int parallel = Interlocked.Increment(ref active);
            int old;
            do { old = Peak; } while (parallel > old && Interlocked.CompareExchange(ref Peak, parallel, old) != old);
            try
            {
                if (IdentityDelay > TimeSpan.Zero) await Task.Delay(IdentityDelay, token);
                if (database.DatabaseId == 6)
                {
                    if (Failure is "missing" or "content-unqualified-state") return null;
                    if (Failure == "slow") await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    if (Failure == "cancel-fault") { Cancel!.Cancel(); throw new InvalidDataException("faulted sibling"); }
                }
                bool after = CacheCalls > 0;
                return new(request.TargetId, Failure == "foreign-identity" ? new(2) : request.TargetRevision,
                    Failure == "wrong-db" ? 7 : database.DatabaseId,
                    after ? at.AddMilliseconds(30) : at.AddMilliseconds(-20),
                    after ? at.AddMilliseconds(40) : at.AddMilliseconds(-10),
                    Failure == "epoch" && database.DatabaseId == 6 && after ? Digest('f') : Digest('c'),
                    Failure == "unknown" && database.DatabaseId == 6 ? null :
                        Failure == "replacement" && database.DatabaseId == 6 && after ? Digest('a') : Digest('d'), true);
            }
            finally { Interlocked.Decrement(ref active); }
        }

        public async Task<PlanCacheRuntimeSourcePage> ReadCacheAsync(CollectorExecutionRequest execution, int maximumRows, CancellationToken token)
        {
            Interlocked.Increment(ref CacheCalls); Assert.Equal(0, Volatile.Read(ref active));
            if (Failure == "cache-timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (Failure == "cache-fault-after-timeout")
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { throw new InvalidDataException("provider fault after cancellation"); }
            }
            PlanCacheRuntimeSourceRow[] rows = Enumerable.Range(0, Math.Min(2, maximumRows)).Select(index =>
            {
                var query = new QueryOpaqueIdentity(index + 5, Digest((char)('a' + index)));
                return new PlanCacheRuntimeSourceRow(query, new(query, Digest('b')), Digest((char)('e' + index)), new(1001, 2002, 3, 4, 5, 6));
            }).ToArray();
            return new(request.TargetId, Failure == "foreign-page" ? new(2) : request.TargetRevision,
                at, at.AddMilliseconds(20), Digest('c'), 2, 0, 0, 0, rows, maximumRows >= 2,
                Failure == "wrong-limit" ? 1000 : maximumRows);
        }
        public async Task<PlanCacheContentSourceRead> ReadCacheContentAsync(CollectorExecutionRequest execution, int maximumRows,
            int maximumContentEntries, IQuerySensitiveContentProtector protector, CancellationToken token)
        {
            Interlocked.Increment(ref ContentCalls);
            var page = await ReadCacheAsync(execution, maximumRows, token);
            var content = page.Rows.Select(row => new PlanCacheProtectedContent(row, "protected", Payload(SensitivePayloadKind.QueryText, 8),
                "protected", Payload(SensitivePayloadKind.ExecutionPlan, 16))).ToArray();
            if (Failure == "content-scope") content[0] = content[0] with { Statement = content[0].Statement with { Counters = new(2, 2, 3, 4, 5, 6) } };
            if (Failure == "content-unqualified-state") content[1] = content[1] with { PlanState = "partial" };
            return new(page, content, content.Length * 48, content.Length * 24 + (Failure == "content-bytes" ? 1 : 0),
                Failure == "content-limit" ? 1 : maximumContentEntries);
        }
        private static ProtectedSensitivePayload Payload(SensitivePayloadKind kind, int size) =>
            new(kind, new(new byte[32]), "AES-256-GCM", "fixture-only", new byte[12], new byte[16], new byte[size]);

        private static string Digest(char value) => new(value, 64);
    }
}
