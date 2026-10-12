using SqlObserver.Domain.Collection;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Private phase inputs are supplied by the independently journaled DBA trial.
/// A catalog DENY is an input to a failure fixture, never proof of effective denial.</summary>
internal sealed record SqlAgentActivityExplicitDenyEnrollment(
    SqlAgentProductionModuleQualificationEnrollment Production, string DeniedPostimageSha256)
{
    internal const string Prefix = "SQLOBSERVER_AGENT_MODULE_EXPLICIT_DENY_";
    internal const string Scope = "msdb.syssessions.select.certificate-user";

    internal static SqlAgentActivityExplicitDenyEnrollment Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read(Prefix + "QUALIFICATION") != "1") throw Invalid();
        try
        {
            if (read(Prefix + "PHASE") != "denied" || read(Prefix + "SCOPE") != Scope ||
                read(Prefix + "JOURNAL_SCHEMA_VERSION") != "4" || read(Prefix + "INSTALLATION_POLICY_VERSION") != "2")
                throw Invalid();
            var production = SqlAgentProductionModuleQualificationEnrollment.Load(read, false);
            string? id = read(Prefix + "DEPLOYMENT_ID");
            if (id != production.Enrollment.EnrollmentId.ToString("D")) throw Invalid();
            string? digest = read(Prefix + "DENIED_POSTIMAGE_SHA256");
            if (digest is null || digest.Length != 64 || digest.All(c => c == '0') ||
                digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                digest == production.Enrollment.EvidenceSha256) throw Invalid();
            return new(production, digest);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { throw Invalid(); }
    }

    public override string ToString() => "Private explicit DENY qualification enrollment";
    private static InvalidOperationException Invalid() =>
        new("Explicit DENY qualification enrollment is absent or invalid; private values withheld.");
}

