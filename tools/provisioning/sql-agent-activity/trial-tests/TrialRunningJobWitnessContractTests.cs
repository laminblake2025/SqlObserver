using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlObserver.AgentActivityTrial.Tests;

public sealed class TrialRunningJobWitnessContractTests
{
    [Theory]
    [InlineData("BaselineSql", false)]
    [InlineData("GuardSql", true)]
    public void ForeignUnfinishedPredicateIncludesEveryRequestedQueuedOrStartedRowAcrossAllSessions(string field, bool excludesOwned)
    {
        string sql = Assert.IsType<string>(typeof(TrialRunningJobWitness).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue());
        string ownedGate = excludesOwned ? @"job_id<>@job\s+AND\s+" : "";
        string unfinished = @"WHERE\s+" + ownedGate + @"\(run_requested_date\s+IS\s+NOT\s+NULL\s+OR\s+queued_date\s+IS\s+NOT\s+NULL\s+OR\s+start_execution_date\s+IS\s+NOT\s+NULL\)\s+AND\s+stop_execution_date\s+IS\s+NULL";
        Assert.True(Regex.IsMatch(sql, unfinished, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        Match activity = Regex.Match(sql, @"EXISTS\(SELECT 1 FROM msdb\.dbo\.sysjobactivity WHERE.*?stop_execution_date IS NULL\)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(activity.Success);
        Assert.DoesNotContain("session_id", activity.Value, StringComparison.Ordinal);
        Assert.Contains("enabled<>0", sql, StringComparison.Ordinal);
        Assert.Contains(excludesOwned ? "@foreign_count>511" : ")>511", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteRunningAndCompletedBracketsRequireIndependentExecutionEvidence()
    {
        var fixture = Fixture();
        Assert.Equal(new TrialVisibilityResult(1, 1, true, true, true), TrialRunningJobWitness.ValidateRunningComparison(
            fixture.Ownership, fixture.Before, fixture.After, fixture.Capture, fixture.ExecutionBefore, fixture.ExecutionAfter));
        DateTime stop = fixture.Source.StartExecutionLocal!.Value.AddMilliseconds(300);
        var stopped = fixture.Source with { StopExecutionLocal = stop, LastExecutedStepId = 1, LastExecutedStepLocal = stop };
        var first = fixture.ExecutionBefore with { Activity = stopped, LiveOwnedRequest = false, Released = true, SuccessfulHistory = true,
            StepCompletedUtc = fixture.ExecutionBefore.StepStartedUtc!.Value.AddMilliseconds(100), StepCompletedLocal = stop,
            StepCompletedOffset = fixture.ExecutionBefore.StepStartedOffset!.Value.AddMilliseconds(100) };
        var second = first with { ObservedUtc = fixture.ExecutionAfter.ObservedUtc };
        Assert.Equal(new TrialVisibilityResult(1, 1, true, true, true), TrialRunningJobWitness.ValidateCompletedComparison(
            fixture.Ownership, fixture.Before with { Rows = [new(stopped, true)] }, fixture.After with { Rows = [new(stopped, true)] },
            fixture.Capture with { Jobs = [stopped] }, first, second));
        Assert.Throws<InvalidDataException>(() => TrialRunningJobWitness.ValidateRunningComparison(fixture.Ownership,
            fixture.Before with { Rows = [new(stopped, true)] }, fixture.After with { Rows = [new(stopped, true)] },
            fixture.Capture with { Jobs = [stopped] }, first, second));
    }

    [Theory]
    [InlineData("nonce")] [InlineData("job")] [InlineData("database")] [InlineData("spid")]
    [InlineData("no-request")] [InlineData("released")] [InlineData("local-kind")] [InlineData("utc-offset")]
    [InlineData("epoch")] [InlineData("outside-bracket")] [InlineData("step-clock-drift")]
    public void ExactSourceRowsAloneCannotQualifyWrongOrUnstableExecution(string mutation)
    {
        var fixture = Fixture();
        RunningJobExecutionWitness second = mutation switch
        {
            "nonce" => fixture.ExecutionAfter with { ExecutionNonce = Guid.NewGuid() },
            "job" => fixture.ExecutionAfter with { JobId = Guid.NewGuid() },
            "database" => fixture.ExecutionAfter with { DatabaseGuid = Guid.NewGuid() },
            "spid" => fixture.ExecutionAfter with { StepSessionId = fixture.Ownership.Baseline.SessionId },
            "no-request" => fixture.ExecutionAfter with { LiveOwnedRequest = false },
            "released" => fixture.ExecutionAfter with { Released = true },
            "local-kind" => fixture.ExecutionAfter with { StepStartedLocal = DateTime.SpecifyKind(fixture.ExecutionAfter.StepStartedLocal!.Value, DateTimeKind.Utc) },
            "utc-offset" => fixture.ExecutionAfter with { StepStartedUtc = fixture.ExecutionAfter.StepStartedUtc!.Value.ToOffset(TimeSpan.FromHours(1)) },
            "epoch" => fixture.ExecutionAfter with { AgentSessionId = fixture.ExecutionAfter.AgentSessionId + 1 },
            "outside-bracket" => fixture.ExecutionAfter with { ObservedUtc = fixture.After.CompletedAtUtc.AddTicks(-1) },
            _ => fixture.ExecutionAfter with { StepStartedUtc = fixture.ExecutionAfter.StepStartedUtc!.Value.AddTicks(1) },
        };
        Assert.Throws<InvalidDataException>(() => TrialRunningJobWitness.ValidateRunningComparison(fixture.Ownership,
            fixture.Before, fixture.After, fixture.Capture, fixture.ExecutionBefore, second));
    }

    [Theory]
    [InlineData("row-cap")] [InlineData("monitor-owner")] [InlineData("private-inventory")] [InlineData("hash")]
    public void UnsafeOrAlteredPrivateBaselineIsRefused(string mutation)
    {
        RunningJobBaseline baseline = Fixture().Ownership.Baseline;
        baseline = mutation switch
        {
            "row-cap" => baseline with { ForeignJobCount = 512 },
            "monitor-owner" => baseline with { OwnerSidHex = baseline.ModuleBaseline.Snapshot.Monitor.SidHex },
            "private-inventory" => baseline with { ForeignInventoryJson = baseline.ForeignInventoryJson.Replace("\"jobs\":[]", "\"jobs\":[{\"command\":\"foreign private command\"}]", StringComparison.Ordinal) },
            _ => baseline with { PermissionsSha256 = new string('f', 64) },
        };
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => TrialRunningJobWitness.ValidateBaseline(baseline));
        Assert.DoesNotContain("foreign private command", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(baseline.OwnerPrincipal, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteAheadCreationIsOrderedAndOwnershipCannotBeRewritten()
    {
        var fixture = Fixture(); RunningJobOwnership ownership = fixture.Ownership;
        RunningJobBaseline baseline = ownership.Baseline;
        var prepared = new RunningJobReceipt(1, baseline.ModuleBaseline.DeploymentId, baseline.FixtureId, "prepared", baseline);
        var pendingDatabase = prepared with { Phase = "database_create_pending" };
        TrialRunningJobJournal.ValidateTransition(prepared, pendingDatabase);
        var databaseOnly = ownership with { JobId = Guid.Empty, JobDefinitionSha256 = "", StepCommandSha256 = "" };
        var database = pendingDatabase with { Phase = "database_created", Ownership = databaseOnly };
        TrialRunningJobJournal.ValidateTransition(pendingDatabase, database);
        var jobPending = database with { Phase = "job_create_pending" };
        TrialRunningJobJournal.ValidateTransition(database, jobPending);
        var postimage = jobPending with { Ownership = ownership };
        TrialRunningJobJournal.ValidateTransition(jobPending, postimage);
        var created = postimage with { Phase = "job_created" };
        TrialRunningJobJournal.ValidateTransition(postimage, created);
        Assert.Throws<InvalidDataException>(() => TrialRunningJobJournal.ValidateTransition(prepared, database));
        Assert.Throws<InvalidDataException>(() => TrialRunningJobJournal.ValidateTransition(created,
            created with { Phase = "agent_start_pending", Ownership = ownership with { DatabaseGuid = Guid.NewGuid() } }));
        Assert.Throws<InvalidDataException>(() => TrialRunningJobJournal.ValidateTransition(prepared,
            pendingDatabase with { Baseline = baseline with { OwnerPrincipal = "TEST\\OtherOwner" } }));
    }

    [Fact]
    public void AmbiguousPendingActionsArePreservedRatherThanRetried()
    {
        var fixture = Fixture(); RunningJobBaseline baseline = fixture.Ownership.Baseline;
        var epoch = new RunningJobAgentEpoch("SQLAgent$" + baseline.ModuleBaseline.Snapshot.Identity.InstanceName, 1234,
            fixture.ExecutionBefore.StepStartedUtc!.Value.AddSeconds(-1), fixture.ExecutionBefore.StepStartedUtc!.Value.AddSeconds(-1),
            fixture.ExecutionBefore.AgentSessionId, fixture.ExecutionBefore.AgentStartedLocal);
        var pending = new RunningJobReceipt(1, baseline.ModuleBaseline.DeploymentId, baseline.FixtureId, "job_start_pending", baseline, fixture.Ownership, epoch);
        Assert.Equal(RunningJobRecoveryAction.PreserveForInspection, TrialRunningJobJournal.SelectRecoveryAction(pending, false, false));
        var deleting = pending with { Phase = "job_delete_pending" };
        Assert.Equal(RunningJobRecoveryAction.PreserveForInspection, TrialRunningJobJournal.SelectRecoveryAction(deleting, false, false));
        var blocked = deleting with { Phase = "cleanup_blocked", BlockedFromPhase = "job_delete_pending", FailureCode = "owned_sql_cleanup_unconfirmed" };
        TrialRunningJobJournal.ValidateTransition(deleting, blocked);
        Assert.Equal(RunningJobRecoveryAction.AcknowledgeJobAbsent, TrialRunningJobJournal.SelectRecoveryAction(blocked, true, false));
        Assert.Throws<InvalidDataException>(() => TrialRunningJobJournal.ValidateTransition(blocked, deleting));
        Assert.Throws<InvalidDataException>(() => TrialRunningJobJournal.ValidateTransition(blocked, blocked with { BlockedFromPhase = "database_drop_pending" }));
        var dropping = pending with { Phase = "database_drop_pending" };
        Assert.Equal(RunningJobRecoveryAction.PreserveForInspection, TrialRunningJobJournal.SelectRecoveryAction(dropping, false, true));
        Assert.Equal(RunningJobRecoveryAction.AcknowledgeDatabaseAbsent, TrialRunningJobJournal.SelectRecoveryAction(dropping, true, true));
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("queued")]
    [InlineData("started")]
    public void UnfinishedOwnedCandidateCannotEnterDeletionOrCleanupAcknowledgement(string candidate)
    {
        var fixture = Fixture(); RunningJobBaseline baseline = fixture.Ownership.Baseline;
        DateTime local = fixture.Source.StartExecutionLocal!.Value;
        var activity = fixture.Source with { RequestedLocal = candidate == "requested" ? local : null,
            QueuedLocal = candidate == "queued" ? local : null, StartExecutionLocal = candidate == "started" ? local : null };
        var pending = fixture.ExecutionBefore with { Activity = activity, StepSessionId = null, StepStartedUtc = null,
            StepStartedLocal = null, StepStartedOffset = null, LiveOwnedRequest = false };
        Assert.Throws<InvalidDataException>(() => TrialRunningJobWitness.ValidateCleanupExecution(fixture.Ownership, pending));
        var epoch = new RunningJobAgentEpoch("SQLAgent$" + baseline.ModuleBaseline.Snapshot.Identity.InstanceName, 1234,
            fixture.ExecutionBefore.StepStartedUtc!.Value.AddSeconds(-1), fixture.ExecutionBefore.StepStartedUtc!.Value.AddSeconds(-1),
            pending.AgentSessionId, pending.AgentStartedLocal);
        foreach (string phase in new[] { "job_delete_pending", "job_absent", "database_drop_pending", "database_absent", "cleanup_verified" })
        {
            var receipt = new RunningJobReceipt(1, baseline.ModuleBaseline.DeploymentId, baseline.FixtureId, phase, baseline,
                fixture.Ownership, epoch, pending);
            Assert.Throws<InvalidDataException>(() => TrialRunningJobJournal.ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId));
        }
    }

    [Fact]
    public void PrivateReceiptRoundtripRetainsClosedOwnershipAndPhaseWitness()
    {
        var fixture = Fixture(); var baseline = fixture.Ownership.Baseline;
        var epoch = new RunningJobAgentEpoch("SQLAgent$" + baseline.ModuleBaseline.Snapshot.Identity.InstanceName, 1234,
            fixture.ExecutionBefore.StepStartedUtc!.Value.AddSeconds(-1), fixture.ExecutionBefore.StepStartedUtc!.Value.AddSeconds(-1),
            fixture.ExecutionBefore.AgentSessionId, fixture.ExecutionBefore.AgentStartedLocal);
        var receipt = new RunningJobReceipt(1, baseline.ModuleBaseline.DeploymentId, baseline.FixtureId, "running_witness", baseline,
            fixture.Ownership, epoch, fixture.ExecutionBefore);
        var roundtrip = JsonSerializer.Deserialize<RunningJobReceipt>(JsonSerializer.Serialize(receipt))!;
        TrialRunningJobJournal.ValidateTransition(receipt, roundtrip);
        Assert.Throws<InvalidDataException>(() => TrialRunningJobJournal.ValidateReceipt(receipt with { AgentEpoch = null }, receipt.DeploymentId, receipt.FixtureId));
        Assert.Throws<InvalidDataException>(() => TrialRunningJobJournal.ValidateReceipt(receipt with { ExecutionWitness = null }, receipt.DeploymentId, receipt.FixtureId));
    }

    private static (RunningJobOwnership Ownership, TrialVisibilityJob Source, TrialVisibilitySnapshot Before, TrialVisibilitySnapshot After,
        TrialVisibilityCapture Capture, RunningJobExecutionWitness ExecutionBefore, RunningJobExecutionWitness ExecutionAfter) Fixture()
    {
        TrialBaseline module = TrialFixture.Create().Baseline;
        Guid id = Guid.Parse("44444444-4444-4444-4444-444444444444"), nonce = Guid.Parse("55555555-5555-5555-5555-555555555555");
        const string inventory = "{\"jobs\":[],\"steps\":[],\"schedules\":[],\"attachments\":[],\"jobServers\":[],\"agentConfiguration\":{},\"permissions\":{},\"oldSessions\":[],\"oldActivity\":[],\"foreignHistory\":[]}";
        string Utf8Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        string Utf16Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(value)));
        var baseline = new RunningJobBaseline(module, id, Guid.Parse("66666666-6666-6666-6666-666666666666"), 71, "TEST\\Dba", "05060708", 1,
            "SqlObserverRunning_" + id.ToString("N"), "SqlObserver Running " + id.ToString("N"), nonce, 0, 7,
            new DateTime(2026, 10, 7, 5, 0, 0, DateTimeKind.Unspecified), inventory,
            Utf16Hash("[]\n[]\n[]\n[]\n[]\n{}\n[]"), Utf8Hash("{}"), Utf8Hash("[]"), Utf8Hash("[]"));
        Guid job = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var ownership = new RunningJobOwnership(baseline, 10, Guid.Parse("88888888-8888-8888-8888-888888888888"),
            module.DeploymentId.ToString("D") + ":" + id.ToString("D"), new string('b', 64), job, new string('a', 64),
            Utf16Hash(TrialRunningJobWitness.BuildStepCommand(baseline, job)));
        DateTime local = new(2026, 10, 7, 6, 0, 0, DateTimeKind.Unspecified);
        DateTimeOffset utc = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
        var source = new TrialVisibilityJob(job, 8, local, null, local, null, null, null);
        var before = new TrialVisibilitySnapshot(module.Snapshot.Identity, baseline.ConnectionId, baseline.SessionId,
            utc.AddSeconds(1), utc.AddMilliseconds(1500), 8, local.AddSeconds(-1), [new(source, true)]);
        var after = before with { StartedAtUtc = utc.AddMilliseconds(3500), CompletedAtUtc = utc.AddSeconds(4) };
        var capture = new TrialVisibilityCapture(utc.AddSeconds(2), utc.AddSeconds(3), 8, before.AgentStartedLocal, 8, before.AgentStartedLocal, [source], 1, false);
        var execution = new RunningJobExecutionWitness(id, job, nonce, 10, ownership.DatabaseGuid, 100, utc.AddMilliseconds(800), utc,
            local, new DateTimeOffset(local, TimeSpan.FromHours(-4)), false, null, null, null, true, 8, before.AgentStartedLocal, source, false);
        return (ownership, source, before, after, capture, execution, execution with { ObservedUtc = utc.AddMilliseconds(4500) });
    }
}
