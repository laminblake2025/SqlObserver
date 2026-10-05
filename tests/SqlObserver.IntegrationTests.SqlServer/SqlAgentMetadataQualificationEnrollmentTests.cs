using System.Globalization;

namespace SqlObserver.IntegrationTests.SqlServer;

internal static class SqlAgentMetadataQualificationEnrollment
{
    internal const string Prefix = "SQLOBSERVER_AGENT_MODULE_METADATA_";

    internal static SqlAgentModuleMetadataEnrollment Load(Func<string, string?> read, bool signatureAbsent)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read(Prefix + "QUALIFICATION") != "1") throw Invalid();
        _ = SqlAgentModuleEnrollment.Load(read);
        string? phase = read("SQLOBSERVER_AGENT_MODULE_UNSIGNED_QUALIFICATION");
        if (signatureAbsent ? phase != "1" : phase is not null) throw Invalid();
        return new(Positive("OBJECT_ID"), Positive("CERTIFICATE_ID"), SqlAgentModuleEnrollment.ReviewedDefinitionHash,
            Hex("PUBLIC_CERTIFICATE_SHA256", 64), Hex("THUMBPRINT_HEX", 40));

        int Positive(string field)
        {
            string? value = read(Prefix + field);
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0 ||
                number.ToString(CultureInfo.InvariantCulture) != value) throw Invalid();
            return number;
        }
        string Hex(string field, int length)
        {
            string? value = read(Prefix + field);
            if (value is null || value.Length != length || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                value.All(c => c == '0')) throw Invalid();
            return value;
        }
    }
    private static InvalidOperationException Invalid() => new("Metadata trial enrollment is absent or invalid; configured values withheld.");
}

public sealed class SqlAgentMetadataQualificationEnrollmentTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")] [InlineData(" 1")]
    public void ExplicitMetadataOptInRequiredBeforeReadingOtherInputs(string? value)
    {
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => SqlAgentMetadataQualificationEnrollment.Load(name =>
        { calls++; Assert.Equal(SqlAgentMetadataQualificationEnrollment.Prefix + "QUALIFICATION", name); return value; }, false));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("OBJECT_ID", "0")] [InlineData("OBJECT_ID", "01")] [InlineData("OBJECT_ID", "2147483648")]
    [InlineData("CERTIFICATE_ID", "-1")] [InlineData("CERTIFICATE_ID", null)]
    [InlineData("PUBLIC_CERTIFICATE_SHA256", "private-value")]
    [InlineData("THUMBPRINT_HEX", "private-value")]
    public void MalformedTrustedIdentitiesFailWithoutEcho(string field, string? value)
    {
        var inputs = Valid(); inputs[SqlAgentMetadataQualificationEnrollment.Prefix + field] = value;
        var error = Assert.Throws<InvalidOperationException>(() => SqlAgentMetadataQualificationEnrollment.Load(inputs.GetValueOrDefault, false));
        Assert.DoesNotContain("private-value", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "1")] [InlineData(false, "0")] [InlineData(true, null)] [InlineData(true, "0")]
    public void SignedAndUnsignedSelectionsRequireExactPhase(bool signatureAbsent, string? phase)
    {
        var inputs = Valid(); inputs["SQLOBSERVER_AGENT_MODULE_UNSIGNED_QUALIFICATION"] = phase;
        Assert.Throws<InvalidOperationException>(() => SqlAgentMetadataQualificationEnrollment.Load(inputs.GetValueOrDefault, signatureAbsent));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExplicitEnrollmentPreservesIndependentExpectedMetadata(bool signatureAbsent)
    {
        var inputs = Valid();
        if (signatureAbsent) inputs["SQLOBSERVER_AGENT_MODULE_UNSIGNED_QUALIFICATION"] = "1";
        var expected = SqlAgentMetadataQualificationEnrollment.Load(inputs.GetValueOrDefault, signatureAbsent);
        Assert.Equal(new SqlAgentModuleMetadataEnrollment(123, 456, SqlAgentModuleEnrollment.ReviewedDefinitionHash,
            new string('a', 64), new string('b', 40)), expected);
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        [SqlAgentMetadataQualificationEnrollment.Prefix + "QUALIFICATION"] = "1",
        [SqlAgentMetadataQualificationEnrollment.Prefix + "OBJECT_ID"] = "123",
        [SqlAgentMetadataQualificationEnrollment.Prefix + "CERTIFICATE_ID"] = "456",
        [SqlAgentMetadataQualificationEnrollment.Prefix + "PUBLIC_CERTIFICATE_SHA256"] = new string('a', 64),
        [SqlAgentMetadataQualificationEnrollment.Prefix + "THUMBPRINT_HEX"] = new string('b', 40),
        [SqlAgentModuleEnrollment.Prefix + "QUALIFICATION"] = "1",
        [SqlAgentModuleEnrollment.Prefix + "SOURCE_VERSION"] = "2",
        [SqlAgentModuleEnrollment.Prefix + "DEFINITION_SHA256"] = SqlAgentModuleEnrollment.ReviewedDefinitionHash,
    };
}
