using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Owns provider lifetime for a reviewed migration caller without exposing provider types across
/// the process boundary. The opaque configuration is never retained or included in an error.
/// </summary>
public sealed class PostgreSqlMigrationExecution
{
    internal PostgreSqlMigrationExecution(MigrationBatchResult? batch)
    {
        Batch = batch;
    }

    public MigrationBatchResult? Batch { get; }

    public bool Succeeded => Batch is not null;
}

public static class PostgreSqlMigrationExecutor
{
    public static async ValueTask<PostgreSqlMigrationExecution> ApplyPendingAsync(
        string repositoryConfiguration,
        MigrationApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryConfiguration);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await using NpgsqlDataSource dataSource = PostgreSqlDataSourceFactory.Create(
                repositoryConfiguration,
                "SqlObserver.LabMigration");
            var port = new PostgreSqlMigrationPort(dataSource);
            MigrationBatchResult batch = await port.ApplyPendingAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return new PostgreSqlMigrationExecution(batch);
        }
        catch (NpgsqlException)
        {
            return new PostgreSqlMigrationExecution(batch: null);
        }
    }
}
