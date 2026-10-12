using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Deduplicates only already-protected diagnostic content. The externally computed fingerprint is
/// deterministic while protection may be randomized, so the first committed protected representation
/// wins and later matches return its identifier without updating nonce, tag, ciphertext, or key metadata.
/// Plaintext and key material are not accepted.
/// </summary>
public sealed class PostgreSqlSensitivePayloadPort : ISensitivePayloadPort
{
    private const string InsertSql = """
        INSERT INTO security.protected_diagnostic_payload
        (
            payload_id,
            instance_id,
            payload_kind,
            fingerprint,
            protection_algorithm,
            key_identifier,
            nonce,
            authentication_tag,
            ciphertext
        )
        VALUES
        (
            @payload_id,
            @instance_id,
            @payload_kind,
            @fingerprint,
            @protection_algorithm,
            @key_identifier,
            @nonce,
            @authentication_tag,
            @ciphertext
        )
        ON CONFLICT (payload_kind, fingerprint) DO UPDATE
        SET last_seen_at = clock_timestamp()
        WHERE security.protected_diagnostic_payload.instance_id = EXCLUDED.instance_id
          AND (security.protected_diagnostic_payload.last_seen_at IS NULL
               OR security.protected_diagnostic_payload.last_seen_at < clock_timestamp() - interval '1 day')
        RETURNING payload_id;
        """;
    private const string SelectExistingSql = """
        SELECT payload_id
        FROM security.protected_diagnostic_payload
        WHERE payload_kind = @payload_kind
          AND fingerprint = @fingerprint
          AND instance_id = @instance_id;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSensitivePayloadPort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<SensitivePayloadReference> GetOrAddAsync(
        SensitivePayloadGetOrAddRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string payloadKind = GetDatabaseKind(request.Payload.Kind);
        byte[] fingerprint = request.Payload.Fingerprint.ToArray();
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
                await PostgreSqlPartitionMaintenancePort.AssertLeaseAsync(
                        connection,
                        transaction,
                        request.Lease,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);

                Guid proposedId = Guid.NewGuid();
                Guid? persistedId;
                await using (var insert = new NpgsqlCommand(InsertSql, connection, transaction)
                {
                    CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
                })
                {
                    insert.Parameters.AddWithValue("payload_id", proposedId);
                    insert.Parameters.AddWithValue("instance_id", request.TargetId.Value);
                    insert.Parameters.AddWithValue("payload_kind", payloadKind);
                    insert.Parameters.AddWithValue("fingerprint", fingerprint);
                    insert.Parameters.AddWithValue("protection_algorithm", request.Payload.ProtectionAlgorithm);
                    insert.Parameters.AddWithValue("key_identifier", request.Payload.KeyIdentifier);
                    insert.Parameters.AddWithValue("nonce", request.Payload.GetNonce());
                    insert.Parameters.AddWithValue("authentication_tag", request.Payload.GetAuthenticationTag());
                    insert.Parameters.AddWithValue("ciphertext", request.Payload.GetCiphertext());
                    object? inserted = await insert.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);
                    persistedId = inserted as Guid?;
                }

                if (persistedId is null)
                {
                    await using var select = new NpgsqlCommand(SelectExistingSql, connection, transaction)
                    {
                        CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
                    };
                    select.Parameters.AddWithValue("payload_kind", payloadKind);
                    select.Parameters.AddWithValue("fingerprint", fingerprint);
                    select.Parameters.AddWithValue("instance_id", request.TargetId.Value);
                    object? existing = await select.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);
                    persistedId = existing as Guid? ?? throw new InvalidOperationException(
                        "Protected-payload fingerprint is already owned by another or legacy target.");
                }

                await PostgreSqlPartitionMaintenancePort.AssertLeaseAsync(
                        connection,
                        transaction,
                        request.Lease,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);

                return new SensitivePayloadReference(
                    new SensitivePayloadId(persistedId.Value),
                    request.Payload.Kind,
                    request.Payload.Fingerprint);
            }
            catch
            {
                await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("sensitive-payload deduplication", exception);
        }
    }

    private static string GetDatabaseKind(SensitivePayloadKind kind) => kind switch
    {
        SensitivePayloadKind.QueryText => "query_text",
        SensitivePayloadKind.ExecutionPlan => "execution_plan",
        _ => throw new NotSupportedException(
            "Milestone 2 persists protected query text and execution plans only; this payload kind is not supported."),
    };

    private static async Task RollbackWithoutMaskingAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Preserve the original failure.
        }
    }
}
