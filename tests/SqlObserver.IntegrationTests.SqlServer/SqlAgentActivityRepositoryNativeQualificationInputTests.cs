using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using DomainEnrollment = SqlObserver.Domain.Collection.SqlAgentModuleEnrollment;

namespace SqlObserver.IntegrationTests.SqlServer;

[Trait("Category", "AgentNativeRepositoryContract")]
public sealed class SqlAgentActivityRepositoryNativeQualificationInputTests
{
    private static readonly Guid Fixture = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private const string Login = "agent_native_test_login";

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")]
    public void ExplicitOptInIsRequiredBeforeReadingAnyPrivateInput(string? value)
    {
        int calls = 0;
        var failure = Assert.Throws<InvalidOperationException>(() => SqlAgentActivityRepositoryNativeQualificationInput.Load(name =>
        {
            calls++; Assert.Equal(SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "QUALIFICATION", name); return value;
        }));
        Assert.Equal(1, calls);
        Assert.Equal("Native repository qualification input or identity is invalid; private values withheld.", failure.Message);
    }

    [Fact]
    public void ExactDisposableInputsRetainIndependentIdentityAndHidePrivateConfiguration()
    {
        var input = Load(Valid());
        Assert.Equal(Fixture, input.FixtureId);
        Assert.Equal("so_agent_native_11111111111141118111111111111111", input.Database);
        Assert.Equal(Login, input.Login);
        Assert.DoesNotContain("not-a-real-password", input.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Login, input.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(input.Database, input.ToString(), StringComparison.Ordinal);
        input.RequireIdentity(Identity(input));
    }

    [Theory]
    [InlineData("FIXTURE_ID", null)] [InlineData("FIXTURE_ID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("FIXTURE_ID", "11111111111141118111111111111111")]
    [InlineData("FIXTURE_ID", "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")]
    [InlineData("DATABASE", "postgres")] [InlineData("DATABASE", "so_agent_native_22222222222242228222222222222222")]
    [InlineData("EXPECTED_LOGIN", "postgres")] [InlineData("EXPECTED_LOGIN", "sqlobserver_server")]
    [InlineData("EXPECTED_LOGIN", "sqlobserver_migrator")] [InlineData("EXPECTED_LOGIN", "sqlobserver_auditor")]
    [InlineData("EXPECTED_LOGIN", "UPPER_LOGIN")] [InlineData("EXPECTED_LOGIN", "bad;login")]
    [InlineData("EXPECTED_LOGIN", "1login")] [InlineData("EXPECTED_LOGIN", " login")]
    [InlineData("COLLECTOR_CONNECTION", null)] [InlineData("COLLECTOR_CONNECTION", "not a connection")]
    public void MissingMalformedOrUnboundIdentityInputsFailWithoutEcho(string name, string? value)
    {
        var environment = Valid(); environment[SqlAgentActivityRepositoryNativeQualificationInput.Prefix + name] = value;
        var failure = Assert.Throws<InvalidOperationException>(() => Load(environment));
        Assert.Equal("Native repository qualification input or identity is invalid; private values withheld.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("Host", "192.168.50.174")] [InlineData("Host", "localhost,remote")]
    [InlineData("Database", "postgres")] [InlineData("Username", "postgres")]
    [InlineData("Password", "")] [InlineData("Timeout", "0")] [InlineData("Timeout", "15")]
    [InlineData("Command Timeout", "0")] [InlineData("Command Timeout", "30")]
    [InlineData("Options", "-c role=sqlobserver_server")]
    [InlineData("Options", "-c role=sqlobserver_collector -c search_path=public")]
    [InlineData("Options", "-c statement_timeout=0")]
    [InlineData("Search Path", "public")] [InlineData("Include Error Detail", "true")]
    [InlineData("Persist Security Info", "true")] [InlineData("Passfile", "private-password-file")]
    [InlineData("Application Name", "untrusted-native")]
    public void ConnectionConfigurationRejectsPrivilegeSwitchFallbackLoggingAndUnboundedCommands(string key, string value)
    {
        var environment = Valid();
        var settings = new NpgsqlConnectionStringBuilder(environment[SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "COLLECTOR_CONNECTION"]);
        settings[key] = value;
        environment[SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "COLLECTOR_CONNECTION"] = settings.ConnectionString;
        var failure = Assert.Throws<InvalidOperationException>(() => Load(environment));
        Assert.Equal("Native repository qualification input or identity is invalid; private values withheld.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Database=untrusted\n;Username=login;Password=secret")]
    [InlineData("Host=localhost;Database=untrusted;UnknownSecretOption=secret")]
    public void MalformedConnectionErrorsNeverRetainProviderDetails(string connection)
    {
        var environment = Valid();
        environment[SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "COLLECTOR_CONNECTION"] = connection;
        var failure = Assert.Throws<InvalidOperationException>(() => Load(environment));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("database")] [InlineData("session")] [InlineData("effective")]
    [InlineData("superuser")] [InlineData("createDatabase")] [InlineData("createRole")]
    [InlineData("replication")] [InlineData("bypassRls")] [InlineData("collector")]
    [InlineData("server")] [InlineData("migrator")] [InlineData("auditor")]
    [InlineData("targetMutation")] [InlineData("enrollmentMutation")] [InlineData("timezone")]
    [InlineData("otherRole")] [InlineData("databaseOwner")]
    [InlineData("effectiveCanLogin")] [InlineData("effectiveSuperuser")] [InlineData("effectiveCreateDatabase")]
    [InlineData("effectiveCreateRole")] [InlineData("effectiveReplication")] [InlineData("effectiveBypassRls")] [InlineData("effectiveInherit")]
    [InlineData("effectiveTargetMutation")] [InlineData("effectiveEnrollmentMutation")]
    public void ActualDatabaseIdentityMustBeBoundAndRestricted(string changed)
    {
        var input = Load(Valid()); var original = Identity(input);
        var altered = changed switch
        {
            "database" => original with { Database = "postgres" },
            "session" => original with { SessionLogin = "postgres" },
            "effective" => original with { EffectiveLogin = Login },
            "superuser" => original with { Superuser = true },
            "createDatabase" => original with { CreateDatabase = true },
            "createRole" => original with { CreateRole = true },
            "replication" => original with { Replication = true },
            "bypassRls" => original with { BypassRls = true },
            "collector" => original with { CollectorMember = false },
            "server" => original with { ServerMember = true },
            "migrator" => original with { MigratorMember = true },
            "auditor" => original with { AuditorMember = true },
            "targetMutation" => original with { DirectTargetMutation = true },
            "enrollmentMutation" => original with { DirectEnrollmentMutation = true },
            "timezone" => original with { Timezone = "America/New_York" },
            "otherRole" => original with { OtherRoleMember = true },
            "databaseOwner" => original with { DatabaseOwner = true },
            "effectiveCanLogin" => original with { EffectiveRole = original.EffectiveRole with { CanLogin = true } },
            "effectiveSuperuser" => original with { EffectiveRole = original.EffectiveRole with { Superuser = true } },
            "effectiveCreateDatabase" => original with { EffectiveRole = original.EffectiveRole with { CreateDatabase = true } },
            "effectiveCreateRole" => original with { EffectiveRole = original.EffectiveRole with { CreateRole = true } },
            "effectiveReplication" => original with { EffectiveRole = original.EffectiveRole with { Replication = true } },
            "effectiveBypassRls" => original with { EffectiveRole = original.EffectiveRole with { BypassRls = true } },
            "effectiveInherit" => original with { EffectiveRole = original.EffectiveRole with { Inherit = true } },
            "effectiveTargetMutation" => original with { EffectiveTargetMutation = true },
            "effectiveEnrollmentMutation" => original with { EffectiveEnrollmentMutation = true },
            _ => throw new InvalidOperationException(),
        };
        var failure = Assert.Throws<InvalidOperationException>(() => input.RequireIdentity(altered));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("postgres", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)] [InlineData(512)] [InlineData(513)]
    public void NewRepositoryReceiptPreservesSourceAccountingAndLeavesFiniteV1ContractUntouched(int sourceRows)
    {
        var input = Load(Valid()); var (original, result, run) = Result(sourceRows);
        string finite = SqlAgentActivityCollectorNativeReceipt.Serialize(result, run);
        string repository = SqlAgentActivityRepositoryNativeReceipt.Capture(input, original, result, run, 3);
        using var old = JsonDocument.Parse(finite); using var current = JsonDocument.Parse(repository);
        Assert.Equal(1, old.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(old.RootElement.GetProperty("repositoryResolverQualified").GetBoolean());
        Assert.Equal(2, current.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(current.RootElement.GetProperty("repositoryResolverQualified").GetBoolean());
        Assert.Equal(19, current.RootElement.EnumerateObject().Count());
        foreach (string field in new[] { "observation", "accounting", "loss", "targetId", "targetRevision", "runId", "outcome", "reason" })
            Assert.Equal(old.RootElement.GetProperty(field).GetRawText(), current.RootElement.GetProperty(field).GetRawText());
        var provenance = current.RootElement.GetProperty("repository");
        Assert.Equal(Fixture.ToString("D"), provenance.GetProperty("fixtureId").GetString());
        Assert.Equal(3, provenance.GetProperty("authorizationResolutions").GetInt32());
        Assert.False(provenance.GetProperty("finiteResolverUsed").GetBoolean());
        Assert.False(provenance.GetProperty("resolverCached").GetBoolean());
        Assert.False(provenance.GetProperty("continuousDeploymentQualified").GetBoolean());
        Assert.True(provenance.GetProperty("expectedOriginalAuthorizationUsedOnlyForComparison").GetBoolean());
        Assert.Equal("2026-10-05T12:00:00.0000000Z", provenance.GetProperty("originalNotBeforeUtc").GetString());
        Assert.False(current.RootElement.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(current.RootElement.GetProperty("serviceSessionAssociationQualified").GetBoolean());
        Assert.Equal("Absent", current.RootElement.GetProperty("nativeQualification").GetString());
        Assert.InRange(Encoding.UTF8.GetByteCount(repository), 1, SqlAgentActivityRepositoryNativeReceipt.MaximumReceiptBytes);
        foreach (string forbidden in new[] { "not-a-real-password", Login, input.Database, original.ExpectedWindowsSid, "connectionString" })
            Assert.DoesNotContain(forbidden, repository, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("explicit_deny", 2, 229, true)]
    [InlineData("repository_revoked", 1, null, true)]
    [InlineData("explicit_deny", 1, 229, false)]
    [InlineData("explicit_deny", 2, 0, false)]
    [InlineData("repository_revoked", 2, null, false)]
    [InlineData("repository_revoked", 1, 229, false)]
    [InlineData("invented", 1, null, false)]
    public void RefusalReceiptRequiresExactScopeAndFenceCount(string reason, int calls, int? sqlError, bool valid)
    {
        var input = Load(Valid()); var (original, _, run) = Result(0);
        if (!valid)
        {
            Assert.Throws<InvalidDataException>(() => SqlAgentActivityRepositoryNativeReceipt.Refusal(input, original, run, calls, reason, sqlError));
            return;
        }
        using var proof = JsonDocument.Parse(SqlAgentActivityRepositoryNativeReceipt.Refusal(input, original, run, calls, reason, sqlError));
        Assert.False(proof.RootElement.GetProperty("captureAccepted").GetBoolean());
        Assert.Equal(reason == "repository_revoked", proof.RootElement.GetProperty("actualMonitorCollector").GetBoolean());
        Assert.Equal(reason, proof.RootElement.GetProperty("refusalReason").GetString());
        Assert.False(proof.RootElement.GetProperty("productionAdmitted").GetBoolean());
        Assert.Equal("Absent", proof.RootElement.GetProperty("nativeQualification").GetString());
        Assert.False(proof.RootElement.TryGetProperty("observation", out _));
    }

    private static SqlAgentActivityRepositoryNativeQualificationInput Load(Dictionary<string, string?> environment) =>
        SqlAgentActivityRepositoryNativeQualificationInput.Load(environment.GetValueOrDefault);

    private static Dictionary<string, string?> Valid()
    {
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = 5432, Database = SqlAgentActivityRepositoryNativeQualificationInput.DatabaseName(Fixture),
            Username = Login, Password = "not-a-real-password", Options = "-c role=sqlobserver_collector", Timeout = 5, CommandTimeout = 5,
        };
        return new(StringComparer.Ordinal)
        {
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "QUALIFICATION"] = "1",
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "FIXTURE_ID"] = Fixture.ToString("D"),
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "DATABASE"] = settings.Database,
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "EXPECTED_LOGIN"] = Login,
            [SqlAgentActivityRepositoryNativeQualificationInput.Prefix + "COLLECTOR_CONNECTION"] = settings.ConnectionString,
        };
    }

    private static SqlAgentActivityRepositoryNativeIdentity Identity(SqlAgentActivityRepositoryNativeQualificationInput input) =>
        new(input.Database, input.Login, "sqlobserver_collector", false, false, false, false, false,
            true, false, false, false, false, false, "UTC", false, false, new(false, false, false, false, false, false, false), false, false);

    private static (DomainEnrollment Original, CollectorExecutionResult Result, CollectorRunId Run) Result(int sourceRows)
    {
        var original = SqlAgentProductionModuleQualificationEnrollment.Load(SqlAgentActivityNativeFixture.ValidEnvironment().GetValueOrDefault, false).Enrollment;
        var run = new CollectorRunId(Guid.Parse("33333333-3333-4333-8333-333333333333"));
        var utc = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var local = new DateTime(2026, 10, 5, 1, 30, 0, DateTimeKind.Unspecified).AddTicks(1234567);
        var header = new SqlAgentActivityHeader(utc, new(1, 1, 0, null), new(1, local));
        var jobs = Enumerable.Range(1, Math.Min(sourceRows, 512)).Select(index => new SqlAgentActivityJob(
            Guid.Parse("00000000-0000-0000-0000-" + index.ToString("000000000000", CultureInfo.InvariantCulture)),
            1, local, null, local, local.AddSeconds(-1), 2, local)).ToArray();
        var capture = new SqlAgentModuleCaptureResult(new(original.TargetId, original.TargetRevision, run, header, jobs,
            new(utc.AddTicks(1), header.Service, header.Session), sourceRows, sourceRows > 512), original);
        var payload = new CollectorPayload(sqlAgentModuleCapture: capture);
        var result = new CollectorExecutionResult(original.TargetId, original.TargetRevision, new("sql-agent.activity-observations"), 1, 1,
            sourceRows > 512 ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
            sourceRows > 512 ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed, payload,
            new(sourceRows, 1, 4096 + sourceRows * 768, payload.EstimatedSizeBytes),
            sourceRows > 512 ? new(CollectorLossKind.SourceRowLimit, 1, false) : CollectorLossEvidence.None);
        return (original, result, run);
    }
}
