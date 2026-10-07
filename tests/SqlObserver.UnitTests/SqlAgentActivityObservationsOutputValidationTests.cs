using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentActivityObservationsOutputValidationTests
{
    [Theory]
    [InlineData("source")] [InlineData("items")] [InlineData("response")] [InlineData("output")]
    public void OutputAccountingMustMatchExactBoundedCapture(string mutation)
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        var value = SqlAgentActivityObservationsTestData.Capture(request);
        var payload = new CollectorPayload(sqlAgentModuleCapture: value);
        var accounting = new CollectorRunAccounting(mutation == "source" ? 2 : 1, mutation == "items" ? 0 : 1,
            SqlAgentActivityObservationsOutputValidation.AccountedResponseBytes(value) + (mutation == "response" ? 1 : 0),
            payload.EstimatedSizeBytes + (mutation == "output" ? 1 : 0));
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityObservationsOutputValidation.Validate(payload,
            request.TargetId, request.TargetRevision, request.RunId, CollectorRunOutcome.Succeeded, accounting, CollectorLossEvidence.None));
    }

    [Fact]
    public void MixedMetricsAreRejectedInsteadOfBeingHiddenInsideOneObservation()
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        var value = SqlAgentActivityObservationsTestData.Capture(request);
        var payload = new CollectorPayload([M4TestData.CreateMetric()], sqlAgentModuleCapture: value);
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityObservationsOutputValidation.Validate(payload,
            request.TargetId, request.TargetRevision, request.RunId, CollectorRunOutcome.Succeeded,
            new(1, 2, SqlAgentActivityObservationsOutputValidation.AccountedResponseBytes(value), payload.EstimatedSizeBytes), CollectorLossEvidence.None));
    }

    [Theory]
    [InlineData(false, CollectorRunOutcome.Partial)] [InlineData(true, CollectorRunOutcome.Succeeded)]
    [InlineData(true, CollectorRunOutcome.TimedOut)]
    public void TruncationAndOutcomeCannotClaimContradictoryCoverage(bool truncated, CollectorRunOutcome outcome)
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        var value = SqlAgentActivityObservationsTestData.Capture(request, truncated ? 513 : 1);
        var payload = new CollectorPayload(sqlAgentModuleCapture: value);
        var loss = truncated ? new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false) : CollectorLossEvidence.None;
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityObservationsOutputValidation.Validate(payload,
            request.TargetId, request.TargetRevision, request.RunId, outcome,
            new(value.Capture.SourceRowsRead, 1, SqlAgentActivityObservationsOutputValidation.AccountedResponseBytes(value), payload.EstimatedSizeBytes), loss));
    }

    [Theory]
    [InlineData("none")] [InlineData("exact")] [InlineData("count")] [InlineData("bytes")]
    public void SentinelIsOnlyAMinimumOneRowLossNotAnExactTotal(string mutation)
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        var value = SqlAgentActivityObservationsTestData.Capture(request, 513);
        var payload = new CollectorPayload(sqlAgentModuleCapture: value);
        var loss = mutation == "none" ? CollectorLossEvidence.None : new(CollectorLossKind.SourceRowLimit,
            mutation == "count" ? 2 : 1, mutation == "exact", mutation == "bytes" ? 1 : 0);
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityObservationsOutputValidation.Validate(payload,
            request.TargetId, request.TargetRevision, request.RunId, CollectorRunOutcome.Partial,
            new(513, 1, SqlAgentActivityObservationsOutputValidation.AccountedResponseBytes(value), payload.EstimatedSizeBytes), loss));
    }
}
