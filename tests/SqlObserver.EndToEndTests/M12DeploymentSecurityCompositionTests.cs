using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Deployment;
using SqlObserver.Domain.Targets;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.EndToEndTests;

public sealed class M12DeploymentSecurityCompositionTests
{
    [Fact]
    public async Task SafeExistingTargetMcpAndSensitiveFactsDoNotAuthorizeActivation()
    {
        var service = new DeploymentSecurityAssessmentService(() => DateTimeOffset.UnixEpoch);
        var result = await service.AssessAsync(new DeploymentSecurityAssessmentRequest([
            PostgreSqlDeploymentConfigurationInspector.TransportObservation("Host=db;Ssl Mode=VerifyFull"),
            DeploymentSecurityObservationFactory.ObserveMcpEndpoint("https://observer.example/mcp"),
            DeploymentSecurityObservationFactory.ObserveTargetConnectionPolicy(new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(1)))),
            DeploymentSecurityObservationFactory.ObserveSensitiveContent(false)]), CancellationToken.None);

        Assert.Equal(DeploymentSecurityAssessmentStatus.NotReady, result.Status);
        Assert.False(result.ReadyToActivate);
        Assert.Equal(DeploymentSecurityCheckStatus.Passed, result.Checks[5].Status);
        Assert.Equal(DeploymentSecurityCheckStatus.Passed, result.Checks[6].Status);
        Assert.Equal(DeploymentSecurityCheckStatus.Blocked, result.Checks[7].Status);
        Assert.Equal(DeploymentSecurityCheckStatus.Passed, result.Checks[8].Status);
        Assert.All(result.Checks.Take(4), static check => Assert.Equal(DeploymentSecurityCheckStatus.Blocked, check.Status));
    }
}
