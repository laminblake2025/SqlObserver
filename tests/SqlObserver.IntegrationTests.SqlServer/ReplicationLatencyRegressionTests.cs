using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class ReplicationLatencyRegressionTests
{
    // MSdistribution_history.current_delivery_latency is an int in milliseconds:
    // https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/msdistribution-history-transact-sql
    [Theory]
    [InlineData(null, null)]
    [InlineData(0, "0")]
    [InlineData(1, "0.001")]
    [InlineData(1501, "1.501")]
    [InlineData(2147483, "2147.483")]
    [InlineData(2147484, "2147.484")]
    [InlineData(3600000, "3600")]
    [InlineData(86400000, "86400")]
    public async Task ParserPreservesMillisecondsThroughOneHourAndItsExistingDayBound(int? milliseconds, string? expectedSeconds)
    {
        ReplicationParseResult result = await ParseAsync(milliseconds);
        decimal? expected = expectedSeconds is null ? null : decimal.Parse(expectedSeconds, CultureInfo.InvariantCulture);
        Assert.Equal(expected, Assert.Single(result.Snapshot.Items).LatencySeconds);
        Assert.Equal(OperationalObservationState.Complete, result.Snapshot.State);
        Assert.False(result.Loss.HasLoss);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86400001)]
    [InlineData(int.MaxValue)]
    public async Task ParserStillRejectsNegativeAndBeyondDayValues(int milliseconds) =>
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ParseAsync(milliseconds));

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [Trait("Category", "RequiresSqlServer")]
    public async Task NativeSqlProjectionPreservesDocumentedUnitsAndUnavailableGuards(int major)
    {
        // Explicit environment-backed selection uses the established fixture
        // guard. No fallback endpoint or target objects are created or read.
        var settings = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString);
        Assert.NotEqual(SqlConnectionEncryptOption.Optional, settings.Encrypt);
        Assert.False(settings.TrustServerCertificate);
        settings.ConnectTimeout = 5;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(deadline.Token);

        string query = SqlServerReplicationAssetCatalog.LoadEmbedded().GetQuery(major);
        const string suffix = " AS latency_millis,";
        string line = Assert.Single(query.Split('\n').Select(static line => line.Trim()),
            line => line.Contains("h.current_delivery_latency", StringComparison.Ordinal) && line.EndsWith(suffix, StringComparison.Ordinal));
        string expression = line[..^suffix.Length];
        // Only the reviewed embedded projection is SQL text. Every fixture
        // value is parameterized, including missing/stopped Agent evidence.
        await using var command = new SqlCommand($"SELECT {expression} FROM (VALUES (@latency)) AS h(current_delivery_latency) CROSS JOIN (VALUES (@job_id, @started, @stopped)) AS j(job_id, start_execution_date, stop_execution_date)", connection)
        {
            CommandTimeout = 5,
        };
        SqlParameter latency = command.Parameters.Add("latency", SqlDbType.Int);
        SqlParameter job = command.Parameters.Add("job_id", SqlDbType.UniqueIdentifier);
        SqlParameter started = command.Parameters.Add("started", SqlDbType.DateTime);
        SqlParameter stopped = command.Parameters.Add("stopped", SqlDbType.DateTime);
        (int? Source, bool HasJob, bool Started, bool Stopped, int? Expected)[] cases =
        [
            (null, true, true, false, null), (-1, true, true, false, null),
            (0, true, true, false, 0), (1, true, true, false, 1),
            (2147483, true, true, false, 2147483), (2147484, true, true, false, 2147484),
            (3600000, true, true, false, 3600000), (86400000, true, true, false, 86400000),
            (86400001, true, true, false, 86400001), (int.MaxValue, true, true, false, int.MaxValue),
            (3600000, false, true, false, null), (3600000, true, false, false, null),
            (3600000, true, true, true, null),
        ];
        foreach (var fixture in cases)
        {
            latency.Value = fixture.Source is int value ? value : DBNull.Value;
            job.Value = fixture.HasJob ? Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") : DBNull.Value;
            started.Value = fixture.Started ? new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Unspecified) : DBNull.Value;
            stopped.Value = fixture.Stopped ? new DateTime(2026, 9, 24, 1, 0, 0, DateTimeKind.Unspecified) : DBNull.Value;
            object? result = await command.ExecuteScalarAsync(deadline.Token);
            int? milliseconds = result is null or DBNull ? null : Assert.IsType<int>(result);
            Assert.Equal(fixture.Expected, milliseconds);
            if (milliseconds > 86400000)
                await Assert.ThrowsAsync<InvalidDataException>(async () => await ParseAsync(milliseconds));
            else
                Assert.Equal(milliseconds / 1000m, Assert.Single((await ParseAsync(milliseconds)).Snapshot.Items).LatencySeconds);
        }
    }

    private static ValueTask<ReplicationParseResult> ParseAsync(int? milliseconds)
    {
        MonitoredInstanceId target = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        ObservationTargetRevision revision = new(1);
        DateTimeOffset checkedAt = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        var profile = new CapabilityProfile(target, revision, new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(16, 0, 1000, 0), new SqlServerEditionName("Enterprise Edition"), SqlServerEngineEdition.Enterprise, SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true, isSysAdmin: false, capabilities: [], permissions: [], TimeSpan.FromMilliseconds(1), 64, checkedAt, checkedAt.AddMinutes(5));
        var request = new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), target, revision,
            new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql.test.example"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))),
            profile, new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(5)));
        return SqlServerReplicationParser.ParseAsync(request, new LatencyRowReader(milliseconds), registeredDistributionDatabase: true,
            new IdentityFingerprintKey(new byte[IdentityFingerprintKey.RequiredLength]), CancellationToken.None);
    }

    private sealed class LatencyRowReader(int? milliseconds) : IReplicationRowReader
    {
        private bool read;
        public int FieldCount => 11;
        public bool IsDBNull(int ordinal) => ordinal is 5 or 7 or 8 || ordinal == 6 && milliseconds is null;
        public byte[] GetBinary(int ordinal) => new byte[32];
        public int GetInt32(int ordinal) => ordinal switch
        {
            0 => (int)ReplicationTopologyState.Transactional,
            1 => (int)ReplicationRole.Distributor,
            4 => (int)ReplicationStatus.Healthy,
            6 => milliseconds!.Value,
            10 => (int)ReplicationCoverage.Complete,
            _ => throw new InvalidOperationException("Unexpected integer column."),
        };
        public long GetInt64(int ordinal) => throw new InvalidOperationException("The fixture has no non-null bigint columns.");
        public bool GetBoolean(int ordinal) => true;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasRow = !read;
            read = true;
            return ValueTask.FromResult(hasRow);
        }
    }
}
