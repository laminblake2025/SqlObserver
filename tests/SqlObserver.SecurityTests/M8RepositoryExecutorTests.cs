using Npgsql;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.SecurityTests;

public sealed class M8RepositoryExecutorTests
{
    [Fact]
    public async Task PostgreSqlAlertRepositoryPortRunsAdminCommandThroughInjectedExecutor()
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Password=none");
        var executor = new RecordingExecutor();
        var repository = new PostgreSqlAlertRepositoryPort(dataSource, executor);
        AlertCatalogEntry entry = AlertCatalog.Entries[0];
        var target = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        var rule = new AlertRuleDefinition(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), entry.Name, entry.Kind, entry.Metric is null ? null : new MetricId(entry.Metric), entry.Comparison, entry.Threshold, entry.Hysteresis, entry.ConfirmationCount, entry.ConfirmationWindow, entry.EvaluationInterval, true);
        var audit = new AdministrativeAuditEnvelope(new ActorSecurityIdentifier("S-1-5-21-1000"), new AuditCorrelationId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")), AdministrativeAuditAction.CreateAlertRule, target);
        var request = new AlertRuleWriteRequest(rule, "dddddddd-dddd-4ddd-8ddd-dddddddddddd", audit, new RepositoryCallTimeout(TimeSpan.FromSeconds(3)));

        AdministrativeAuditReceipt receipt = await repository.UpsertRuleAsync(request, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, receipt.AuditId.Value);
        Assert.Contains("alerting.upsert_rule", executor.Command!.Statement, StringComparison.Ordinal);
        Assert.Equal(request.IdempotencyKey, executor.Command.Parameters["idempotency_key"]);
        Assert.Equal(target.Value, executor.Command.Parameters["target_id"]);
        Assert.Equal(3, executor.Command.Timeout.Value.TotalSeconds);
        Assert.True(executor.TransactionBoundaryObserved);
    }

    private sealed class RecordingExecutor : IAlertRepositoryCommandExecutor
    {
        public AlertRepositoryAdminCommand? Command { get; private set; }
        public bool TransactionBoundaryObserved { get; private set; }
        public ValueTask<AdministrativeAuditReceipt> ExecuteAsync(AlertRepositoryAdminCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Command = command;
            TransactionBoundaryObserved = true;
            return ValueTask.FromResult(new AdministrativeAuditReceipt(new AdministrativeAuditId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")), new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)));
        }
    }
}
