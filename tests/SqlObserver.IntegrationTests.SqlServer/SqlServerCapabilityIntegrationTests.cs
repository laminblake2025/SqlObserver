using System.Data;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class SqlServerCapabilityIntegrationTests
{
    [Fact]
    public void EmbeddedCollectorAssetsAreChecksumVerifiedBoundedAndPassive()
    {
        SqlServerCapabilityAssetCatalog catalog = SqlServerCapabilityAssetCatalog.LoadEmbedded();

        Assert.Equal("capability.connection", catalog.Manifest.Id.Value);
        Assert.Equal(1, catalog.Manifest.ManifestVersion.Value);
        Assert.Equal(1, catalog.Manifest.OutputSchemaVersion.Value);
        Assert.Equal(TimeSpan.FromSeconds(5), catalog.Manifest.Limits.Timeout);
        Assert.Equal(1, catalog.Manifest.Limits.MaxRows);
        Assert.Equal(16_384, catalog.Manifest.Limits.MaxResponseBytes);
        Assert.Equal(CollectorFallbackMode.Unsupported, catalog.Manifest.Fallback.Mode);
        Assert.Equal(CollectorOperationalMode.Passive, catalog.Manifest.OperationalMode);
        Assert.Equal([SqlServerPlatform.Windows], catalog.Manifest.SupportedPlatforms);

        string bootstrap = catalog.BootstrapSql;
        string version15 = catalog.GetSupportedQuery(15);
        string version16 = catalog.GetSupportedQuery(16);
        string version17 = catalog.GetSupportedQuery(17);
        string fallback = catalog.PermissionFallbackSql;
        string[] sqlAssets = [bootstrap, version15, version16, version17, fallback];

        foreach (string sql in sqlAssets)
        {
            Assert.StartsWith("SET NOCOUNT ON;\n\nSELECT TOP (1)", sql, StringComparison.Ordinal);
            Assert.Contains("IS_SRVROLEMEMBER(N'sysadmin')", sql, StringComparison.Ordinal);
            Assert.Contains("AS is_sysadmin", sql, StringComparison.Ordinal);
            AssertPassiveSql(sql);
        }

        Assert.Contains("SERVERPROPERTY(N'PathSeparator')", bootstrap, StringComparison.Ordinal);
        Assert.Contains("SERVERPROPERTY(N'PathSeparator')", fallback, StringComparison.Ordinal);
        Assert.Contains("WHEN N'/' THEN N'Linux'", fallback, StringComparison.Ordinal);

        Assert.Contains(
            "HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER STATE')",
            version15,
            StringComparison.Ordinal);
        Assert.DoesNotContain("VIEW SERVER PERFORMANCE STATE') AS has_required_permission", version15, StringComparison.Ordinal);
        foreach (string sql in new[] { version16, version17 })
        {
            Assert.Contains(
                "HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER PERFORMANCE STATE')",
                sql,
                StringComparison.Ordinal);
            Assert.Contains(
                "IS_SRVROLEMEMBER(N'##MS_ServerPerformanceStateReader##')",
                sql,
                StringComparison.Ordinal);
        }

        Assert.Contains("\"mode\": \"unsupported\"", catalog.ManifestJson, StringComparison.Ordinal);
        Assert.Contains("\"server.view-state\"", catalog.ManifestJson, StringComparison.Ordinal);
        Assert.Contains("\"server.view-performance-state\"", catalog.ManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionFactoryBuildsOnlyTheStrictIntegratedPolicy()
    {
        var namedEndpoint = new SqlServerEndpoint(
            new SqlServerHostName("sql01.contoso.example"),
            new SqlServerInstanceName("OBSERVE"));
        var policy = new SqlServerConnectionPolicy(
            namedEndpoint,
            new SqlServerConnectTimeout(TimeSpan.FromMilliseconds(5_100)),
            new SqlServerCertificateHostName("sql01.contoso.example"));

        using SqlConnection connection = SqlServerIntegratedConnectionFactory.CreateConnection(policy);
        var builder = new SqlConnectionStringBuilder(connection.ConnectionString);

        Assert.Equal("sql01.contoso.example\\OBSERVE", builder.DataSource);
        Assert.Equal("master", builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.Equal("sql01.contoso.example", builder.HostNameInCertificate);
        Assert.Equal(SqlServerIntegratedConnectionFactory.ApplicationName, builder.ApplicationName);
        Assert.Equal(5, builder.ConnectTimeout);
        Assert.False(builder.MultiSubnetFailover);
        Assert.Equal(ApplicationIntent.ReadWrite, builder.ApplicationIntent);
        Assert.False(builder.PersistSecurityInfo);
        Assert.False(builder.MultipleActiveResultSets);
        Assert.False(builder.Enlist);
        Assert.Empty(builder.UserID);
        Assert.Empty(builder.Password);

        var portEndpoint = new SqlServerEndpoint(new SqlServerHostName("10.10.20.30"), tcpPort: 1433);
        var portPolicy = new SqlServerConnectionPolicy(
            portEndpoint,
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)));
        using SqlConnection portConnection = SqlServerIntegratedConnectionFactory.CreateConnection(portPolicy);
        var portBuilder = new SqlConnectionStringBuilder(portConnection.ConnectionString);
        Assert.Equal("tcp:10.10.20.30,1433", portBuilder.DataSource);
        Assert.True(portBuilder.MultiSubnetFailover);
        Assert.Equal(ApplicationIntent.ReadWrite, portBuilder.ApplicationIntent);

        Type[] publicTypes = typeof(SqlObserver.Infrastructure.SqlServer.AssemblyMarker)
            .Assembly
            .GetExportedTypes();
        IEnumerable<MethodBase> publicCallSurfaces = publicTypes
            .SelectMany(static type => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Cast<MethodBase>()
                .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)));
        Assert.DoesNotContain(
            publicCallSurfaces,
            static member => member.GetParameters().Any(static parameter =>
                parameter.ParameterType == typeof(string) &&
                parameter.Name?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public void LinuxAndExcessPrivilegeEvidenceCannotBecomeHealthy()
    {
        (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) linux =
            SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(
                productMajorVersion: 16,
                engineEdition: 3,
                SqlServerPlatform.Linux,
                SqlServerAuthenticationScheme.Kerberos,
                transportEncrypted: true,
                isSysAdmin: false,
                hasRequiredPermission: true,
                usedPermissionFallback: false);
        Assert.Equal(CapabilityDiscoveryOutcome.Unsupported, linux.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.UnsupportedPlatform, linux.Reason);

        (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) sysAdmin =
            SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(
                productMajorVersion: 16,
                engineEdition: 4,
                SqlServerPlatform.Windows,
                SqlServerAuthenticationScheme.Kerberos,
                transportEncrypted: true,
                isSysAdmin: true,
                hasRequiredPermission: true,
                usedPermissionFallback: false);
        Assert.Equal(CapabilityDiscoveryOutcome.SecurityPolicyRejected, sysAdmin.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.ExcessivePrivilege, sysAdmin.Reason);

        (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) sharedMemory =
            SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(
                productMajorVersion: 16,
                engineEdition: 4,
                SqlServerPlatform.Windows,
                SqlServerAuthenticationScheme.Kerberos,
                transportEncrypted: false,
                isSysAdmin: false,
                hasRequiredPermission: true,
                usedPermissionFallback: true);
        Assert.Equal(CapabilityDiscoveryOutcome.SecurityPolicyRejected, sharedMemory.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.TransportNotEncrypted, sharedMemory.Reason);

        (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) effectiveDirectGrant =
            SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(
                productMajorVersion: 16,
                engineEdition: 4,
                SqlServerPlatform.Windows,
                SqlServerAuthenticationScheme.Kerberos,
                transportEncrypted: true,
                isSysAdmin: false,
                hasRequiredPermission: true,
                usedPermissionFallback: false);
        Assert.Equal(CapabilityDiscoveryOutcome.Supported, effectiveDirectGrant.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.Verified, effectiveDirectGrant.Reason);
    }

    [Fact]
    public void V2EvidenceDistinguishesDeniedPermissionFromUnsupportedEdition()
    {
        (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) denied =
            SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(
                productMajorVersion: 16,
                engineEdition: 3,
                SqlServerPlatform.Windows,
                SqlServerAuthenticationScheme.Kerberos,
                transportEncrypted: true,
                isSysAdmin: false,
                hasRequiredPermission: false,
                usedPermissionFallback: false);
        Assert.Equal(CapabilityDiscoveryOutcome.Degraded, denied.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.RequiredPermissionMissing, denied.Reason);

        (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) unsupportedEdition =
            SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(
                productMajorVersion: 16,
                engineEdition: 5,
                SqlServerPlatform.Windows,
                SqlServerAuthenticationScheme.Kerberos,
                transportEncrypted: true,
                isSysAdmin: false,
                hasRequiredPermission: true,
                usedPermissionFallback: false);
        Assert.Equal(CapabilityDiscoveryOutcome.Unsupported, unsupportedEdition.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.UnsupportedEdition, unsupportedEdition.Reason);
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    public async Task LocalSqlServerDiscoveryIsBoundedSecurityAwareAndNonMutating()
    {
        var connectionFactory = new LabSqlServerConnectionFactory();
        SqlServerCapabilityAssetCatalog catalog = SqlServerCapabilityAssetCatalog.LoadEmbedded();
        var adapter = new SqlServerCapabilityDiscoveryPort(connectionFactory, catalog);
        CapabilityDiscoveryRequest request = CreateLabRequest(TimeSpan.FromSeconds(10));

        TargetStateSnapshot before = await CaptureTargetStateAsync(connectionFactory);
        CapabilityProfile profile = await adapter.DiscoverAsync(request, CancellationToken.None);
        TargetStateSnapshot after = await CaptureTargetStateAsync(connectionFactory);

        Assert.Equal(before, after);
        Assert.Equal(request.TargetId, profile.TargetId);
        Assert.Equal(request.TargetRevision, profile.TargetRevision);
        Assert.Equal("capability.connection", profile.CollectorId.Value);
        Assert.Equal(1, profile.CollectorManifestVersion);
        Assert.Equal(1, profile.OutputSchemaVersion);
        Assert.NotNull(profile.ServerIdentity);
        // Compare discovery with independent server evidence so this contract
        // also validates supported SQL Server 2025 and non-Express labs.
        await using (SqlConnection identityConnection = await connectionFactory.OpenConnectionAsync(request.ConnectionPolicy, CancellationToken.None))
        await using (var identityCommand = new SqlCommand("SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion')), CONVERT(int, SERVERPROPERTY('EngineEdition'));", identityConnection) { CommandTimeout = 5 })
        await using (SqlDataReader identity = await identityCommand.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await identity.ReadAsync(CancellationToken.None));
            Assert.Equal(identity.GetInt32(0), profile.ServerIdentity.Version.Major);
            Assert.Equal((SqlServerEngineEdition)identity.GetInt32(1), profile.ServerIdentity.EngineEdition);
        }
        Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.Equal(SqlServerAuthenticationScheme.Ntlm, profile.AuthenticationScheme);
        Assert.True(profile.IsSysAdmin);
        Assert.Equal(CapabilityDiscoveryOutcome.SecurityPolicyRejected, profile.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.ExcessivePrivilege, profile.Reason);
        Assert.InRange(profile.EvidenceBytes, 1, 16_384);
        Assert.InRange(profile.DiscoveryDuration, TimeSpan.Zero, request.Timeout.Value);

        PermissionEvidence effectivePermission = Assert.Single(
            profile.Permissions,
            static permission => permission.PermissionId.Value == "server.view-performance-state");
        Assert.Equal(PermissionEvidenceOutcome.Granted, effectivePermission.Outcome);
        Assert.Single(
            profile.Permissions,
            static permission => permission.PermissionId.Value == "server.performance-reader-role-membership");
        Assert.Contains(
            profile.Capabilities,
            static capability => capability.CapabilityId.Value == "platform.windows" &&
                capability.Availability == CapabilityAvailability.Available);
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    public async Task LocalBackupPermissionDiscoveryAgreesWithNativeBackupQuery()
    {
        var connectionFactory = new LabSqlServerConnectionFactory();
        var discovery = new SqlServerCapabilityDiscoveryPort(
            connectionFactory,
            SqlServerCapabilityAssetCatalog.LoadEmbedded(),
            SqlServerCapabilityV2AssetCatalog.LoadEmbedded(),
            SqlServerCapabilityV3AssetCatalog.LoadEmbedded());
        CapabilityDiscoveryRequest request = CreateLabRequest(TimeSpan.FromSeconds(10));
        CapabilityProfile profile = await discovery.DiscoverAsync(request, CancellationToken.None);
        PermissionEvidence visibility = Assert.Single(profile.Permissions,
            static permission => permission.PermissionId.Value == "server.view-any-database");
        Assert.Equal(PermissionEvidenceOutcome.Granted, visibility.Outcome);

        await using SqlConnection connection = await connectionFactory.OpenConnectionAsync(
            request.ConnectionPolicy, CancellationToken.None);
        string query = SqlServerOperationalHealthAssetCatalog.LoadEmbedded().Get(
            $"backups.status.sqlserver{profile.ServerIdentity!.Version.Major}-windows.v1.sql");
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 10 };
        command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = 1538;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, CancellationToken.None);
        Assert.Equal(10, reader.FieldCount);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    public async Task LocalV4MetadataPermissionEvidenceAgreesWithNativeServerProbe()
    {
        var connectionFactory = new LabSqlServerConnectionFactory();
        var discovery = new SqlServerCapabilityDiscoveryPort(
            connectionFactory,
            SqlServerCapabilityAssetCatalog.LoadEmbedded(),
            SqlServerCapabilityV2AssetCatalog.LoadEmbedded(),
            SqlServerCapabilityV3AssetCatalog.LoadEmbedded(),
            assetsV4: SqlServerCapabilityV4AssetCatalog.LoadEmbedded());
        CapabilityDiscoveryRequest request = CreateLabRequest(TimeSpan.FromSeconds(10));
        CapabilityProfile profile = await discovery.DiscoverAsync(request, CancellationToken.None);

        Assert.Equal(4, profile.CollectorManifestVersion);
        Assert.Equal(4, profile.OutputSchemaVersion);
        Assert.NotNull(profile.ServerIdentity);
        PermissionEvidence metadata = Assert.Single(profile.Permissions,
            static permission => permission.PermissionId.Value == "server.view-any-definition");
        Assert.Equal(PermissionEvidenceScope.Server, metadata.Scope);
        await using SqlConnection connection = await connectionFactory.OpenConnectionAsync(
            request.ConnectionPolicy, CancellationToken.None);
        await using var probe = new SqlCommand(
            "SELECT CONVERT(bit,COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW ANY DEFINITION'),0));",
            connection) { CommandTimeout = 5 };
        bool nativeGrant = Assert.IsType<bool>(await probe.ExecuteScalarAsync(CancellationToken.None));
        Assert.Equal(nativeGrant ? PermissionEvidenceOutcome.Granted : PermissionEvidenceOutcome.Denied,
            metadata.Outcome);
    }

    [Fact]
    public async Task DiscoveryTimeoutCancelsAnActuallyBlockedAdapterOperation()
    {
        var adapter = new SqlServerCapabilityDiscoveryPort(
            new BlockingConnectionFactory(),
            SqlServerCapabilityAssetCatalog.LoadEmbedded());
        CapabilityDiscoveryRequest request = CreateLabRequest(TimeSpan.FromSeconds(1));
        Stopwatch stopwatch = Stopwatch.StartNew();

        CapabilityProfile profile = await adapter.DiscoverAsync(request, CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(CapabilityDiscoveryOutcome.TimedOut, profile.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.DiscoveryTimedOut, profile.Reason);
        Assert.Null(profile.ServerIdentity);
        Assert.Empty(profile.Capabilities);
        Assert.Empty(profile.Permissions);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedFromABlockedAdapterOperation()
    {
        var adapter = new SqlServerCapabilityDiscoveryPort(
            new BlockingConnectionFactory(),
            SqlServerCapabilityAssetCatalog.LoadEmbedded());
        CapabilityDiscoveryRequest request = CreateLabRequest(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await adapter.DiscoverAsync(request, cancellation.Token));
    }

    [Fact]
    public async Task ProductionConnectionFailureIsClassifiedWithoutRawErrorDetails()
    {
        var endpoint = new SqlServerEndpoint(new SqlServerHostName("127.0.0.1"), tcpPort: 1);
        var request = new CapabilityDiscoveryRequest(
            new MonitoredInstanceId(Guid.NewGuid()),
            new ObservationTargetRevision(1),
            new SqlServerConnectionPolicy(
                endpoint,
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))),
            new CapabilityDiscoveryTimeout(TimeSpan.FromSeconds(2)),
            new CapabilityProfileRefreshInterval(TimeSpan.FromMinutes(1)));
        var adapter = new SqlServerCapabilityDiscoveryPort();

        CapabilityProfile profile = await adapter.DiscoverAsync(
            request,
            CancellationToken.None);

        Assert.Contains(
            profile.Outcome,
            new[] { CapabilityDiscoveryOutcome.Unreachable, CapabilityDiscoveryOutcome.TimedOut });
        Assert.Null(profile.ServerIdentity);
        Assert.Equal(SqlServerAuthenticationScheme.Unknown, profile.AuthenticationScheme);
        Assert.False(profile.TransportEncrypted);
        Assert.False(profile.IsSysAdmin);
        Assert.Empty(profile.Capabilities);
        Assert.Empty(profile.Permissions);
        Assert.Equal(0, profile.EvidenceBytes);
        Assert.InRange(profile.DiscoveryDuration, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private static void AssertPassiveSql(string sql)
    {
        string normalized = string.Concat(" ", sql.ToUpperInvariant(), " ");
        string[] forbiddenTokens =
        [
            " INSERT ",
            " UPDATE ",
            " DELETE ",
            " MERGE ",
            " GRANT ",
            " REVOKE ",
            " DENY ",
            " CREATE ",
            " ALTER ",
            " DROP ",
            " TRUNCATE ",
            " EXEC ",
            " EXECUTE ",
            " DBCC ",
            " SP_CONFIGURE",
        ];

        foreach (string forbidden in forbiddenTokens)
        {
            Assert.DoesNotContain(forbidden, normalized, StringComparison.Ordinal);
        }
    }

    private static CapabilityDiscoveryRequest CreateLabRequest(TimeSpan timeout)
    {
        // The request is also used by cancellation-only tests; the live
        // factory below supplies the configured endpoint when it opens SQL.
        var endpoint = new SqlServerEndpoint(new SqlServerHostName("test"), new SqlServerInstanceName("SQLEXPRESS"));
        return new CapabilityDiscoveryRequest(
            new MonitoredInstanceId(Guid.NewGuid()),
            new ObservationTargetRevision(1),
            new SqlServerConnectionPolicy(
                endpoint,
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))),
            new CapabilityDiscoveryTimeout(timeout),
            new CapabilityProfileRefreshInterval(TimeSpan.FromMinutes(5)));
    }

    private static async Task<TargetStateSnapshot> CaptureTargetStateAsync(
        ISqlServerConnectionFactory connectionFactory)
    {
        CapabilityDiscoveryRequest request = CreateLabRequest(TimeSpan.FromSeconds(10));
        await using SqlConnection connection = await connectionFactory.OpenConnectionAsync(
            request.ConnectionPolicy,
            CancellationToken.None);
        const string sql = """
            SET NOCOUNT ON;

            SELECT
                (SELECT COUNT_BIG(*) FROM sys.configurations),
                (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(configuration_id, value, value_in_use)) FROM sys.configurations),
                (SELECT COUNT_BIG(*) FROM sys.server_event_sessions),
                (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(event_session_id, name, startup_state)) FROM sys.server_event_sessions),
                (SELECT COUNT_BIG(*) FROM sys.server_permissions),
                (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(class, major_id, minor_id, grantee_principal_id, permission_name, state)) FROM sys.server_permissions),
                (SELECT COUNT_BIG(*) FROM sys.server_role_members),
                (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(role_principal_id, member_principal_id)) FROM sys.server_role_members),
                (SELECT COUNT_BIG(*) FROM sys.databases),
                (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(database_id, is_auto_close_on, is_auto_shrink_on, is_query_store_on)) FROM sys.databases);
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return new TargetStateSnapshot(
            reader.GetInt64(0),
            GetNullableInt32(reader, 1),
            reader.GetInt64(2),
            GetNullableInt32(reader, 3),
            reader.GetInt64(4),
            GetNullableInt32(reader, 5),
            reader.GetInt64(6),
            GetNullableInt32(reader, 7),
            reader.GetInt64(8),
            GetNullableInt32(reader, 9));
    }

    private static int? GetNullableInt32(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private sealed class LabSqlServerConnectionFactory : ISqlServerConnectionFactory
    {
        public async ValueTask<SqlConnection> OpenConnectionAsync(
            SqlServerConnectionPolicy policy,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(policy);
            var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class BlockingConnectionFactory : ISqlServerConnectionFactory
    {
        public async ValueTask<SqlConnection> OpenConnectionAsync(
            SqlServerConnectionPolicy policy,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(policy);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocked test seam returned unexpectedly.");
        }
    }

    private sealed record TargetStateSnapshot(
        long ConfigurationCount,
        int? ConfigurationChecksum,
        long EventSessionCount,
        int? EventSessionChecksum,
        long PermissionCount,
        int? PermissionChecksum,
        long RoleMembershipCount,
        int? RoleMembershipChecksum,
        long DatabaseCount,
        int? DatabaseOptionsChecksum);
}
