using System.Globalization;
using System.Text.Json;
using SqlObserver.Domain.Collection;
using DomainEnrollment = SqlObserver.Domain.Collection.SqlAgentModuleEnrollment;

namespace SqlObserver.IntegrationTests.SqlServer;

internal sealed class SqlAgentProductionModuleQualificationEnrollment
{
    internal const string Prefix = "SQLOBSERVER_AGENT_MODULE_PRODUCTION_";
    private SqlAgentProductionModuleQualificationEnrollment(PairedCpuQualificationLabContract lab,
        DomainEnrollment enrollment, SqlAgentModuleMetadataEnrollment metadata)
    { Lab = lab; Enrollment = enrollment; Metadata = metadata; }
    internal PairedCpuQualificationLabContract Lab { get; }
    internal DomainEnrollment Enrollment { get; }
    internal SqlAgentModuleMetadataEnrollment Metadata { get; }
    public override string ToString() => "Private production module qualification enrollment";

    internal static SqlAgentProductionModuleQualificationEnrollment Load(Func<string, string?> read, bool unsigned)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read(Prefix + "QUALIFICATION") != "1") throw Invalid();
        try
        {
            var metadata = SqlAgentMetadataQualificationEnrollment.Load(read, unsigned);
            var lab = PairedCpuQualificationLabContract.Load(read);
            if (lab.ExpectedSqlMajor != 17) throw Invalid();
            string revisionText = Required("ENROLLMENT_REVISION", 16);
            if (!long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out long revision) ||
                revision.ToString(CultureInfo.InvariantCulture) != revisionText) throw Invalid();
            string idText = Required("ENROLLMENT_ID", 36);
            if (!Guid.TryParseExact(idText, "D", out Guid id) || id == Guid.Empty || id.ToString("D") != idText) throw Invalid();
            using var instanceJson = JsonDocument.Parse(Required("INSTANCE_NAME_JSON", 1024), new JsonDocumentOptions { MaxDepth = 1 });
            if (instanceJson.RootElement.ValueKind != JsonValueKind.String) throw Invalid();
            var binding = new SqlAgentModuleBinding(lab.Policy.Endpoint.HostName.Value, lab.Policy.Endpoint.TcpPort!.Value,
                lab.Policy.CertificateHostName!.Value, Required("SERVER_NAME", 128), Required("MACHINE_NAME", 128),
                instanceJson.RootElement.GetString()!, metadata.ObjectId, metadata.CertificateId, metadata.DefinitionSha256,
                metadata.PublicCertificateSha256, metadata.ThumbprintHex);
            var enrollment = new DomainEnrollment(lab.TargetId, lab.TargetRevision, revision, id, lab.ExpectedWindowsSid,
                binding, Utc("NOT_BEFORE_UTC"), Utc("EXPIRES_AT_UTC"), Required("EVIDENCE_SHA256", 64));
            return new(lab, enrollment, metadata);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or OverflowException or InvalidOperationException)
        { throw Invalid(); }

        string Required(string name, int maximum)
        {
            string? value = read(Prefix + name);
            if (string.IsNullOrEmpty(value) || value.Length > maximum) throw Invalid();
            return value;
        }
        DateTimeOffset Utc(string name)
        {
            string value = Required(name, 33);
            if (!DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset result) || result.Offset != TimeSpan.Zero)
                throw Invalid();
            return result;
        }
    }
    private static InvalidOperationException Invalid() => new("Public module qualification enrollment is absent or invalid; private values withheld.");
}

