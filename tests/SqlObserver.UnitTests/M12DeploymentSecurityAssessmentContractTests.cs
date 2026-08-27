using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Deployment;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.UnitTests;

public sealed class M12DeploymentSecurityAssessmentContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly string[] AssessmentProperties = ["evaluatedAtUtc", "checks", "status", "readyToActivate"];

    [Fact]
    public async Task AssessmentHasExactOrderAndCanNeverBecomeReady()
    {
        var service = new DeploymentSecurityAssessmentService(() => new DateTimeOffset(2026, 8, 27, 1, 2, 3, TimeSpan.Zero));
        var result = await service.AssessAsync(new DeploymentSecurityAssessmentRequest([
            new("postgresql_transport", DeploymentSecurityObservationDisposition.Accepted, "verify_full"),
            new("mcp_endpoint", DeploymentSecurityObservationDisposition.Accepted, "https_endpoint"),
            new("target_connection_policy", DeploymentSecurityObservationDisposition.Accepted, "validated_policy"),
            new("credential_policy", DeploymentSecurityObservationDisposition.Unsafe, "inline_secret"),
            new("sensitive_content", DeploymentSecurityObservationDisposition.Accepted, "sensitive_enable_unsafe"),
            new("configuration_integrity", DeploymentSecurityObservationDisposition.Accepted, "integrity_verified"),
            new("runtime_permissions", DeploymentSecurityObservationDisposition.Accepted, "least_privilege"),
            new("release_evidence", DeploymentSecurityObservationDisposition.Accepted, "local_only")]), CancellationToken.None);

        Assert.Equal(DeploymentSecurityCheckCatalog.OrderedIds, result.Checks.Select(static x => x.CheckId));
        Assert.Equal(DeploymentSecurityAssessmentStatus.NotReady, result.Status);
        Assert.False(result.ReadyToActivate);
        Assert.All(result.Checks.Take(4), static check => Assert.Equal(DeploymentSecurityCheckStatus.Blocked, check.Status));
        Assert.Equal(DeploymentSecurityCheckStatus.Failed, result.Checks[7].Status);
        Assert.Equal(DeploymentSecurityCheckStatus.Failed, result.Checks[8].Status);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(result, Options));
        Assert.Equal("not_ready", json.RootElement.GetProperty("status").GetString());
        Assert.False(json.RootElement.GetProperty("readyToActivate").GetBoolean());
        Assert.EndsWith(".0000000Z", json.RootElement.GetProperty("evaluatedAtUtc").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Host=db;Ssl Mode=VerifyFull", "verify_full", true)]
    [InlineData("Host=db;Ssl Mode=Require", "transport_not_validated", false)]
    [InlineData("Host=db;Ssl Mode=VerifyCA", "transport_not_validated", false)]
    [InlineData("Host=db;Ssl Mode=Disable", "transport_not_validated", false)]
    [InlineData("Host=db", "ssl_mode_unspecified", false)]
    public void PostgreSqlInspectorReturnsOnlySafeTransportFacts(string config, string code, bool accepted)
    {
        var observation = PostgreSqlDeploymentConfigurationInspector.TransportObservation(config);
        Assert.Equal(code, observation.Code);
        Assert.Equal(accepted ? DeploymentSecurityObservationDisposition.Accepted : code == "ssl_mode_unspecified" ? DeploymentSecurityObservationDisposition.Missing : DeploymentSecurityObservationDisposition.Unsafe, observation.Disposition);
    }

    [Fact]
    public void PostgreSqlInspectorRejectsDuplicateMalformedAndOversizedConfigurations()
    {
        Assert.False(PostgreSqlDeploymentConfigurationInspector.Inspect("Host=db;Host=db;Ssl Mode=VerifyFull").IsValid);
        Assert.False(PostgreSqlDeploymentConfigurationInspector.Inspect("Host=db;Ssl Mode").IsValid);
        Assert.False(PostgreSqlDeploymentConfigurationInspector.Inspect(new string('x', PostgreSqlDeploymentConfigurationInspector.MaximumConfigurationLength + 1)).IsValid);
        Assert.True(PostgreSqlDeploymentConfigurationInspector.Inspect("Host=db;Password=secret;Ssl Mode=VerifyFull").HasInlineSecret);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(" TRUE ", true)]
    [InlineData("False", false)]
    [InlineData("maybe", null)]
    [InlineData("1", null)]
    [InlineData("0", null)]
    public void TrustServerCertificateUsesNpgsqlBooleanSemantics(string value, bool? expected)
    {
        var facts = PostgreSqlDeploymentConfigurationInspector.Inspect("Host=db;Ssl Mode=VerifyFull;Trust Server Certificate=" + value);
        Assert.Equal(expected is null ? !facts.IsValid : expected.Value, expected is null ? !facts.IsValid : facts.TrustServerCertificate);
        Assert.Equal(expected is null ? DeploymentSecurityObservationDisposition.Unsafe : expected.Value ? DeploymentSecurityObservationDisposition.Unsafe : DeploymentSecurityObservationDisposition.Accepted,
            expected is null || expected.Value ? PostgreSqlDeploymentConfigurationInspector.TransportObservation("Host=db;Ssl Mode=VerifyFull;Trust Server Certificate=" + value).Disposition : PostgreSqlDeploymentConfigurationInspector.TransportObservation("Host=db;Ssl Mode=VerifyFull;Trust Server Certificate=" + value).Disposition);
    }

    [Fact]
    public void SecurityTokensRequireLowercaseLeadingLetters()
    {
        Assert.Throws<ArgumentException>(() => new DeploymentSecurityAssessmentCheck("1check", DeploymentSecurityCheckStatus.Passed, "safe"));
        Assert.Throws<ArgumentException>(() => new DeploymentSecurityAssessmentCheck("check", DeploymentSecurityCheckStatus.Passed, "-unsafe"));
        Assert.Throws<ArgumentException>(() => new DeploymentSecurityObservation("_check", DeploymentSecurityObservationDisposition.Accepted, "safe"));
        Assert.Throws<ArgumentException>(() => new DeploymentSecurityObservation("check", DeploymentSecurityObservationDisposition.Accepted, "_unsafe"));
    }

    [Theory]
    [InlineData("Username", false)]
    [InlineData("User ID", false)]
    [InlineData("UID", false)]
    [InlineData("Password", true)]
    [InlineData("Pwd", true)]
    [InlineData("Passfile", false)]
    [InlineData("SSL Password", true)]
    [InlineData("Kerberos Service Name", false)]
    public void CredentialAliasesProduceBooleanFactsOnly(string keyword, bool inlineSecret)
    {
        string config = "Host=db;Ssl Mode=VerifyFull;" + keyword + "=value-without-serialization";
        PostgreSqlDeploymentConfigurationFacts facts = PostgreSqlDeploymentConfigurationInspector.Inspect(config);
        Assert.True(facts.HasCredentialKeywords);
        Assert.Equal(inlineSecret, facts.HasInlineSecret);
        Assert.DoesNotContain("value-without-serialization", JsonSerializer.Serialize(facts, Options), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultWirePayloadMatchesClosedAssessmentShape()
    {
        var checks = DeploymentSecurityCheckCatalog.OrderedIds
            .Select(id => new DeploymentSecurityAssessmentCheck(id, DeploymentSecurityCheckStatus.Blocked, "observation_missing"))
            .ToArray();
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new DeploymentSecurityAssessment(checks, DateTimeOffset.UnixEpoch), Options));
        Assert.Equal(AssessmentProperties, document.RootElement.EnumerateObject().Select(static p => p.Name));
        Assert.Equal("not_ready", document.RootElement.GetProperty("status").GetString());
        Assert.False(document.RootElement.GetProperty("readyToActivate").GetBoolean());
        Assert.Equal(12, document.RootElement.GetProperty("checks").GetArrayLength());
        Assert.DoesNotContain("detail", document.RootElement.GetProperty("checks")[0].EnumerateObject().Select(static p => p.Name));
    }

    [Theory]
    [InlineData("inline-secret", true)]
    [InlineData("inline_secret", true)]
    [InlineData("sensitive-content-enable", true)]
    [InlineData("sensitive_content_enable", true)]
    [InlineData("trust-server", true)]
    [InlineData("trust_server", true)]
    [InlineData("clear-text", true)]
    [InlineData("clear_text", true)]
    [InlineData("anonymous-auth", true)]
    [InlineData("anonymous_auth", true)]
    [InlineData("validation-bypass", true)]
    [InlineData("validation_bypass", true)]
    [InlineData("inline-secret-value", false)]
    [InlineData("safe-inline-secret", false)]
    [InlineData("trusted-server", false)]
    public async Task UnsafeCodeClassificationIsExactAcrossSeparators(string code, bool unsafeCode)
    {
        var service = new DeploymentSecurityAssessmentService(() => DateTimeOffset.UnixEpoch);
        DeploymentSecurityAssessment result = await service.AssessAsync(new DeploymentSecurityAssessmentRequest([
            new("mcp_endpoint", DeploymentSecurityObservationDisposition.Accepted, code)]), CancellationToken.None);
        Assert.Equal(unsafeCode ? DeploymentSecurityCheckStatus.Failed : DeploymentSecurityCheckStatus.Passed, result.Checks[5].Status);
    }

    [Theory]
    [MemberData(nameof(MaliciousObservationSets))]
    public async Task MaliciousObservationPortsFailClosedWithoutRawEnumerationErrors(IReadOnlyList<DeploymentSecurityObservation> observations)
    {
        var service = new DeploymentSecurityAssessmentService(new FixedObservationPort(observations), () => DateTimeOffset.UnixEpoch);
        DeploymentSecurityAssessment result = await service.AssessAsync(new DeploymentSecurityAssessmentRequest(), CancellationToken.None);
        Assert.Equal(DeploymentSecurityCheckStatus.Failed, result.Checks[5].Status);
        Assert.Equal("observation_failed", result.Checks[5].Code);
        Assert.Equal(DeploymentSecurityAssessmentStatus.NotReady, result.Status);
    }

    [Fact]
    public void RequestConstructorSinglePassesBoundedUntrustedCollectionsAndHidesErrors()
    {
        DeploymentSecurityAssessmentRequest changing = new(new ChangingCollection());
        Assert.Single(changing.Observations);

        IReadOnlyList<DeploymentSecurityObservation>[] hostile =
        [
            new List<DeploymentSecurityObservation>(Enumerable.Repeat(new DeploymentSecurityObservation("mcp_endpoint", DeploymentSecurityObservationDisposition.Accepted, "safe"), 13)),
            new InfiniteObservationList(),
            new NullObservationList(),
            new UnboundedObservationList(),
            new EnumeratorThrowingObservationList(),
        ];
        foreach (IReadOnlyList<DeploymentSecurityObservation> source in hostile)
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() => new DeploymentSecurityAssessmentRequest(source));
            Assert.DoesNotContain("must not", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("provider", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("enumerator", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static IEnumerable<object[]> MaliciousObservationSets()
    {
        yield return [new List<DeploymentSecurityObservation>(Enumerable.Repeat(new DeploymentSecurityObservation("mcp_endpoint", DeploymentSecurityObservationDisposition.Accepted, "safe"), 13))];
        yield return [new List<DeploymentSecurityObservation> { new("mcp_endpoint", DeploymentSecurityObservationDisposition.Accepted, "safe"), new("mcp_endpoint", DeploymentSecurityObservationDisposition.Accepted, "safe") }];
        yield return [new List<DeploymentSecurityObservation> { new("unknown_check", DeploymentSecurityObservationDisposition.Accepted, "safe") }];
        yield return [new List<DeploymentSecurityObservation?> { null }];
        yield return [new UnboundedObservationList()];
        yield return [new EnumeratorThrowingObservationList()];
    }

    private sealed class FixedObservationPort(IReadOnlyList<DeploymentSecurityObservation> observations) : IDeploymentSecurityObservationPort
    {
        public ValueTask<IReadOnlyList<DeploymentSecurityObservation>> ObserveAsync(CancellationToken cancellationToken) => ValueTask.FromResult(observations);
    }

    private sealed class UnboundedObservationList : IReadOnlyList<DeploymentSecurityObservation>
    {
        public int Count => int.MaxValue;
        public DeploymentSecurityObservation this[int index] => throw new InvalidOperationException("must not enumerate");
        public IEnumerator<DeploymentSecurityObservation> GetEnumerator() => throw new InvalidOperationException("must not enumerate");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task ChangingCountAndIndexerAreNeverReadFromPortCollection()
    {
        var service = new DeploymentSecurityAssessmentService(new FixedObservationPort(new ChangingCollection()), () => DateTimeOffset.UnixEpoch);
        DeploymentSecurityAssessment result = await service.AssessAsync(new DeploymentSecurityAssessmentRequest(), CancellationToken.None);
        Assert.Equal(DeploymentSecurityCheckStatus.Passed, result.Checks[5].Status);
    }

    private sealed class ChangingCollection : IReadOnlyList<DeploymentSecurityObservation>
    {
        public int Count => throw new InvalidOperationException("Count must not be read");
        public DeploymentSecurityObservation this[int index] => throw new InvalidOperationException("Indexer must not be read");
        public IEnumerator<DeploymentSecurityObservation> GetEnumerator() => new[]
        {
            new DeploymentSecurityObservation("mcp_endpoint", DeploymentSecurityObservationDisposition.Accepted, "safe")
        }.AsEnumerable().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EnumeratorThrowingObservationList : IReadOnlyList<DeploymentSecurityObservation>
    {
        public int Count => int.MaxValue;
        public DeploymentSecurityObservation this[int index] => throw new InvalidOperationException("Indexer must not be read");
        public IEnumerator<DeploymentSecurityObservation> GetEnumerator() => throw new InvalidOperationException("enumerator failure");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class NullObservationList : IReadOnlyList<DeploymentSecurityObservation>
    {
        public int Count => 1;
        public DeploymentSecurityObservation this[int index] => null!;
        public IEnumerator<DeploymentSecurityObservation> GetEnumerator() => Enumerate().GetEnumerator();
        private static IEnumerable<DeploymentSecurityObservation> Enumerate()
        {
            yield return null!;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class InfiniteObservationList : IReadOnlyList<DeploymentSecurityObservation>
    {
        public int Count => int.MaxValue;
        public DeploymentSecurityObservation this[int index] => throw new InvalidOperationException("Indexer must not be read");
        public IEnumerator<DeploymentSecurityObservation> GetEnumerator() => Enumerate().GetEnumerator();
        private static IEnumerable<DeploymentSecurityObservation> Enumerate()
        {
            while (true)
                yield return new DeploymentSecurityObservation("mcp_endpoint", DeploymentSecurityObservationDisposition.Accepted, "safe");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
