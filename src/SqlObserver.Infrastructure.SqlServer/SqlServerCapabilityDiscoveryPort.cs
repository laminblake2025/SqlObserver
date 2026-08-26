using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Runs the checksum-pinned, passive SQL Server capability discovery contract.</summary>
public sealed class SqlServerCapabilityDiscoveryPort : ISqlServerCapabilityDiscoveryPort
{
    private static readonly CapabilityId ConnectionCapabilityId = new("connection.tds");
    private static readonly CapabilityId WindowsAuthenticationCapabilityId =
        new("authentication.windows-integrated");
    private static readonly CapabilityId ValidatedTlsCapabilityId = new("transport.tls-validated");
    private static readonly CapabilityId NonSysAdminCapabilityId = new("privilege.non-sysadmin");
    private static readonly CapabilityId WindowsPlatformCapabilityId = new("platform.windows");
    private static readonly CapabilityId AvailabilityGroupsCapabilityId =
        new("feature.availability-groups");
    private static readonly CapabilityId SqlAgentHistoryCapabilityId = new("feature.sql-agent-history");
    private static readonly SqlServerPermissionId ViewServerStatePermissionId =
        new("server.view-state");
    private static readonly SqlServerPermissionId ViewServerPerformanceStatePermissionId =
        new("server.view-performance-state");
    private static readonly SqlServerPermissionId PerformanceReaderMembershipPermissionId =
        new("server.performance-reader-role-membership");
    private static readonly SqlServerPermissionId BackupsetSelectPermissionId = new("msdb.backupset.select");
    private static readonly SqlServerPermissionId SysjobhistorySelectPermissionId = new("msdb.sysjobhistory.select");

    private readonly ISqlServerConnectionFactory _connectionFactory;
    private readonly SqlServerCapabilityAssetCatalog _assets;
    private readonly SqlServerCapabilityV2AssetCatalog? _assetsV2;

    public SqlServerCapabilityDiscoveryPort()
        : this(
            new SqlServerIntegratedConnectionFactory(),
            SqlServerCapabilityAssetCatalog.LoadEmbedded(),
            SqlServerCapabilityV2AssetCatalog.LoadEmbedded())
    {
    }

    internal SqlServerCapabilityDiscoveryPort(
        ISqlServerConnectionFactory connectionFactory,
        SqlServerCapabilityAssetCatalog assets,
        SqlServerCapabilityV2AssetCatalog? assetsV2 = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _assetsV2 = assetsV2;
    }

    public async ValueTask<CapabilityProfile> DiscoverAsync(
        CapabilityDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        long started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout.Value);

