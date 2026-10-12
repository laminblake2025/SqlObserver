using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;
using Xunit.Abstractions;
using DomainEnrollment = SqlObserver.Domain.Collection.SqlAgentModuleEnrollment;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Finite actual-monitor execution with authoritative disposable PostgreSQL enrollment.
/// Original trusted enrollment inputs verify repository authority; they never replace that authority.</summary>
[Trait("Category", "RequiresSqlServer")]
[Trait("Category", "RequiresSqlAgentActivityProductionModule")]
[Trait("Category", "RequiresSqlAgentActivityRepositoryNative")]
public sealed class SqlAgentActivityRepositoryNativeQualificationTests(ITestOutputHelper output)
{
    [Fact]
    public Task ActualMonitorCapturesThroughRepositoryEnrolledPublicObservationsCollector() =>
        new SqlAgentActivityNativeQualificationHarness(output).RunAsync(false, async (context, token) =>
        {
            var input = SqlAgentActivityRepositoryNativeQualificationInput.Load(Environment.GetEnvironmentVariable);
            await using var dataSource = input.CreateDataSource();
            await input.RequireRepositoryIdentityAsync(dataSource, token);
            var repository = new PostgreSqlSqlAgentModuleEnrollmentPort(dataSource);
            var resolver = new AuthoritativeResolver(repository, context.Input.Enrollment, revoked: false);
            var collector = new SqlAgentActivityObservationsCollector(new SqlServerAgentActivityModuleCaptureReader(resolver));
            long started = Stopwatch.GetTimestamp();
            CollectorExecutionResult result = await collector.CollectAsync(context.Request, token);
            Assert.True(Stopwatch.GetElapsedTime(started) < SqlServerAgentActivityModuleCaptureReader.MaximumBudget);
            Assert.Equal(3, resolver.Calls);
            Assert.True(result.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial);
            Assert.Equal(context.Request.TargetId, result.TargetId); Assert.Equal(context.Request.TargetRevision, result.TargetRevision);
            Assert.Equal("sql-agent.activity-observations", result.CollectorId.Value);
            Assert.Equal(1, result.CollectorManifestVersion); Assert.Equal(1, result.OutputSchemaVersion);
            var value = Assert.IsType<SqlAgentModuleCaptureResult>(result.Payload.SqlAgentModuleCapture);
            Assert.Equal(context.Request.RunId, value.Capture.RunId);
            Assert.Equal(context.Input.Enrollment.Revision, value.EnrollmentRevision);
            Assert.Equal(context.Input.Enrollment.EnrollmentId, value.EnrollmentId);
            Assert.Equal(context.Input.Enrollment.EvidenceSha256, value.EvidenceSha256);
            SqlAgentActivityObservationsOutputValidation.Validate(result.Payload, context.Request.TargetId,
                context.Request.TargetRevision, context.Request.RunId, result.Outcome, result.Accounting, result.Loss);
            Assert.Equal(4096 + value.Capture.SourceRowsRead * 768, result.Accounting.ResponseBytes);
            Assert.Equal(4096 + value.Capture.Jobs.Count * 768, result.Accounting.OutputBytes);
            Assert.Equal(1, result.Accounting.OutputItemsProduced);
            Assert.InRange(value.Capture.Jobs.Count, 0, 512); Assert.InRange(value.Capture.SourceRowsRead, 0, 513);
            Assert.Equal(value.Capture.Truncated, result.Outcome == CollectorRunOutcome.Partial);
            Assert.Equal(value.Capture.Truncated ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed, result.Reason);
            Assert.False(value.ProductionAdmitted); Assert.False(value.ServiceSessionAssociationQualified);
            Assert.False(value.Capture.ProductionAdmitted); Assert.False(value.Capture.ServiceSessionAssociationQualified);
            Assert.Equal(SqlAgentActivityNativeQualification.Absent, value.NativeQualification);
            Assert.Equal(SqlAgentActivityNativeQualification.Absent, value.Capture.NativeQualification);
            Assert.Equal(1, value.Capture.Before.Service.MatchingServiceCount);
            Assert.Equal(1, value.Capture.After.Service.MatchingServiceCount);
            Assert.Equal(1, value.Capture.Before.Service.Status); Assert.Equal(1, value.Capture.After.Service.Status);
            Assert.True(context.Input.Enrollment.IsActiveAt(value.Capture.Before.SourceObservedAtUtc));
            Assert.True(context.Input.Enrollment.IsActiveAt(value.Capture.After.SourceObservedAtUtc));
            var evaluation = SqlAgentActivityEvaluator.Evaluate(value.Capture);
            Assert.Equal(value.Capture.Truncated ? SqlAgentActivityGap.SourceTruncated : SqlAgentActivityGap.ServiceNotRunning, evaluation.Gap);
            Assert.All(evaluation.Jobs, job =>
            {
                Assert.Equal(SqlAgentActivityCandidateState.Unknown, job.CandidateState);
                Assert.Null(job.StartedAtUtc); Assert.Null(job.ElapsedSeconds); Assert.Null(job.LongRunning);
            });
            await input.RequireRepositoryIdentityAsync(dataSource, token);
            context.DeferredRepositoryReceipt = SqlAgentActivityRepositoryNativeReceipt.Capture(input,
                context.Input.Enrollment, result, context.Request.RunId, resolver.Calls);
        });

