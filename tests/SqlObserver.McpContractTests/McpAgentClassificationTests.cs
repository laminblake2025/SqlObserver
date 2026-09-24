using System.Text.Json;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;

namespace SqlObserver.McpContractTests;

public sealed class McpAgentClassificationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(0, 0, AgentFailureKind.Failed, true, true)]
    [InlineData(1, 0, AgentFailureKind.Failed, false, false)]
    [InlineData(1, 2, AgentFailureKind.Retry, false, false)]
    [InlineData(0, 3, AgentFailureKind.Cancelled, true, false)]
    [InlineData(1, 3, AgentFailureKind.Cancelled, false, false)]
    [InlineData(0, 1, AgentFailureKind.Failed, true, false)]
    [InlineData(0, 0, AgentFailureKind.Retry, true, false)]
    public void AgentRowsRetainEvidenceAndIdentifyOnlyFailedJobOutcomes(int step, int status, AgentFailureKind kind, bool jobOutcome, bool jobFailure)
    {
        var target = new MonitoredInstanceId(Guid.Parse("11111111-1111-4111-8111-111111111111"));
        var revision = new ObservationTargetRevision(1);
        DateTimeOffset observed = DateTimeOffset.UnixEpoch;
        var row = new SqlAgentFailureObservation(target, revision, Guid.Parse("22222222-2222-4222-8222-222222222222"),
            1, step, status, kind, null, null, 0, 1, observed, new string('a', 64));
        var snapshot = new SqlAgentFailureSnapshot(target, revision, null, observed,
            OperationalObservationState.Complete, [row], 1, false, observed.AddHours(-1), observed);
        var result = McpResults.Json(snapshot, Options, "get_job_failures", new Dictionary<string, JsonElement>(), null);
        Assert.False(result.IsError);
        JsonElement structured = result.StructuredContent!.Value;
        JsonElement item = Assert.Single(structured.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal(step, item.GetProperty("stepId").GetInt32());
        Assert.Equal(status, item.GetProperty("runStatus").GetInt32());
        Assert.Equal(jobOutcome, item.GetProperty("isJobOutcome").GetBoolean());
        Assert.Equal(jobFailure, item.GetProperty("countsAsJobFailure").GetBoolean());
        Assert.Equal(row.FailureFingerprint, item.GetProperty("failureFingerprint").GetString());
        Assert.Equal(row.FirstObservedAtUtc, item.GetProperty("firstObservedAtUtc").GetDateTimeOffset());
        JsonSchemaAssertions.AssertValid(structured, McpCatalog.OutputSchema("get_job_failures"), "get_job_failures");
    }
}