        try
        {
            await using SqlConnection connection = await _connectionFactory
                .OpenConnectionAsync(request.ConnectionPolicy, timeout.Token)
                .ConfigureAwait(false);

            int commandTimeout = Math.Max(
                1,
                Math.Min(
                    SqlServerCapabilityAssetCatalog.CommandTimeoutSeconds,
                    checked((int)Math.Ceiling(request.Timeout.Value.TotalSeconds))));
            ProbeBudget budget = new(SqlServerCapabilityAssetCatalog.MaximumResponseBytes);
            BootstrapEvidence bootstrap = await ExecuteBootstrapAsync(
                    connection,
                    commandTimeout,
                    budget,
                    timeout.Token)
                .ConfigureAwait(false);

            if (bootstrap.ProductMajorVersion is < 15 or > 17)
            {
                DetailedEvidence unsupportedVersion = bootstrap.ToDetail(
                    platform: bootstrap.Platform,
                    hasRequiredPermission: false,
                    isPerformanceReaderMember: false,
                    transportEncrypted: bootstrap.TransportEncryptedByPolicy,
                    usedPermissionFallback: true);
                (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) unsupportedDisposition =
                    GetBootstrapDisposition(
                        unsupportedVersion,
                        CapabilityDiscoveryReason.UnsupportedVersion);
                return CreateConnectedProfile(
                    request,
                    unsupportedVersion,
                    unsupportedDisposition.Outcome,
                    unsupportedDisposition.Reason,
                    started,
                    budget.ConsumedBytes);
            }

            if (bootstrap.EngineEdition is not (2 or 3 or 4))
            {
                DetailedEvidence unsupportedEdition = bootstrap.ToDetail(
                    platform: bootstrap.Platform,
                    hasRequiredPermission: false,
                    isPerformanceReaderMember: bootstrap.IsPerformanceReaderMember,
                    transportEncrypted: bootstrap.TransportEncryptedByPolicy,
                    usedPermissionFallback: true);
                (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) editionDisposition =
                    GetBootstrapDisposition(
                        unsupportedEdition,
                        CapabilityDiscoveryReason.UnsupportedEdition);
                return CreateConnectedProfile(
                    request,
                    unsupportedEdition,
                    editionDisposition.Outcome,
                    editionDisposition.Reason,
                    started,
                    budget.ConsumedBytes);
            }

            DetailedEvidence detail;
            try
            {
                detail = await ExecuteDetailAsync(
                        connection,
                        _assetsV2?.Get($"capability.connection.sqlserver{bootstrap.ProductMajorVersion}-windows.v2.sql") ?? _assets.GetSupportedQuery(bootstrap.ProductMajorVersion),
                        commandTimeout,
                        budget,
                        usedPermissionFallback: false,
                        strictV2: _assetsV2 is not null,
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (SqlException exception) when (IsPermissionDenied(exception))
            {
                if (_assetsV2 is not null)
                {
                    // A v2 contract must never silently downgrade to the
                    // historical v1 fallback while still labeling its profile v2.
                    throw new InvalidDataException("Capability v2 detail evidence was denied; v1 fallback is forbidden.", exception);
                }
                detail = await ExecuteDetailAsync(
                        connection,
                        _assets.PermissionFallbackSql,
                        commandTimeout,
                        budget,
                        usedPermissionFallback: true,
                        strictV2: false,
                        timeout.Token)
                    .ConfigureAwait(false);
            }

            if (detail.ProductMajorVersion != bootstrap.ProductMajorVersion)
            {
                throw new InvalidDataException("SQL Server version evidence changed during capability discovery.");
            }

            (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) disposition =
                GetConnectedDisposition(detail);
            if (disposition.Outcome == CapabilityDiscoveryOutcome.Supported &&
                (!detail.HasBackupsetSelect || !detail.HasSysjobhistorySelect))
            {
                disposition = (CapabilityDiscoveryOutcome.Degraded, CapabilityDiscoveryReason.RequiredPermissionMissing);
            }
            return CreateConnectedProfile(
                request,
                detail,
                disposition.Outcome,
                disposition.Reason,
                started,
                budget.ConsumedBytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateConnectionFailureProfile(
                request,
                CapabilityDiscoveryOutcome.TimedOut,
                CapabilityDiscoveryReason.DiscoveryTimedOut,
                started);
        }
        catch (SqlException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (SqlException) when (timeout.IsCancellationRequested)
        {
            return CreateConnectionFailureProfile(
                request,
                CapabilityDiscoveryOutcome.TimedOut,
                CapabilityDiscoveryReason.DiscoveryTimedOut,
                started);
        }
        catch (SqlException exception)
        {
            (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason) failure =
                ClassifyConnectionFailure(exception);
            return CreateConnectionFailureProfile(request, failure.Outcome, failure.Reason, started);
        }
        catch (Exception exception) when (
            exception is AuthenticationException or CryptographicException &&
            !cancellationToken.IsCancellationRequested)
        {
            return CreateConnectionFailureProfile(
                request,
                CapabilityDiscoveryOutcome.TlsValidationFailed,
                CapabilityDiscoveryReason.CertificateValidationFailed,
                started);
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateConnectionFailureProfile(
                request,
                CapabilityDiscoveryOutcome.TimedOut,
                CapabilityDiscoveryReason.DiscoveryTimedOut,
                started);
        }
    }

    private async ValueTask<BootstrapEvidence> ExecuteBootstrapAsync(
        SqlConnection connection,
        int commandTimeout,
        ProbeBudget budget,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(_assets.BootstrapSql, connection)
        {
            CommandTimeout = commandTimeout,
        };
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleResult,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQL Server bootstrap discovery returned no evidence.");
        }

        var result = new BootstrapEvidence(
            reader.GetInt32(0),
            ReadBoundedString(reader, 1, 128, budget),
            ReadBoundedString(reader, 2, 128, budget),
            ReadBoundedString(reader, 3, 128, budget),
            reader.GetInt32(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.GetBoolean(10),
            ReadBoundedString(reader, 11, 40, budget),
            ReadBoundedString(reader, 12, 40, budget),
            ReadBoundedString(reader, 13, 1, budget));
        budget.AddFixedBytes(4 + (8 * sizeof(byte)));
        await EnsureSingleRowAsync(reader, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask<DetailedEvidence> ExecuteDetailAsync(
        SqlConnection connection,
        string commandText,
        int commandTimeout,
        ProbeBudget budget,
        bool usedPermissionFallback,
        bool strictV2,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(commandText, connection)
        {
            CommandTimeout = commandTimeout,
        };
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
                strictV2 ? CommandBehavior.Default : CommandBehavior.SingleResult,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQL Server detail discovery returned no evidence.");
        }

        const int expectedColumns = 16;
        if (reader.FieldCount != expectedColumns)
        {
            throw new InvalidDataException($"SQL Server capability result contract requires exactly {expectedColumns} columns.");
        }

        int productMajorVersion = reader.GetInt32(0);
        string productVersion = ReadBoundedString(reader, 1, 128, budget);
        string productLevel = ReadBoundedString(reader, 2, 128, budget);
        string edition = ReadBoundedString(reader, 3, 128, budget);
        int engineEdition = reader.GetInt32(4);
        string? hostPlatform = ReadNullableBoundedString(reader, 5, 256, budget);
        string? hostDistribution = ReadNullableBoundedString(reader, 6, 256, budget);
        string? hostRelease = ReadNullableBoundedString(reader, 7, 256, budget);
        bool isHadrEnabled = reader.GetBoolean(8);
        bool integratedSecurityOnly = reader.GetBoolean(9);
        bool hasRequiredPermission = reader.GetBoolean(10);
        bool isPerformanceReaderMember = reader.GetBoolean(11);
        bool isSysAdmin = reader.GetBoolean(12);
        string authScheme = ReadBoundedString(reader, 13, 40, budget);
        string netTransport = ReadBoundedString(reader, 14, 40, budget);
        string? encryptOption = ReadNullableBoundedString(reader, 15, 40, budget);
        bool hasBackupsetSelect = false;
        bool hasSysjobhistorySelect = false;
        bool hasSqlAgentHistory = false;
        budget.AddFixedBytes(8 + (7 * sizeof(byte)));
        await EnsureSingleRowAsync(reader, cancellationToken).ConfigureAwait(false);
        if (strictV2)
        {
            if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) || reader.FieldCount != 3 || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Capability v2 database/feature evidence result is missing or malformed.");
            hasBackupsetSelect = reader.GetBoolean(0);
            hasSysjobhistorySelect = reader.GetBoolean(1);
            hasSqlAgentHistory = reader.GetBoolean(2);
            await EnsureSingleRowAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        return new DetailedEvidence(
            productMajorVersion,
            productVersion,
            productLevel,
            edition,
            engineEdition,
            ParsePlatform(hostPlatform),
            hostDistribution,
            hostRelease,
            isHadrEnabled,
            integratedSecurityOnly,
            hasRequiredPermission,
            isPerformanceReaderMember,
            isSysAdmin,
            ParseAuthenticationScheme(authScheme),
            netTransport,
            encryptOption is null
                ? IsTransportEncryptedByPolicy(netTransport)
                : string.Equals(encryptOption, "TRUE", StringComparison.OrdinalIgnoreCase),
            usedPermissionFallback, hasBackupsetSelect, hasSysjobhistorySelect, hasSqlAgentHistory);
    }

    private static async ValueTask EnsureSingleRowAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQL Server discovery exceeded its one-row bound.");
        }
    }

    private static string ReadBoundedString(
        SqlDataReader reader,
        int ordinal,
        int maximumCharacters,
        ProbeBudget budget)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidDataException("SQL Server discovery returned incomplete required evidence.");
        }

        string value = reader.GetString(ordinal);
        if (value.Length > maximumCharacters)
        {
            throw new InvalidDataException("SQL Server discovery returned overlong evidence.");
        }

        budget.AddString(value);
        return value;
    }