public sealed class SqlAgentActivityExplicitDenyEnrollmentTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")] [InlineData(" 1")]
    public void DenyOptInIsCheckedBeforeOtherPrivateInputs(string? value)
    {
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => SqlAgentActivityExplicitDenyEnrollment.Load(name =>
        { calls++; Assert.Equal(SqlAgentActivityExplicitDenyEnrollment.Prefix + "QUALIFICATION", name); return value; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ExactPhasePreservesOriginalSignedEnrollmentAndIndependentDeniedImage()
    {
        var input = SqlAgentActivityNativeFixture.ValidEnvironment();
        var value = SqlAgentActivityExplicitDenyEnrollment.Load(input.GetValueOrDefault);
        Assert.Equal(new string('c', 64), value.Production.Enrollment.EvidenceSha256);
        Assert.Equal(new string('d', 64), value.DeniedPostimageSha256);
        Assert.Equal(1, value.Production.Enrollment.Revision);
        Assert.DoesNotContain(value.DeniedPostimageSha256, value.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PHASE", null)] [InlineData("PHASE", "signed")] [InlineData("PHASE", "deny_pending")]
    [InlineData("PHASE", "deny_restored")] [InlineData("SCOPE", "msdb.syssessions.select.monitor")]
    [InlineData("JOURNAL_SCHEMA_VERSION", "3")] [InlineData("JOURNAL_SCHEMA_VERSION", "04")]
    [InlineData("INSTALLATION_POLICY_VERSION", "1")] [InlineData("INSTALLATION_POLICY_VERSION", "02")]
    [InlineData("DEPLOYMENT_ID", "33333333-3333-4333-8333-333333333333")]
    [InlineData("DEPLOYMENT_ID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("DENIED_POSTIMAGE_SHA256", "private-secret")]
    public void InvalidPhaseIdentityAndPinsFailClosedWithoutEcho(string field, string? value)
    {
        var input = SqlAgentActivityNativeFixture.ValidEnvironment();
        input[SqlAgentActivityExplicitDenyEnrollment.Prefix + field] = value;
        var error = Assert.Throws<InvalidOperationException>(() => SqlAgentActivityExplicitDenyEnrollment.Load(input.GetValueOrDefault));
        Assert.DoesNotContain("private-secret", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData('0')] [InlineData('C')] [InlineData('c')]
    public void DeniedDigestMustBeNonzeroLowercaseAndDifferentFromOriginalSignedImage(char digit)
    {
        var input = SqlAgentActivityNativeFixture.ValidEnvironment();
        input[SqlAgentActivityExplicitDenyEnrollment.Prefix + "DENIED_POSTIMAGE_SHA256"] = new string(digit, 64);
        Assert.Throws<InvalidOperationException>(() => SqlAgentActivityExplicitDenyEnrollment.Load(input.GetValueOrDefault));
    }

    [Theory]
    [InlineData("SQLOBSERVER_AGENT_MODULE_UNSIGNED_QUALIFICATION", "1")]
    [InlineData("SQLOBSERVER_AGENT_MODULE_PRODUCTION_QUALIFICATION", null)]
    [InlineData("SQLOBSERVER_AGENT_MODULE_METADATA_QUALIFICATION", null)]
    [InlineData("SQLOBSERVER_CPU_EXPECTED_SQL_MAJOR", "16")]
    [InlineData("SQLOBSERVER_AGENT_MODULE_PRODUCTION_ENROLLMENT_REVISION", "01")]
    public void ExistingSignedIdentityAndMetadataContractCannotBeBypassed(string key, string? value)
    {
        var input = SqlAgentActivityNativeFixture.ValidEnvironment(); input[key] = value;
        Assert.Throws<InvalidOperationException>(() => SqlAgentActivityExplicitDenyEnrollment.Load(input.GetValueOrDefault));
    }

    [Theory]
    [InlineData("1")] [InlineData("0")] [InlineData("")]
    public void SignedCollectorPhaseCannotConsumeAnExplicitDenyOptIn(string value)
    {
        var input = SqlAgentActivityNativeFixture.ValidEnvironment();
        input[SqlAgentActivityExplicitDenyEnrollment.Prefix + "QUALIFICATION"] = value;
        Assert.Throws<InvalidOperationException>(() => SqlAgentActivityNativeFixture.LoadSigned(input.GetValueOrDefault));
    }

    [Fact]
    public void SignedCollectorPhaseUsesSameStrictProductionEnrollmentWhenDenyOptInIsAbsent()
    {
        var input = SqlAgentActivityNativeFixture.ValidEnvironment();
        input.Remove(SqlAgentActivityExplicitDenyEnrollment.Prefix + "QUALIFICATION");
        var production = SqlAgentActivityNativeFixture.LoadSigned(input.GetValueOrDefault);
        Assert.Equal(new string('c', 64), production.Enrollment.EvidenceSha256);
    }
}

internal static class SqlAgentActivityNativeFixture
{
    internal static SqlAgentProductionModuleQualificationEnrollment LoadSigned(Func<string, string?> read)
    {
        var production = SqlAgentProductionModuleQualificationEnrollment.Load(read, false);
        if (read(SqlAgentActivityExplicitDenyEnrollment.Prefix + "QUALIFICATION") is not null)
            throw new InvalidOperationException("Signed collector qualification phase is invalid; private values withheld.");
        return production;
    }

    internal static Dictionary<string, string?> ValidEnvironment() => new(StringComparer.Ordinal)
    {
        ["SQLOBSERVER_CPU_QUALIFICATION"] = "1", ["SQLOBSERVER_CPU_HOST"] = "lab", ["SQLOBSERVER_CPU_PORT"] = "1433",
        ["SQLOBSERVER_CPU_CERTIFICATE_HOST"] = "lab.example.test",
        ["SQLOBSERVER_CPU_TARGET_ID"] = "11111111-1111-4111-8111-111111111111", ["SQLOBSERVER_CPU_TARGET_REVISION"] = "1",
        ["SQLOBSERVER_CPU_HOST_BINDING_REVISION"] = "1", ["SQLOBSERVER_CPU_HOST_PROFILE_REVISION"] = "1",
        ["SQLOBSERVER_CPU_EXPECTED_HOST_FINGERPRINT"] = new string('b', 64), ["SQLOBSERVER_CPU_FINGERPRINT_KEY"] = new string('a', 64),
        ["SQLOBSERVER_CPU_EXPECTED_SQL_MAJOR"] = "17", ["SQLOBSERVER_CPU_EXPECTED_WINDOWS_SID"] = "S-1-5-21-123-456",
        ["SQLOBSERVER_AGENT_MODULE_QUALIFICATION"] = "1", ["SQLOBSERVER_AGENT_MODULE_SOURCE_VERSION"] = "2",
        ["SQLOBSERVER_AGENT_MODULE_DEFINITION_SHA256"] = SqlAgentModuleBinding.ApprovedDefinitionSha256,
        ["SQLOBSERVER_AGENT_MODULE_METADATA_QUALIFICATION"] = "1", ["SQLOBSERVER_AGENT_MODULE_METADATA_OBJECT_ID"] = "123",
        ["SQLOBSERVER_AGENT_MODULE_METADATA_CERTIFICATE_ID"] = "456",
        ["SQLOBSERVER_AGENT_MODULE_METADATA_PUBLIC_CERTIFICATE_SHA256"] = new string('a', 64),
        ["SQLOBSERVER_AGENT_MODULE_METADATA_THUMBPRINT_HEX"] = new string('b', 40),
        ["SQLOBSERVER_AGENT_MODULE_PRODUCTION_QUALIFICATION"] = "1",
        ["SQLOBSERVER_AGENT_MODULE_PRODUCTION_ENROLLMENT_REVISION"] = "1",
        ["SQLOBSERVER_AGENT_MODULE_PRODUCTION_ENROLLMENT_ID"] = "22222222-2222-4222-8222-222222222222",
        ["SQLOBSERVER_AGENT_MODULE_PRODUCTION_EVIDENCE_SHA256"] = new string('c', 64),
        ["SQLOBSERVER_AGENT_MODULE_PRODUCTION_NOT_BEFORE_UTC"] = "2026-10-05T12:00:00.0000000Z",
        ["SQLOBSERVER_AGENT_MODULE_PRODUCTION_EXPIRES_AT_UTC"] = "2026-10-05T13:00:00.0000000+00:00",
        ["SQLOBSERVER_AGENT_MODULE_PRODUCTION_SERVER_NAME"] = "expected-server",
        ["SQLOBSERVER_AGENT_MODULE_PRODUCTION_MACHINE_NAME"] = "expected-machine",
        ["SQLOBSERVER_AGENT_MODULE_PRODUCTION_INSTANCE_NAME_JSON"] = "\"\"",
        [SqlAgentActivityExplicitDenyEnrollment.Prefix + "QUALIFICATION"] = "1",
        [SqlAgentActivityExplicitDenyEnrollment.Prefix + "PHASE"] = "denied",
        [SqlAgentActivityExplicitDenyEnrollment.Prefix + "SCOPE"] = SqlAgentActivityExplicitDenyEnrollment.Scope,
        [SqlAgentActivityExplicitDenyEnrollment.Prefix + "DEPLOYMENT_ID"] = "22222222-2222-4222-8222-222222222222",
        [SqlAgentActivityExplicitDenyEnrollment.Prefix + "JOURNAL_SCHEMA_VERSION"] = "4",
        [SqlAgentActivityExplicitDenyEnrollment.Prefix + "INSTALLATION_POLICY_VERSION"] = "2",
        [SqlAgentActivityExplicitDenyEnrollment.Prefix + "DENIED_POSTIMAGE_SHA256"] = new string('d', 64),
    };
}
