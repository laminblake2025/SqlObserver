using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace SqlObserver.AgentActivityTrial.Tests;

public sealed class TrialVisibilityReceiptTests
{
    private const string ClassName = "SqlObserver.IntegrationTests.SqlServer.SqlAgentActivitySignedModuleQualificationTests";
    private const string Marker = "SQL_AGENT_MODULE_PRIVATE_RECEIPT_V1:";
    private const string CaptureMethod = "ActualMonitorCapturesSignedModuleThroughProductionParser";
    private static readonly string[] Methods = [CaptureMethod, "ActualMonitorRejectsNullAndNoncanonicalModuleBounds", "PreCancelledModuleSourceExecutesNoCommand"];
    private static readonly Guid Target = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Run = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Earliest = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    [Fact]
    public void ExactPassedReceiptPreservesEveryTupleAndOffsetFreeClockEvenWhenLocalDatesReverse()
    {
        var receipt = Parse(Document());
        Assert.Equal(Target, receipt.TargetId); Assert.Equal(2, receipt.TargetRevision); Assert.Equal(Run, receipt.RunId);
        Assert.NotEqual(Guid.Empty, receipt.TrxRunId);
        Assert.Equal(2, receipt.Capture.SourceRowsRead); Assert.False(receipt.Capture.Truncated);
        Assert.Equal(4, receipt.Capture.BeforeAgentSessionId); Assert.Equal(receipt.Capture.BeforeAgentSessionId, receipt.Capture.AfterAgentSessionId);
        var job = receipt.Capture.Jobs[0];
        Assert.Equal(Guid.Parse("30000000-0000-0000-0000-000000000003"), job.JobId);
        Assert.Equal(4, job.AgentSessionId); Assert.Equal(2, job.LastExecutedStepId);
        Assert.Equal(new DateTime(2026, 9, 14, 20, 54, 24, DateTimeKind.Unspecified), job.RequestedLocal);
        Assert.Null(job.QueuedLocal); Assert.True(job.StopExecutionLocal < job.StartExecutionLocal);
        Assert.Equal(DateTimeKind.Unspecified, job.LastExecutedStepLocal!.Value.Kind);
        Assert.Null(receipt.Capture.Jobs[1].StartExecutionLocal);
    }

