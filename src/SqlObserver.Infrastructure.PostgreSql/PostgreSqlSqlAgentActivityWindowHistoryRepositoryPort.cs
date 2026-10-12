using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Scoped, repeatable reads of retained SQL Agent source observations.</summary>
public sealed class PostgreSqlSqlAgentActivityWindowHistoryRepositoryPort(NpgsqlDataSource dataSource) : ISqlAgentActivityWindowHistoryReadPort
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private readonly NpgsqlDataSource source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public ValueTask<SqlAgentActivityWindowHistoryPage?> ListAsync(SqlAgentActivityWindowHistoryListRequest request, CancellationToken cancellationToken)
    {
        SqlAgentActivityWindowHistoryRequestValidation.Bind(request);
        return ReadAsync<SqlAgentActivityWindowHistoryPage>(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
            request.Limit, null, null, request.Cursor, request.Timeout, cancellationToken);
    }

    public ValueTask<SqlAgentActivityWindowHistoryExactPage?> GetAsync(SqlAgentActivityWindowHistoryExactRequest request, CancellationToken cancellationToken)
    {
        SqlAgentActivityWindowHistoryRequestValidation.Exact(request);
        return ReadAsync<SqlAgentActivityWindowHistoryExactPage>(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
            1, request.RunId, request.SampleTargetRevision, null, request.Timeout, cancellationToken);
    }

    private async ValueTask<T?> ReadAsync<T>(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit,
        Guid? run, long? sampleRevision, SqlAgentActivityWindowHistoryCursor? cursor, RepositoryCallTimeout requestedTimeout,
        CancellationToken cancellationToken) where T : class
    {
        var timeout = new RepositoryCallTimeout(requestedTimeout.Value < TimeSpan.FromSeconds(5) ? requestedTimeout.Value : TimeSpan.FromSeconds(5));
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, deadline.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, deadline.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope", target.ToString("D"));
                await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand("SELECT reporting.read_sql_agent_activity_window_history(@target,@revision,@from,@to,@limit,@run,@sample,@cursor);", connection, transaction)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to); command.Parameters.AddWithValue("limit", limit);
            command.Parameters.Add("run", NpgsqlDbType.Uuid).Value = (object?)run ?? DBNull.Value;
            command.Parameters.Add("sample", NpgsqlDbType.Bigint).Value = (object?)sampleRevision ?? DBNull.Value;
            command.Parameters.Add("cursor", NpgsqlDbType.Jsonb).Value = cursor is null ? DBNull.Value : JsonSerializer.Serialize(cursor, Json);
            object? result = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            T? page;
            try
            {
                page = result is null or DBNull ? null : JsonSerializer.Deserialize<T>((string)result, Json)
                    ?? throw new InvalidDataException("Agent activity history returned an invalid native page.");
                if (page is SqlAgentActivityWindowHistoryPage list)
                {
                    SqlAgentActivityWindowHistoryValidation.Coverage(list.Coverage);
                    SqlAgentActivityWindowHistoryValidation.Qualification(list.ProductionAdmitted, list.ServiceSessionAssociationQualified, list.NativeQualification);
                    foreach (var receipt in list.Items) SqlAgentActivityWindowHistoryValidation.Receipt(receipt);
                    if (list.NextAnchor is { } next) SqlAgentActivityWindowHistoryRequestValidation.Cursor(next);
                }
                if (page is SqlAgentActivityWindowHistoryExactPage exact)
                {
                    if (exact.Receipt is { } receipt) SqlAgentActivityWindowHistoryValidation.Receipt(receipt);
                    if (exact.Capture is { } capture) SqlAgentActivityWindowHistoryValidation.Capture(capture);
                    SqlAgentActivityWindowHistoryValidation.Qualification(exact.ProductionAdmitted, exact.ServiceSessionAssociationQualified, exact.NativeQualification);
                }
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { throw new InvalidDataException("Agent activity history returned invalid native evidence.", error); }
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return page;
        }
        catch (PostgresException error)
        {
            throw error.SqlState switch
            {
                "XX001" => new InvalidDataException("Agent activity history returned invalid native evidence.", error),
                "54000" => new SqlAgentActivityWindowHistoryReadException("history_window_overflow", "The Agent activity window exceeds its retained attempt bound. Choose a narrower window.", 422, error),
                "40001" or "55000" => new SqlAgentActivityWindowHistoryReadException("history_context_changed", "The target, retained evidence or continuation changed. Read this window again.", 409, error),
                "42501" => new SqlAgentActivityWindowHistoryReadException("forbidden", "A same-target read role is required.", 403, error),
                "22023" or "22P02" or "22003" or "22007" or "22008" => new SqlAgentActivityWindowHistoryReadException("invalid_history_cursor", "The bounded window or continuation is invalid.", 400, error),
                "57014" or "55P03" => new SqlAgentActivityWindowHistoryReadException("history_request_timed_out", "The bounded Agent activity history deadline expired. Choose a narrower window or retry.", 503, error),
                _ => new SqlAgentActivityWindowHistoryReadException("history_repository_unavailable", "Agent activity observation history is temporarily unavailable.", 503, error),
            };
        }
        catch (NpgsqlException error)
        { throw new SqlAgentActivityWindowHistoryReadException("history_repository_unavailable", "Agent activity observation history is temporarily unavailable.", 503, error); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new SqlAgentActivityWindowHistoryReadException("history_request_timed_out", "The bounded Agent activity history deadline expired. Choose a narrower window or retry.", 503, error); }
    }
}
