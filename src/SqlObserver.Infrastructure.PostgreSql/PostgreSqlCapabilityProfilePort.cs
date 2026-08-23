using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Repository-clock capability scheduling and lease/revision-fenced profile history.</summary>
public sealed class PostgreSqlCapabilityProfilePort : ICapabilityProfileRepositoryPort
{
    private const string ListDueSql = """
        SELECT
            target_instance_id,
            target_revision,
            target_host_name,
            target_instance_name,
            target_tcp_port,
            target_certificate_host_name,
            target_connect_timeout,
            target_authentication_mode,
            target_transport_security_mode,
            has_more
        FROM control.list_due_capability_targets(@max_targets);
        """;
    private const string RecordSql = """
        SELECT result_status, recorded_at
        FROM control.record_capability_profile(
            @instance_id,
            @target_revision,
            @collector_id,
            @collector_manifest_version,
            @output_schema_version,
            @outcome,
            @discovery_reason,
            @sql_server_major_version,
            @sql_server_minor_version,
            @sql_server_build,
            @sql_server_revision,
            @edition,
            @engine_edition,
            @platform,
            @authentication_scheme,
            @transport_encrypted,
            @is_sysadmin,
            @duration_ms,
            @response_bytes,
            @checked_at,
            @valid_until,
            @capability_names,
            @capability_availabilities,
            @capability_reasons,
            @permission_names,
            @permission_scopes,
            @permission_states,
            @work_key,
            @owner_execution_id,
            @fencing_token,
            @actor_identifier,
            @correlation_id,
            @audit_action);
        """;
    private const string GetLatestSql = """
        SELECT *
        FROM control.get_latest_capability_profile(@instance_id);
        """;
    private const string GetLatestForTargetsSql = """
        SELECT *
        FROM control.get_latest_capability_profiles(@instance_ids);
        """;
    private const string GetCapabilitiesSql = """
        SELECT capability_name, availability, reason_code
        FROM control.get_capability_profile_reasons(@profile_id);
        """;
    private const string GetPermissionsSql = """
        SELECT permission_name, permission_scope, permission_state
        FROM control.get_capability_profile_permissions(@profile_id);
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlCapabilityProfilePort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<CapabilityDiscoveryDueBatch> ListDueAsync(
        CapabilityDiscoveryDueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(ListDueSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("max_targets", request.MaxTargets);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            var targets = new List<CapabilityDiscoveryDueTarget>(request.MaxTargets);
            bool hasMore = false;

            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                targets.Add(new CapabilityDiscoveryDueTarget(
                    new MonitoredInstanceId(reader.GetGuid(0)),
                    new ObservationTargetRevision(reader.GetInt64(1)),
                    ReadConnectionPolicy(reader, 2)));
                hasMore = reader.GetBoolean(9);
            }

            return new CapabilityDiscoveryDueBatch(targets, hasMore);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("capability due-list", exception);
        }
    }

    public async ValueTask<CapabilityProfileRecordResult> RecordAsync(
        RecordCapabilityProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection
                .BeginTransactionAsync(timeout.Token)
                .ConfigureAwait(false);

            try
            {
                await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(
                        connection,
                        transaction,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await using var command = new NpgsqlCommand(RecordSql, connection, transaction)
                {
                    CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
                };
                AddRecordParameters(command, request);
                await using NpgsqlDataReader reader = await command
                    .ExecuteReaderAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("PostgreSQL capability recording returned no result row.");
                }

                string statusValue = reader.GetString(0);
                DateTimeOffset? recordedAt = reader.IsDBNull(1)
                    ? null
                    : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1);
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);

                return new CapabilityProfileRecordResult(
                    statusValue switch
                    {
                        "recorded" => CapabilityProfileRecordStatus.Recorded,
                        "target_not_found" => CapabilityProfileRecordStatus.TargetNotFound,
                        "target_inactive" => CapabilityProfileRecordStatus.TargetInactive,
                        "revision_conflict" => CapabilityProfileRecordStatus.RevisionConflict,
                        _ => throw new InvalidDataException("PostgreSQL returned an unknown profile-record status."),
                    },
                    recordedAt);
            }
            catch
            {
                await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("capability profile record", exception);
        }
    }

    public async ValueTask<CapabilityProfile?> GetLatestAsync(
        GetLatestCapabilityProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var header = new NpgsqlCommand(GetLatestSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            header.Parameters.AddWithValue("instance_id", request.TargetId.Value);
            Guid profileId;
            ProfileHeader? profileHeader;
            await using (NpgsqlDataReader reader = await header
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    return null;
                }

                profileId = reader.GetGuid(0);
                profileHeader = ReadProfileHeader(reader);
            }

            IReadOnlyList<CapabilityEvidence> capabilities = await ReadCapabilitiesAsync(
                    connection,
                    profileId,
                    request.Timeout,
                    timeout.Token)
                .ConfigureAwait(false);
            IReadOnlyList<PermissionEvidence> permissions = await ReadPermissionsAsync(
                    connection,
                    profileId,
                    request.Timeout,
                    timeout.Token)
                .ConfigureAwait(false);

            return CreateProfile(profileHeader, capabilities, permissions);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("latest capability profile read", exception);
        }
    }

    public async ValueTask<CapabilityProfileBatch> GetLatestForTargetsAsync(
        GetLatestCapabilityProfilesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(GetLatestForTargetsSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.Add(
                new NpgsqlParameter<Guid[]>("instance_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    TypedValue = request.TargetIds.Select(static target => target.Value).ToArray(),
                });
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            var profiles = new List<CapabilityProfile>(request.TargetIds.Count);

            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                ProfileHeader header = ReadProfileHeader(reader);
                string[] capabilityNames = reader.GetFieldValue<string[]>(23);
                string[] capabilityAvailabilities = reader.GetFieldValue<string[]>(24);
                string[] capabilityReasons = reader.GetFieldValue<string[]>(25);
                string[] permissionNames = reader.GetFieldValue<string[]>(26);
                string[] permissionScopes = reader.GetFieldValue<string[]>(27);
                string[] permissionStates = reader.GetFieldValue<string[]>(28);
                if (capabilityNames.Length != capabilityAvailabilities.Length ||
                    capabilityNames.Length != capabilityReasons.Length ||
                    permissionNames.Length != permissionScopes.Length ||
                    permissionNames.Length != permissionStates.Length)
                {
                    throw new InvalidDataException("PostgreSQL returned misaligned capability-profile evidence arrays.");
                }

                var capabilities = new CapabilityEvidence[capabilityNames.Length];
                for (int index = 0; index < capabilities.Length; index++)
                {
                    capabilities[index] = new CapabilityEvidence(
                        new CapabilityId(capabilityNames[index]),
                        MapAvailability(capabilityAvailabilities[index]),
                        MapCapabilityReason(capabilityReasons[index]));
                }

                var permissions = new PermissionEvidence[permissionNames.Length];
                for (int index = 0; index < permissions.Length; index++)
                {
                    permissions[index] = new PermissionEvidence(
                        new SqlServerPermissionId(permissionNames[index]),
                        MapPermissionScope(permissionScopes[index]),
                        MapPermissionOutcome(permissionStates[index]));
                }

                profiles.Add(CreateProfile(header, capabilities, permissions));
            }

            return new CapabilityProfileBatch(profiles);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("bulk capability profile read", exception);
        }
    }

    private static void AddRecordParameters(NpgsqlCommand command, RecordCapabilityProfileRequest request)
    {
        CapabilityProfile profile = request.Profile;
        SqlServerIdentity? identity = profile.ServerIdentity;
        command.Parameters.AddWithValue("instance_id", profile.TargetId.Value);
        command.Parameters.AddWithValue("target_revision", profile.TargetRevision.Value);
        command.Parameters.AddWithValue("collector_id", profile.CollectorId.Value);
        command.Parameters.AddWithValue("collector_manifest_version", profile.CollectorManifestVersion);
        command.Parameters.AddWithValue("output_schema_version", profile.OutputSchemaVersion);
        command.Parameters.AddWithValue("outcome", MapOutcome(profile.Outcome));
        command.Parameters.AddWithValue("discovery_reason", MapDiscoveryReason(profile.Reason));
        AddNullable(command, "sql_server_major_version", NpgsqlDbType.Integer, identity?.Version.Major);
        AddNullable(command, "sql_server_minor_version", NpgsqlDbType.Integer, identity?.Version.Minor);
        AddNullable(command, "sql_server_build", NpgsqlDbType.Integer, identity?.Version.Build);
        AddNullable(command, "sql_server_revision", NpgsqlDbType.Integer, identity?.Version.Revision);
        AddNullable(command, "edition", NpgsqlDbType.Text, identity?.Edition.Value);
        AddNullable(
            command,
            "engine_edition",
            NpgsqlDbType.Integer,
            identity is null ? (int?)null : (int)identity.EngineEdition);
        AddNullable(command, "platform", NpgsqlDbType.Text, identity is null ? null : MapPlatform(identity.Platform));
        command.Parameters.AddWithValue("authentication_scheme", MapAuthenticationScheme(profile.AuthenticationScheme));
        command.Parameters.AddWithValue("transport_encrypted", profile.TransportEncrypted);
        command.Parameters.AddWithValue("is_sysadmin", profile.IsSysAdmin);
        command.Parameters.AddWithValue("duration_ms", checked((long)profile.DiscoveryDuration.TotalMilliseconds));
        command.Parameters.AddWithValue("response_bytes", profile.EvidenceBytes);
        command.Parameters.AddWithValue("checked_at", profile.CheckedAtUtc);
        command.Parameters.AddWithValue("valid_until", profile.ValidUntilUtc);
        command.Parameters.AddWithValue(
            "capability_names",
            profile.Capabilities.Select(static item => item.CapabilityId.Value).ToArray());
        command.Parameters.AddWithValue(
            "capability_availabilities",
            profile.Capabilities.Select(static item => MapAvailability(item.Availability)).ToArray());
        command.Parameters.AddWithValue(
            "capability_reasons",
            profile.Capabilities.Select(static item => MapCapabilityReason(item.Reason)).ToArray());
        command.Parameters.AddWithValue(
            "permission_names",
            profile.Permissions.Select(static item => item.PermissionId.Value).ToArray());
        command.Parameters.AddWithValue(
            "permission_scopes",
            profile.Permissions.Select(static item => MapPermissionScope(item.Scope)).ToArray());
        command.Parameters.AddWithValue(
            "permission_states",
            profile.Permissions.Select(static item => MapPermissionOutcome(item.Outcome)).ToArray());
        PostgreSqlWorkerLeasePort.AddLeaseIdentity(command, request.Lease);
        command.Parameters.AddWithValue("actor_identifier", request.Audit.ActorSid.Value);
        command.Parameters.AddWithValue("correlation_id", request.Audit.CorrelationId.Value);
        command.Parameters.AddWithValue(
            "audit_action",
            PostgreSqlObservationTargetPort.GetAuditAction(request.Audit.Action));
    }

    private static void AddNullable<T>(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        T? value)
    {
        command.Parameters.Add(new NpgsqlParameter<T>(name, type) { TypedValue = value });
    }

    private static ProfileHeader ReadProfileHeader(NpgsqlDataReader reader)
    {
        SqlServerIdentity? identity = reader.IsDBNull(8)
            ? null
            : new SqlServerIdentity(
                new SqlServerVersion(
                    reader.GetInt32(8),
                    reader.GetInt32(9),
                    reader.GetInt32(10),
                    reader.GetInt32(11)),
                new SqlServerEditionName(reader.GetString(12)),
                MapEngineEdition(reader.GetInt32(13)),
                MapPlatform(reader.GetString(14)));

        return new ProfileHeader(
            new MonitoredInstanceId(reader.GetGuid(1)),
            new ObservationTargetRevision(reader.GetInt64(2)),
            new CollectorId(reader.GetString(3)),
            reader.GetInt32(4),
            reader.GetInt32(5),
            identity,
            MapOutcome(reader.GetString(6)),
            MapDiscoveryReason(reader.GetString(7)),
            MapAuthenticationScheme(reader.GetString(15)),
            reader.GetBoolean(16),
            reader.GetBoolean(17),
            TimeSpan.FromMilliseconds(reader.GetInt64(18)),
            checked((int)reader.GetInt64(19)),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 20),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 21));
    }

    private static CapabilityProfile CreateProfile(
        ProfileHeader header,
        IReadOnlyList<CapabilityEvidence> capabilities,
        IReadOnlyList<PermissionEvidence> permissions) =>
        new(
            header.TargetId,
            header.TargetRevision,
            header.CollectorId,
            header.CollectorManifestVersion,
            header.OutputSchemaVersion,
            header.ServerIdentity,
            header.Outcome,
            header.Reason,
            header.AuthenticationScheme,
            header.TransportEncrypted,
            header.IsSysAdmin,
            capabilities,
            permissions,
            header.DiscoveryDuration,
            header.EvidenceBytes,
            header.CheckedAtUtc,
            header.ValidUntilUtc);

    private static async Task<IReadOnlyList<CapabilityEvidence>> ReadCapabilitiesAsync(
        NpgsqlConnection connection,
        Guid profileId,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(GetCapabilitiesSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        command.Parameters.AddWithValue("profile_id", profileId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CapabilityEvidence>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CapabilityEvidence(
                new CapabilityId(reader.GetString(0)),
                MapAvailability(reader.GetString(1)),
                MapCapabilityReason(reader.GetString(2))));
        }

        return result;
    }

    private static async Task<IReadOnlyList<PermissionEvidence>> ReadPermissionsAsync(
        NpgsqlConnection connection,
        Guid profileId,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(GetPermissionsSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        command.Parameters.AddWithValue("profile_id", profileId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PermissionEvidence>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PermissionEvidence(
                new SqlServerPermissionId(reader.GetString(0)),
                MapPermissionScope(reader.GetString(1)),
                MapPermissionOutcome(reader.GetString(2))));
        }

        return result;
    }

    private static SqlServerConnectionPolicy ReadConnectionPolicy(NpgsqlDataReader reader, int offset)
    {
        if (reader.GetString(offset + 5) != "windows_integrated_service_identity" ||
            reader.GetString(offset + 6) != "mandatory_validated")
        {
            throw new InvalidDataException("PostgreSQL returned an unsupported target connection policy.");
        }

        return new SqlServerConnectionPolicy(
            new SqlServerEndpoint(
                new SqlServerHostName(reader.GetString(offset)),
                reader.IsDBNull(offset + 1) ? null : new SqlServerInstanceName(reader.GetString(offset + 1)),
                reader.IsDBNull(offset + 2) ? null : reader.GetInt32(offset + 2)),
            new SqlServerConnectTimeout(reader.GetFieldValue<TimeSpan>(offset + 4)),
            reader.IsDBNull(offset + 3)
                ? null
                : new SqlServerCertificateHostName(reader.GetString(offset + 3)));
    }

    private static string MapOutcome(CapabilityDiscoveryOutcome value) => value switch
    {
        CapabilityDiscoveryOutcome.Supported => "supported",
        CapabilityDiscoveryOutcome.Degraded => "degraded",
        CapabilityDiscoveryOutcome.Unsupported => "unsupported",
        CapabilityDiscoveryOutcome.Unreachable => "unreachable",
        CapabilityDiscoveryOutcome.AuthenticationFailed => "authentication_failed",
        CapabilityDiscoveryOutcome.TlsValidationFailed => "tls_validation_failed",
        CapabilityDiscoveryOutcome.TimedOut => "timed_out",
        CapabilityDiscoveryOutcome.SecurityPolicyRejected => "security_policy_rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static CapabilityDiscoveryOutcome MapOutcome(string value) => value switch
    {
        "supported" => CapabilityDiscoveryOutcome.Supported,
        "degraded" => CapabilityDiscoveryOutcome.Degraded,
        "unsupported" => CapabilityDiscoveryOutcome.Unsupported,
        "unreachable" => CapabilityDiscoveryOutcome.Unreachable,
        "authentication_failed" => CapabilityDiscoveryOutcome.AuthenticationFailed,
        "tls_validation_failed" => CapabilityDiscoveryOutcome.TlsValidationFailed,
        "timed_out" => CapabilityDiscoveryOutcome.TimedOut,
        "security_policy_rejected" => CapabilityDiscoveryOutcome.SecurityPolicyRejected,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown capability outcome."),
    };

    private static string MapDiscoveryReason(CapabilityDiscoveryReason value) => value switch
    {
        CapabilityDiscoveryReason.Verified => "verified",
        CapabilityDiscoveryReason.OptionalCapabilityUnavailable => "optional_capability_unavailable",
        CapabilityDiscoveryReason.RequiredPermissionMissing => "required_permission_missing",
        CapabilityDiscoveryReason.UnsupportedVersion => "unsupported_version",
        CapabilityDiscoveryReason.UnsupportedPlatform => "unsupported_platform",
        CapabilityDiscoveryReason.NetworkUnreachable => "network_unreachable",
        CapabilityDiscoveryReason.ConnectionRefused => "connection_refused",
        CapabilityDiscoveryReason.AuthenticationRejected => "authentication_rejected",
        CapabilityDiscoveryReason.CertificateValidationFailed => "certificate_validation_failed",
        CapabilityDiscoveryReason.DiscoveryTimedOut => "discovery_timed_out",
        CapabilityDiscoveryReason.ExcessivePrivilege => "excessive_privilege",
        CapabilityDiscoveryReason.TransportNotEncrypted => "transport_not_encrypted",
        CapabilityDiscoveryReason.AuthenticationSchemeMismatch => "authentication_scheme_mismatch",
        CapabilityDiscoveryReason.AuthenticationSchemeFallback => "authentication_scheme_fallback",
        CapabilityDiscoveryReason.UnsupportedEdition => "unsupported_edition",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static CapabilityDiscoveryReason MapDiscoveryReason(string value) => value switch
    {
        "verified" => CapabilityDiscoveryReason.Verified,
        "optional_capability_unavailable" => CapabilityDiscoveryReason.OptionalCapabilityUnavailable,
        "required_permission_missing" => CapabilityDiscoveryReason.RequiredPermissionMissing,
        "unsupported_version" => CapabilityDiscoveryReason.UnsupportedVersion,
        "unsupported_platform" => CapabilityDiscoveryReason.UnsupportedPlatform,
        "network_unreachable" => CapabilityDiscoveryReason.NetworkUnreachable,
        "connection_refused" => CapabilityDiscoveryReason.ConnectionRefused,
        "authentication_rejected" => CapabilityDiscoveryReason.AuthenticationRejected,
        "certificate_validation_failed" => CapabilityDiscoveryReason.CertificateValidationFailed,
        "discovery_timed_out" => CapabilityDiscoveryReason.DiscoveryTimedOut,
        "excessive_privilege" => CapabilityDiscoveryReason.ExcessivePrivilege,
        "transport_not_encrypted" => CapabilityDiscoveryReason.TransportNotEncrypted,
        "authentication_scheme_mismatch" => CapabilityDiscoveryReason.AuthenticationSchemeMismatch,
        "authentication_scheme_fallback" => CapabilityDiscoveryReason.AuthenticationSchemeFallback,
        "unsupported_edition" => CapabilityDiscoveryReason.UnsupportedEdition,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown discovery reason."),
    };

    private static string MapPlatform(SqlServerPlatform value) => value switch
    {
        SqlServerPlatform.Windows => "windows",
        SqlServerPlatform.Linux => "linux",
        SqlServerPlatform.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SqlServerPlatform MapPlatform(string value) => value switch
    {
        "windows" => SqlServerPlatform.Windows,
        "linux" => SqlServerPlatform.Linux,
        "other" => SqlServerPlatform.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown SQL Server platform."),
    };

    private static SqlServerEngineEdition MapEngineEdition(int value) => value switch
    {
        2 => SqlServerEngineEdition.Standard,
        3 => SqlServerEngineEdition.Enterprise,
        4 => SqlServerEngineEdition.Express,
        5 => SqlServerEngineEdition.AzureSqlDatabase,
        6 => SqlServerEngineEdition.AzureSynapseAnalytics,
        8 => SqlServerEngineEdition.AzureSqlManagedInstance,
        255 => SqlServerEngineEdition.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown SQL Server engine edition."),
    };

    private static string MapAuthenticationScheme(SqlServerAuthenticationScheme value) => value switch
    {
        SqlServerAuthenticationScheme.Kerberos => "kerberos",
        SqlServerAuthenticationScheme.Ntlm => "ntlm",
        SqlServerAuthenticationScheme.SqlAuthentication => "sql_authentication",
        SqlServerAuthenticationScheme.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SqlServerAuthenticationScheme MapAuthenticationScheme(string value) => value switch
    {
        "kerberos" => SqlServerAuthenticationScheme.Kerberos,
        "ntlm" => SqlServerAuthenticationScheme.Ntlm,
        "sql_authentication" => SqlServerAuthenticationScheme.SqlAuthentication,
        "unknown" => SqlServerAuthenticationScheme.Unknown,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown authentication scheme."),
    };

    private static string MapAvailability(CapabilityAvailability value) => value switch
    {
        CapabilityAvailability.Available => "available",
        CapabilityAvailability.Unavailable => "unavailable",
        CapabilityAvailability.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static CapabilityAvailability MapAvailability(string value) => value switch
    {
        "available" => CapabilityAvailability.Available,
        "unavailable" => CapabilityAvailability.Unavailable,
        "unknown" => CapabilityAvailability.Unknown,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown capability availability."),
    };

    private static string MapCapabilityReason(CapabilityEvidenceReason value) => value switch
    {
        CapabilityEvidenceReason.Verified => "verified",
        CapabilityEvidenceReason.VersionUnsupported => "version_unsupported",
        CapabilityEvidenceReason.PlatformUnsupported => "platform_unsupported",
        CapabilityEvidenceReason.EditionUnsupported => "edition_unsupported",
        CapabilityEvidenceReason.PermissionDenied => "permission_denied",
        CapabilityEvidenceReason.FeatureDisabled => "feature_disabled",
        CapabilityEvidenceReason.EnhancedSetupAbsent => "enhanced_setup_absent",
        CapabilityEvidenceReason.ProbeUnavailable => "probe_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static CapabilityEvidenceReason MapCapabilityReason(string value) => value switch
    {
        "verified" => CapabilityEvidenceReason.Verified,
        "version_unsupported" => CapabilityEvidenceReason.VersionUnsupported,
        "platform_unsupported" => CapabilityEvidenceReason.PlatformUnsupported,
        "edition_unsupported" => CapabilityEvidenceReason.EditionUnsupported,
        "permission_denied" => CapabilityEvidenceReason.PermissionDenied,
        "feature_disabled" => CapabilityEvidenceReason.FeatureDisabled,
        "enhanced_setup_absent" => CapabilityEvidenceReason.EnhancedSetupAbsent,
        "probe_unavailable" => CapabilityEvidenceReason.ProbeUnavailable,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown capability reason."),
    };

    private static string MapPermissionScope(PermissionEvidenceScope value)
    {
        if (value == PermissionEvidenceScope.Server)
        {
            return "server";
        }

        if (value == PermissionEvidenceScope.Database)
        {
            return "database";
        }

        throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static PermissionEvidenceScope MapPermissionScope(string value) => value switch
    {
        "server" => PermissionEvidenceScope.Server,
        "database" => PermissionEvidenceScope.Database,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown permission scope."),
    };

    private static string MapPermissionOutcome(PermissionEvidenceOutcome value) => value switch
    {
        PermissionEvidenceOutcome.Granted => "granted",
        PermissionEvidenceOutcome.Denied => "denied",
        PermissionEvidenceOutcome.Unknown => "unknown",
        PermissionEvidenceOutcome.NotApplicable => "not_applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PermissionEvidenceOutcome MapPermissionOutcome(string value) => value switch
    {
        "granted" => PermissionEvidenceOutcome.Granted,
        "denied" => PermissionEvidenceOutcome.Denied,
        "unknown" => PermissionEvidenceOutcome.Unknown,
        "not_applicable" => PermissionEvidenceOutcome.NotApplicable,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown permission outcome."),
    };

    private static async Task RollbackWithoutMaskingAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Preserve the original failure; disposal handles a broken transaction/connection.
        }
    }

    private sealed record ProfileHeader(
        MonitoredInstanceId TargetId,
        ObservationTargetRevision TargetRevision,
        CollectorId CollectorId,
        int CollectorManifestVersion,
        int OutputSchemaVersion,
        SqlServerIdentity? ServerIdentity,
        CapabilityDiscoveryOutcome Outcome,
        CapabilityDiscoveryReason Reason,
        SqlServerAuthenticationScheme AuthenticationScheme,
        bool TransportEncrypted,
        bool IsSysAdmin,
        TimeSpan DiscoveryDuration,
        int EvidenceBytes,
        DateTimeOffset CheckedAtUtc,
        DateTimeOffset ValidUntilUtc);
}