    [Fact]
    public void DigestBindsExactlyTheParsedBytesIncludingXmlWhitespace()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Document().ToString());
        var receipt = TrialVisibilityReceipt.ParseTrx(bytes, Target, 2, Earliest, Earliest.AddMinutes(1));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), receipt.TrxSha256);
    }

    [Fact]
    public void CallerIdentityAndCurrentSourceClockEnvelopeAreRequired()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Document().ToString());
        Assert.Throws<InvalidDataException>(() => TrialVisibilityReceipt.ParseTrx(bytes, Guid.NewGuid(), 2, Earliest, Earliest.AddMinutes(1)));
        Assert.Throws<InvalidDataException>(() => TrialVisibilityReceipt.ParseTrx(bytes, Target, 3, Earliest, Earliest.AddMinutes(1)));
        Assert.Throws<InvalidDataException>(() => TrialVisibilityReceipt.ParseTrx(bytes, Target, 2, Earliest.AddMinutes(1), Earliest.AddMinutes(2)));
        Assert.Throws<InvalidDataException>(() => TrialVisibilityReceipt.ParseTrx(bytes, Target, 2, Earliest, Earliest));
        Assert.Throws<InvalidDataException>(() => TrialVisibilityReceipt.ParseTrx(bytes, Target, 2, Earliest.ToOffset(TimeSpan.FromHours(1)), Earliest.AddMinutes(1)));
    }

    [Fact]
    public void FileAccessFailuresDoNotDisclosePrivatePaths()
    {
        var error = Assert.Throws<InvalidDataException>(() => TrialVisibilityReceipt.ReadTrx("\0PRIVATE_CANARY", Target, 2, Earliest, Earliest.AddMinutes(1)));
        Assert.Equal("Native visibility receipt is invalid.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("revision")]
    [InlineData("run")]
    [InlineData("schema")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("diagnostic")]
    [InlineData("monitor-definition")]
    [InlineData("admission")]
    [InlineData("association")]
    [InlineData("qualification")]
    [InlineData("extra-root")]
    [InlineData("extra-capture")]
    [InlineData("missing-row")]
    [InlineData("duplicate-row")]
    [InlineData("row-order")]
    [InlineData("row-session")]
    [InlineData("row-extra")]
    [InlineData("row-field-omitted")]
    [InlineData("row-local-offset")]
    [InlineData("row-local-date-only")]
    [InlineData("row-local-overprecision")]
    [InlineData("row-local-year")]
    [InlineData("negative-step")]
    [InlineData("session-drift")]
    [InlineData("session-start-drift")]
    [InlineData("no-session")]
    [InlineData("service-running")]
    [InlineData("service-ambiguous")]
    [InlineData("service-live-pid")]
    [InlineData("reversed-clock")]
    [InlineData("long-bracket")]
    [InlineData("stale-clock")]
    [InlineData("non-utc-clock")]
    [InlineData("truncated")]
    [InlineData("accounting")]
    public void MalformedOrContradictoryPrivateEnvelopeIsRejected(string mutation)
    {
        JsonObject receipt = Receipt(); JsonNode capture = receipt["capture"]!;
        JsonArray jobs = capture["jobs"]!.AsArray();
        switch (mutation)
        {
            case "target": capture["targetId"]!["value"] = Guid.NewGuid(); break;
            case "revision": capture["targetRevision"]!["value"] = 3; break;
            case "run": capture["runId"]!["value"] = Guid.Empty; break;
            case "schema": receipt["schemaVersion"] = 2; break;
            case "version": receipt["sourceAssetVersion"] = 1; break;
            case "hash": receipt["moduleDefinitionEnrollmentHash"] = new string('a', 64); break;
            case "diagnostic": receipt["diagnosticOnly"] = false; break;
            case "monitor-definition": receipt["databaseDefinitionVerifiedByMonitor"] = true; break;
            case "admission": capture["productionAdmitted"] = true; break;
            case "association": capture["serviceSessionAssociationQualified"] = true; break;
            case "qualification": capture["nativeQualification"] = 1; break;
            case "extra-root": receipt["private"] = "PRIVATE_CANARY"; break;
            case "extra-capture": capture["elapsed"] = 100; break;
            case "missing-row": jobs.RemoveAt(1); break;
            case "duplicate-row": jobs[1] = jobs[0]!.DeepClone(); break;
            case "row-order": var first = jobs[0]!.DeepClone(); jobs[0] = jobs[1]!.DeepClone(); jobs[1] = first; break;
            case "row-session": jobs[0]!["sessionId"] = 5; break;
            case "row-extra": jobs[0]!["jobName"] = "PRIVATE_CANARY"; break;
            case "row-field-omitted": jobs[0]!.AsObject().Remove("queuedLocal"); break;
            case "row-local-offset": jobs[0]!["requestedLocal"] = "2026-09-14T20:54:24Z"; break;
            case "row-local-date-only": jobs[0]!["requestedLocal"] = "2026-09-14"; break;
            case "row-local-overprecision": jobs[0]!["requestedLocal"] = "2026-09-14T20:54:24.12345678"; break;
            case "row-local-year": jobs[0]!["requestedLocal"] = "1752-09-14T20:54:24"; break;
            case "negative-step": jobs[0]!["lastExecutedStepId"] = -1; break;
            case "session-drift": capture["after"]!["session"]!["sessionId"] = 5; break;
            case "session-start-drift": capture["after"]!["session"]!["agentStartLocal"] = "2026-09-15T20:52:46.86"; break;
            case "no-session": capture["before"]!["session"]!["sessionId"] = null; break;
            case "service-running": capture["after"]!["service"]!["status"] = 4; break;
            case "service-ambiguous": capture["before"]!["service"]!["matchingServiceCount"] = 2; break;
            case "service-live-pid": capture["after"]!["service"]!["processId"] = 42; break;
            case "reversed-clock": capture["after"]!["sourceObservedAtUtc"] = "2026-10-05T08:00:00+00:00"; break;
            case "long-bracket": capture["after"]!["sourceObservedAtUtc"] = "2026-10-05T08:00:20+00:00"; break;
            case "stale-clock": capture["before"]!["sourceObservedAtUtc"] = "2026-10-05T07:59:59+00:00"; break;
            case "non-utc-clock": capture["before"]!["sourceObservedAtUtc"] = "2026-10-05T09:00:01+01:00"; break;
            case "truncated": capture["truncated"] = true; break;
            case "accounting": capture["sourceRowsRead"] = 1; break;
        }
        Invalid(Document(receipt));
    }

    [Theory]
    [InlineData("actualMonitorIdentityVerified")]
    [InlineData("ownedIntegratedTlsSessionStable")]
    [InlineData("originalDiscoveryProfilePreserved")]
    [InlineData("directSessionsSelectGranted")]
    [InlineData("directSecurityStateGranted")]
    [InlineData("productionAdmitted")]
    [InlineData("fullForeignJobVisibilityQualified")]
    [InlineData("longRunningQualified")]
    public void SanitizedEvidenceMustRemainConsistentWithPrivateReceipt(string flag)
    {
        var evidence = Evidence(); var details = evidence["evidence"]!;
        details[flag] = !details[flag]!.GetValue<bool>();
        Invalid(Document(evidence: evidence));
    }

    [Theory]
    [InlineData("failed-case")]
    [InlineData("skipped-case")]
    [InlineData("wrong-class")]
    [InlineData("wrong-method")]
    [InlineData("disconnected-test-id")]
    [InlineData("disconnected-execution-id")]
    [InlineData("missing-case")]
    [InlineData("extra-case")]
    [InlineData("counter-failure")]
    [InlineData("counter-skip")]
    [InlineData("wrong-case-receipt")]
    [InlineData("duplicate-receipt")]
    [InlineData("summary-receipt")]
    [InlineData("duplicate-json")]
    [InlineData("duplicate-evidence-json")]
    [InlineData("oversized-json")]
    [InlineData("extra-stdout")]
    public void TrxCaseLinkageAndBoundariesFailClosed(string mutation)
    {
        XDocument document = Document(); var root = document.Root!;
        var result = root.Element(Ns + "Results")!.Elements().First();
        var stdout = result.Element(Ns + "Output")!.Element(Ns + "StdOut")!;
        switch (mutation)
        {
            case "failed-case": result.SetAttributeValue("outcome", "Failed"); break;
            case "skipped-case": result.SetAttributeValue("outcome", "NotExecuted"); break;
            case "wrong-class": root.Descendants(Ns + "TestMethod").First().SetAttributeValue("className", "OtherClass"); break;
            case "wrong-method": root.Descendants(Ns + "TestMethod").First().SetAttributeValue("name", "OtherMethod"); break;
            case "disconnected-test-id": result.SetAttributeValue("testId", Guid.NewGuid()); break;
            case "disconnected-execution-id": result.SetAttributeValue("executionId", Guid.NewGuid()); break;
            case "missing-case": result.Remove(); break;
            case "extra-case": result.Parent!.Add(new XElement(result)); break;
            case "counter-failure": root.Descendants(Ns + "Counters").Single().SetAttributeValue("failed", 1); break;
            case "counter-skip": root.Descendants(Ns + "Counters").Single().SetAttributeValue("notExecuted", 1); break;
            case "wrong-case-receipt": root.Element(Ns + "Results")!.Elements().Last().Add(new XElement(result.Element(Ns + "Output")!)); stdout.Value = "unrelated"; break;
            case "duplicate-receipt": stdout.Value += "\n" + Marker + Receipt().ToJsonString(); break;
            case "summary-receipt": root.Element(Ns + "ResultSummary")!.Add(new XElement(Ns + "Output", Marker)); break;
            case "duplicate-json": stdout.Value = stdout.Value.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal); break;
            case "duplicate-evidence-json": stdout.Value = stdout.Value.Replace("\"actualMonitorIdentityVerified\":true", "\"actualMonitorIdentityVerified\":true,\"actualMonitorIdentityVerified\":true", StringComparison.Ordinal); break;
            case "oversized-json": stdout.Value += new string(' ', TrialVisibilityReceipt.MaximumReceiptBytes); break;
            case "extra-stdout": stdout.Value += "\nPRIVATE_CANARY"; break;
        }
        Invalid(document);
    }

    [Fact]
    public void XmlDtdAndOversizedDocumentAreRejectedWithoutLeakingDetails()
    {
        InvalidBytes(Encoding.UTF8.GetBytes("<!DOCTYPE TestRun [<!ENTITY secret SYSTEM 'file:///PRIVATE_CANARY'>]><TestRun>&secret;</TestRun>"));
        InvalidBytes(new byte[TrialVisibilityReceipt.MaximumTrxBytes + 1]);
        InvalidBytes(Encoding.UTF8.GetBytes("<PRIVATE_CANARY>"));
    }

    private static void Invalid(XDocument document) => InvalidBytes(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
    private static void InvalidBytes(byte[] bytes)
    {
        var error = Assert.Throws<InvalidDataException>(() => TrialVisibilityReceipt.ParseTrx(bytes, Target, 2, Earliest, Earliest.AddMinutes(1)));
        Assert.Equal("Native visibility receipt is invalid.", error.Message); Assert.Null(error.InnerException);
    }
    private static TrialVisibilityReceipt Parse(XDocument document) => TrialVisibilityReceipt.ParseTrx(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)), Target, 2, Earliest, Earliest.AddMinutes(1));
    private static JsonObject Receipt()
    {
        object Header(string utc) => new { sourceObservedAtUtc = utc, service = new { matchingServiceCount = 1, status = 1, processId = (int?)null, startupAtUtc = (string?)null }, session = new { sessionId = 4, agentStartLocal = "2026-09-14T20:52:46.86" } };
        return JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1, diagnosticOnly = true, sourceAssetVersion = 2, moduleDefinitionEnrollmentHash = TrialVisibilityReceipt.DefinitionHash,
            databaseDefinitionVerifiedByMonitor = false,
            capture = new
            {
                targetId = new { value = Target }, targetRevision = new { value = 2L }, runId = new { value = Run },
                before = Header("2026-10-05T08:00:01+00:00"), after = Header("2026-10-05T08:00:01.1234567+00:00"),
                jobs = new[]
                {
                    new { jobId = "30000000-0000-0000-0000-000000000003", sessionId = 4, requestedLocal = (string?)"2026-09-14T20:54:24", queuedLocal = (string?)null,
                        startExecutionLocal = (string?)"2026-09-14T20:54:24", stopExecutionLocal = (string?)"2026-09-14T20:53:24", lastExecutedStepId = (int?)2, lastExecutedStepLocal = (string?)"2026-09-14T20:54:24" },
                    new { jobId = "40000000-0000-0000-0000-000000000004", sessionId = 4, requestedLocal = (string?)null, queuedLocal = (string?)null,
                        startExecutionLocal = (string?)null, stopExecutionLocal = (string?)null, lastExecutedStepId = (int?)null, lastExecutedStepLocal = (string?)null },
                },
                sourceRowsRead = 2, truncated = false, nativeQualification = 0, productionAdmitted = false, serviceSessionAssociationQualified = false,
            },
        })!.AsObject();
    }
    private static JsonObject Evidence() => JsonSerializer.SerializeToNode(new
    {
        stage = "signed_module_capture", evidence = new
        {
            actualMonitorIdentityVerified = true, ownedIntegratedTlsSessionStable = true, originalDiscoveryProfilePreserved = true,
            directSecurityStateGranted = false, directSessionsSelectGranted = false, directActivitySelectGranted = true, directSessionsSelectError = 229,
            sourceRowsRead = 2, retainedRows = 2, truncated = false, gap = "ServiceNotRunning", moduleDefinitionEnrollmentMatched = true,
            monitorDefinitionVerified = false, signatureVerifiedByMonitor = false, databaseDefinitionAndSignatureVerification = "separate_DBA_evidence_required",
            fullForeignJobVisibilityQualified = false, productionAdmitted = false, serviceSessionAssociationQualified = false, elapsedQualified = false, longRunningQualified = false,
        },
    })!.AsObject();
    private static XDocument Document(JsonObject? receipt = null, JsonObject? evidence = null)
    {
        var results = new XElement(Ns + "Results"); var definitions = new XElement(Ns + "TestDefinitions"); var entries = new XElement(Ns + "TestEntries");
        foreach (string method in Methods)
        {
            string testId = Guid.NewGuid().ToString("D"), execution = Guid.NewGuid().ToString("D"), name = ClassName + "." + method;
            var result = new XElement(Ns + "UnitTestResult", new XAttribute("testId", testId), new XAttribute("executionId", execution), new XAttribute("testName", name), new XAttribute("outcome", "Passed"));
            if (method == CaptureMethod) result.Add(new XElement(Ns + "Output", new XElement(Ns + "StdOut", (evidence ?? Evidence()).ToJsonString() + "\n" + Marker + (receipt ?? Receipt()).ToJsonString())));
            results.Add(result);
            definitions.Add(new XElement(Ns + "UnitTest", new XAttribute("id", testId), new XAttribute("name", name), new XElement(Ns + "Execution", new XAttribute("id", execution)),
                new XElement(Ns + "TestMethod", new XAttribute("className", ClassName), new XAttribute("name", method))));
            entries.Add(new XElement(Ns + "TestEntry", new XAttribute("testId", testId), new XAttribute("executionId", execution)));
        }
        var counters = new XElement(Ns + "Counters");
        foreach (string name in new[] { "total", "executed", "passed" }) counters.Add(new XAttribute(name, 3));
        foreach (string name in new[] { "failed", "error", "timeout", "aborted", "notExecuted", "notRunnable", "inconclusive" }) counters.Add(new XAttribute(name, 0));
        return new(new XElement(Ns + "TestRun", new XAttribute("id", Guid.NewGuid()), results, definitions, entries,
            new XElement(Ns + "ResultSummary", new XAttribute("outcome", "Completed"), counters)));
    }
}