    [Fact]
    public Task ActualMonitorRepositoryEnrolledSignedModuleExplicitDenyRefusesPublicCapture() =>
        new SqlAgentActivityNativeQualificationHarness(output).RunAsync(true, async (context, token) =>
        {
            var input = SqlAgentActivityRepositoryNativeQualificationInput.Load(Environment.GetEnvironmentVariable);
            await using var dataSource = input.CreateDataSource();
            await input.RequireRepositoryIdentityAsync(dataSource, token);
            var resolver = new AuthoritativeResolver(new PostgreSqlSqlAgentModuleEnrollmentPort(dataSource),
                context.Input.Enrollment, revoked: false);
            var reader = new SqlServerAgentActivityModuleCaptureReader(resolver);
            long started = Stopwatch.GetTimestamp();
            var refusal = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(context.Request, token).AsTask());
            Assert.Equal("Agent module SQL permissions are unavailable.", refusal.Message);
            Assert.Null(refusal.InnerException);
            Assert.True(Stopwatch.GetElapsedTime(started) < SqlServerAgentActivityModuleCaptureReader.MaximumBudget);
            Assert.Equal(2, resolver.Calls);
            await SqlAgentActivityNativeQualificationHarness.CanonicalExecRefusalAsync(context.Diagnostics, 513, 229, token);
            await input.RequireRepositoryIdentityAsync(dataSource, token);
            context.DeferredRepositoryReceipt = SqlAgentActivityRepositoryNativeReceipt.Refusal(input,
                context.Input.Enrollment, context.Request.RunId, resolver.Calls, "explicit_deny", 229);
        });

    [Fact]
    public Task ActualMonitorRevokedRepositoryAuthorizationRefusesPublicObservationsCollector() =>
        new SqlAgentActivityNativeQualificationHarness(output).RunAsync(false, async (context, token) =>
        {
            var input = SqlAgentActivityRepositoryNativeQualificationInput.Load(Environment.GetEnvironmentVariable);
            await using var dataSource = input.CreateDataSource();
            await input.RequireRepositoryIdentityAsync(dataSource, token);
            var repository = new PostgreSqlSqlAgentModuleEnrollmentPort(dataSource);
            var snapshot = Assert.IsType<SqlAgentModuleEnrollmentSnapshot>(await repository.ResolveAsync(
                context.Request.TargetId, context.Request.TargetRevision, token));
            Assert.Equal(context.Input.Enrollment.Revision + 1, snapshot.Revision);
            Assert.Null(snapshot.Enrollment);
            // Requiring the original independent window to remain active separates revocation
            // from expiration. The coordinator, never this monitor, owns the audited revocation.
            Assert.True(context.Input.Enrollment.IsActiveAt(DateTimeOffset.UtcNow));
            var resolver = new AuthoritativeResolver(repository, context.Input.Enrollment, revoked: true);
            var collector = new SqlAgentActivityObservationsCollector(new SqlServerAgentActivityModuleCaptureReader(resolver));
            long started = Stopwatch.GetTimestamp();
            CollectorExecutionResult result = await collector.CollectAsync(context.Request, token);
            Assert.True(Stopwatch.GetElapsedTime(started) < SqlServerAgentActivityModuleCaptureReader.MaximumBudget);
            Assert.Equal(1, resolver.Calls);
            Assert.Equal(context.Request.TargetId, result.TargetId); Assert.Equal(context.Request.TargetRevision, result.TargetRevision);
            Assert.Equal("sql-agent.activity-observations", result.CollectorId.Value);
            Assert.Equal(1, result.CollectorManifestVersion); Assert.Equal(1, result.OutputSchemaVersion);
            Assert.Equal(CollectorRunOutcome.PermissionDenied, result.Outcome);
            Assert.Equal(CollectorRunReason.RequiredPermissionMissing, result.Reason);
            Assert.Same(CollectorPayload.Empty, result.Payload);
            Assert.Equal(0, result.Accounting.SourceRowsRead); Assert.Equal(0, result.Accounting.OutputItemsProduced);
            Assert.Equal(0, result.Accounting.ResponseBytes); Assert.Equal(0, result.Accounting.OutputBytes);
            Assert.Same(CollectorLossEvidence.None, result.Loss);
            Assert.True(context.Input.Enrollment.IsActiveAt(DateTimeOffset.UtcNow));
            await input.RequireRepositoryIdentityAsync(dataSource, token);
            context.DeferredRepositoryReceipt = SqlAgentActivityRepositoryNativeReceipt.Refusal(input,
                context.Input.Enrollment, context.Request.RunId, resolver.Calls, "repository_revoked", null);
        });

    /// <summary>Transparent observer: every call delegates to the real repository port, returns
    /// precisely its answer, and compares independently supplied original authority. No cache or fallback.</summary>
    private sealed class AuthoritativeResolver(PostgreSqlSqlAgentModuleEnrollmentPort repository,
        DomainEnrollment expected, bool revoked) : ISqlAgentModuleEnrollmentResolver
    {
        internal int Calls { get; private set; }
        public async ValueTask<SqlAgentModuleEnrollmentSnapshot?> ResolveAsync(MonitoredInstanceId targetId,
            ObservationTargetRevision targetRevision, CancellationToken cancellationToken)
        {
            Assert.Equal(expected.TargetId, targetId); Assert.Equal(expected.TargetRevision, targetRevision);
            SqlAgentModuleEnrollmentSnapshot? snapshot = await repository.ResolveAsync(targetId, targetRevision, cancellationToken);
            Calls++;
            Assert.NotNull(snapshot);
            if (revoked)
            {
                Assert.Equal(expected.Revision + 1, snapshot.Revision); Assert.Null(snapshot.Enrollment);
            }
            else
            {
                Assert.Equal(expected.Revision, snapshot.Revision);
                Assert.True(expected.SameAuthorization(snapshot.Enrollment), "Repository authorization differs; private values withheld.");
            }
            return snapshot;
        }
    }
}