    private static string? ReadNullableBoundedString(
        SqlDataReader reader,
        int ordinal,
        int maximumCharacters,
        ProbeBudget budget)
    {
        if (reader.IsDBNull(ordinal))
        {
            budget.AddFixedBytes(1);
            return null;
        }

        return ReadBoundedString(reader, ordinal, maximumCharacters, budget);
    }

    private static (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason)
        GetBootstrapDisposition(
            DetailedEvidence detail,
            CapabilityDiscoveryReason unsupportedReason)
    {
        if (detail.IsSysAdmin)
        {
            return (
                CapabilityDiscoveryOutcome.SecurityPolicyRejected,
                CapabilityDiscoveryReason.ExcessivePrivilege);
        }

        if (detail.AuthenticationScheme is not (
                SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm))
        {
            return (
                CapabilityDiscoveryOutcome.SecurityPolicyRejected,
                CapabilityDiscoveryReason.AuthenticationSchemeMismatch);
        }

        if (!detail.TransportEncrypted)
        {
            return (
                CapabilityDiscoveryOutcome.SecurityPolicyRejected,
                CapabilityDiscoveryReason.TransportNotEncrypted);
        }

        return (CapabilityDiscoveryOutcome.Unsupported, unsupportedReason);
    }

    private static (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason)
        GetConnectedDisposition(DetailedEvidence detail)
    {
        return ClassifyConnectedEvidence(
            detail.ProductMajorVersion,
            detail.EngineEdition,
            detail.Platform,
            detail.AuthenticationScheme,
            detail.TransportEncrypted,
            detail.IsSysAdmin,
            detail.HasRequiredPermission,
            detail.UsedPermissionFallback);
    }

