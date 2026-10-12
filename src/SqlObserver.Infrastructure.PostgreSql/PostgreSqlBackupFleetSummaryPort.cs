using System.Data;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Bounded materializer input; all pages share one repeatable-read snapshot and a locked worker fence.</summary>
public sealed class PostgreSqlBackupFleetSummaryPort(NpgsqlDataSource dataSource) : IBackupFleetSummaryPort
{
    public async ValueTask<BackupFleetSummary> ReadAsync(OverviewRefreshTarget target, WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(target.TargetId);
        ArgumentNullException.ThrowIfNull(lease); ArgumentNullException.ThrowIfNull(timeout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.TargetRevision);
        if (lease.Key.Value != "overview/latest-status") throw new ArgumentException("Backup summary requires the overview worker lease.", nameof(lease));
        var bounded = new RepositoryCallTimeout(timeout.Value < TimeSpan.FromSeconds(5) ? timeout.Value : TimeSpan.FromSeconds(5));
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(bounded, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, deadline.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, bounded, deadline.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope", target.TargetId.Value.ToString("D"));
                await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            }
            BackupEvidenceHeader header;
            await using (var command = new NpgsqlCommand("SELECT * FROM reporting.read_overview_backup_header(@target,@revision,@owner,@fencing);", connection, transaction))
            {
                Bind(command, target, lease, bounded);
                await using NpgsqlDataReader row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
                if (!await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
                DateTimeOffset at = PostgreSqlRuntimeSupport.ReadUtcTimestamp(row, 0);
                header = PostgreSqlBackupEvidencePort.ReadHeader(row, target.TargetId, at, offset: 1);
                if (await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
            }
            var pages = new List<BackupEvidencePage>(BackupFleetSummaryBuilder.MaximumPages);
            int after = 0;
            if (header.EvidenceStatus == BackupEvidenceStatus.Retained)
            {
                do
                {
                    if (pages.Count == BackupFleetSummaryBuilder.MaximumPages) throw Invalid();
                    var items = new List<BackupDatabaseEvidence>(BackupFleetSummaryBuilder.PageSize);
                    bool hasMore = false;
                    await using (var command = new NpgsqlCommand("SELECT * FROM reporting.read_overview_backup_databases(@target,@revision,@at,@run,@owner,@fencing,@after);", connection, transaction))
                    {
                        Bind(command, target, lease, bounded);
                        command.Parameters.AddWithValue("at", header.AtUtc); command.Parameters.AddWithValue("run", header.RunId!.Value);
                        command.Parameters.AddWithValue("after", after);
                        await using NpgsqlDataReader row = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
                        while (await row.ReadAsync(deadline.Token).ConfigureAwait(false))
                        {
                            if (hasMore) throw Invalid();
                            BackupDatabaseEvidence item = PostgreSqlBackupEvidencePort.ReadDatabase(row, header);
                            if (items.Count == BackupFleetSummaryBuilder.PageSize) hasMore = true; else items.Add(item);
                        }
                    }
                    if (items.Count == 0) throw Invalid();
                    after = items[^1].DatabaseOrdinal;
                    pages.Add(new(header, items.AsReadOnly(), hasMore ? new(header.TargetId, header.AtUtc, header.TargetRevision,
                        header.RunId!.Value, after, header.PolicyHistoryCount, header.QualificationCommandRevision) : null));
                    if (!hasMore) break;
                } while (true);
            }
            else pages.Add(new(header, [], null));
            BackupFleetSummary summary = BackupFleetSummaryBuilder.Build(pages);
            await using (var fence = new NpgsqlCommand("SELECT control.assert_worker_lease('overview/latest-status',@owner,@fencing);", connection, transaction))
            {
                fence.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(bounded);
                fence.Parameters.AddWithValue("owner", lease.Owner.Value); fence.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
                await fence.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return summary;
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        { throw new UnauthorizedAccessException("Overview backup target scope or current revision was rejected.", exception); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("overview backup summary", exception); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Overview backup evidence could not be decoded.", exception); }
    }

    private static void Bind(NpgsqlCommand command, OverviewRefreshTarget target, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout)
    {
        command.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout);
        command.Parameters.AddWithValue("target", target.TargetId.Value); command.Parameters.AddWithValue("revision", target.TargetRevision);
        command.Parameters.AddWithValue("owner", lease.Owner.Value); command.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
    }
    private static InvalidDataException Invalid() => new("The bounded overview backup read contract is invalid.");
}
