using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Server;

namespace SqlObserver.SecurityTests;

/// <summary>Smoke tests that construct the production seams; these must not be replaced by test-only facades.</summary>
public sealed class M8Pass18ProductionClassTests
{
    [Fact]
    public async Task ProductionRepositoryAndWorkersAreConstructibleThroughTheirRealTypes()
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Password=none");
        var repository = new PostgreSqlAlertRepositoryPort(dataSource);
        var execution = new WorkerExecutionId(Guid.NewGuid());
        using var evaluation = new AlertEvaluationWorker(null!, repository, null!, execution, NullLogger<AlertEvaluationWorker>.Instance);
        using var delivery = new AlertDeliveryWorker(repository, null!, null!, execution, NullLogger<AlertDeliveryWorker>.Instance);

        Assert.IsType<PostgreSqlAlertRepositoryPort>(repository);
        Assert.IsType<AlertEvaluationWorker>(evaluation);
        Assert.IsType<AlertDeliveryWorker>(delivery);
        Assert.NotNull(typeof(AlertEndpoints).GetMethod(nameof(AlertEndpoints.MapAlertEndpoints)));
    }
}