    internal static (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason)
        ClassifyConnectedEvidence(
            int productMajorVersion,
            int engineEdition,
            SqlServerPlatform platform,
            SqlServerAuthenticationScheme authenticationScheme,
            bool transportEncrypted,
            bool isSysAdmin,
            bool hasRequiredPermission,
            bool usedPermissionFallback)
    {
        if (isSysAdmin)
        {
            return (
                CapabilityDiscoveryOutcome.SecurityPolicyRejected,
                CapabilityDiscoveryReason.ExcessivePrivilege);
        }

        if (authenticationScheme is not (
                SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm))
        {
            return (
                CapabilityDiscoveryOutcome.SecurityPolicyRejected,
                CapabilityDiscoveryReason.AuthenticationSchemeMismatch);
        }

        if (!transportEncrypted)
        {
            return (
                CapabilityDiscoveryOutcome.SecurityPolicyRejected,
                CapabilityDiscoveryReason.TransportNotEncrypted);
        }

        if (productMajorVersion is < 15 or > 17)
        {
            return (
                CapabilityDiscoveryOutcome.Unsupported,
                CapabilityDiscoveryReason.UnsupportedVersion);
        }

        if (platform != SqlServerPlatform.Windows)
        {
            return (
                CapabilityDiscoveryOutcome.Unsupported,
                CapabilityDiscoveryReason.UnsupportedPlatform);
        }

        if (engineEdition is not (2 or 3 or 4))
        {
            return (
                CapabilityDiscoveryOutcome.Unsupported,
                CapabilityDiscoveryReason.UnsupportedEdition);
        }

        if (authenticationScheme == SqlServerAuthenticationScheme.Ntlm)
        {
            return (
                CapabilityDiscoveryOutcome.Degraded,
                CapabilityDiscoveryReason.AuthenticationSchemeFallback);
        }

        if (!hasRequiredPermission || usedPermissionFallback)
        {
            return (
                CapabilityDiscoveryOutcome.Degraded,
                CapabilityDiscoveryReason.RequiredPermissionMissing);
        }

        return (CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified);
    }

    private CapabilityProfile CreateConnectedProfile(
        CapabilityDiscoveryRequest request,
        DetailedEvidence detail,
        CapabilityDiscoveryOutcome outcome,
        CapabilityDiscoveryReason reason,
        long started,
        int evidenceBytes)
    {
        SqlServerIdentity identity = new(
            ParseVersion(detail.ProductMajorVersion, detail.ProductVersion),
            new SqlServerEditionName(detail.Edition),
            ParseEngineEdition(detail.EngineEdition),
            detail.Platform);
        CapabilityEvidence[] capabilities = CreateCapabilityEvidence(detail);
        PermissionEvidence[] permissions = CreatePermissionEvidence(detail);
        DateTimeOffset checkedAt = GetUtcMicrosecondNow();

        return new CapabilityProfile(
            request.TargetId,
            request.TargetRevision,
            _assets.Manifest.Id,
            _assetsV2?.ManifestVersion ?? _assets.Manifest.ManifestVersion.Value,
            _assetsV2 is null ? _assets.Manifest.OutputSchemaVersion.Value : 2,
            identity,
            outcome,
            reason,
            detail.AuthenticationScheme,
            detail.TransportEncrypted,
            detail.IsSysAdmin,
            capabilities,
            permissions,
            GetBoundedDuration(started),
            evidenceBytes,
            checkedAt,
            AddAligned(checkedAt, request.RefreshInterval.Value));
    }

