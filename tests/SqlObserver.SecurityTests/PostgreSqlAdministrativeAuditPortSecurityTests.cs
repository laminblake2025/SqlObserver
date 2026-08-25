using System.Reflection;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.SecurityTests;

public sealed class PostgreSqlAdministrativeAuditPortSecurityTests
{
    [Fact]
    public void AdapterUsesOnlyTheFiveParameterM8DeniedAuditWrapper()
    {
        FieldInfo? sqlField = typeof(PostgreSqlAdministrativeAuditPort).GetField(
            "AppendSql",
            BindingFlags.NonPublic | BindingFlags.Static);
        string sql = Assert.IsType<string>(sqlField?.GetRawConstantValue());
        const string expected = """
            SELECT audit_activity_id, repository_time
            FROM audit.append_denied_m8_administrative_activity(
                @actor_identifier,
                @correlation_id,
                @audit_action,
                @target_id,
                @reason);
            """;

        Assert.Equal(
            expected.ReplaceLineEndings("\n"),
            sql.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("append_administrative_activity(", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization_decision", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("operation_outcome", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterHasSeparateBoundedM8OutcomeWrapperWithOperationAndDetails()
    {
        FieldInfo? sqlField = typeof(PostgreSqlAdministrativeAuditPort).GetField("AppendOutcomeSql", BindingFlags.NonPublic | BindingFlags.Static);
        string sql = Assert.IsType<string>(sqlField?.GetRawConstantValue());
        Assert.Contains("audit.append_m8_administrative_activity", sql, StringComparison.Ordinal);
        Assert.Contains("@operation_id", sql, StringComparison.Ordinal);
        Assert.Contains("@operation_outcome", sql, StringComparison.Ordinal);
        Assert.Contains("@safe_details", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AdministrativeAuditAction.RegisterObservationTarget)]
    [InlineData(AdministrativeAuditAction.UpdateObservationTarget)]
    [InlineData(AdministrativeAuditAction.RetireObservationTarget)]
    [InlineData(AdministrativeAuditAction.RequestCapabilityRediscovery)]
    public async Task AllowedDeniedActionsReachCancellationBoundary(
        AdministrativeAuditAction action)
    {
        await AssertAcceptedUntilCancellationAsync(CreateRecord(
            action,
            AdministrativeAuthorizationDecision.Denied,
            AdministrativeOperationOutcome.Denied,
            AdministrativeAuditReason.PrincipalDisabled));
    }

    [Theory]
    [InlineData(AdministrativeAuditAction.CreateAlertRule)]
    [InlineData(AdministrativeAuditAction.UpdateAlertRule)]
    [InlineData(AdministrativeAuditAction.RetireAlertRule)]
    [InlineData(AdministrativeAuditAction.CreateMaintenanceWindow)]
    [InlineData(AdministrativeAuditAction.UpdateMaintenanceWindow)]
    [InlineData(AdministrativeAuditAction.RetireMaintenanceWindow)]
    [InlineData(AdministrativeAuditAction.AcknowledgeAlert)]
    [InlineData(AdministrativeAuditAction.ConfigureAlertDestination)]
    [InlineData(AdministrativeAuditAction.UpdateAlertDestination)]
    [InlineData(AdministrativeAuditAction.RetireAlertDestination)]
    [InlineData(AdministrativeAuditAction.CancelAlertDelivery)]
    [InlineData(AdministrativeAuditAction.ApproveAlertDestination)]
    public async Task M8DeniedActionsReachCancellationBoundary(AdministrativeAuditAction action)
    {
        await AssertAcceptedUntilCancellationAsync(CreateRecord(action, AdministrativeAuthorizationDecision.Denied, AdministrativeOperationOutcome.Denied, AdministrativeAuditReason.InvalidRequest, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")));
    }

    [Theory]
    [InlineData(AdministrativeAuditReason.PrincipalDisabled)]
    [InlineData(AdministrativeAuditReason.RequiredRoleMissing)]
    [InlineData(AdministrativeAuditReason.TargetOutOfScope)]
    public async Task AllowedDeniedReasonsReachCancellationBoundary(
        AdministrativeAuditReason reason)
    {
        await AssertAcceptedUntilCancellationAsync(CreateRecord(
            AdministrativeAuditAction.UpdateObservationTarget,
            AdministrativeAuthorizationDecision.Denied,
            AdministrativeOperationOutcome.Denied,
            reason));
    }

    [Theory]
    [InlineData(AdministrativeOperationOutcome.Succeeded)]
    [InlineData(AdministrativeOperationOutcome.Failed)]
    [InlineData(AdministrativeOperationOutcome.Conflict)]
    public async Task M8OutcomeRecordsReachCancellationBoundaryBeforeIo(
        AdministrativeOperationOutcome outcome)
    {
        AdministrativeAuditRecord record = CreateRecord(
            AdministrativeAuditAction.CreateAlertRule,
            AdministrativeAuthorizationDecision.Granted,
            outcome,
            AdministrativeAuditReason.InvalidRequest,
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));

        await AssertAcceptedUntilCancellationAsync(record);
    }

    [Fact]
    public async Task CapabilityProfileActionFailsClosedBeforeCancellationOrIo()
    {
        AdministrativeAuditRecord record = CreateRecord(
            AdministrativeAuditAction.RecordCapabilityProfile,
            AdministrativeAuthorizationDecision.Denied,
            AdministrativeOperationOutcome.Denied,
            AdministrativeAuditReason.RequiredRoleMissing);

        await AssertRejectedBeforeIoAsync(record);
    }

    [Theory]
    [InlineData(AdministrativeAuditReason.Completed)]
    [InlineData(AdministrativeAuditReason.AlreadyExists)]
    [InlineData(AdministrativeAuditReason.RevisionConflict)]
    [InlineData(AdministrativeAuditReason.TargetNotFound)]
    [InlineData(AdministrativeAuditReason.TargetRetired)]
    [InlineData(AdministrativeAuditReason.DiscoveryFailed)]
    [InlineData(AdministrativeAuditReason.RepositoryFailure)]
    public async Task NonDenialReasonsFailClosedBeforeCancellationOrIo(
        AdministrativeAuditReason reason)
    {
        AdministrativeAuditRecord record = CreateRecord(
            AdministrativeAuditAction.RetireObservationTarget,
            AdministrativeAuthorizationDecision.Denied,
            AdministrativeOperationOutcome.Denied,
            reason);

        await AssertRejectedBeforeIoAsync(record);
    }

    private static async Task AssertAcceptedUntilCancellationAsync(AdministrativeAuditRecord record)
    {
        await using NpgsqlDataSource dataSource = CreateUnreachableDataSource();
        var adapter = new PostgreSqlAdministrativeAuditPort(dataSource);
        var request = new AppendAdministrativeAuditRequest(
            record,
            new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            _ = await adapter.AppendAsync(request, new CancellationToken(canceled: true));
        });
    }

    private static async Task AssertRejectedBeforeIoAsync(AdministrativeAuditRecord record)
    {
        await using NpgsqlDataSource dataSource = CreateUnreachableDataSource();
        var adapter = new PostgreSqlAdministrativeAuditPort(dataSource);
        var request = new AppendAdministrativeAuditRequest(
            record,
            new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            _ = await adapter.AppendAsync(request, new CancellationToken(canceled: true));
        });
    }

    private static NpgsqlDataSource CreateUnreachableDataSource() =>
        PostgreSqlDataSourceFactory.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1",
            "SqlObserver.SecurityTests");

    private static AdministrativeAuditRecord CreateRecord(
        AdministrativeAuditAction action,
        AdministrativeAuthorizationDecision decision,
        AdministrativeOperationOutcome outcome,
        AdministrativeAuditReason reason,
        Guid? operationId = null) =>
        new(
            new AdministrativeAuditEnvelope(
                new ActorSecurityIdentifier("S-1-5-21-1000"),
                new AuditCorrelationId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                action,
                new MonitoredInstanceId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))),
            decision,
            outcome,
            reason,
            operationId ?? Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            "security-test");
}
