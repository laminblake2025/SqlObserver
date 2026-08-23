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
    public void AdapterUsesOnlyTheFiveParameterDeniedAuditWrapper()
    {
        FieldInfo? sqlField = typeof(PostgreSqlAdministrativeAuditPort).GetField(
            "AppendSql",
            BindingFlags.NonPublic | BindingFlags.Static);
        string sql = Assert.IsType<string>(sqlField?.GetRawConstantValue());
        const string expected = """
            SELECT audit_activity_id, repository_time
            FROM audit.append_denied_administrative_activity(
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
    public async Task NonDeniedRecordsFailClosedBeforeCancellationOrIo(
        AdministrativeOperationOutcome outcome)
    {
        AdministrativeAuditRecord record = CreateRecord(
            AdministrativeAuditAction.RegisterObservationTarget,
            AdministrativeAuthorizationDecision.Granted,
            outcome,
            AdministrativeAuditReason.PrincipalDisabled);

        await AssertRejectedBeforeIoAsync(record);
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
        AdministrativeAuditReason reason) =>
        new(
            new AdministrativeAuditEnvelope(
                new ActorSecurityIdentifier("S-1-5-21-1000"),
                new AuditCorrelationId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                action,
                new MonitoredInstanceId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))),
            decision,
            outcome,
            reason);
}
