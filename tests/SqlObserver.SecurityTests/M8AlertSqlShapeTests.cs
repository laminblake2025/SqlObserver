using System.Text.Json;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.SecurityTests;

public sealed class M8AlertSqlShapeTests
{
    [Fact]
    public void AllWireIdentifiersRequireCanonicalLowercaseRfc4122()
    {
        Assert.True(AlertIdentifier.TryParseRfc4122("cccccccc-cccc-4ccc-8ccc-cccccccccccc", out _));
        Assert.False(AlertIdentifier.TryParseRfc4122("CCCCCCCC-CCCC-4CCC-8CCC-CCCCCCCCCCCC", out _));
        Assert.False(AlertIdentifier.TryParseRfc4122(Guid.Empty.ToString("D"), out _));
    }
    [Fact]
    public void CollectorEvidenceLateralUnionHasStableAliasesAndBoundedLimits()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        Assert.Contains("m.observed_at AS observed_at", sql, StringComparison.Ordinal);
        Assert.Contains("m.sample_id AS sample_id", sql, StringComparison.Ordinal);
        Assert.Contains("AS collector_healthy", sql, StringComparison.Ordinal);
        Assert.Contains("AS evidence_text", sql, StringComparison.Ordinal);
        Assert.Contains("p_max_results IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT p_max_results+1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservationFixtureUsesCanonicalWireShape()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "m8-alert-observation.json")));
        JsonElement root = document.RootElement;
        foreach (string property in new[] { "TargetId", "RuleId", "ObservedAtUtc", "Value", "CollectorHealthy", "Reason", "OperationId", "EvidenceDigest" }) Assert.True(root.TryGetProperty(property, out _), property);
        Assert.Equal(JsonValueKind.String, root.GetProperty("TargetId").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("OperationId").ValueKind);
    }