internal sealed class SqlAgentActivityRepositoryNativeQualificationInput
{
    internal const string Prefix = "SQLOBSERVER_AGENT_NATIVE_REPOSITORY_";
    private readonly string configuration;
    private SqlAgentActivityRepositoryNativeQualificationInput(Guid fixtureId, string database, string login, string configuration)
    { FixtureId = fixtureId; Database = database; Login = login; this.configuration = configuration; }
    internal Guid FixtureId { get; }
    internal string Database { get; }
    internal string Login { get; }
    internal static string DatabaseName(Guid fixtureId) => "so_agent_native_" + fixtureId.ToString("N");
    public override string ToString() => "Private disposable Agent repository qualification input";

    internal static SqlAgentActivityRepositoryNativeQualificationInput Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read(Prefix + "QUALIFICATION") != "1") throw Invalid();
        try
        {
            string id = Required("FIXTURE_ID", 36);
            if (!Guid.TryParseExact(id, "D", out Guid fixture) || fixture == Guid.Empty || fixture.ToString("D") != id) throw Invalid();
            string database = Required("DATABASE", 63);
            if (database != DatabaseName(fixture)) throw Invalid();
            string login = Required("EXPECTED_LOGIN", 63);
            if (login[0] is not (>= 'a' and <= 'z') || login.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
                login is "postgres" or "sqlobserver_server" or "sqlobserver_migrator" or "sqlobserver_auditor") throw Invalid();
            string connection = Required("COLLECTOR_CONNECTION", PostgreSqlDataSourceFactory.MaximumConfigurationLength);
            var settings = new NpgsqlConnectionStringBuilder(connection);
            string[] allowed = ["Host", "Port", "Database", "Username", "Password", "SSL Mode", "Timeout", "Command Timeout", "Pooling", "Maximum Pool Size", "Minimum Pool Size", "Options"];
            if (settings.Keys.Cast<string>().Any(name => !allowed.Contains(name, StringComparer.Ordinal)) ||
                settings.Host is not ("127.0.0.1" or "localhost") || settings.Port is < 1 or > 65535 ||
                settings.Database != database || settings.Username != login || string.IsNullOrEmpty(settings.Password) ||
                !string.IsNullOrEmpty(settings.Options) && settings.Options != "-c role=sqlobserver_collector" ||
                settings.Timeout is < 1 or > 10 || settings.CommandTimeout is < 1 or > 10)
                throw Invalid();
            return new(fixture, database, login, settings.ConnectionString);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException)
        { throw Invalid(); }

        string Required(string name, int maximum)
        {
            string? value = read(Prefix + name);
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl) || value.Trim() != value) throw Invalid();
            return value;
        }
    }

    internal NpgsqlDataSource CreateDataSource() => PostgreSqlDataSourceFactory.Create(configuration, "SqlObserver.Agent.NativeRepositoryQualification");

    internal void RequireIdentity(SqlAgentActivityRepositoryNativeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Database != Database || identity.SessionLogin != Login || identity.EffectiveLogin != "sqlobserver_collector" ||
            identity.Superuser || identity.CreateDatabase || identity.CreateRole || identity.Replication || identity.BypassRls ||
            !identity.CollectorMember || identity.ServerMember || identity.MigratorMember || identity.AuditorMember ||
            identity.OtherRoleMember || identity.DatabaseOwner || identity.DirectTargetMutation || identity.DirectEnrollmentMutation ||
            identity.EffectiveTargetMutation || identity.EffectiveEnrollmentMutation || identity.Timezone != "UTC" ||
            identity.EffectiveRole.CanLogin || identity.EffectiveRole.Superuser || identity.EffectiveRole.CreateDatabase ||
            identity.EffectiveRole.CreateRole || identity.EffectiveRole.Replication || identity.EffectiveRole.BypassRls || identity.EffectiveRole.Inherit) throw Invalid();
    }

    internal async Task RequireRepositoryIdentityAsync(NpgsqlDataSource dataSource, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT current_database(),session_user::text,current_user::text,r.rolsuper,r.rolcreatedb,r.rolcreaterole,
              r.rolreplication,r.rolbypassrls,pg_has_role(session_user,'sqlobserver_collector','MEMBER'),
              pg_has_role(session_user,'sqlobserver_server','MEMBER'),pg_has_role(session_user,'sqlobserver_migrator','MEMBER'),
              pg_has_role(session_user,'sqlobserver_auditor','MEMBER'),
              (has_table_privilege(session_user,'control.observation_target','INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                OR has_any_column_privilege(session_user,'control.observation_target','INSERT,UPDATE,REFERENCES')),
              (has_table_privilege(session_user,'control.sql_agent_module_enrollment_command','INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                OR has_any_column_privilege(session_user,'control.sql_agent_module_enrollment_command','INSERT,UPDATE,REFERENCES')),current_setting('TimeZone'),
              EXISTS(SELECT 1 FROM pg_auth_members m JOIN pg_roles parent ON parent.oid=m.roleid
                WHERE m.member IN(r.oid,e.oid) AND parent.rolname<>'sqlobserver_collector'),
              EXISTS(SELECT 1 FROM pg_database d WHERE d.datname=current_database() AND pg_has_role(session_user,d.datdba,'MEMBER')),
              e.rolcanlogin,e.rolsuper,e.rolcreatedb,e.rolcreaterole,e.rolreplication,e.rolbypassrls,e.rolinherit,
              (has_table_privilege(current_user,'control.observation_target','INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                OR has_any_column_privilege(current_user,'control.observation_target','INSERT,UPDATE,REFERENCES')),
              (has_table_privilege(current_user,'control.sql_agent_module_enrollment_command','INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                OR has_any_column_privilege(current_user,'control.sql_agent_module_enrollment_command','INSERT,UPDATE,REFERENCES'))
            FROM pg_roles r JOIN pg_roles e ON e.rolname=current_user WHERE r.rolname=session_user;
            """);
        command.CommandTimeout = 5;
        await using var row = await command.ExecuteReaderAsync(token);
        if (row.FieldCount != 26 || !await row.ReadAsync(token)) throw Invalid();
        RequireIdentity(new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetBoolean(3), row.GetBoolean(4), row.GetBoolean(5),
            row.GetBoolean(6), row.GetBoolean(7), row.GetBoolean(8), row.GetBoolean(9), row.GetBoolean(10), row.GetBoolean(11),
            row.GetBoolean(12), row.GetBoolean(13), row.GetString(14), row.GetBoolean(15), row.GetBoolean(16),
            new(row.GetBoolean(17), row.GetBoolean(18), row.GetBoolean(19), row.GetBoolean(20), row.GetBoolean(21), row.GetBoolean(22), row.GetBoolean(23)),
            row.GetBoolean(24), row.GetBoolean(25)));
        if (await row.ReadAsync(token) || await row.NextResultAsync(token)) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Native repository qualification input or identity is invalid; private values withheld.");
}

internal sealed record SqlAgentActivityRepositoryNativeIdentity(string Database, string SessionLogin, string EffectiveLogin,
    bool Superuser, bool CreateDatabase, bool CreateRole, bool Replication, bool BypassRls, bool CollectorMember,
    bool ServerMember, bool MigratorMember, bool AuditorMember, bool DirectTargetMutation, bool DirectEnrollmentMutation, string Timezone,
    bool OtherRoleMember, bool DatabaseOwner, SqlAgentActivityRepositoryEffectiveRoleIdentity EffectiveRole,
    bool EffectiveTargetMutation, bool EffectiveEnrollmentMutation);

internal sealed record SqlAgentActivityRepositoryEffectiveRoleIdentity(bool CanLogin, bool Superuser, bool CreateDatabase,
    bool CreateRole, bool Replication, bool BypassRls, bool Inherit);

internal static class SqlAgentActivityRepositoryNativeReceipt
{
    internal const string Marker = "SQL_AGENT_ACTIVITY_REPOSITORY_NATIVE_RECEIPT_V2:";
    internal const int MaximumReceiptBytes = 532000;

    internal static string Capture(SqlAgentActivityRepositoryNativeQualificationInput input, DomainEnrollment original,
        CollectorExecutionResult result, CollectorRunId runId, int resolutions)
    {
        SqlAgentModuleCaptureResult? capture = result.Payload.SqlAgentModuleCapture;
        if (resolutions != 3 || capture is null || original.Revision != capture.EnrollmentRevision ||
            original.EnrollmentId != capture.EnrollmentId || original.EvidenceSha256 != capture.EvidenceSha256) throw Invalid();
        // Reuse the closed source serialization, not its finite-resolver provenance label.
        var value = JsonNode.Parse(SqlAgentActivityCollectorNativeReceipt.Serialize(result, runId))!.AsObject();
        value["schemaVersion"] = 2;
        value["repositoryResolverQualified"] = true;
        value["repository"] = Provenance(input, original, resolutions, "captured");
        return Bounded(value);
    }

    internal static string Refusal(SqlAgentActivityRepositoryNativeQualificationInput input, DomainEnrollment original,
        CollectorRunId runId, int resolutions, string reason, int? canonicalExecError)
    {
        if (reason == "explicit_deny" ? resolutions != 2 || canonicalExecError != 229 :
            reason != "repository_revoked" || resolutions != 1 || canonicalExecError is not null) throw Invalid();
        return Bounded(new JsonObject
        {
            ["schemaVersion"] = 2, ["diagnosticOnly"] = true, ["actualMonitorCollector"] = reason == "repository_revoked",
            ["publicReaderInvoked"] = true, ["repositoryResolverQualified"] = true,
            ["productionAdmitted"] = false, ["serviceSessionAssociationQualified"] = false, ["nativeQualification"] = "Absent",
            ["targetId"] = original.TargetId.Value.ToString("D"), ["targetRevision"] = original.TargetRevision.Value,
            ["runId"] = runId.Value.ToString("D"), ["captureAccepted"] = false, ["refusalReason"] = reason,
            ["canonicalExecError"] = canonicalExecError,
            ["repository"] = Provenance(input, original, resolutions, reason),
        });
    }

    private static JsonNode Provenance(SqlAgentActivityRepositoryNativeQualificationInput input, DomainEnrollment original,
        int resolutions, string outcome) => JsonSerializer.SerializeToNode(new
        {
            fixtureId = input.FixtureId.ToString("D"), actualDatabaseVerified = true, restrictedLoginVerified = true,
            collectorGroupVerified = true, elevatedRoleMembershipAbsent = true, directControlMutationPrivilegesAbsent = true,
            effectiveCollectorGroupSafeAttributesVerified = true, sessionAndEffectiveRoleTableAndColumnMutationPrivilegesAbsent = true,
            otherDirectRoleMembershipAbsent = true, databaseOwnerMembershipAbsent = true,
            authoritativeRepositoryPort = "PostgreSqlSqlAgentModuleEnrollmentPort", authorizationResolutions = resolutions,
            resolverCached = false, finiteResolverUsed = false, expectedOriginalAuthorizationUsedOnlyForComparison = true,
            originalAuthorizationWindowUnchanged = true,
            originalNotBeforeUtc = original.NotBeforeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            originalExpiresAtUtc = original.ExpiresAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            originalEnrollmentRevision = original.Revision, originalEnrollmentId = original.EnrollmentId.ToString("D"),
            originalEvidenceSha256 = original.EvidenceSha256, outcome,
            continuousDeploymentQualified = false, scope = "Finite actual monitor collector/reader with disposable authoritative repository enrollment",
        })!;

    private static string Bounded(JsonObject value)
    {
        string receipt = value.ToJsonString();
        if (Encoding.UTF8.GetByteCount(receipt) > MaximumReceiptBytes) throw Invalid();
        return receipt;
    }
    private static InvalidDataException Invalid() => new("Native repository receipt contract is invalid; private values withheld.");
}
