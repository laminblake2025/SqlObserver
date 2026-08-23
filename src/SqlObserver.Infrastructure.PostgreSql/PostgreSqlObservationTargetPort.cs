using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Atomic, audited PostgreSQL persistence for structured observation targets.</summary>
public sealed class PostgreSqlObservationTargetPort : IObservationTargetRepositoryPort
{
    private const string TargetColumns = """
        instance_id,
        instance_key,
        display_name,
        host_name,
        instance_name,
        tcp_port,
        certificate_host_name,
        connect_timeout,
        authentication_mode,
        transport_security_mode,
        lifecycle_state,
        revision,
        created_at,
        discovery_requested_at,
        updated_at,
        retired_at
        """;
    private const string RegisterSql = """
        SELECT *
        FROM control.register_observation_target(
            @instance_id,
            @instance_key,
            @display_name,
            @host_name,
            @instance_name,
            @tcp_port,
            @certificate_host_name,
            @connect_timeout,
            @authentication_mode,
            @transport_security_mode,
            @actor_identifier,
            @correlation_id,
            @audit_action);
        """;
    private const string UpdateSql = """
        SELECT *
        FROM control.update_observation_target(
            @instance_id,
            @expected_revision,
            @display_name,
            @host_name,
            @instance_name,
            @tcp_port,
            @certificate_host_name,
            @connect_timeout,
            @authentication_mode,
            @transport_security_mode,
            @actor_identifier,
            @correlation_id,
            @audit_action);
        """;
    private const string RetireSql = """
        SELECT *
        FROM control.retire_observation_target(
            @instance_id,
            @expected_revision,
            @actor_identifier,
            @correlation_id,
            @audit_action);
        """;
    private const string RediscoverySql = """
        SELECT *
        FROM control.request_capability_rediscovery(
            @instance_id,
            @expected_revision,
            @actor_identifier,
            @correlation_id,
            @audit_action);
        """;
    private static readonly string GetSql = $"""
        SELECT {TargetColumns}
        FROM reporting.observation_target_status
        WHERE instance_id = @instance_id
          AND endpoint_configured;
        """;
    private static readonly string ListSql = $"""
        SELECT {TargetColumns}
        FROM reporting.observation_target_status
        WHERE endpoint_configured
          AND (@include_retired OR lifecycle_state <> 'retired')
          AND (@all_targets OR instance_id = ANY(@target_ids))
          AND
          (
              @cursor_key IS NULL
              OR (instance_key, instance_id) > (@cursor_key, @cursor_id)
          )
        ORDER BY instance_key, instance_id
        LIMIT @result_limit;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlObservationTargetPort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<ObservationTargetRegistrationResult> RegisterAsync(
        RegisterObservationTargetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        (string status, ObservationTarget? target) = await ExecuteMutationAsync(
                RegisterSql,
                request.Timeout,
                command =>
                {
                    command.Parameters.AddWithValue("instance_id", request.Registration.TargetId.Value);
                    command.Parameters.AddWithValue("instance_key", request.Registration.Key.Value);
                    AddTargetConfiguration(command, request.Registration.DisplayName, request.Registration.ConnectionPolicy);
                    AddAudit(command, request.Audit);
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new ObservationTargetRegistrationResult(
            status switch
            {
                "registered" => ObservationTargetRegistrationStatus.Registered,
                "already_exists" => ObservationTargetRegistrationStatus.AlreadyExists,
                "target_id_conflict" => ObservationTargetRegistrationStatus.TargetIdConflict,
                "target_key_conflict" => ObservationTargetRegistrationStatus.TargetKeyConflict,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown target-registration status."),
            },
            target);
    }

    public async ValueTask<ObservationTarget?> GetAsync(
        GetObservationTargetRequest request,
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
            await using var command = new NpgsqlCommand(GetSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("instance_id", request.TargetId.Value);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            return await reader.ReadAsync(timeout.Token).ConfigureAwait(false)
                ? ReadTarget(reader, 0)
                : null;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("observation-target read", exception);
        }
    }

    public async ValueTask<ObservationTargetPage> ListObservationTargetsAsync(
        ListObservationTargetsRequest request,
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
            await using var command = new NpgsqlCommand(ListSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("include_retired", request.IncludeRetired);
            command.Parameters.AddWithValue("all_targets", request.TargetScope.AllTargets);
            command.Parameters.Add(
                new NpgsqlParameter<Guid[]>("target_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    TypedValue = request.TargetScope.TargetIds.Select(static id => id.Value).ToArray(),
                });
            command.Parameters.Add(
                new NpgsqlParameter<string>("cursor_key", NpgsqlDbType.Text)
                {
                    TypedValue = request.Cursor?.LastKey.Value,
                });
            command.Parameters.Add(
                new NpgsqlParameter<Guid?>("cursor_id", NpgsqlDbType.Uuid)
                {
                    TypedValue = request.Cursor?.LastTargetId.Value,
                });
            command.Parameters.AddWithValue("result_limit", request.MaxResults + 1);

            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            var targets = new List<ObservationTarget>(request.MaxResults + 1);
            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                targets.Add(ReadTarget(reader, 0));
            }

            bool hasMore = targets.Count > request.MaxResults;
            if (hasMore)
            {
                targets.RemoveAt(targets.Count - 1);
            }

            ObservationTargetListCursor? nextCursor = hasMore
                ? new ObservationTargetListCursor(targets[^1].Key, targets[^1].TargetId)
                : null;
            return new ObservationTargetPage(targets, nextCursor);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("observation-target list", exception);
        }
    }

    public async ValueTask<ObservationTargetMutationResult> UpdateAsync(
        UpdateObservationTargetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteTargetMutationAsync(
                UpdateSql,
                request.TargetId,
                request.ExpectedRevision,
                request.Audit,
                request.Timeout,
                command => AddTargetConfiguration(command, request.DisplayName, request.ConnectionPolicy),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ObservationTargetMutationResult> RetireAsync(
        RetireObservationTargetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteTargetMutationAsync(
                RetireSql,
                request.TargetId,
                request.ExpectedRevision,
                request.Audit,
                request.Timeout,
                null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ObservationTargetMutationResult> RequestRediscoveryAsync(
        RequestCapabilityRediscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteTargetMutationAsync(
                RediscoverySql,
                request.TargetId,
                request.ExpectedRevision,
                request.Audit,
                request.Timeout,
                null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ObservationTargetMutationResult> ExecuteTargetMutationAsync(
        string sql,
        MonitoredInstanceId targetId,
        ObservationTargetRevision expectedRevision,
        AdministrativeAuditEnvelope audit,
        RepositoryCallTimeout timeout,
        Action<NpgsqlCommand>? addConfiguration,
        CancellationToken cancellationToken)
    {
        (string status, ObservationTarget? target) = await ExecuteMutationAsync(
                sql,
                timeout,
                command =>
                {
                    command.Parameters.AddWithValue("instance_id", targetId.Value);
                    command.Parameters.AddWithValue("expected_revision", expectedRevision.Value);
                    addConfiguration?.Invoke(command);
                    AddAudit(command, audit);
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new ObservationTargetMutationResult(
            status switch
            {
                "applied" => ObservationTargetMutationStatus.Applied,
                "not_found" => ObservationTargetMutationStatus.NotFound,
                "revision_conflict" => ObservationTargetMutationStatus.RevisionConflict,
                "already_retired" => ObservationTargetMutationStatus.AlreadyRetired,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown target-mutation status."),
            },
            target);
    }

    private async Task<(string Status, ObservationTarget? Target)> ExecuteMutationAsync(
        string sql,
        RepositoryCallTimeout timeoutValue,
        Action<NpgsqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            timeoutValue,
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
                        timeoutValue,
                        timeout.Token)
                    .ConfigureAwait(false);
                await using var command = new NpgsqlCommand(sql, connection, transaction)
                {
                    CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeoutValue),
                };
                addParameters(command);
                await using NpgsqlDataReader reader = await command
                    .ExecuteReaderAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("PostgreSQL target mutation returned no result row.");
                }

                string status = reader.GetString(0);
                ObservationTarget? target = reader.IsDBNull(1) ? null : ReadTarget(reader, 1);
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
                return (status, target);
            }
            catch
            {
                await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("observation-target mutation", exception);
        }
    }

    private static void AddTargetConfiguration(
        NpgsqlCommand command,
        ObservationTargetDisplayName displayName,
        SqlServerConnectionPolicy policy)
    {
        command.Parameters.AddWithValue("display_name", displayName.Value);
        command.Parameters.AddWithValue("host_name", policy.Endpoint.HostName.Value);
        command.Parameters.Add(
            new NpgsqlParameter<string>("instance_name", NpgsqlDbType.Text)
            {
                TypedValue = policy.Endpoint.InstanceName?.Value,
            });
        command.Parameters.Add(
            new NpgsqlParameter<int?>("tcp_port", NpgsqlDbType.Integer)
            {
                TypedValue = policy.Endpoint.TcpPort,
            });
        command.Parameters.Add(
            new NpgsqlParameter<string>("certificate_host_name", NpgsqlDbType.Text)
            {
                TypedValue = policy.CertificateHostName?.Value,
            });
        command.Parameters.AddWithValue("connect_timeout", policy.ConnectTimeout.Value);
        command.Parameters.AddWithValue("authentication_mode", "windows_integrated_service_identity");
        command.Parameters.AddWithValue("transport_security_mode", "mandatory_validated");
    }

    private static void AddAudit(NpgsqlCommand command, AdministrativeAuditEnvelope audit)
    {
        command.Parameters.AddWithValue("actor_identifier", audit.ActorSid.Value);
        command.Parameters.AddWithValue("correlation_id", audit.CorrelationId.Value);
        command.Parameters.AddWithValue("audit_action", GetAuditAction(audit.Action));
    }

    internal static string GetAuditAction(AdministrativeAuditAction action) => action switch
    {
        AdministrativeAuditAction.RegisterObservationTarget => "register_observation_target",
        AdministrativeAuditAction.UpdateObservationTarget => "update_observation_target",
        AdministrativeAuditAction.RetireObservationTarget => "retire_observation_target",
        AdministrativeAuditAction.RequestCapabilityRediscovery => "request_capability_rediscovery",
        AdministrativeAuditAction.RecordCapabilityProfile => "record_capability_profile",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    internal static ObservationTarget ReadTarget(NpgsqlDataReader reader, int offset)
    {
        string authenticationMode = reader.GetString(offset + 8);
        string transportSecurity = reader.GetString(offset + 9);
        if (authenticationMode != "windows_integrated_service_identity" ||
            transportSecurity != "mandatory_validated")
        {
            throw new InvalidDataException("PostgreSQL returned an unsupported target connection policy.");
        }

        var endpoint = new SqlServerEndpoint(
            new SqlServerHostName(reader.GetString(offset + 3)),
            reader.IsDBNull(offset + 4) ? null : new SqlServerInstanceName(reader.GetString(offset + 4)),
            reader.IsDBNull(offset + 5) ? null : reader.GetInt32(offset + 5));
        var policy = new SqlServerConnectionPolicy(
            endpoint,
            new SqlServerConnectTimeout(reader.GetFieldValue<TimeSpan>(offset + 7)),
            reader.IsDBNull(offset + 6)
                ? null
                : new SqlServerCertificateHostName(reader.GetString(offset + 6)));
        DateTimeOffset? retiredAt = reader.IsDBNull(offset + 15)
            ? null
            : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, offset + 15);

        return new ObservationTarget(
            new MonitoredInstanceId(reader.GetGuid(offset)),
            new ObservationTargetKey(reader.GetString(offset + 1)),
            new ObservationTargetDisplayName(reader.GetString(offset + 2)),
            policy,
            reader.GetString(offset + 10) switch
            {
                "pending_discovery" => ObservationTargetLifecycle.PendingDiscovery,
                "active" => ObservationTargetLifecycle.Active,
                "disabled" => ObservationTargetLifecycle.Disabled,
                "retired" => ObservationTargetLifecycle.Retired,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown target lifecycle."),
            },
            new ObservationTargetRevision(reader.GetInt64(offset + 11)),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, offset + 12),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, offset + 13),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, offset + 14),
            retiredAt);
    }

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
}
