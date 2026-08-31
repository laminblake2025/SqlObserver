using System.Text.Json;
using SqlObserver.Domain.Deployment;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.SecurityTests;

public sealed class M12DeploymentSecuritySecurityTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AssessmentWirePayloadContainsNoRawSecurityMaterial()
    {
        var checks = DeploymentSecurityCheckCatalog.OrderedIds
            .Select(id => new DeploymentSecurityAssessmentCheck(id, DeploymentSecurityCheckStatus.Blocked, "observation_missing"))
            .ToArray();
        string json = JsonSerializer.Serialize(new DeploymentSecurityAssessment(checks, DateTimeOffset.UnixEpoch), Options);
        foreach (string forbidden in new[] { "Password=", "Host=", "BEGIN CERTIFICATE", "Exception:", "connectionString" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InlineSecretAndTrustBypassAreNeverAccepted()
    {
        Assert.Equal(DeploymentSecurityObservationDisposition.Unsafe, PostgreSqlDeploymentConfigurationInspector.CredentialObservation("Host=db;Password=secret;Ssl Mode=VerifyFull").Disposition);
        Assert.Equal(DeploymentSecurityObservationDisposition.Unsafe, PostgreSqlDeploymentConfigurationInspector.TransportObservation("Host=db;Ssl Mode=VerifyFull;Trust Server Certificate=true").Disposition);
    }
}
