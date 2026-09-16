using Npgsql;
using SqlObserver.Reporting;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Repository-only report producer. It invokes fixed migration functions
/// and never accepts SQL, target endpoints, or arbitrary report sections.</summary>
public sealed class PostgreSqlReportRepository(NpgsqlDataSource dataSource) : IReportRepository, IReportExpiryRepository
{
    public async ValueTask<ReportRun> CreateAsync(Guid targetId, ReportRequest request, string actorSid, CancellationToken cancellationToken)
    {
        if (targetId == Guid.Empty || string.IsNullOrWhiteSpace(actorSid)) throw new ArgumentException("Report identity is required.");
        byte[] digest = ReportRequestValidation.CanonicalDigest(request);
        using CancellationTokenSource deadline = StartRepositoryDeadline(cancellationToken, out CancellationToken operationToken);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(operationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(operationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, targetId, operationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT run_id,target_revision,definition_version,snapshot_utc,expires_at_utc,state FROM reporting.create_report_run(@target,@kind,@operation,@actor,@from_utc,@to_utc,@digest);", connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("target", targetId); command.Parameters.AddWithValue("kind", request.ReportKind); command.Parameters.AddWithValue("operation", request.OperationId); command.Parameters.AddWithValue("actor", actorSid); command.Parameters.AddWithValue("from_utc", (object?)request.FromUtc ?? DBNull.Value); command.Parameters.AddWithValue("to_utc", (object?)request.ToUtc ?? DBNull.Value); command.Parameters.AddWithValue("digest", digest);
        NpgsqlDataReader reader;
        try { reader = await command.ExecuteReaderAsync(operationToken).ConfigureAwait(false); }
        catch (PostgresException exception) when (exception.SqlState == "22023" && (exception.MessageText.Contains("report_row_limit", StringComparison.Ordinal) || exception.MessageText.Contains("report_materialization_bytes", StringComparison.Ordinal))) { throw new ReportLimitException("The report materialization exceeds its bound."); }
        catch (PostgresException exception) when (exception.SqlState == "22023") { throw new ArgumentException("The report request is invalid.", exception); }
        catch (PostgresException exception) when (exception.SqlState == "02000") { throw new KeyNotFoundException("The observation target was not found.", exception); }
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(operationToken).ConfigureAwait(false)) throw new InvalidDataException("Report producer returned no run.");
            ReportRun run = ReadRun(reader, targetId, request.ParsedKind, request.ReportKind, Convert.ToHexString(digest).ToLowerInvariant());
            await reader.DisposeAsync().ConfigureAwait(false); await transaction.CommitAsync(operationToken).ConfigureAwait(false); return run;
        }
    }

    public async ValueTask<ReportRun?> GetRunAsync(Guid targetId, Guid runId, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = StartRepositoryDeadline(cancellationToken, out CancellationToken operationToken);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(operationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(operationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, targetId, operationToken).ConfigureAwait(false);
        ReportRun? result = await ReadRunCoreAsync(connection, transaction, targetId, runId, operationToken).ConfigureAwait(false);
        await transaction.CommitAsync(operationToken).ConfigureAwait(false); return result;
    }

    public async ValueTask<ReportSectionPage> ReadPageAsync(Guid targetId, Guid runId, string section, long afterOrdinal, int limit, CancellationToken cancellationToken)
    {
        if (!ReportCatalog.Get(ReportKind.InstanceHealth).Sections.Concat(ReportCatalog.All.Skip(1).SelectMany(x => x.Sections)).Any(x => x.Key == section)) throw new ArgumentException("Unknown report section.");
        using CancellationTokenSource deadline = StartRepositoryDeadline(cancellationToken, out CancellationToken operationToken);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(operationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(operationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, targetId, operationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT ordinal,\"values\",has_more FROM reporting.read_report_page(@target,@run,@section,@after,@limit);", connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("target", targetId); command.Parameters.AddWithValue("run", runId); command.Parameters.AddWithValue("section", section); command.Parameters.AddWithValue("after", afterOrdinal); command.Parameters.AddWithValue("limit", limit);
        string[] columns = ReportCatalog.All.SelectMany(static x => x.Sections).Single(x => x.Key == section).Columns.ToArray();
        var rows = new List<ReportRow>(); bool more = false;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(operationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(operationToken).ConfigureAwait(false)) { string[] values = reader.GetFieldValue<string[]>(1); rows.Add(new ReportRow(reader.GetInt64(0), values.Select((v, i) => new { v, i }).Where(x => x.i < columns.Length).ToDictionary(x => columns[x.i], x => (string?)x.v))); more |= reader.GetBoolean(2); }
        // The fixed SQL page function returns one look-ahead row so it can
        // determine HasMore without a second query. Never expose that row to
        // API callers: the public contract is exactly 200 rows per page.
        if (rows.Count > limit) { more = true; rows.RemoveRange(limit, rows.Count - limit); }
        await reader.DisposeAsync().ConfigureAwait(false);
        ReportRun run = await ReadRunCoreAsync(connection, transaction, targetId, runId, operationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        await transaction.CommitAsync(operationToken).ConfigureAwait(false);
        return new ReportSectionPage(run, section, rows, more && rows.Count > 0 ? rows[^1].Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, more);
    }

    public async ValueTask<int> ExpireAsync(int maximumRows, SqlObserver.Domain.Coordination.WorkerLeaseIdentity lease, CancellationToken cancellationToken)
    {
        if (lease is null || lease.Key.Value != "reports/expiry" || maximumRows is < 1 or > 100) throw new ArgumentException("The report expiry lease is invalid.");
        using CancellationTokenSource deadline = StartRepositoryDeadline(cancellationToken, out CancellationToken operationToken);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(operationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT reporting.expire_report_runs(@limit,@owner,@fencing);", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("limit", maximumRows); command.Parameters.AddWithValue("owner", lease.Owner.Value); command.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(operationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetId, CancellationToken cancellationToken)
    { await using var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction); scope.Parameters.AddWithValue("scope", targetId.ToString("D")); await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
    private static async ValueTask<ReportRun?> ReadRunCoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetId, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT run_id,target_revision,definition_version,snapshot_utc,expires_at_utc,state,report_kind,parameter_digest_hex FROM reporting.read_report_run(@target,@run);", connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("target", targetId); command.Parameters.AddWithValue("run", runId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRun(reader, targetId, Parse(reader.GetString(6)), reader.GetString(6), reader.GetString(7)) : null;
    }
    private static ReportKind Parse(string value) => ReportKindExtensions.TryParse(value, out ReportKind kind) ? kind : throw new InvalidDataException("Unknown report kind.");
    private static ReportRun ReadRun(NpgsqlDataReader reader, Guid targetId, ReportKind kind, string wireKind, string digest) => new(reader.GetGuid(0), targetId, reader.GetInt64(1), kind, reader.GetInt32(2), ReadUtc(reader, 3), ReadUtc(reader, 4), digest, reader.GetString(5));
    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) { DateTime value = reader.GetDateTime(ordinal); return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)); }
    private static CancellationTokenSource StartRepositoryDeadline(CancellationToken callerToken, out CancellationToken operationToken)
    { CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken); deadline.CancelAfter(ReportContract.RepositoryTimeout); operationToken = deadline.Token; return deadline; }
}