    [Fact]
    public void DestinationApprovalVariablesBelongToTheirFunctionAndRenewalIsFenced()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        string destination = sql.Split("CREATE OR REPLACE FUNCTION alerting.upsert_destination", StringSplitOptions.None)[1].Split("CREATE OR REPLACE FUNCTION", StringSplitOptions.None)[0];
        Assert.Contains("next_revision integer", destination, StringComparison.Ordinal);
        Assert.Contains("expected_approval_digest bytea", destination, StringComparison.Ordinal);
        Assert.Contains("INTO next_revision", destination, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE FUNCTION alerting.renew_delivery", sql, StringComparison.Ordinal);
        Assert.Contains("control.assert_worker_lease", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Pass19RequiresBoundHealthRunsAndFullDecisionSnapshotFields()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        Assert.Contains("h.run_id IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("coh.outcome IN ('succeeded','partial','timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open')", sql, StringComparison.Ordinal);
        Assert.Contains("crh.collector_id='engine.core'", sql, StringComparison.Ordinal);
        foreach (string field in new[] { "FirstMatchUtc", "LastObservedUtc", "fired_at", "acknowledged_at", "ResolvedUtc", "EpisodeStartedUtc", "AlertId", "EpisodeId", "LastOperationId", "EvidenceDigest", "DeliverySuppressed", "Revision", "TargetId", "RuleId" })
            Assert.Contains(field, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCallAndGrantUseOnlyTargetScopedEvaluationSignature()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlAlertRepositoryPort.cs")));
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        Assert.Contains("evaluate_and_enqueue(@target_id,@observations,@decisions,@work_key,@owner_id,@lease_id)", source, StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON FUNCTION alerting.evaluate_and_enqueue(uuid,jsonb,jsonb,text,uuid,bigint) TO sqlobserver_collector", sql, StringComparison.Ordinal);
        Assert.Contains("DROP FUNCTION IF EXISTS alerting.evaluate_and_enqueue(jsonb,jsonb,text,uuid,bigint)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REVOKE ALL ON FUNCTION alerting.evaluate_and_enqueue(jsonb,jsonb,text,uuid,bigint)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationSignatureGraphDoesNotRevokeAnUndefinedEvaluationOverload()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        Assert.Contains("CREATE OR REPLACE FUNCTION alerting.evaluate_and_enqueue(p_instance_id uuid", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON FUNCTION alerting.evaluate_and_enqueue(uuid,jsonb,jsonb,text,uuid,bigint)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("evaluate_and_enqueue(jsonb,jsonb,text,uuid,bigint) FROM", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluationClaimHelperIsInternalOnlyAndRevokedFromApplicationRoles()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        const string signature = "alerting.assert_evaluation_claims(uuid,jsonb,text,uuid,bigint)";
        string revoke = sql.Split('\n').Single(line => line.Contains(signature, StringComparison.Ordinal));
        Assert.Contains("REVOKE ALL ON FUNCTION", revoke, StringComparison.Ordinal);
        Assert.Contains("FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor", revoke, StringComparison.Ordinal);
        Assert.DoesNotContain($"GRANT EXECUTE ON FUNCTION {signature}", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluationFunctionPrivilegeGraphNamesOnlyDeclaredOverloads()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        const string internalSignature = "alerting.evaluate_and_enqueue_internal(jsonb,jsonb,text,uuid,bigint)";
        const string droppedInternalSignature = "alerting.evaluate_and_enqueue_internal(" + "jsonb,jsonb,bigint)";
        Assert.Contains("CREATE OR REPLACE FUNCTION alerting.evaluate_and_enqueue_internal(p_observations jsonb, p_decisions jsonb, p_work_key text, p_owner_execution_id uuid, p_lease_fencing bigint)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(droppedInternalSignature, sql, StringComparison.Ordinal);

        string revoke = sql.Split('\n').Single(line => line.Contains(internalSignature, StringComparison.Ordinal));
        Assert.Contains(internalSignature, revoke, StringComparison.Ordinal);
        string wrapperDrop = sql.Split('\n').Single(line => line.Contains("DROP FUNCTION IF EXISTS alerting.evaluate_and_enqueue(jsonb,jsonb,text,uuid,bigint)", StringComparison.Ordinal));
        Assert.Contains("DROP FUNCTION IF EXISTS", wrapperDrop, StringComparison.Ordinal);
        string collectorGrant = sql.Split('\n').First(line => line.Contains("GRANT EXECUTE ON FUNCTION alerting.evaluate_and_enqueue(uuid,jsonb,jsonb,text,uuid,bigint)", StringComparison.Ordinal));
        Assert.Contains("GRANT EXECUTE ON FUNCTION", collectorGrant, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayAndHistoryIdentityAreOperationScopedAndSnapshotsAreServerCanonical()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        Assert.Contains("CREATE OR REPLACE FUNCTION alerting.canonical_decision_snapshot", sql, StringComparison.Ordinal);
        Assert.Contains("canonical_result_digest", sql, StringComparison.Ordinal);
        Assert.Contains("result_digest,result) VALUES(operation,target,rule", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("result_digest,result) VALUES(operation,target,rule,rule_config.revision,target,to_jsonb(rule_config),evidence,sha256(convert_to(d::text", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_alert_history_operation", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("h.observed_at=observed AND h.to_state=next_state", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY observed_at ASC, sample_id ASC, run_id ASC NULLS LAST, operation_id ASC", sql, StringComparison.Ordinal);
        Assert.Contains("'|run=' || m.collection_run_id::text", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthEvidenceRequiresProjectionProducerAndSchemaContractMatch()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        Assert.Contains("h.collector_version=crh.collector_version", sql, StringComparison.Ordinal);
        Assert.Contains("h.output_schema_version=crh.output_schema_version", sql, StringComparison.Ordinal);
        Assert.Contains("h.run_id IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("crh.output_schema_version=1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CoordinatorDiscoveryIsFencedAndOperationIdentityIsCanonical()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        string repository = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlAlertRepositoryPort.cs")));
        Assert.Contains("list_targets_with_due_alert_work(text,text,uuid,bigint,integer)", sql, StringComparison.Ordinal);
        Assert.Contains("PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing)", sql, StringComparison.Ordinal);
        Assert.Contains("list_targets_with_due_alert_work(@work_kind,@work_key,@owner_id,@fence,@max_results)", repository, StringComparison.Ordinal);
        Assert.Contains("require_canonical_operation_uuid", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("list_targets_with_due_alert_work(text,integer) TO sqlobserver_collector", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DestinationMutationIsActionBoundAndUsesAuthoritativeHealthOutcomes()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        string repository = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlAlertRepositoryPort.cs")));
        Assert.Contains("@approval_digest,@action", repository, StringComparison.Ordinal);
        Assert.Contains("destination already exists; create cannot update", sql, StringComparison.Ordinal);
        Assert.Contains("destination does not exist", sql, StringComparison.Ordinal);
        Assert.Contains("p_action='alert.destination.configure'", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT(destination_id) DO NOTHING", sql, StringComparison.Ordinal);
        Assert.Contains("p_action || coalesce(p_expected_revision", sql, StringComparison.Ordinal);
        Assert.Contains("alert.destination.configure", sql, StringComparison.Ordinal);
        Assert.Contains("alert.destination.approve", sql, StringComparison.Ordinal);
        foreach (string outcome in new[] { "succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open" }) Assert.Contains(outcome, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleAndMaintenanceCreatesAreInsertOnlyAndMaintenanceSharesDispatchFence()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        Assert.Contains("p_rule->>'Action'='CreateAlertRule'", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT(rule_id) DO NOTHING", sql, StringComparison.Ordinal);
        Assert.Contains("p_window->>'Action'='CreateMaintenanceWindow'", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT(window_id) DO NOTHING", sql, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock(hashtextextended(p_window->>'TargetId',0))", sql, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock(hashtextextended(p_target_id::text,0))", sql, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock(hashtextextended(target::text,0))", sql, StringComparison.Ordinal);
        Assert.Contains("destination_revision_changed", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryUsesDatabaseBackedDispatchPermitAndPinnedConnectPolicy()
    {
        string repository = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlAlertRepositoryPort.cs")));
        string destinations = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.Windows/AlertDestinations.cs")));
        string registration = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Collector/CollectorServiceRegistration.cs")));
        Assert.Contains("pg_advisory_lock_shared(hashtextextended", repository, StringComparison.Ordinal);
        Assert.Contains("AcquireDeliveryDispatchPermitAsync", repository, StringComparison.Ordinal);
        Assert.Contains("pinnedAddresses", destinations, StringComparison.Ordinal);
        Assert.Contains("Destination DNS answers changed outside the approved address set", destinations, StringComparison.Ordinal);
        Assert.Contains("IAlertDestinationHttpClientFactory", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPostgreSqlResourcesMapToTypedNotFoundOperations()
    {
        string repository = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlAlertRepositoryPort.cs")));
        string service = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Application/Services/AlertServices.cs")));
        Assert.Contains("\"P0002\"", repository, StringComparison.Ordinal);
        Assert.Contains("target_not_found", repository, StringComparison.Ordinal);
        Assert.Contains("AdministrativeAuditReason.TargetNotFound", service, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryTerminalAndRetryTransitionsAlwaysClearLeaseOwnership()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0012_alerts_maintenance_notifications.sql")));
        Assert.Contains("leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL", sql, StringComparison.Ordinal);
        Assert.Contains("cancel_reason=left(p_reason,64),completed_at=clock_timestamp(),leased_until=NULL", sql, StringComparison.Ordinal);
        Assert.Contains("due_at=CASE WHEN p_succeeded OR p_permanent", sql, StringComparison.Ordinal);
    }
}
