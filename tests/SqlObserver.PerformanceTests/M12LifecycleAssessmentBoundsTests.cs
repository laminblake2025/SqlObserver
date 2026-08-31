using SqlObserver.Application.Ports;
using SqlObserver.Domain.Deployment;

namespace SqlObserver.PerformanceTests;

public sealed class M12LifecycleAssessmentBoundsTests
{
    [Fact]
    public void AssessmentCheckAndHistoryBoundsRemainFinite()
    {
        Assert.Equal(256, MigrationAssessmentRequest.MaximumHistory);
        Assert.Equal(256, DeploymentLifecycleAssessment.MaximumChecks);
        var checks = Enumerable.Range(0, 256)
            .Select(i => new LifecycleAssessmentCheck($"check-{i}", LifecycleCheckStatus.NotEvaluated, "not_evaluated"))
            .ToArray();
        var product = new ProductIdentityBinding("sqlobserver", "1.0.0", new string('a', 40), new string('b', 64), 1, "run", "local");
        var result = new DeploymentLifecycleAssessment(DeploymentLifecycleAction.Install, product, checks, DateTimeOffset.UtcNow);
        Assert.Equal(256, result.Checks.Count);
        Assert.False(result.ReadyToMutate);
    }
}
