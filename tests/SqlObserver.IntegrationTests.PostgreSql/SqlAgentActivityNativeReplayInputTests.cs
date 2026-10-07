using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SqlObserver.Domain.Collection;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Synthetic artifact contract tests only; no native SQL, repository or installation authority.</summary>
[Trait("Category", "AgentNativeReplayContract")]
public sealed class SqlAgentActivityNativeReplayInputTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 6, 0, 0, TimeSpan.Zero);
    private const string Target = "11111111-1111-4111-8111-111111111111";
    private const string Deployment = "22222222-2222-4222-8222-222222222222";
    private const string OriginalRun = "33333333-3333-4333-8333-333333333333";
    private const string Sid = "S-1-5-21-123-456-1002";
    private static readonly string[] RunnerNames = ["NativeDiagnostics.cs", "NativeRunner.csproj", "Program.cs", "TemporaryService.cs", "NativeAuthorizationSidecar.cs"];
    private static readonly string[] InputNames =
    ["tools/provisioning/sql-agent-activity/trial/AgentActivityTrialSql.cs", "tools/provisioning/sql-agent-activity/trial/TrialCoordinator.cs",
     "tools/provisioning/sql-agent-activity/trial/TrialJournal.cs", "tests/SqlObserver.IntegrationTests.SqlServer/SqlAgentActivityExplicitDenyQualificationTests.cs",
     "tests/SqlObserver.IntegrationTests.SqlServer/SqlAgentActivityCollectorNativeReceiptTests.cs",
     "src/SqlObserver.Infrastructure.SqlServer/SqlServerAgentActivityModuleCaptureReader.cs"];
    private static readonly string[] DeniedCases =
    ["SqlObserver.IntegrationTests.SqlServer.SqlAgentActivityExplicitDenyQualificationTests.ActualMonitorSignedModuleExplicitDenyRefusesPublicCapture",
     "SqlObserver.IntegrationTests.SqlServer.SqlAgentActivityExplicitDenyQualificationTests.ActualMonitorDeniedModuleRetainsCanonicalBoundRefusal"];
    private const string Pin = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Thumb = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("signed", 0)] [InlineData("signed", 2)] [InlineData("signed", 512)] [InlineData("signed", 513)]
    [InlineData("restored", 0)] [InlineData("restored", 2)] [InlineData("restored", 512)] [InlineData("restored", 513)]
    public void SyntheticContractPreservesOriginalAuthorityIdentityPrecisionAndBoundedAccounting(string phase, int sourceRows)
    {
        var evidence = Create(phase, sourceRows); var parsed = Read(evidence);
        Assert.Equal(Guid.Parse(Target), parsed.Enrollment.TargetId.Value); Assert.Equal(1, parsed.Enrollment.TargetRevision.Value);
        Assert.Equal(1, parsed.Enrollment.Revision); Assert.Equal(Guid.Parse(Deployment), parsed.Enrollment.EnrollmentId);
        Assert.Equal(Sid, parsed.Enrollment.ExpectedWindowsSid); Assert.Equal(Now.AddMinutes(-1), parsed.Enrollment.NotBeforeUtc);
        Assert.Equal(Now.AddMinutes(12), parsed.Enrollment.ExpiresAtUtc); Assert.Equal(Guid.Parse(OriginalRun), parsed.RunId.Value);
        var capture = parsed.Result.Payload.SqlAgentModuleCapture!.Capture;
        Assert.Equal(Now.AddSeconds(-2).AddTicks(1), capture.Before.SourceObservedAtUtc);
        Assert.Equal(Now.AddSeconds(-1).AddTicks(2), capture.After.SourceObservedAtUtc);
        Assert.Equal(sourceRows, capture.SourceRowsRead); Assert.Equal(Math.Min(sourceRows, 512), capture.Jobs.Count);
        Assert.Equal(sourceRows > 512, capture.Truncated); Assert.Equal(4096 + sourceRows * 768, parsed.Result.Accounting.ResponseBytes);
        Assert.Equal(4096 + Math.Min(sourceRows, 512) * 768, parsed.Result.Accounting.OutputBytes);
        Assert.Equal(sourceRows > 512 ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded, parsed.Result.Outcome);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(evidence.Receipt)), parsed.RawPrivateReceiptSha256);
        Assert.All(capture.Jobs, job =>
        {
            Assert.Equal(DateTimeKind.Unspecified, job.StartExecutionLocal!.Value.Kind);
            Assert.Equal("2026-11-01T01:30:00.1234567", job.StartExecutionLocal.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
            Assert.Equal("2026-11-01T00:30:00.1234567", job.StopExecutionLocal!.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        });
        Assert.All(SqlAgentActivityEvaluator.Evaluate(capture).Jobs, job => { Assert.Equal(SqlAgentActivityCandidateState.Unknown, job.CandidateState); Assert.Null(job.ElapsedSeconds); Assert.Null(job.StartedAtUtc); });
        Assert.Equal(4, parsed.Proof.NativeCasesPassed); Assert.Equal(5, parsed.Proof.RunnerInputCount);
        Assert.True(parsed.Proof.FullDbaValidationAtOriginalExport); Assert.False(parsed.Proof.FullDbaValidationRepeatedByParser);
        string proof = JsonSerializer.Serialize(parsed.Proof);
        foreach (string privateValue in new[] { Sid, "private-sql", "private-cert", Deployment, parsed.Enrollment.EvidenceSha256 }) Assert.DoesNotContain(privateValue, proof, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sql", parsed.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")] [InlineData(" 1")]
    public void ExplicitOptInFailsBeforeReadingAnyPrivatePath(string? value)
    {
        int calls = 0;
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityNativeReplayInput.Load(name =>
        { calls++; Assert.Equal(SqlAgentActivityNativeReplayInput.Prefix + "ACCEPTANCE", name); return value; }, Now));
        Assert.Equal(1, calls);
    }
    [Fact]
    public void WindowsReportSingleUtf8BomPreservesRawArtifactDigestAndTypedOwnedStopReceipt()
    {
        var evidence = Create(); var report = JsonNode.Parse(evidence.Report)!.AsObject(); var action = report["actionResult"]!.AsObject();
        action["firstProcess"]!["metadata"]!["ownServiceStopRequested"] = true;
        action["runner"] = action["firstProcess"]!["metadata"]!.DeepClone();
        byte[] bytes = [0xef, 0xbb, 0xbf, ..Encoding.UTF8.GetBytes(report.ToJsonString())];
        var parsed = Read(evidence with { Report = bytes });
        Assert.Equal(Hash(bytes), parsed.Proof.NativeReportSha256);
        Assert.Equal(Guid.Parse(OriginalRun), parsed.RunId.Value);
    }
    [Theory]
    [InlineData("double-bom")] [InlineData("partial-bom")] [InlineData("embedded-bom")]
    public void MalformedJsonEncodingPrefixesRemainRefused(string mutation)
    {
        var evidence = Create();
        byte[] bytes = mutation switch
        {
            "double-bom" => [0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf, ..evidence.Report],
            "partial-bom" => [0xef, 0xbb, ..evidence.Report],
            _ => [evidence.Report[0], 0xef, 0xbb, 0xbf, ..evidence.Report[1..]],
        };
        Refuses(evidence with { Report = bytes });
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void DiagnosticProcessClocksAcceptDefaultJsonPrecisionWithoutChangingSourceClocks(int fractionDigits)
    {
        var evidence = Create(); var report = JsonNode.Parse(evidence.Report)!.AsObject(); var action = report["actionResult"]!.AsObject();
        var process = action["firstProcess"]!.AsObject(); var runner = process["metadata"]!.AsObject();
        string format = "yyyy-MM-dd'T'HH:mm:ss" + (fractionDigits == 0 ? "" : "." + new string('f', fractionDigits)) + "'Z'";
        process["processStartedUtc"] = Now.AddSeconds(-5).ToString(format, CultureInfo.InvariantCulture);
        process["processExitedUtc"] = Now.ToString(format, CultureInfo.InvariantCulture);
        runner["runnerProcessStartedUtc"] = process["processStartedUtc"]!.DeepClone();
        foreach (string phase in new[] { "signed", "denied", "restored" }) runner[phase + "Phase"]!["nativeChildProcessStartedUtc"] = Now.AddSeconds(-4).ToString(format, CultureInfo.InvariantCulture);
        action["runner"] = runner.DeepClone();
        var parsed = Read(evidence with { Report = Encoding.UTF8.GetBytes(report.ToJsonString()) });
        Assert.Equal(Now.AddSeconds(-2).AddTicks(1), parsed.Result.Payload.SqlAgentModuleCapture!.Capture.Before.SourceObservedAtUtc);
    }
    [Theory]
    [InlineData("denied")] [InlineData("SIGNED")] [InlineData("signed ")] [InlineData("unsigned")]
    public void OnlyOriginalPassingSignedOrRestoredCollectorCaseMayBeSelected(string phase) => Refuses(Create() with { Phase = phase });

    [Theory]
    [InlineData("sidecar")] [InlineData("report")] [InlineData("trx")] [InlineData("package")]
    public void ExactExternalDigestIsRequiredForEachInputArtifact(string artifact)
    {
        var evidence = Create(); var hashes = Digests(evidence);
        evidence = artifact switch
        {
            "sidecar" => evidence with { Sidecar = [..evidence.Sidecar, (byte)' '] },
            "report" => evidence with { Report = [..evidence.Report, (byte)' '] },
            "trx" => evidence with { Trx = [..evidence.Trx, (byte)' '] },
            _ => evidence with { Package = [..evidence.Package, (byte)' '] },
        };
        Assert.Throws<InvalidDataException>(() => SqlAgentActivityNativeReplayInput.Parse(evidence.Sidecar, evidence.Report, evidence.Trx, evidence.Package, evidence.Phase, hashes, Now));
    }
    [Theory]
    [InlineData("sidecar")] [InlineData("report")] [InlineData("trx")] [InlineData("package")]
    public void ArtifactBoundsAreEnforcedBeforeDeserialization(string artifact)
    {
        var evidence = Create();
        evidence = artifact switch
        {
            "sidecar" => evidence with { Sidecar = new byte[SqlAgentActivityNativeReplayInput.MaximumSidecarBytes + 1] },
            "report" => evidence with { Report = new byte[SqlAgentActivityNativeReplayInput.MaximumReportBytes + 1] },
            "trx" => evidence with { Trx = new byte[SqlAgentActivityNativeReplayInput.MaximumTrxBytes + 1] },
            _ => evidence with { Package = new byte[SqlAgentActivityNativeReplayInput.MaximumPackageBytes + 1] },
        };
        Refuses(evidence);
    }

    [Theory]
    [InlineData("root-unknown")] [InlineData("auth-unknown")] [InlineData("binding-unknown")]
    [InlineData("root-duplicate")] [InlineData("auth-duplicate")] [InlineData("snapshot-duplicate")]
    [InlineData("snapshot-digest")] [InlineData("snapshot-pin")]
    [InlineData("snapshot-kind")] [InlineData("snapshot-scope")] [InlineData("snapshot-drift-fact")] [InlineData("snapshot-duplicate-object")]
    [InlineData("target")] [InlineData("revision")] [InlineData("deployment")]
    [InlineData("sid")] [InlineData("window-short")] [InlineData("window-future")]
    [InlineData("window-six-digits")] [InlineData("window-eight-digits")] [InlineData("source-version")]
    public void SidecarShapeProvenanceAndOriginalAuthorityRefuseMutation(string mutation)
    {
        var evidence = Create(); var node = JsonNode.Parse(evidence.Sidecar)!.AsObject();
        var authority = node["authorization"]!.AsObject(); var binding = authority["binding"]!.AsObject();
        switch (mutation)
        {
            case "root-unknown": node["password"] = "PRIVATE_SECRET"; break;
            case "auth-unknown": authority["password"] = "PRIVATE_SECRET"; break;
            case "binding-unknown": binding["connectionString"] = "PRIVATE_SECRET"; break;
            case "snapshot-digest": node["installedSnapshot"]!["Monitor"]!["Principal"] = "OTHER\\Monitor"; break;
            case "snapshot-pin": binding["serverName"] = "wrong"; break;
            case "snapshot-kind": node["installedSnapshot"]!["Objects"]![0]!["Kind"] = "unowned_object"; break;
            case "snapshot-scope": node["installedSnapshot"]!["Facts"]![0]!["Scope"] = "other_database"; break;
            case "snapshot-drift-fact": node["installedSnapshot"]!["Facts"]![0]!["Kind"] = "unexpected_signature"; break;
            case "snapshot-duplicate-object": node["installedSnapshot"]!["Objects"]![0] = node["installedSnapshot"]!["Objects"]![1]!.DeepClone(); break;
            case "target": node["targetId"] = Guid.NewGuid().ToString("D"); break;
            case "revision": authority["targetRevision"] = 2; break;
            case "deployment": authority["enrollmentId"] = Guid.NewGuid().ToString("D"); break;
            case "sid": authority["expectedWindowsSid"] = "S-1-5-21-123-456-1003"; break;
            case "window-short": authority["notBeforeUtc"] = Utc(Now.AddSeconds(-2).AddTicks(2)); break;
            case "window-future": authority["notBeforeUtc"] = Utc(Now.AddSeconds(1)); break;
            case "window-six-digits": authority["notBeforeUtc"] = "2026-10-07T05:59:00.000000Z"; break;
            case "window-eight-digits": authority["notBeforeUtc"] = "2026-10-07T05:59:00.00000000Z"; break;
            case "source-version": binding["sourceVersion"] = 1; break;
        }
        if (mutation is "snapshot-kind" or "snapshot-scope" or "snapshot-drift-fact" or "snapshot-duplicate-object")
        {
            string digest = Hash(Encoding.UTF8.GetBytes(node["installedSnapshot"]!.ToJsonString()));
            node["installedEvidenceSha256"] = digest; authority["evidenceSha256"] = digest;
        }
        string text = node.ToJsonString();
        if (mutation == "root-duplicate") text = text.Insert(1, "\"schemaVersion\":1,");
        if (mutation == "auth-duplicate") text = text.Replace("\"authorization\":{", "\"authorization\":{\"targetId\":\"" + Target + "\",", StringComparison.Ordinal);
        if (mutation == "snapshot-duplicate") text = text.Replace("\"Monitor\":{", "\"Monitor\":{\"Principal\":\"PRIVATE_SECRET\",", StringComparison.Ordinal);
        evidence = Rebind(evidence with { Sidecar = Encoding.UTF8.GetBytes(text) });
        Refuses(evidence);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("duplicate")] [InlineData("report-failed")] [InlineData("cleanup-failed")]
    [InlineData("snapshot-copy-mismatch")] [InlineData("source-changed")] [InlineData("source-count")]
    [InlineData("runner-hash")] [InlineData("runner-input-missing")] [InlineData("sidecar-not-before-launch")]
    [InlineData("sidecar-not-readback")] [InlineData("sidecar-length")]
    [InlineData("phase-executed")] [InlineData("phase-skip")] [InlineData("phase-child-unconfirmed")]
    [InlineData("phase-identity-unconfirmed")] [InlineData("phase-service-delete-unconfirmed")]
    [InlineData("phase-typed-flag")] [InlineData("deny-case-substitution")] [InlineData("trial-failure")]
    [InlineData("agent-running")] [InlineData("qualification-process-remains")] [InlineData("archive-mismatch")]
    [InlineData("runner-pid")] [InlineData("runner-start")] [InlineData("runner-exit")]
    [InlineData("phase-start-before-runner")] [InlineData("phase-start-after-runner")] [InlineData("phase-start-offset")]
    [InlineData("phase-start-eight-digits")] [InlineData("phase-start-24")] [InlineData("phase-start-leap-second")]
    [InlineData("phase-trx-reused")]
    [InlineData("phase-launch-missing")]
    [InlineData("owned-stop-false")] [InlineData("owned-stop-string")]
    public void PassingReportRequiresExactNativeCasesInventoriesTerminationAndCleanup(string mutation)
    {
        var evidence = Create(); var report = JsonNode.Parse(evidence.Report)!.AsObject(); var action = report["actionResult"]!.AsObject();
        var runner = action["firstProcess"]!["metadata"]!.AsObject(); var signedPhase = runner["signedPhase"]!.AsObject();
        switch (mutation)
        {
            case "unknown": report["credentials"] = "PRIVATE_SECRET"; break;
            case "report-failed": report["qualificationPassed"] = false; break;
            case "cleanup-failed": report["cleanupVerified"] = false; break;
            case "snapshot-copy-mismatch": action["runner"]!["nativeTestsPassed"] = 3; break;
            case "source-changed": report["sourceAfter"]!["allInputsUnchanged"] = false; break;
            case "source-count": report["sourceBefore"]!["verifiedFileCount"] = InputNames.Length - 1; break;
            case "runner-hash": action["runnerSourceSha256"]!["Program.cs"] = new string('c', 64); break;
            case "runner-input-missing": report["reviewedRunnerInputs"]!.AsArray().RemoveAt(0); break;
            case "sidecar-not-before-launch": runner["authorizationSidecarWrittenBeforeMonitorLaunch"] = false; break;
            case "sidecar-not-readback": runner["authorizationSidecarReadbackVerified"] = false; break;
            case "sidecar-length": runner["authorizationSidecarBytes"] = 1; break;
            case "phase-executed": signedPhase["nativeTestsExecuted"] = 0; break;
            case "phase-skip": signedPhase["nativeTestsSkipped"] = 1; break;
            case "phase-child-unconfirmed": signedPhase["directChildExited"] = false; break;
            case "phase-identity-unconfirmed": signedPhase["pipeClientIdentityVerified"] = false; break;
            case "phase-service-delete-unconfirmed": signedPhase["serviceDeletionVerified"] = false; break;
            case "phase-typed-flag": signedPhase["workerReceiptReceived"] = "true"; break;
            case "deny-case-substitution": action["deniedTrx"]!["expectedCases"]![0] = SqlAgentActivityNativeReplayInput.CollectorCase; break;
            case "trial-failure": runner["trialResult"]!["FailureCode"] = "PRIVATE_SECRET"; break;
            case "agent-running": action["postRunAudit"]!["agentServiceSnapshot"]![0]!["State"] = "Running"; break;
            case "qualification-process-remains": action["postRunAudit"]!["qualificationProcesses"]!.AsArray().Add(new JsonObject { ["Name"] = "testhost" }); break;
            case "archive-mismatch": report["packageSha256"] = new string('e', 64); break;
            case "runner-pid": runner["runnerProcessId"] = 99999; break;
            case "runner-start": runner["runnerProcessStartedUtc"] = Utc(Now.AddSeconds(-6)); break;
            case "runner-exit": action["firstProcess"]!["processExitedUtc"] = Utc(Now.AddSeconds(-6)); break;
            case "phase-start-before-runner": signedPhase["nativeChildProcessStartedUtc"] = Utc(Now.AddSeconds(-6)); break;
            case "phase-start-after-runner": signedPhase["nativeChildProcessStartedUtc"] = Utc(Now.AddSeconds(1)); break;
            case "phase-start-offset": signedPhase["nativeChildProcessStartedUtc"] = "2026-10-07T06:59:56.0000000+01:00"; break;
            case "phase-start-eight-digits": signedPhase["nativeChildProcessStartedUtc"] = "2026-10-07T05:59:56.00000000Z"; break;
            case "phase-start-24": signedPhase["nativeChildProcessStartedUtc"] = "2026-10-07T24:00:00Z"; break;
            case "phase-start-leap-second": signedPhase["nativeChildProcessStartedUtc"] = "2026-10-07T05:59:60Z"; break;
            case "phase-trx-reused":
                runner["restoredPhase"]!["trxSha256"] = signedPhase["trxSha256"]!.DeepClone();
                action["restoredTrx"]!["sha256"] = signedPhase["trxSha256"]!.DeepClone(); break;
            case "phase-launch-missing": signedPhase.Remove("nativeLaunchStarted"); break;
            case "owned-stop-false": runner["ownServiceStopRequested"] = false; break;
            case "owned-stop-string": runner["ownServiceStopRequested"] = "true"; break;
        }
        if (mutation != "snapshot-copy-mismatch") action["runner"] = runner.DeepClone();
        string text = report.ToJsonString(); if (mutation == "duplicate") text = text.Insert(1, "\"completed\":true,");
        Refuses(evidence with { Report = Encoding.UTF8.GetBytes(text) });
    }

    [Theory]
    [InlineData("unknown")] [InlineData("duplicate")] [InlineData("identity")] [InlineData("run-empty")]
    [InlineData("enrollment")] [InlineData("digest")] [InlineData("failed")] [InlineData("wrong-reason")]
    [InlineData("accounting")] [InlineData("loss")] [InlineData("admitted")] [InlineData("association")]
    [InlineData("native-claim")] [InlineData("service-running")] [InlineData("job-duplicate")]
    [InlineData("job-order")] [InlineData("job-session")]
    [InlineData("utc-six")] [InlineData("utc-eight")] [InlineData("utc-offset")] [InlineData("utc-24")]
    [InlineData("utc-leap-second")] [InlineData("utc-reversed-tick")] [InlineData("utc-bracket-extra-tick")]
    [InlineData("local-offset")] [InlineData("local-six")] [InlineData("local-24")]
    public void PrivateReceiptPreservesClosedSourceAndRejectsIdentityAccountingAndClockNormalization(string mutation)
    {
        var evidence = Create(); var value = JsonNode.Parse(evidence.Receipt)!.AsObject(); var observation = value["observation"]!.AsObject();
        var before = observation["before"]!.AsObject(); var after = observation["after"]!.AsObject(); var jobs = observation["jobs"]!.AsArray();
        switch (mutation)
        {
            case "unknown": value["password"] = "PRIVATE_SECRET"; break;
            case "identity": value["targetId"] = Guid.NewGuid().ToString("D"); break;
            case "run-empty": value["runId"] = Guid.Empty.ToString("D"); break;
            case "enrollment": observation["enrollmentRevision"] = 2; break;
            case "digest": observation["evidenceSha256"] = new string('e', 64); break;
            case "failed": value["outcome"] = "permission_denied"; break;
            case "wrong-reason": value["reason"] = "required_permission_missing"; break;
            case "accounting": value["accounting"]!["responseBytes"] = 1; break;
            case "loss": value["loss"]!["kind"] = "source_row_limit"; break;
            case "admitted": value["productionAdmitted"] = true; break;
            case "association": observation["serviceSessionAssociationQualified"] = true; break;
            case "native-claim": observation["nativeQualification"] = "Qualified"; break;
            case "service-running": before["service"]!["status"] = 4; break;
            case "job-duplicate": jobs[1] = jobs[0]!.DeepClone(); break;
            case "job-order": var first = jobs[0]!.DeepClone(); jobs[0] = jobs[1]!.DeepClone(); jobs[1] = first; break;
            case "job-session": jobs[0]!["sessionId"] = 10; break;
            case "utc-six": before["sourceObservedAtUtc"] = "2026-10-07T05:59:58.000000Z"; break;
            case "utc-eight": before["sourceObservedAtUtc"] = "2026-10-07T05:59:58.00000001Z"; break;
            case "utc-offset": before["sourceObservedAtUtc"] = "2026-10-07T06:59:58.0000001+01:00"; break;
            case "utc-24": before["sourceObservedAtUtc"] = "2026-10-07T24:00:00.0000001Z"; break;
            case "utc-leap-second": before["sourceObservedAtUtc"] = "2026-10-07T05:59:60.0000001Z"; break;
            case "utc-reversed-tick": after["sourceObservedAtUtc"] = Utc(Now.AddSeconds(-2)); break;
            case "utc-bracket-extra-tick": after["sourceObservedAtUtc"] = Utc(Now.AddSeconds(8).AddTicks(2)); break;
            case "local-offset": jobs[0]!["startExecutionLocal"] = "2026-11-01T01:30:00.1234567-04:00"; break;
            case "local-six": jobs[0]!["startExecutionLocal"] = "2026-11-01T01:30:00.123456"; break;
            case "local-24": jobs[0]!["startExecutionLocal"] = "2026-11-01T24:00:00.1234567"; break;
        }
        string receipt = value.ToJsonString(); if (mutation == "duplicate") receipt = receipt.Insert(1, "\"schemaVersion\":1,");
        Refuses(Rebind(evidence with { Receipt = receipt, Trx = Trx(receipt) }));
    }

    [Theory]
    [InlineData("failed")] [InlineData("case-substitution")] [InlineData("extra-case")]
    [InlineData("duplicate-receipt")] [InlineData("missing-receipt")] [InlineData("skip")] [InlineData("dtd")]
    [InlineData("definition-substitution")] [InlineData("definition-missing")] [InlineData("definition-extra")]
    [InlineData("method-substitution")] [InlineData("result-unlinked")] [InlineData("entry-unlinked")] [InlineData("execution-unlinked")]
    public void SelectedTrxMustBeExactlyOnePassingExpectedCaseAndOnePrivateReceipt(string mutation)
    {
        var evidence = Create(); XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var trx = XDocument.Parse(Encoding.UTF8.GetString(evidence.Trx)); var result = trx.Descendants(ns + "UnitTestResult").Single();
        switch (mutation)
        {
            case "failed": result.SetAttributeValue("outcome", "Failed"); break;
            case "case-substitution": result.SetAttributeValue("testName", DeniedCases[0]); break;
            case "extra-case": result.Parent!.Add(new XElement(result)); break;
            case "duplicate-receipt": result.Descendants(ns + "StdOut").Single().Value += "\n" + SqlAgentActivityNativeReplayInput.Marker + evidence.Receipt; break;
            case "missing-receipt": result.Descendants(ns + "StdOut").Single().Value = "No capture."; break;
            case "skip": trx.Descendants(ns + "Counters").Single().SetAttributeValue("notExecuted", "1"); break;
            case "definition-substitution": trx.Descendants(ns + "UnitTest").Single().SetAttributeValue("name", DeniedCases[0]); break;
            case "definition-missing": trx.Descendants(ns + "TestDefinitions").Single().Remove(); break;
            case "definition-extra": var definition = trx.Descendants(ns + "UnitTest").Single(); definition.Parent!.Add(new XElement(definition)); break;
            case "method-substitution": trx.Descendants(ns + "TestMethod").Single().SetAttributeValue("name", "OtherCase"); break;
            case "result-unlinked": result.SetAttributeValue("testId", Guid.NewGuid().ToString("D")); break;
            case "entry-unlinked": trx.Descendants(ns + "TestEntry").Single().SetAttributeValue("testId", Guid.NewGuid().ToString("D")); break;
            case "execution-unlinked": trx.Descendants(ns + "Execution").Single().SetAttributeValue("id", Guid.NewGuid().ToString("D")); break;
        }
        string text = trx.ToString(SaveOptions.DisableFormatting);
        if (mutation == "dtd") text = "<!DOCTYPE TestRun [<!ENTITY external SYSTEM 'file:///PRIVATE_SECRET'>]>" + text;
        Refuses(Rebind(evidence with { Trx = Encoding.UTF8.GetBytes(text) }));
    }

    [Theory]
    [InlineData(6)] [InlineData(13)]
    public void OriginalSourceOrAuthorizationExpiryCannotBeReissuedForLateReplay(int minutes)
    { var evidence = Create(); Assert.Throws<InvalidDataException>(() => Read(evidence, Now.AddMinutes(minutes))); }

    private sealed record Evidence(byte[] Sidecar, byte[] Report, byte[] Trx, byte[] Package, string Receipt, string Phase);
    private static Evidence Create(string phase = "signed", int sourceRows = 2)
    {
        var facts = new List<object>
        {
            new { Scope = "master", Kind = "definition", Key = "sha256", Value = SqlAgentModuleBinding.ApprovedDefinitionSha256 },
            new { Scope = "master", Kind = "definition", Key = "execute_as", Value = "CALLER" },
            new { Scope = "master", Kind = "definition", Key = "ansi", Value = "1:1" },
            new { Scope = "master", Kind = "marker", Key = "SqlObserver.AgentActivity.DeploymentId", Value = Deployment },
            new { Scope = "master", Kind = "signature_validity", Key = "module", Value = "1:1" },
            new { Scope = "master", Kind = "signature", Key = "62", Value = "SPVC:" + Thumb.ToUpperInvariant() },
        };
        foreach (string scope in new[] { "master", "msdb" })
        {
            facts.Add(new { Scope = scope, Kind = "certificate", Key = "public_sha256", Value = Pin });
            facts.Add(new { Scope = scope, Kind = "certificate", Key = "thumbprint", Value = Thumb.ToUpperInvariant() });
            facts.Add(new { Scope = scope, Kind = "certificate", Key = "private_key", Value = "NO_PRIVATE_KEY" });
        }
        byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Identity = new { ServerName = "private-sql", MachineName = "private-machine", InstanceName = "", ProductVersion = "17.0.1.0", MajorVersion = 17, EngineEdition = 2, PathSeparator = "\\", MasterDatabaseGuid = Target, MsdbDatabaseGuid = OriginalRun },
            Monitor = new { Principal = "PRIVATE\\Monitor", SidHex = "0104000000000005150000007B000000C8010000EA030000", LoginId = 50, MasterUserId = 51 },
            DeploymentId = Deployment,
            Objects = new[]
            {
                new { Kind = "owner_user", Database = "master", Name = "SqlObserverAgentActivitySchemaOwnerV1", Id = 60, OwnerId = (int?)1, Type = "S", Detail = "NONE" },
                new { Kind = "schema", Database = "master", Name = "sqlobserver_agent", Id = 61, OwnerId = (int?)60, Type = "SCHEMA", Detail = "" },
                new { Kind = "module", Database = "master", Name = "capture_v1", Id = 62, OwnerId = (int?)60, Type = "P", Detail = "" },
                new { Kind = "master_certificate", Database = "master", Name = "SqlObserverAgentActivityCertificateV1", Id = 63, OwnerId = (int?)1, Type = "CERTIFICATE", Detail = "" },
                new { Kind = "certificate_login", Database = "server", Name = "SqlObserverAgentActivityCertificateLoginV1", Id = 64, OwnerId = (int?)1, Type = "C", Detail = "ABCD:0:" },
                new { Kind = "msdb_certificate", Database = "msdb", Name = "SqlObserverAgentActivityCertificateV1", Id = 65, OwnerId = (int?)1, Type = "CERTIFICATE", Detail = "" },
                new { Kind = "certificate_user", Database = "msdb", Name = "SqlObserverAgentActivityCertificateUserV1", Id = 66, OwnerId = (int?)1, Type = "C", Detail = "ABCD" },
            },
            PackagePermissions = Array.Empty<object>(), MonitorPermissions = Array.Empty<object>(), Facts = facts,
        });
        string digest = Hash(snapshot);
        var binding = new { endpointHost = "private-sql", tcpPort = 1433, certificateHost = "private-cert", serverName = "private-sql", machineName = "private-machine", instanceName = "", objectId = 62, certificateId = 63,
            definitionSha256 = SqlAgentModuleBinding.ApprovedDefinitionSha256, publicCertificateSha256 = Pin, thumbprintHex = Thumb, moduleVersion = 1, sourceVersion = 2, probeVersion = 1, installationPolicyVersion = 2 };
        using var snapshotJson = JsonDocument.Parse(snapshot);
        byte[] sidecar = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, diagnosticOnly = true, deploymentId = Deployment, targetId = Target, targetRevision = 1,
            authorization = new { targetId = Target, targetRevision = 1, enrollmentRevision = 1, enrollmentId = Deployment, expectedWindowsSid = Sid, notBeforeUtc = Now.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture), expiresAtUtc = Now.AddMinutes(12).ToString("O", CultureInfo.InvariantCulture), evidenceSha256 = digest, binding },
            installedEvidenceSha256 = digest, installedSnapshot = snapshotJson.RootElement });
        object Header(DateTimeOffset observed) => new { sourceObservedAtUtc = Utc(observed), service = new { matchingServiceCount = 1, status = 1, processId = 0, startupAtUtc = (string?)null }, session = new { sessionId = 9, agentStartLocal = "2026-11-01T00:00:00.0000000" } };
        string receipt = JsonSerializer.Serialize(new { schemaVersion = 1, diagnosticOnly = true, actualMonitorCollector = true, repositoryResolverQualified = false, productionAdmitted = false, serviceSessionAssociationQualified = false, nativeQualification = "Absent", targetId = Target, targetRevision = 1, runId = OriginalRun, collectorId = "sql-agent.activity-observations", collectorManifestVersion = 1, outputSchemaVersion = 1,
            outcome = sourceRows > 512 ? "partial" : "succeeded", reason = sourceRows > 512 ? "source_row_limit" : "completed",
            accounting = new { sourceRowsRead = sourceRows, outputItemsProduced = 1, responseBytes = 4096 + sourceRows * 768, outputBytes = 4096 + Math.Min(sourceRows, 512) * 768 },
            loss = new { kind = sourceRows > 512 ? "source_row_limit" : "none", minimumLostItems = sourceRows > 512 ? 1 : 0, countIsExact = sourceRows <= 512, minimumLostBytes = 0 },
            observation = new { sourceSchemaVersion = 1, enrollmentRevision = 1, enrollmentId = Deployment, evidenceSha256 = digest, before = Header(Now.AddSeconds(-2).AddTicks(1)), after = Header(Now.AddSeconds(-1).AddTicks(2)),
                jobs = Enumerable.Range(1, Math.Min(sourceRows, 512)).Select(index => new { jobId = "44444444-4444-4444-8444-" + index.ToString("000000000000", CultureInfo.InvariantCulture), sessionId = 9, requestedLocal = "2026-11-01T01:30:00.1234567", queuedLocal = (string?)null, startExecutionLocal = "2026-11-01T01:30:00.1234567", stopExecutionLocal = "2026-11-01T00:30:00.1234567", lastExecutedStepId = 2, lastExecutedStepLocal = "2026-11-01T01:30:00.1234567" }).ToArray(),
                sourceRowsRead = sourceRows, truncated = sourceRows > 512, productionAdmitted = false, serviceSessionAssociationQualified = false, nativeQualification = "Absent" } });
        byte[] package = JsonSerializer.SerializeToUtf8Bytes(new { createdAtUtc = Utc(Now.AddMinutes(-10)), archiveSha256 = Pin, fileCount = InputNames.Length, inputs = InputNames.ToDictionary(name => name, _ => Pin), head = new string('a', 40), status = "Synthetic contract fixture only; no native qualification" });
        byte[] trx = Trx(receipt);
        object Phase(int count, string hash) => new { nativeTestsExecuted = count, nativeTestsPassed = count, nativeTestsSkipped = 0, exactNativeCasesVerified = true, actualServiceMonitorIdentityVerified = true, pipeClientIdentityVerified = true, trxSha256 = hash, serviceStopped = true, serviceDeletionVerified = true, directChildExited = true, childExitCode = 0, workerReceiptReceived = true, nativeLaunchStarted = true, nativeExecutionMayHaveStarted = true, serviceCreated = true, serviceStartAccepted = true, serviceDeleteRequested = true, success = true, timedOut = false, diagnosticOnly = false, nativeChildProcessId = 12345, nativeChildProcessStartedUtc = Utc(Now.AddSeconds(-4)) };
        string signedHash = phase == "signed" ? Hash(trx) : new string('c', 64), restoredHash = phase == "restored" ? Hash(trx) : new string('d', 64), deniedHash = new string('e', 64);
        var runner = new { schemaVersion = 1, runnerProcessId = 12344, runnerProcessStartedUtc = Utc(Now.AddSeconds(-5)), testContract = "agent-production-explicit-deny-original-authorization-v1", deploymentId = Deployment, installationPolicyVersion = 2, journalSchemaVersion = 4, enrollmentEvidenceSha256 = digest,
            enrollmentEvidenceFormat = "System.Text.Json default serialization of validated installed TrialSnapshot", authorizationSidecarSha256 = Hash(sidecar), authorizationSidecarSchemaVersion = 1, authorizationSidecarBytes = sidecar.Length,
            workerTerminationConfirmed = true, exactNativeCasesVerified = true, actualServiceMonitorIdentityVerified = true, success = true, explicitDenyQualified = true, restoredSignedCollectorQualified = true, trustedIdentityFromValidatedBaseline = true, trustedMetadataFromValidatedInstalledReceipt = true, authorizationSidecarWrittenBeforeMonitorLaunch = true, authorizationSidecarReadbackVerified = true,
            existingServiceAdmissionChanged = false, sqlRecoveryBlockedWorkerTerminationUnconfirmed = false, preflightOnly = false, attachedInvalidSignatureQualified = false, diagnosticOnly = false, timedOut = false,
            workerTerminationFenceVersion = 1, workerLaunchPhaseCount = 3, nativeTestsExecuted = 4, nativeTestsPassed = 4, nativeTestsSkipped = 0, explicitDenyScope = "msdb.syssessions.select.certificate-user",
            trialResult = new { DeploymentId = Deployment, QualificationPassed = true, CleanupVerified = true, FailureCode = (string?)null }, signedPhase = Phase(1, signedHash), deniedPhase = Phase(2, deniedHash), restoredPhase = Phase(1, restoredHash) };
        object TrxProof(string hash, string[] cases) => new { exactCasesPassed = true, passed = cases.Length, skipped = 0, executed = cases.Length, sha256 = hash, expectedCases = cases };
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new { operation = "Run", nativeSqlQualificationRequested = true, completed = true, remoteSourceVerified = true, qualificationPassed = true, cleanupVerified = true, nativeProcessDaclChanged = false, packageSha256 = Pin,
            privateAuthorizationSidecar = new { sha256 = Hash(sidecar), bytes = sidecar.Length, originalAuthorizationPreserved = true, localArtifact = "synthetic/native-enrollment-v1.json" },
            sourceBefore = new { verifiedFileCount = InputNames.Length, allInputsUnchanged = true }, sourceAfter = new { verifiedFileCount = InputNames.Length, allInputsUnchanged = true }, runnerSourceBefore = new { verifiedFileCount = 5, allInputsUnchanged = true, exactFileSet = true }, runnerSourceAfter = new { verifiedFileCount = 5, allInputsUnchanged = true, exactFileSet = true },
            reviewedRunnerInputs = RunnerNames.Select(name => new { path = name, digest = Pin }).ToArray(), sourceSha256 = new Dictionary<string, string> { ["guest-native-run.ps1"] = Pin, ["inspect-native-sql.ps1"] = Pin, ["invoke-native-sql.ps1"] = Pin },
            actionResult = new { normalQualificationPassed = true, cleanupVerified = true, success = true, testAssemblyStableBeforeLaunch = true, runnerLaunchStarted = true, firstProcessIdentityVerified = true, workerTerminationFenceVerified = true, normalCleanupVerified = true, fallbackRecoveryAttempted = false, stderrNonEmpty = false, testBuildExitCode = 0, buildExitCode = 0, runnerExitCode = 0, nativeTestsExecuted = 4, testAssemblySha256 = Pin, runnerAssemblySha256 = Pin, runnerSourceSha256 = RunnerNames.ToDictionary(name => name, _ => Pin),
                firstProcess = new { stderrJoined = true, exitConfirmed = true, stdoutJoined = true, metadataParsed = true, timedOut = false, stderrNonEmpty = false, exitCode = 0, processId = 12344, processStartedUtc = Utc(Now.AddSeconds(-5)), processExitedUtc = Utc(Now), stdoutSha256 = Pin, stderrSha256 = Pin, metadata = runner }, runner,
                signedTrx = TrxProof(signedHash, [SqlAgentActivityNativeReplayInput.CollectorCase]), deniedTrx = TrxProof(deniedHash, DeniedCases), restoredTrx = TrxProof(restoredHash, [SqlAgentActivityNativeReplayInput.CollectorCase]),
                postRunAudit = new { observation = "Synthetic contract fixture only", qualificationServices = Array.Empty<object>(), existingServices = new[] { new { Name = "MSSQLSERVER", State = "Running" } }, agentServiceSnapshot = new[] { new { Name = "SQLSERVERAGENT", State = "Stopped" } }, qualificationProcesses = Array.Empty<object>(), observedAtUtc = Utc(Now) } } });
        return new(sidecar, report, trx, package, receipt, phase);
    }
    private static Evidence Rebind(Evidence evidence)
    {
        var report = JsonNode.Parse(evidence.Report)!.AsObject(); var action = report["actionResult"]!.AsObject(); var runner = action["firstProcess"]!["metadata"]!.AsObject();
        runner["authorizationSidecarSha256"] = Hash(evidence.Sidecar); runner["authorizationSidecarBytes"] = evidence.Sidecar.Length;
        report["privateAuthorizationSidecar"]!["sha256"] = Hash(evidence.Sidecar); report["privateAuthorizationSidecar"]!["bytes"] = evidence.Sidecar.Length;
        if (evidence.Phase is "signed" or "restored") { runner[evidence.Phase + "Phase"]!["trxSha256"] = Hash(evidence.Trx); action[evidence.Phase + "Trx"]!["sha256"] = Hash(evidence.Trx); }
        action["runner"] = runner.DeepClone(); return evidence with { Report = Encoding.UTF8.GetBytes(report.ToJsonString()) };
    }
    private static byte[] Trx(string receipt)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        const string testId = "55555555-5555-4555-8555-555555555555", executionId = "66666666-6666-4666-8666-666666666666";
        int separator = SqlAgentActivityNativeReplayInput.CollectorCase.LastIndexOf('.');
        var document = new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", new XElement(ns + "UnitTestResult",
            new XAttribute("testId", testId), new XAttribute("executionId", executionId),
            new XAttribute("testName", SqlAgentActivityNativeReplayInput.CollectorCase), new XAttribute("outcome", "Passed"),
            new XElement(ns + "Output", new XElement(ns + "StdOut", SqlAgentActivityNativeReplayInput.Marker + receipt)))),
            new XElement(ns + "TestDefinitions", new XElement(ns + "UnitTest", new XAttribute("id", testId), new XAttribute("name", SqlAgentActivityNativeReplayInput.CollectorCase),
                new XElement(ns + "Execution", new XAttribute("id", executionId)), new XElement(ns + "TestMethod", new XAttribute("className", SqlAgentActivityNativeReplayInput.CollectorCase[..separator]), new XAttribute("name", SqlAgentActivityNativeReplayInput.CollectorCase[(separator + 1)..])))),
            new XElement(ns + "TestEntries", new XElement(ns + "TestEntry", new XAttribute("testId", testId), new XAttribute("executionId", executionId))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"), new XElement(ns + "Counters", new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", 1), new XAttribute("failed", 0), new XAttribute("notExecuted", 0)))));
        return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
    }
    private static SqlAgentActivityNativeReplayHashes Digests(Evidence evidence) => new(Hash(evidence.Sidecar), Hash(evidence.Report), Hash(evidence.Trx), Hash(evidence.Package));
    private static SqlAgentActivityNativeReplayInput Read(Evidence evidence, DateTimeOffset? now = null) => SqlAgentActivityNativeReplayInput.Parse(evidence.Sidecar, evidence.Report, evidence.Trx, evidence.Package, evidence.Phase, Digests(evidence), now ?? Now);
    private static void Refuses(Evidence evidence)
    {
        var failure = Assert.Throws<InvalidDataException>(() => Read(evidence)); Assert.Null(failure.InnerException);
        Assert.DoesNotContain("PRIVATE_SECRET", failure.Message, StringComparison.Ordinal); Assert.DoesNotContain(Sid, failure.Message, StringComparison.Ordinal);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Utc(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
