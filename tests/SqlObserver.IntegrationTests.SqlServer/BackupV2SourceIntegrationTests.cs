using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class BackupV2SourceIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly int[] ExpectedLimitedDatabases = [5, 6];
    private static readonly string[] Columns =
    [
        "row_kind", "catalog_database_id", "database_fingerprint", "family_fingerprint", "recovery_fork_fingerprint",
        "group_fingerprint", "ag_database_fingerprint", "local_replica_fingerprint", "database_identity_known",
        "recovery_model_desc", "catalog_database_state_desc", "visibility_scope", "local_replica_role_desc",
        "local_replica_preferred", "backup_kind", "copy_only", "source_local_start", "source_local_finish",
        "recorded_start_offset_minutes", "size_bytes", "has_checksum", "is_damaged", "metadata_complete",
        "backup_first_recovery_fork_fingerprint", "backup_last_recovery_fork_fingerprint", "source_observed_utc",
        "source_time_zone_id", "history_lower_bound_local", "unclassified_backup_seen",
    ];

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [Trait("Category", "RequiresSqlServer")]
    public async Task NativeSqlProjectionPreservesInventorySlotsLineageAndLocalOnlyAgContext(int sourceMajor)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        string query = FixtureQuery(sourceMajor);
        var rows = await ReadAsync(connection, query, 3073, 513, deadline.Token);
        Assert.Equal(15, rows.Count(row => (int)row[0]! == 0));
        Assert.DoesNotContain(rows, row => (int)row[1]! == 2);
        Assert.Equal(rows.Select(Key).Order(StringComparer.Ordinal), rows.Select(Key));
        Assert.Equal(rows.Count, rows.Select(Key).Distinct(StringComparer.Ordinal).Count());

        var normal = rows.Where(row => (int)row[1]! == 5 && (int)row[0]! == 1).ToArray();
        Assert.Equal(5, normal.Length);
        Assert.Equal(200L, Assert.Single(normal, row => (int)row[14]! == 1 && !(bool)row[15]!)[19]);
        Assert.Contains(normal, row => (int)row[14]! == 1 && (bool)row[15]!);
        Assert.Contains(normal, row => (int)row[14]! == 3 && (bool)row[15]!);
        Assert.True((bool)Assert.Single(rows, row => (int)row[1]! == 6 && (int)row[0]! == 1)[15]!);
        foreach (int database in new[] { 7, 8, 9, 10, 11, 13, 16, 17 })
            Assert.Single(rows, row => (int)row[1]! == database);
        foreach (int unknown in new[] { 9, 10, 11, 16 })
            Assert.False((bool)Assert.Single(rows, row => (int)row[1]! == unknown)[8]!);
        Assert.True((bool)Assert.Single(rows, row => (int)row[1]! == 17)[28]!);
        var localAg = rows.Where(row => (int)row[1]! == 12).ToArray();
        Assert.Equal(2, localAg.Length);
        Assert.All(localAg, row =>
        {
            Assert.Equal((short)2, row[11]); Assert.Equal("SECONDARY", row[12]); Assert.Equal(false, row[13]);
            Assert.IsType<byte[]>(row[5]); Assert.IsType<byte[]>(row[6]); Assert.IsType<byte[]>(row[7]);
        });
        Assert.Null(Assert.Single(rows, row => (int)row[1]! == 14 && (int)row[0]! == 1)[18]);
        Assert.Null(Assert.Single(rows, row => (int)row[1]! == 18 && (int)row[0]! == 1)[17]);
        var partialAg = Assert.Single(rows, row => (int)row[1]! == 19);
        Assert.Equal((short)2, partialAg[11]); Assert.Null(partialAg[6]); Assert.Null(partialAg[13]);
        Assert.All(rows, row =>
        {
            Assert.Equal(DateTimeKind.Unspecified, ((DateTime)row[25]!).Kind);
            Assert.Equal(0, ((DateTime)row[25]!).Ticks % TimeSpan.TicksPerMicrosecond);
            if (sourceMajor == 15) Assert.Null(row[26]); else Assert.InRange(Assert.IsType<string>(row[26]).Length, 1, 128);
        });
        var limitedDatabases = await ReadAsync(connection, query, 3073, 2, deadline.Token);
        Assert.Equal(ExpectedLimitedDatabases, limitedDatabases.Where(row => (int)row[0]! == 0).Select(row => (int)row[1]!));
        var limitedRows = await ReadAsync(connection, query, 2, 513, deadline.Token);
        Assert.Equal(rows.Take(2).Select(Key), limitedRows.Select(Key));

        BackupV2ParseResult parsed = await ReadParsedAsync(connection, query, deadline.Token);
        Assert.Equal(rows.Count, parsed.Snapshot.SourceRowsRead);
        Assert.Equal(15, parsed.Snapshot.Databases.Count);
        Assert.Equal(5, parsed.Snapshot.Databases[0].Samples.Count);
        Assert.Equal(200L, Assert.Single(parsed.Snapshot.Databases[0].Samples,
            sample => sample.Freshness.Kind == BackupKind.Full && !sample.Freshness.CopyOnly).SizeBytes);
        Assert.Equal(4, parsed.Snapshot.Databases.Count(database => database.DatabaseFingerprint is null));
        Assert.Equal(2, parsed.Snapshot.Databases.Count(database => database.Scope == BackupFreshnessScope.AvailabilityGroupLocalOnly));
        Assert.Contains(parsed.Snapshot.Databases, database => database.UnclassifiedBackupSeen);
        Assert.All(parsed.Snapshot.Databases.SelectMany(database => database.Samples), sample => Assert.Null(sample.Freshness.FinishUtc));
        Assert.Equal(CollectorLossKind.VisibilityIncomplete, parsed.Loss.Kind);
        Assert.InRange(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(parsed.Snapshot).Length, 1, parsed.ResponseBytes);
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    public async Task NativeInstalledMajorReturnsThePinnedShapeAndRejectsInvalidSourceBounds()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var version = new SqlCommand("SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion'));", connection) { CommandTimeout = 5 };
        int major = Assert.IsType<int>(await version.ExecuteScalarAsync(deadline.Token));
        string query = SqlServerBackupV2AssetCatalog.LoadEmbedded().GetQuery(major);
        var rows = await ReadAsync(connection, query, 3073, 513, deadline.Token);
        output.WriteLine(FormattableString.Invariant($"Installed SQL Server major {major}; source rows {rows.Count}."));
        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => (int)row[1]! == 1 && (int)row[0]! == 0);
        Assert.DoesNotContain(rows, row => (int)row[1]! == 2);
        BackupV2ParseResult parsed = await ReadParsedAsync(connection, query, deadline.Token);
        Assert.NotEmpty(parsed.Snapshot.Databases);
        Assert.Equal(parsed.Snapshot.Databases.Count, parsed.Snapshot.Databases.Count(database => database.Samples.Count <= 5));
        foreach (var limits in new[] { (0, 513), (3074, 513), (3073, 0), (3073, 514) })
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() => ReadAsync(connection, query, limits.Item1, limits.Item2, deadline.Token));
            Assert.Equal(51007, exception.Number);
        }
    }

    private static string FixtureQuery(int major)
    {
        using Stream fixture = typeof(BackupV2SourceIntegrationTests).Assembly.GetManifestResourceStream(
            "SqlObserver.IntegrationTests.SqlServer.Fixtures.backup-v2-source.sql")!;
        using var text = new StreamReader(fixture);
        string query = SqlServerBackupV2AssetCatalog.LoadEmbedded().GetQuery(major);
        foreach (var change in new[]
        {
            ("CONVERT(int,SERVERPROPERTY('IsHadrEnabled'))", "CONVERT(int,1)"),
            ("sys.databases AS d", "@fixture_databases AS d"),
            ("sys.database_recovery_status AS rs", "@fixture_recovery AS rs"),
            ("FROM sys.dm_hadr_availability_replica_states", "FROM @fixture_local_replicas"),
            ("sys.fn_hadr_backup_is_preferred_replica(d.name)", "CONVERT(bit,0)"),
            ("msdb.dbo.backupset AS b", "@fixture_backups AS b"),
        })
        {
            Assert.Contains(change.Item1, query, StringComparison.Ordinal);
            query = query.Replace(change.Item1, change.Item2, StringComparison.Ordinal);
        }
        return text.ReadToEnd() + Environment.NewLine + query;
    }

    private static string Key(object?[] row) =>
        string.Join(':', ((int)row[1]!).ToString("D5", System.Globalization.CultureInfo.InvariantCulture), row[0], row[14], row[15]);

    private static async Task<BackupV2ParseResult> ReadParsedAsync(SqlConnection connection, string query, CancellationToken token)
    {
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 5 };
        command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = 3073;
        command.Parameters.Add("maximum_databases", SqlDbType.Int).Value = 513;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        DateTimeOffset receipt = DateTimeOffset.UtcNow;
        receipt = receipt.AddTicks(-receipt.Ticks % TimeSpan.TicksPerMicrosecond);
        // Parser-only context; this profile does not certify the connected principal or transport.
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
        var profile = new CapabilityProfile(target, revision, new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(16, 0, 1, 0), new SqlServerEditionName("parser fixture"),
                SqlServerEngineEdition.Express, SqlServerPlatform.Windows), CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos, true, false, [], [],
            TimeSpan.FromSeconds(1), 64, receipt, receipt.AddMinutes(5));
        var request = new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), target, revision,
            new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("parser-fixture"), tcpPort: 1433),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))), profile,
            new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(5)));
        return await SqlServerBackupV2Parser.ParseAsync(request, new SqlDataReaderOperationalHealthRowReader(reader),
            new IdentityFingerprintKey(new byte[32]), receipt, token);
    }

    private static async Task<List<object?[]>> ReadAsync(SqlConnection connection, string query,
        int maximumRows, int maximumDatabases, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 5 };
        command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = maximumRows;
        command.Parameters.Add("maximum_databases", SqlDbType.Int).Value = maximumDatabases;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.Equal(Columns, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object?[reader.FieldCount];
            for (int i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }
        return rows;
    }
}