public sealed class SqlAgentProductionModuleEnrollmentTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")]
    public void ExplicitProductionOptInIsCheckedBeforeOtherConfiguration(string? value)
    {
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => SqlAgentProductionModuleQualificationEnrollment.Load(name =>
        { calls++; Assert.Equal(SqlAgentProductionModuleQualificationEnrollment.Prefix + "QUALIFICATION", name); return value; }, false));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IndependentEnrollmentPreservesExpectedEndpointMetadataIdentityAndEmptyInstance(bool signatureRemoved)
    {
        var input = Valid();
        if (signatureRemoved) input["SQLOBSERVER_AGENT_MODULE_UNSIGNED_QUALIFICATION"] = "1";
        var result = SqlAgentProductionModuleQualificationEnrollment.Load(input.GetValueOrDefault, signatureRemoved);
        Assert.Equal("expected-server", result.Enrollment.Binding.ServerName);
        Assert.Equal("expected-machine", result.Enrollment.Binding.MachineName);
        Assert.Equal("", result.Enrollment.Binding.InstanceName);
        Assert.Equal("lab", result.Enrollment.Binding.EndpointHost);
        Assert.Equal("lab.example.test", result.Enrollment.Binding.CertificateHost);
        Assert.Equal(123, result.Enrollment.Binding.ObjectId); Assert.Equal(456, result.Enrollment.Binding.CertificateId);
        Assert.Equal(result.Lab.TargetId, result.Enrollment.TargetId);
        Assert.Equal(result.Lab.TargetRevision, result.Enrollment.TargetRevision);
        Assert.Equal(1, result.Enrollment.Revision);
        Assert.Equal(new string('c', 64), result.Enrollment.EvidenceSha256);
        Assert.DoesNotContain("expected-server", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ENROLLMENT_REVISION", "01")] [InlineData("ENROLLMENT_REVISION", "0")]
    [InlineData("ENROLLMENT_REVISION", "9007199254740992")]
    [InlineData("ENROLLMENT_ID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("EVIDENCE_SHA256", "private-value")]
    [InlineData("NOT_BEFORE_UTC", "2026-10-05T12:00:00.0000000+01:00")]
    [InlineData("NOT_BEFORE_UTC", "2026-10-05T12:00:00")]
    [InlineData("EXPIRES_AT_UTC", "2026-10-05T12:00:00.0000000Z")]
    [InlineData("SERVER_NAME", null)] [InlineData("MACHINE_NAME", "private\nvalue")]
    [InlineData("INSTANCE_NAME_JSON", null)] [InlineData("INSTANCE_NAME_JSON", "")]
    [InlineData("INSTANCE_NAME_JSON", "null")] [InlineData("INSTANCE_NAME_JSON", "{}")]
    [InlineData("INSTANCE_NAME_JSON", "\"\" \"private-value\"")]
    [InlineData("INSTANCE_NAME_JSON", "\"private\\nvalue\"")]
    public void InvalidInputsFailClosedWithoutEchoOrFallback(string name, string? value)
    {
        var input = Valid(); input[SqlAgentProductionModuleQualificationEnrollment.Prefix + name] = value;
        var failure = Assert.Throws<InvalidOperationException>(() => SqlAgentProductionModuleQualificationEnrollment.Load(input.GetValueOrDefault, false));
        Assert.DoesNotContain("private", failure.Message.Replace("private values withheld", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData(false, "1")] [InlineData(true, null)] [InlineData(true, "0")]
    public void SignedAndUnsignedPhasesCannotBeConfused(bool signatureRemoved, string? phase)
    {
        var input = Valid(); input["SQLOBSERVER_AGENT_MODULE_UNSIGNED_QUALIFICATION"] = phase;
        Assert.Throws<InvalidOperationException>(() => SqlAgentProductionModuleQualificationEnrollment.Load(input.GetValueOrDefault, signatureRemoved));
    }

    private static Dictionary<string, string?> Valid() => new(StringComparer.Ordinal)
    {
        ["SQLOBSERVER_CPU_QUALIFICATION"] = "1", ["SQLOBSERVER_CPU_HOST"] = "lab", ["SQLOBSERVER_CPU_PORT"] = "1433",
        ["SQLOBSERVER_CPU_CERTIFICATE_HOST"] = "lab.example.test",
        ["SQLOBSERVER_CPU_TARGET_ID"] = "11111111-1111-4111-8111-111111111111", ["SQLOBSERVER_CPU_TARGET_REVISION"] = "1",
        ["SQLOBSERVER_CPU_HOST_BINDING_REVISION"] = "1", ["SQLOBSERVER_CPU_HOST_PROFILE_REVISION"] = "1",
        ["SQLOBSERVER_CPU_EXPECTED_HOST_FINGERPRINT"] = new string('b', 64), ["SQLOBSERVER_CPU_FINGERPRINT_KEY"] = new string('a', 64),
        ["SQLOBSERVER_CPU_EXPECTED_SQL_MAJOR"] = "17", ["SQLOBSERVER_CPU_EXPECTED_WINDOWS_SID"] = "S-1-5-21-123-456",
        ["SQLOBSERVER_AGENT_MODULE_QUALIFICATION"] = "1", ["SQLOBSERVER_AGENT_MODULE_SOURCE_VERSION"] = "2",
        ["SQLOBSERVER_AGENT_MODULE_DEFINITION_SHA256"] = DomainEnrollmentDefinition,
        ["SQLOBSERVER_AGENT_MODULE_METADATA_QUALIFICATION"] = "1", ["SQLOBSERVER_AGENT_MODULE_METADATA_OBJECT_ID"] = "123",
        ["SQLOBSERVER_AGENT_MODULE_METADATA_CERTIFICATE_ID"] = "456", ["SQLOBSERVER_AGENT_MODULE_METADATA_PUBLIC_CERTIFICATE_SHA256"] = new string('a', 64),
        ["SQLOBSERVER_AGENT_MODULE_METADATA_THUMBPRINT_HEX"] = new string('b', 40),
        [SqlAgentProductionModuleQualificationEnrollment.Prefix + "QUALIFICATION"] = "1",
        [SqlAgentProductionModuleQualificationEnrollment.Prefix + "ENROLLMENT_REVISION"] = "1",
        [SqlAgentProductionModuleQualificationEnrollment.Prefix + "ENROLLMENT_ID"] = "22222222-2222-4222-8222-222222222222",
        [SqlAgentProductionModuleQualificationEnrollment.Prefix + "EVIDENCE_SHA256"] = new string('c', 64),
        [SqlAgentProductionModuleQualificationEnrollment.Prefix + "NOT_BEFORE_UTC"] = "2026-10-05T12:00:00.0000000Z",
        [SqlAgentProductionModuleQualificationEnrollment.Prefix + "EXPIRES_AT_UTC"] = "2026-10-05T13:00:00.0000000+00:00",
        [SqlAgentProductionModuleQualificationEnrollment.Prefix + "SERVER_NAME"] = "expected-server",
        [SqlAgentProductionModuleQualificationEnrollment.Prefix + "MACHINE_NAME"] = "expected-machine",
        [SqlAgentProductionModuleQualificationEnrollment.Prefix + "INSTANCE_NAME_JSON"] = "\"\"",
    };
    private const string DomainEnrollmentDefinition = SqlAgentModuleBinding.ApprovedDefinitionSha256;
}