    private CapabilityProfile CreateConnectionFailureProfile(
        CapabilityDiscoveryRequest request,
        CapabilityDiscoveryOutcome outcome,
        CapabilityDiscoveryReason reason,
        long started)
    {
        DateTimeOffset checkedAt = GetUtcMicrosecondNow();
        return new CapabilityProfile(
            request.TargetId,
            request.TargetRevision,
            _assets.Manifest.Id,
            _assetsV2?.ManifestVersion ?? _assets.Manifest.ManifestVersion.Value,
            _assetsV2 is null ? _assets.Manifest.OutputSchemaVersion.Value : 2,
            serverIdentity: null,
            outcome,
            reason,
            SqlServerAuthenticationScheme.Unknown,
            transportEncrypted: false,
            isSysAdmin: false,
            capabilities: [],
            permissions: [],
            GetBoundedDuration(started),
            evidenceBytes: 0,
            checkedAt,
            AddAligned(checkedAt, request.RefreshInterval.Value));
    }

    private static CapabilityEvidence[] CreateCapabilityEvidence(DetailedEvidence detail)
    {
        bool windowsAuthentication = detail.AuthenticationScheme is
            SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm;
        CapabilityAvailability platformAvailability = detail.Platform switch
        {
            SqlServerPlatform.Windows => CapabilityAvailability.Available,
            SqlServerPlatform.Linux => CapabilityAvailability.Unavailable,
            _ => CapabilityAvailability.Unknown,
        };
        CapabilityEvidenceReason platformReason = detail.Platform switch
        {
            SqlServerPlatform.Windows => CapabilityEvidenceReason.Verified,
            SqlServerPlatform.Linux => CapabilityEvidenceReason.PlatformUnsupported,
            _ => CapabilityEvidenceReason.ProbeUnavailable,
        };

        return
        [
            new CapabilityEvidence(
                ConnectionCapabilityId,
                CapabilityAvailability.Available,
                CapabilityEvidenceReason.Verified),
            new CapabilityEvidence(
                WindowsAuthenticationCapabilityId,
                windowsAuthentication ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable,
                windowsAuthentication ? CapabilityEvidenceReason.Verified : CapabilityEvidenceReason.ProbeUnavailable),
            new CapabilityEvidence(
                ValidatedTlsCapabilityId,
                detail.TransportEncrypted ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable,
                detail.TransportEncrypted ? CapabilityEvidenceReason.Verified : CapabilityEvidenceReason.FeatureDisabled),
            new CapabilityEvidence(
                NonSysAdminCapabilityId,
                detail.IsSysAdmin ? CapabilityAvailability.Unavailable : CapabilityAvailability.Available,
                detail.IsSysAdmin ? CapabilityEvidenceReason.PermissionDenied : CapabilityEvidenceReason.Verified),
            new CapabilityEvidence(WindowsPlatformCapabilityId, platformAvailability, platformReason),
            new CapabilityEvidence(
                AvailabilityGroupsCapabilityId,
                detail.IsHadrEnabled ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable,
                detail.IsHadrEnabled ? CapabilityEvidenceReason.Verified : CapabilityEvidenceReason.FeatureDisabled),
            new CapabilityEvidence(
                SqlAgentHistoryCapabilityId,
                detail.HasSqlAgentHistory ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable,
                detail.HasSqlAgentHistory ? CapabilityEvidenceReason.Verified : CapabilityEvidenceReason.FeatureDisabled),
        ];
    }

