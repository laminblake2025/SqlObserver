using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Deployment;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.PerformanceTests;

public sealed class M12DeploymentSecurityBoundsTests
{
    [Fact]
    public async Task FixedAssessmentAndOpaqueConfigurationRemainBounded()
    {
        var observations = DeploymentSecurityCheckCatalog.OrderedIds
            .Skip(4)
            .Select(id => new DeploymentSecurityObservation(id, DeploymentSecurityObservationDisposition.Accepted, "accepted"))
            .ToArray();
        var service = new DeploymentSecurityAssessmentService(() => DateTimeOffset.UnixEpoch);
        for (int i = 0; i < 100; i++)
        {
            DeploymentSecurityAssessment assessment = await service.AssessAsync(new DeploymentSecurityAssessmentRequest(observations), CancellationToken.None);
            Assert.Equal(12, assessment.Checks.Count);
        }

        PostgreSqlDeploymentConfigurationFacts facts = PostgreSqlDeploymentConfigurationInspector.Inspect(new string('x', PostgreSqlDeploymentConfigurationInspector.MaximumConfigurationLength + 1));
        Assert.False(facts.IsValid);
        Assert.False(facts.HasInlineSecret);
    }

    [Fact]
    public async Task CanceledObservationPortIsNotConvertedToAResult()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new DeploymentSecurityAssessmentService(new CanceledPort());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await service.AssessAsync(new DeploymentSecurityAssessmentRequest(), cancellation.Token));
    }

    private sealed class CanceledPort : IDeploymentSecurityObservationPort
    {
        public ValueTask<IReadOnlyList<DeploymentSecurityObservation>> ObserveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<IReadOnlyList<DeploymentSecurityObservation>>(cancellationToken);
    }
}
