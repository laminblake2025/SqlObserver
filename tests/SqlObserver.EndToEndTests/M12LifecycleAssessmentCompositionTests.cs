using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Deployment;

namespace SqlObserver.EndToEndTests;

public sealed class M12LifecycleAssessmentCompositionTests
{
    [Fact]
    public async Task CompositionUsesAssessmentOnlyPortAndNeverAuthorizesMutation()
    {
        var product = new ProductIdentityBinding("sqlobserver", "1.0.0", new string('a', 40), new string('b', 64), 1, "run", "local");
        var service = new LifecycleAssessmentService(new StubPort());
        DeploymentLifecycleAssessment assessment = await service.AssessAsync(
            new LifecycleAssessmentRequest(DeploymentLifecycleAction.Install, product, product, new MigrationAssessmentRequest(1, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)))),
            CancellationToken.None);
        Assert.False(assessment.ReadyToMutate);
        Assert.Equal(LifecycleAssessmentStatus.NotReady, assessment.Status);
    }

    private sealed class StubPort : IMigrationAssessmentPort
    {
        public ValueTask<MigrationAssessmentResult> AssessAsync(MigrationAssessmentRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MigrationAssessmentResult(MigrationAssessmentStatus.Current, [], DateTimeOffset.UtcNow, "current"));
    }
}