    private static PermissionEvidence[] CreatePermissionEvidence(DetailedEvidence detail)
    {
        SqlServerPermissionId requiredPermission = detail.ProductMajorVersion == 15
            ? ViewServerStatePermissionId
            : ViewServerPerformanceStatePermissionId;
        PermissionEvidenceOutcome roleOutcome = detail.ProductMajorVersion == 15
            ? PermissionEvidenceOutcome.NotApplicable
            : detail.IsPerformanceReaderMember
                ? PermissionEvidenceOutcome.Granted
                : PermissionEvidenceOutcome.Denied;

        return
        [
            new PermissionEvidence(
                requiredPermission,
                PermissionEvidenceScope.Server,
                detail.HasRequiredPermission
                    ? PermissionEvidenceOutcome.Granted
                    : PermissionEvidenceOutcome.Denied),
            new PermissionEvidence(
                PerformanceReaderMembershipPermissionId,
                PermissionEvidenceScope.Server,
                roleOutcome),
            new PermissionEvidence(BackupsetSelectPermissionId, PermissionEvidenceScope.Database,
                detail.HasBackupsetSelect ? PermissionEvidenceOutcome.Granted : PermissionEvidenceOutcome.Denied),
            new PermissionEvidence(SysjobhistorySelectPermissionId, PermissionEvidenceScope.Database,
                detail.HasSysjobhistorySelect ? PermissionEvidenceOutcome.Granted : PermissionEvidenceOutcome.Denied),
        ];
    }

    private static SqlServerVersion ParseVersion(int expectedMajor, string productVersion)
    {
        string[] components = productVersion.Split('.', StringSplitOptions.None);
        if (components.Length is < 3 or > 4 ||
            !int.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture, out int build) ||
            (components.Length == 4 && !int.TryParse(
                components[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _)) ||
            major != expectedMajor)
        {
            throw new InvalidDataException("SQL Server returned an invalid product version.");
        }

        int parsedRevision = components.Length == 4
            ? int.Parse(components[3], CultureInfo.InvariantCulture)
            : 0;
        return new SqlServerVersion(major, minor, build, parsedRevision);
    }

    private static SqlServerEngineEdition ParseEngineEdition(int value)
    {
        return value switch
        {
            2 => SqlServerEngineEdition.Standard,
            3 => SqlServerEngineEdition.Enterprise,
            4 => SqlServerEngineEdition.Express,
            5 => SqlServerEngineEdition.AzureSqlDatabase,
            6 => SqlServerEngineEdition.AzureSynapseAnalytics,
            8 => SqlServerEngineEdition.AzureSqlManagedInstance,
            _ => SqlServerEngineEdition.Other,
        };
    }

    private static SqlServerPlatform ParsePlatform(string? value)
    {
        if (string.Equals(value, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            return SqlServerPlatform.Windows;
        }

        return string.Equals(value, "Linux", StringComparison.OrdinalIgnoreCase)
            ? SqlServerPlatform.Linux
            : SqlServerPlatform.Other;
    }

    private static SqlServerAuthenticationScheme ParseAuthenticationScheme(string value)
    {
        if (string.Equals(value, "KERBEROS", StringComparison.OrdinalIgnoreCase))
        {
            return SqlServerAuthenticationScheme.Kerberos;
        }

        if (string.Equals(value, "NTLM", StringComparison.OrdinalIgnoreCase))
        {
            return SqlServerAuthenticationScheme.Ntlm;
        }

        return string.Equals(value, "SQL", StringComparison.OrdinalIgnoreCase)
            ? SqlServerAuthenticationScheme.SqlAuthentication
            : SqlServerAuthenticationScheme.Unknown;
    }

    private static bool IsTransportEncryptedByPolicy(string netTransport)
    {
        return string.Equals(netTransport, "TCP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(netTransport, "Named pipe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPermissionDenied(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number is 229 or 297 or 300)
            {
                return true;
            }
        }

        return false;
    }

    private static (CapabilityDiscoveryOutcome Outcome, CapabilityDiscoveryReason Reason)
        ClassifyConnectionFailure(SqlException exception)
    {
        if (HasErrorNumber(exception, -2, 258))
        {
            return (CapabilityDiscoveryOutcome.TimedOut, CapabilityDiscoveryReason.DiscoveryTimedOut);
        }

        if (HasCertificateFailure(exception))
        {
            return (
                CapabilityDiscoveryOutcome.TlsValidationFailed,
                CapabilityDiscoveryReason.CertificateValidationFailed);
        }

        if (HasErrorNumber(exception, 18452, 18456, 18458, 18488))
        {
            return (
                CapabilityDiscoveryOutcome.AuthenticationFailed,
                CapabilityDiscoveryReason.AuthenticationRejected);
        }

        if (HasErrorNumber(exception, 20, 64, 233, 10054, 10061))
        {
            return (
                CapabilityDiscoveryOutcome.Unreachable,
                CapabilityDiscoveryReason.ConnectionRefused);
        }

        return (
            CapabilityDiscoveryOutcome.Unreachable,
            CapabilityDiscoveryReason.NetworkUnreachable);
    }

    private static bool HasErrorNumber(SqlException exception, params int[] numbers)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (numbers.Contains(error.Number))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCertificateFailure(Exception exception)
    {
        int? sqlNumber = exception is SqlException sqlException ? sqlException.Number : null;
        if (sqlNumber is -2_146_893_019 or -2_146_893_022 or -2_146_762_487 or -2_146_762_481)
        {
            return true;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException or CryptographicException)
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan GetBoundedDuration(long started)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        return elapsed <= CapabilityProfile.MaximumDiscoveryDuration
            ? elapsed
            : CapabilityProfile.MaximumDiscoveryDuration;
    }

    private static DateTimeOffset GetUtcMicrosecondNow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }

