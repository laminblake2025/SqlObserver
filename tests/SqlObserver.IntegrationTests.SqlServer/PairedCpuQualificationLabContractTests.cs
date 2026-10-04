namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Pure prerequisite validation. These cases never read live settings,
/// discover a server, mutate environment variables or start native helpers.</summary>
public sealed class PairedCpuQualificationLabContractTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")] [InlineData(" 1")]
    public void SelectedDisabledQualificationFailsBeforeReadingAnyPrivateInput(string? optIn)
    {
        int reads = 0;
        var failure = Assert.Throws<InvalidOperationException>(() => PairedCpuQualificationLabContract.Load(name =>
        { reads++; Assert.Equal("SQLOBSERVER_CPU_QUALIFICATION", name); return optIn; }));
        Assert.Equal(1, reads); Assert.Contains("disabled", failure.Message, StringComparison.Ordinal);
        Assert.Contains("never skip", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidIndependentConfigurationBuildsExactProductionPoliciesAndRevisions()
    {
        Dictionary<string, string?> settings = Settings();
        var contract = Load(settings);
        Assert.Equal("sql-node.example.test", contract.Policy.Endpoint.HostName.Value);
        Assert.Equal(15433, contract.Policy.Endpoint.TcpPort); Assert.Null(contract.Policy.Endpoint.InstanceName);
        Assert.Equal("sql-cert.example.test", contract.Policy.CertificateHostName!.Value);
        Assert.Equal(TimeSpan.FromSeconds(5), contract.Policy.ConnectTimeout.Value);
        Assert.Equal(7, contract.TargetRevision.Value); Assert.Equal(11, contract.Host.Binding.Revision.Value);
        Assert.Equal(contract.Host.Binding.Revision, contract.Host.Profile.ProfileRevision);
        Assert.Equal(contract.TargetId, contract.Host.TargetId); Assert.Equal(contract.TargetRevision, contract.Host.TargetRevision);
        Assert.Equal(settings["EXPECTED_HOST_FINGERPRINT"], contract.Host.Binding.HostFingerprint.Value);
        Assert.Equal(16, contract.ExpectedSqlMajor);
        Assert.Equal(TimeSpan.FromSeconds(10), contract.DiscoveryRequest().Timeout.Value);
        Assert.DoesNotContain(settings["EXPECTED_WINDOWS_SID"]!, contract.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(settings["FINGERPRINT_KEY"]!, contract.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HOST")] [InlineData("PORT")] [InlineData("CERTIFICATE_HOST")] [InlineData("TARGET_ID")]
    [InlineData("TARGET_REVISION")] [InlineData("HOST_BINDING_REVISION")] [InlineData("HOST_PROFILE_REVISION")]
    [InlineData("EXPECTED_HOST_FINGERPRINT")] [InlineData("FINGERPRINT_KEY")] [InlineData("EXPECTED_SQL_MAJOR")]
    [InlineData("EXPECTED_WINDOWS_SID")]
    public void MissingIndependentPrerequisiteCannotBecomeALocalFallback(string name)
    {
        Dictionary<string, string?> settings = Settings(); settings.Remove(name);
        var failure = Assert.Throws<InvalidOperationException>(() => Load(settings));
        Assert.Contains(PairedCpuQualificationLabContract.Prefix + name, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HOST", "tcp:sql-node")] [InlineData("HOST", "sql-node,1433")]
    [InlineData("HOST", "sql-node\\INSTANCE")] [InlineData("HOST", "sql-node;Password=synthetic_private")]
    [InlineData("HOST", " sql-node")] [InlineData("HOST", "sql-node\n")]
    [InlineData("CERTIFICATE_HOST", "certificate.pfx;TrustServerCertificate=true")]
    [InlineData("CERTIFICATE_HOST", "*.example.test")] [InlineData("CERTIFICATE_HOST", "")]
    [InlineData("PORT", "0")] [InlineData("PORT", "65536")] [InlineData("PORT", "01433")]
    [InlineData("TARGET_ID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("TARGET_ID", "ABABABAB-ABAB-4BAB-8BAB-ABABABABABAB")]
    [InlineData("TARGET_REVISION", "0")] [InlineData("TARGET_REVISION", "-1")]
    [InlineData("TARGET_REVISION", "9223372036854775808")] [InlineData("TARGET_REVISION", "01")]
    [InlineData("HOST_BINDING_REVISION", "0")] [InlineData("HOST_PROFILE_REVISION", "12")]
    [InlineData("EXPECTED_SQL_MAJOR", "14")] [InlineData("EXPECTED_SQL_MAJOR", "18")]
    [InlineData("EXPECTED_HOST_FINGERPRINT", "not-an-independent-fingerprint")]
    [InlineData("FINGERPRINT_KEY", "synthetic_private_not_a_key")]
    [InlineData("EXPECTED_WINDOWS_SID", "LAB\\monitor")]
    [InlineData("EXPECTED_WINDOWS_SID", "S-1-5-")] [InlineData("EXPECTED_WINDOWS_SID", "S-1-5--1")]
    [InlineData("EXPECTED_WINDOWS_SID", "S-1-281474976710656-1")]
    [InlineData("EXPECTED_WINDOWS_SID", "S-1-5-4294967296")]
    [InlineData("EXPECTED_WINDOWS_SID", "S-1-5-01")]
    [InlineData("EXPECTED_WINDOWS_SID", "S-1-5-1-1-1-1-1-1-1-1-1-1-1-1-1-1-1-1")]
    public void UnsafeOrUnsupportedConfigurationFailsWithRedactedValue(string name, string value)
    {
        Dictionary<string, string?> settings = Settings(); settings[name] = value;
        var failure = Assert.Throws<InvalidOperationException>(() => Load(settings));
        Assert.Contains(PairedCpuQualificationLabContract.Prefix + name, failure.Message, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(value)) Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("FINGERPRINT_KEY")] [InlineData("EXPECTED_HOST_FINGERPRINT")]
    public void PlaceholderOrNoncanonicalHexCannotQualifyAHost(string name)
    {
        foreach (string value in new[] { new string('0', 64), new string('a', 63), new string('A', 64), new string('g', 64) })
        {
            Dictionary<string, string?> settings = Settings(); settings[name] = value;
            var failure = Assert.Throws<InvalidOperationException>(() => Load(settings));
            Assert.Contains(PairedCpuQualificationLabContract.Prefix + name, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(15)] [InlineData(16)] [InlineData(17)]
    public void SupportedExpectedVersionsRemainExplicit(int major)
    {
        Dictionary<string, string?> settings = Settings(); settings["EXPECTED_SQL_MAJOR"] = major.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(major, Load(settings).ExpectedSqlMajor);
    }

    private static PairedCpuQualificationLabContract Load(Dictionary<string, string?> values) =>
        PairedCpuQualificationLabContract.Load(name => name.StartsWith(PairedCpuQualificationLabContract.Prefix, StringComparison.Ordinal)
            ? values.GetValueOrDefault(name[PairedCpuQualificationLabContract.Prefix.Length..]) : throw new InvalidOperationException("Unexpected ambient setting lookup."));

    private static Dictionary<string, string?> Settings() => new(StringComparer.Ordinal)
    {
        ["QUALIFICATION"] = "1", ["HOST"] = "sql-node.example.test", ["PORT"] = "15433", ["CERTIFICATE_HOST"] = "sql-cert.example.test",
        ["TARGET_ID"] = "abababab-abab-4bab-8bab-abababababab", ["TARGET_REVISION"] = "7", ["HOST_BINDING_REVISION"] = "11", ["HOST_PROFILE_REVISION"] = "11",
        ["EXPECTED_HOST_FINGERPRINT"] = new string('b', 64), ["FINGERPRINT_KEY"] = new string('a', 64), ["EXPECTED_SQL_MAJOR"] = "16",
        ["EXPECTED_WINDOWS_SID"] = "S-1-5-21-100-200-300-1001",
    };
}
