using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SqlObserver.AgentActivityTrial;

/// <summary>Private, bounded evidence from the exact enrolled monitor test. This is not production admission.
/// The caller supplies the current DBA SQL-clock bracket and a fresh worker-owned TRX path.</summary>
public sealed record TrialVisibilityReceipt(Guid TargetId, long TargetRevision, Guid RunId, Guid TrxRunId, string TrxSha256,
    TrialVisibilityCapture Capture)
{
    public const int MaximumTrxBytes = 2 * 1024 * 1024;
    public const int MaximumReceiptBytes = 32768;
    public const string DefinitionHash = "447846fb7a7eaa0442b2292b17b82ee17aaf998c3b91c161cba403f7c0bf8512";
    private const string Marker = "SQL_AGENT_MODULE_PRIVATE_RECEIPT_V1:";
    private const string ClassName = "SqlObserver.IntegrationTests.SqlServer.SqlAgentActivitySignedModuleQualificationTests";
    private const string CaptureMethod = "ActualMonitorCapturesSignedModuleThroughProductionParser";
    private static readonly string[] Methods = [CaptureMethod, "ActualMonitorRejectsNullAndNoncanonicalModuleBounds", "PreCancelledModuleSourceExecutesNoCommand"];
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TrialVisibilityReceipt ReadTrx(string path, Guid expectedTargetId, long expectedTargetRevision,
        DateTimeOffset earliestUtc, DateTimeOffset latestUtc)
    {
        try
        {
            // Limit before allocation, and deny concurrent writers while reading this one artifact.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Require(stream.Length is > 0 and <= MaximumTrxBytes);
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return ParseTrx(bytes, expectedTargetId, expectedTargetRevision, earliestUtc, latestUtc);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw Invalid(); }
    }

    public static TrialVisibilityReceipt ParseTrx(ReadOnlyMemory<byte> bytes, Guid expectedTargetId,
        long expectedTargetRevision, DateTimeOffset earliestUtc, DateTimeOffset latestUtc)
    {
        try
        {
            Require(bytes.Length is > 0 and <= MaximumTrxBytes && expectedTargetId != Guid.Empty && expectedTargetRevision > 0);
            Require(earliestUtc.Offset == TimeSpan.Zero && latestUtc.Offset == TimeSpan.Zero && earliestUtc <= latestUtc);
            byte[] snapshot = bytes.ToArray();
            using var stream = new MemoryStream(snapshot, writable: false);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumTrxBytes, MaxCharactersFromEntities = 0,
            });
            XDocument document = XDocument.Load(xml, LoadOptions.None);
            XElement root = document.Root ?? throw Invalid();
            Require(root.Name == Ns + "TestRun");
            Guid trxId = Id(Attribute(root, "id"));
            var definitions = One(root, "TestDefinitions").Elements().ToArray();
            var results = One(root, "Results").Elements().ToArray();
            var entries = One(root, "TestEntries").Elements().ToArray();
            Require(definitions.Length == 3 && results.Length == 3 && entries.Length == 3);
            Require(root.Descendants(Ns + "UnitTestResult").Count() == 3);
            var identities = new HashSet<Guid>(); var executions = new HashSet<Guid>();
            var foundMethods = new HashSet<string>(StringComparer.Ordinal);
            XElement? captureResult = null;
            foreach (var definition in definitions)
            {
                Require(definition.Name == Ns + "UnitTest");
                Guid testId = Id(Attribute(definition, "id"));
                Guid executionId = Id(Attribute(One(definition, "Execution"), "id"));
                XElement method = One(definition, "TestMethod");
                string name = Attribute(method, "name");
                Require(Attribute(method, "className") == ClassName && Methods.Contains(name, StringComparer.Ordinal));
                Require(identities.Add(testId) && executions.Add(executionId) && foundMethods.Add(name));
                string fullName = ClassName + "." + name;
                Require(Attribute(definition, "name") == fullName);
                var matching = results.Where(r => Attribute(r, "testId") == testId.ToString("D")).ToArray();
                Require(matching.Length == 1);
                var result = matching[0];
                Require(result.Name == Ns + "UnitTestResult" && Attribute(result, "outcome") == "Passed" &&
                    Attribute(result, "testName") == fullName && Id(Attribute(result, "executionId")) == executionId);
                Require(entries.Count(e => e.Name == Ns + "TestEntry" && Attribute(e, "testId") == testId.ToString("D") &&
                    Attribute(e, "executionId") == executionId.ToString("D")) == 1);
                if (name == CaptureMethod) captureResult = result;
            }
            var summary = One(root, "ResultSummary");
            Require(Attribute(summary, "outcome") == "Completed");
            var counters = One(summary, "Counters");
            foreach (string name in new[] { "total", "executed", "passed" }) Require(Attribute(counters, name) == "3");
            foreach (var attribute in counters.Attributes().Where(a => a.Name.LocalName is not ("total" or "executed" or "passed")))
                Require(attribute.Value == "0");
            foreach (string name in new[] { "failed", "error", "timeout", "aborted", "notExecuted", "notRunnable", "inconclusive" })
                Require(Attribute(counters, name) == "0");
            Require(document.DescendantNodes().OfType<XText>().Sum(t => Occurrences(t.Value, Marker)) == 1);
            Require(captureResult is not null);
            XElement stdout = One(One(captureResult!, "Output"), "StdOut");
            Require(!stdout.HasElements);
            string[] lines = stdout.Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Require(lines.Length == 2 && lines[1].StartsWith(Marker, StringComparison.Ordinal));
            string json = lines[1][Marker.Length..];
            Require(Encoding.UTF8.GetByteCount(json) <= MaximumReceiptBytes);
            using var receipt = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            JsonElement envelope = receipt.RootElement;
            Shape(envelope, "schemaVersion", "diagnosticOnly", "sourceAssetVersion", "moduleDefinitionEnrollmentHash", "databaseDefinitionVerifiedByMonitor", "capture");
            Require(Int(envelope, "schemaVersion") == 1 && Int(envelope, "sourceAssetVersion") == 2 && Bool(envelope, "diagnosticOnly") &&
                !Bool(envelope, "databaseDefinitionVerifiedByMonitor") && Text(envelope, "moduleDefinitionEnrollmentHash") == DefinitionHash);
            JsonElement capture = envelope.GetProperty("capture");
            Shape(capture, "targetId", "targetRevision", "runId", "before", "after", "jobs", "sourceRowsRead", "truncated", "nativeQualification", "productionAdmitted", "serviceSessionAssociationQualified");
            JsonElement target = capture.GetProperty("targetId"), revision = capture.GetProperty("targetRevision"), run = capture.GetProperty("runId");
            Shape(target, "value"); Shape(revision, "value"); Shape(run, "value");
            Require(Id(Text(target, "value")) == expectedTargetId && revision.GetProperty("value").GetInt64() == expectedTargetRevision);
            Guid runId = Id(Text(run, "value"));
            Require(Int(capture, "nativeQualification") == 0 && !Bool(capture, "productionAdmitted") && !Bool(capture, "serviceSessionAssociationQualified"));
            var before = Header(capture.GetProperty("before")); var after = Header(capture.GetProperty("after"));
            Require(before.Utc >= earliestUtc && after.Utc <= latestUtc && after.Utc >= before.Utc && after.Utc - before.Utc <= TimeSpan.FromSeconds(10));
            Require(before.SessionId == after.SessionId && before.Started == after.Started);
            JsonElement jobsElement = capture.GetProperty("jobs");
            Require(jobsElement.ValueKind == JsonValueKind.Array && jobsElement.GetArrayLength() <= 512);
            var jobs = new List<TrialVisibilityJob>(); string? previous = null;
            foreach (JsonElement job in jobsElement.EnumerateArray())
            {
                Shape(job, "jobId", "sessionId", "requestedLocal", "queuedLocal", "startExecutionLocal", "stopExecutionLocal", "lastExecutedStepId", "lastExecutedStepLocal");
                Guid jobId = Id(Text(job, "jobId")); string key = jobId.ToString("D");
                int sessionId = Int(job, "sessionId"); int? step = NullableInt(job, "lastExecutedStepId");
                Require(sessionId > 0 && sessionId == before.SessionId && step is not < 0 && (previous is null || string.CompareOrdinal(previous, key) < 0));
                previous = key;
                jobs.Add(new(jobId, sessionId, Local(job, "requestedLocal"), Local(job, "queuedLocal"), Local(job, "startExecutionLocal"),
                    Local(job, "stopExecutionLocal"), step, Local(job, "lastExecutedStepLocal")));
            }
            int sourceRows = Int(capture, "sourceRowsRead"); bool truncated = Bool(capture, "truncated");
            // Visibility comparison deliberately refuses lookahead/truncation; this is not a cap qualification.
            Require(!truncated && sourceRows == jobs.Count && sourceRows is >= 0 and <= 512);
            ValidateEvidence(lines[0], sourceRows);
            return new(expectedTargetId, expectedTargetRevision, runId, trxId, Convert.ToHexString(SHA256.HashData(snapshot)).ToLowerInvariant(),
                new(before.Utc, after.Utc, before.SessionId, before.Started, after.SessionId, after.Started, jobs.AsReadOnly(), sourceRows, false));
        }
        catch (Exception error) when (error is XmlException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or InvalidDataException)
        { throw Invalid(); }
    }

    private static void ValidateEvidence(string json, int rows)
    {
        Require(Encoding.UTF8.GetByteCount(json) <= 8192);
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        var root = parsed.RootElement; Shape(root, "stage", "evidence"); Require(Text(root, "stage") == "signed_module_capture");
        var evidence = root.GetProperty("evidence");
        string[] trueKeys = ["actualMonitorIdentityVerified", "ownedIntegratedTlsSessionStable", "originalDiscoveryProfilePreserved", "moduleDefinitionEnrollmentMatched"];
        string[] falseKeys = ["directSecurityStateGranted", "directSessionsSelectGranted", "truncated", "monitorDefinitionVerified", "signatureVerifiedByMonitor", "fullForeignJobVisibilityQualified", "productionAdmitted", "serviceSessionAssociationQualified", "elapsedQualified", "longRunningQualified"];
        Shape(evidence, [.. trueKeys, .. falseKeys, "directActivitySelectGranted", "directSessionsSelectError", "sourceRowsRead", "retainedRows", "gap", "databaseDefinitionAndSignatureVerification"]);
        foreach (string key in trueKeys) Require(Bool(evidence, key));
        foreach (string key in falseKeys) Require(!Bool(evidence, key));
        _ = Bool(evidence, "directActivitySelectGranted");
        Require(Int(evidence, "directSessionsSelectError") == 229 && Int(evidence, "sourceRowsRead") == rows && Int(evidence, "retainedRows") == rows &&
            Text(evidence, "gap") == "ServiceNotRunning" && Text(evidence, "databaseDefinitionAndSignatureVerification") == "separate_DBA_evidence_required");
    }

    private static (DateTimeOffset Utc, int SessionId, DateTime Started) Header(JsonElement header)
    {
        Shape(header, "sourceObservedAtUtc", "service", "session");
        var service = header.GetProperty("service"); Shape(service, "matchingServiceCount", "status", "processId", "startupAtUtc");
        Require(Int(service, "matchingServiceCount") == 1 && Int(service, "status") == 1 && NullableInt(service, "processId") is null or 0);
        if (service.GetProperty("startupAtUtc").ValueKind != JsonValueKind.Null) _ = Utc(Text(service, "startupAtUtc"));
        var session = header.GetProperty("session"); Shape(session, "sessionId", "agentStartLocal");
        int id = Int(session, "sessionId"); DateTime? start = Local(session, "agentStartLocal");
        Require(id > 0 && start.HasValue);
        return (Utc(Text(header, "sourceObservedAtUtc")), id, start.GetValueOrDefault());
    }
    private static DateTimeOffset Utc(string value)
    {
        Require(value.EndsWith("+00:00", StringComparison.Ordinal) || value.EndsWith('Z'));
        string local = value.EndsWith('Z') ? value[..^1] : value[..^6];
        DateTime date = Date(local);
        return new(date, TimeSpan.Zero);
    }
    private static DateTime? Local(JsonElement parent, string name) => parent.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Date(Text(parent, name));
    private static DateTime Date(string value)
    {
        Require(value.Length == 19 || (value.Length is >= 21 and <= 27 && value[19] == '.' && value.AsSpan(20).IndexOfAnyExceptInRange('0', '9') < 0));
        Require(DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) && date.Year >= 1753 && date.Kind == DateTimeKind.Unspecified);
        return date;
    }
    private static int? NullableInt(JsonElement parent, string name) => parent.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Int(parent, name);
    private static int Int(JsonElement parent, string name) => parent.GetProperty(name).GetInt32();
    private static bool Bool(JsonElement parent, string name) => parent.GetProperty(name).GetBoolean();
    private static string Text(JsonElement parent, string name) => parent.GetProperty(name).GetString() ?? throw Invalid();
    private static void Shape(JsonElement element, params string[] names)
    {
        Require(element.ValueKind == JsonValueKind.Object);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) Require(names.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name));
        Require(seen.Count == names.Length);
    }
    private static Guid Id(string value) { Require(Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty); return id; }
    private static XElement One(XElement parent, string name) { var elements = parent.Elements(Ns + name).ToArray(); Require(elements.Length == 1); return elements[0]; }
    private static string Attribute(XElement element, string name) => (string?)element.Attribute(name) ?? throw Invalid();
    private static int Occurrences(string text, string value) { int count = 0, at = 0; while ((at = text.IndexOf(value, at, StringComparison.Ordinal)) >= 0) { count++; at += value.Length; } return count; }
    private static void Require(bool condition) { if (!condition) throw Invalid(); }
    private static InvalidDataException Invalid() => new("Native visibility receipt is invalid.");
}