    private static DateTimeOffset AddAligned(DateTimeOffset value, TimeSpan interval)
    {
        DateTimeOffset result = value.Add(interval);
        return new DateTimeOffset(result.Ticks - (result.Ticks % 10), TimeSpan.Zero);
    }

    private sealed class ProbeBudget
    {
        private readonly int _maximumBytes;

        internal ProbeBudget(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
        }

        internal int ConsumedBytes { get; private set; }

        internal void AddString(string value)
        {
            AddFixedBytes(Encoding.UTF8.GetByteCount(value));
        }

        internal void AddFixedBytes(int count)
        {
            ConsumedBytes = checked(ConsumedBytes + count);
            if (ConsumedBytes > _maximumBytes)
            {
                throw new InvalidDataException("SQL Server discovery exceeded its response-byte bound.");
            }
        }
    }

    private sealed record BootstrapEvidence(
        int ProductMajorVersion,
        string ProductVersion,
        string ProductLevel,
        string Edition,
        int EngineEdition,
        bool IsHadrEnabled,
        bool IntegratedSecurityOnly,
        bool HasViewServerState,
        bool HasViewServerPerformanceState,
        bool IsPerformanceReaderMember,
        bool IsSysAdmin,
        string AuthScheme,
        string NetTransport,
        string PathSeparator)
    {
        internal SqlServerPlatform Platform => PathSeparator switch
        {
            "\\" => SqlServerPlatform.Windows,
            "/" => SqlServerPlatform.Linux,
            _ => SqlServerPlatform.Other,
        };

        internal bool TransportEncryptedByPolicy => IsTransportEncryptedByPolicy(NetTransport);

        internal DetailedEvidence ToDetail(
            SqlServerPlatform platform,
            bool hasRequiredPermission,
            bool isPerformanceReaderMember,
            bool transportEncrypted,
            bool usedPermissionFallback)
        {
            return new DetailedEvidence(
                ProductMajorVersion,
                ProductVersion,
                ProductLevel,
                Edition,
                EngineEdition,
                platform,
                HostDistribution: null,
                HostRelease: null,
                IsHadrEnabled,
                IntegratedSecurityOnly,
                hasRequiredPermission,
                isPerformanceReaderMember,
                IsSysAdmin,
                ParseAuthenticationScheme(AuthScheme),
                NetTransport,
                transportEncrypted,
                usedPermissionFallback);
        }
    }

    private sealed record DetailedEvidence(
        int ProductMajorVersion,
        string ProductVersion,
        string ProductLevel,
        string Edition,
        int EngineEdition,
        SqlServerPlatform Platform,
        string? HostDistribution,
        string? HostRelease,
        bool IsHadrEnabled,
        bool IntegratedSecurityOnly,
        bool HasRequiredPermission,
        bool IsPerformanceReaderMember,
        bool IsSysAdmin,
        SqlServerAuthenticationScheme AuthenticationScheme,
        string NetTransport,
        bool TransportEncrypted,
        bool UsedPermissionFallback,
        bool HasBackupsetSelect = false,
        bool HasSysjobhistorySelect = false,
        bool HasSqlAgentHistory = false);
}
