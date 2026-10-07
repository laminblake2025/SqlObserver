using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.IntegrationTests.PostgreSql;

internal sealed record SqlAgentActivityNativeReplayHashes(string SidecarSha256, string NativeReportSha256,
    string TrxSha256, string PackageSha256);
internal sealed record SqlAgentActivityNativeReplayProof(string Phase, string SidecarSha256, string NativeReportSha256,
    string TrxSha256, string PackageSha256, string SourceArchiveSha256, string RawPrivateReceiptSha256,
    int NativeCasesPassed, int SourceInputCount, int RunnerInputCount)
{
    public bool RecordedNativeSourceReplay { get; } = true;
    public bool OriginalAuthorizationPreserved { get; } = true;
    public bool FullDbaValidationAtOriginalExport { get; } = true;
    public bool FullDbaValidationRepeatedByParser { get; }
    public bool RepositoryResolverNativeQualified { get; }
    public bool ProductionAdmitted { get; }
    public bool ServiceSessionAssociationQualified { get; }
}

/// <summary>Strict artifact linking for unchanged recorded source, not installation authority.
/// The native exporter ran the full DBA validator; this portable parser checks its closed
/// postimage, raw digest and enrolled pins without pretending to repeat live DBA validation.</summary>
internal sealed record SqlAgentActivityNativeReplayInput(SqlAgentModuleEnrollment Enrollment, CollectorRunId RunId,
    CollectorExecutionResult Result, string RawPrivateReceiptSha256, SqlAgentActivityNativeReplayProof Proof)
{
    internal const string Prefix = "SQLOBSERVER_AGENT_NATIVE_REPLAY_";
    internal const string Marker = "SQL_AGENT_ACTIVITY_COLLECTOR_PRIVATE_RECEIPT_V1:";
    internal const string CollectorCase = "SqlObserver.IntegrationTests.SqlServer.SqlAgentActivityCollectorNativeQualificationTests.ActualMonitorCapturesThroughPublicObservationsCollector";
    internal const int MaximumSidecarBytes = 262144;
    internal const int MaximumTrxBytes = 4 * 1024 * 1024;
    internal const int MaximumReportBytes = 8 * 1024 * 1024;
    internal const int MaximumPackageBytes = 2 * 1024 * 1024;
    internal const int MaximumReceiptBytes = 530000;
    private const string Failure = "Native Agent replay evidence is absent, stale, mismatched or invalid; private values withheld.";
    private static readonly string[] DeniedCases =
    ["SqlObserver.IntegrationTests.SqlServer.SqlAgentActivityExplicitDenyQualificationTests.ActualMonitorSignedModuleExplicitDenyRefusesPublicCapture",
     "SqlObserver.IntegrationTests.SqlServer.SqlAgentActivityExplicitDenyQualificationTests.ActualMonitorDeniedModuleRetainsCanonicalBoundRefusal"];
    private static readonly string[] RunnerNames = ["NativeDiagnostics.cs", "NativeRunner.csproj", "Program.cs", "TemporaryService.cs", "NativeAuthorizationSidecar.cs"];
    private static readonly string[] PhaseTrxNames = ["signedTrx", "deniedTrx", "restoredTrx"];
    private static readonly string[] RequiredInputs =
    ["tools/provisioning/sql-agent-activity/trial/AgentActivityTrialSql.cs", "tools/provisioning/sql-agent-activity/trial/TrialCoordinator.cs",
     "tools/provisioning/sql-agent-activity/trial/TrialJournal.cs", "tests/SqlObserver.IntegrationTests.SqlServer/SqlAgentActivityExplicitDenyQualificationTests.cs",
     "tests/SqlObserver.IntegrationTests.SqlServer/SqlAgentActivityCollectorNativeReceiptTests.cs",
     "src/SqlObserver.Infrastructure.SqlServer/SqlServerAgentActivityModuleCaptureReader.cs"];

    public override string ToString() => "Recorded native Agent replay input";

    internal static SqlAgentActivityNativeReplayInput Load() => Load(Environment.GetEnvironmentVariable, DateTimeOffset.UtcNow);
    internal static SqlAgentActivityNativeReplayInput Load(Func<string, string?> read, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read(Prefix + "ACCEPTANCE") != "1") throw Invalid();
        try
        {
            string Required(string name) => read(Prefix + name) ?? throw Invalid();
            var hashes = new SqlAgentActivityNativeReplayHashes(Required("SIDECAR_SHA256"), Required("NATIVE_REPORT_SHA256"),
                Required("TRX_SHA256"), Required("PACKAGE_SHA256"));
            return Parse(ReadBounded(Required("SIDECAR_PATH"), MaximumSidecarBytes), ReadBounded(Required("NATIVE_REPORT_PATH"), MaximumReportBytes),
                ReadBounded(Required("TRX_PATH"), MaximumTrxBytes), ReadBounded(Required("PACKAGE_PATH"), MaximumPackageBytes), Required("PHASE"), hashes, nowUtc);
        }
        catch (Exception error) when (IsContractError(error)) { throw Invalid(); }
    }

    internal static SqlAgentActivityNativeReplayInput Parse(byte[] sidecarBytes, byte[] reportBytes, byte[] trxBytes,
        byte[] packageBytes, string phase, SqlAgentActivityNativeReplayHashes hashes, DateTimeOffset nowUtc)
    {
        try
        {
            if (hashes is null) throw Invalid();
            if (phase is not ("signed" or "restored") || nowUtc.Offset != TimeSpan.Zero) throw Invalid();
            CheckHash(sidecarBytes, MaximumSidecarBytes, hashes.SidecarSha256);
            CheckHash(reportBytes, MaximumReportBytes, hashes.NativeReportSha256);
            CheckHash(trxBytes, MaximumTrxBytes, hashes.TrxSha256);
            CheckHash(packageBytes, MaximumPackageBytes, hashes.PackageSha256);
            using var sidecar = ParseJson(sidecarBytes); using var report = ParseJson(reportBytes); using var package = ParseJson(packageBytes);
            var root = sidecar.RootElement;
            Shape(root, "schemaVersion diagnosticOnly deploymentId targetId targetRevision authorization installedEvidenceSha256 installedSnapshot");
            Equal(Int(root, "schemaVersion"), 1); Boolean(root, "diagnosticOnly", true);
            var deployment = Id(root, "deploymentId"); var target = new MonitoredInstanceId(Id(root, "targetId"));
            var revision = new ObservationTargetRevision(Long(root, "targetRevision"));
            var enrollment = ParseEnrollment(root.GetProperty("authorization"));
            Equal(enrollment.TargetId, target); Equal(enrollment.TargetRevision, revision); Equal(enrollment.EnrollmentId, deployment);
            Equal(enrollment.Revision, 1L);
            if (!enrollment.IsActiveAt(nowUtc)) throw Invalid();
            string installedDigest = HashText(root, "installedEvidenceSha256"); Equal(installedDigest, enrollment.EvidenceSha256);
            var installed = root.GetProperty("installedSnapshot");
            Equal(Hash(Encoding.UTF8.GetBytes(installed.GetRawText())), installedDigest);
            ValidateInstalledLinks(installed, enrollment, deployment);
            int inputCount = ValidateReport(report.RootElement, package.RootElement, phase, hashes, deployment, installedDigest, sidecarBytes.Length);
            string privateReceipt = ParseTrx(trxBytes);
            if (Encoding.UTF8.GetByteCount(privateReceipt) > MaximumReceiptBytes) throw Invalid();
            using var receipt = ParseJson(Encoding.UTF8.GetBytes(privateReceipt));
            var (runId, result) = ParseReceipt(receipt.RootElement, enrollment, nowUtc);
            string receiptHash = Hash(Encoding.UTF8.GetBytes(privateReceipt));
            return new(enrollment, runId, result, receiptHash, new(phase, hashes.SidecarSha256, hashes.NativeReportSha256,
                hashes.TrxSha256, hashes.PackageSha256, HashText(package.RootElement, "archiveSha256"), receiptHash, 4, inputCount, RunnerNames.Length));
        }
        catch (Exception error) when (IsContractError(error)) { throw Invalid(); }
    }

    private static SqlAgentModuleEnrollment ParseEnrollment(JsonElement value)
    {
        Shape(value, "targetId targetRevision enrollmentRevision enrollmentId expectedWindowsSid notBeforeUtc expiresAtUtc evidenceSha256 binding");
        var binding = value.GetProperty("binding");
        Shape(binding, "endpointHost tcpPort certificateHost serverName machineName instanceName objectId certificateId definitionSha256 publicCertificateSha256 thumbprintHex moduleVersion sourceVersion probeVersion installationPolicyVersion");
        Equal(Int(binding, "moduleVersion"), 1); Equal(Int(binding, "sourceVersion"), 2);
        Equal(Int(binding, "probeVersion"), 1); Equal(Int(binding, "installationPolicyVersion"), 2);
        var pins = new SqlAgentModuleBinding(Text(binding, "endpointHost"), Int(binding, "tcpPort"), Text(binding, "certificateHost"),
            Text(binding, "serverName"), Text(binding, "machineName"), Text(binding, "instanceName", allowEmpty: true),
            Int(binding, "objectId"), Int(binding, "certificateId"), HashText(binding, "definitionSha256"),
            HashText(binding, "publicCertificateSha256"), Hex(Text(binding, "thumbprintHex"), 40));
        return new(new(Id(value, "targetId")), new(Long(value, "targetRevision")), Long(value, "enrollmentRevision"), Id(value, "enrollmentId"),
            Text(value, "expectedWindowsSid"), pins, Utc(value, "notBeforeUtc", allowOffsetSuffix: true),
            Utc(value, "expiresAtUtc", allowOffsetSuffix: true), HashText(value, "evidenceSha256"));
    }

    private static void ValidateInstalledLinks(JsonElement value, SqlAgentModuleEnrollment enrollment, Guid deployment)
    {
        Shape(value, "Identity Monitor DeploymentId Objects PackagePermissions MonitorPermissions Facts");
        Equal(Id(value, "DeploymentId"), deployment);
        var identity = value.GetProperty("Identity");
        Shape(identity, "ServerName MachineName InstanceName ProductVersion MajorVersion EngineEdition PathSeparator MasterDatabaseGuid MsdbDatabaseGuid");
        Equal(Text(identity, "ServerName"), enrollment.Binding.ServerName); Equal(Text(identity, "MachineName"), enrollment.Binding.MachineName);
        Equal(Text(identity, "InstanceName", allowEmpty: true), enrollment.Binding.InstanceName); Equal(Int(identity, "MajorVersion"), 17);
        if (Int(identity, "EngineEdition") is not (2 or 3)) throw Invalid();
        Equal(Text(identity, "PathSeparator"), "\\"); _ = Text(identity, "ProductVersion");
        _ = Id(identity, "MasterDatabaseGuid"); _ = Id(identity, "MsdbDatabaseGuid");
        var monitor = value.GetProperty("Monitor"); Shape(monitor, "Principal SidHex LoginId MasterUserId");
        _ = Text(monitor, "Principal"); Positive(Int(monitor, "LoginId")); Positive(Int(monitor, "MasterUserId"));
        Equal(Text(monitor, "SidHex"), SidHex(enrollment.ExpectedWindowsSid));
        var objects = Rows(value, "Objects", 8);
        if (objects.Length is not (7 or 8)) throw Invalid();
        foreach (var item in objects)
        {
            Shape(item, "Kind Database Name Id OwnerId Type Detail");
            if (Text(item, "Kind") is not ("owner_user" or "schema" or "module" or "master_certificate" or "certificate_login" or "msdb_certificate" or "certificate_user" or "monitor_user")) throw Invalid();
            Scope(item, "Database"); _ = Text(item, "Name");
            Positive(Int(item, "Id")); _ = NullableInt(item, "OwnerId"); _ = Text(item, "Type"); _ = Text(item, "Detail", allowEmpty: true);
        }
        if (objects.Select(item => Text(item, "Kind")).Distinct(StringComparer.Ordinal).Count() != objects.Length) throw Invalid();
        var module = Single(objects, item => Text(item, "Kind") == "module");
        Equal(Int(module, "Id"), enrollment.Binding.ObjectId); Equal(Text(module, "Database"), "master"); Equal(Text(module, "Name"), "capture_v1");
        var certificate = Single(objects, item => Text(item, "Kind") == "master_certificate");
        Equal(Int(certificate, "Id"), enrollment.Binding.CertificateId); Equal(Text(certificate, "Database"), "master");
        Equal(Text(certificate, "Name"), "SqlObserverAgentActivityCertificateV1");
        foreach (string name in new[] { "PackagePermissions", "MonitorPermissions" })
            foreach (var grant in Rows(value, name, 10000))
            {
                Shape(grant, "Scope GranteeId GrantorId Class MajorId MinorId Name State");
                Scope(grant, "Scope"); Positive(Int(grant, "GranteeId")); Positive(Int(grant, "GrantorId"));
                _ = Int(grant, "Class"); _ = Int(grant, "MajorId"); _ = Int(grant, "MinorId"); _ = Text(grant, "Name");
                if (Text(grant, "State") is not ("G" or "W" or "D" or "R")) throw Invalid();
            }
        var facts = Rows(value, "Facts", 10000);
        foreach (var fact in facts)
        {
            Shape(fact, "Scope Kind Key Value"); Scope(fact, "Scope");
            if (Text(fact, "Kind") is not ("definition" or "marker" or "certificate" or "signature_validity" or "signature" or "base_object" or "monitor_mapping" or "monitor_marker" or "monitor_user_properties" or "monitor_role")) throw Invalid();
            _ = Text(fact, "Key"); _ = Text(fact, "Value", allowEmpty: true, maximum: 32768);
        }
        if (facts.Select(item => (Text(item, "Scope"), Text(item, "Kind"), Text(item, "Key"))).Distinct().Count() != facts.Length) throw Invalid();
        string Fact(string scope, string kind, string key) => Text(Single(facts, row => Text(row, "Scope") == scope && Text(row, "Kind") == kind && Text(row, "Key") == key), "Value", maximum: 32768);
        Equal(Fact("master", "definition", "sha256"), enrollment.Binding.DefinitionSha256);
        Equal(Fact("master", "definition", "execute_as"), "CALLER"); Equal(Fact("master", "definition", "ansi"), "1:1");
        Equal(Fact("master", "marker", "SqlObserver.AgentActivity.DeploymentId"), deployment.ToString("D"));
        foreach (string scope in new[] { "master", "msdb" })
        {
            Equal(Fact(scope, "certificate", "public_sha256"), enrollment.Binding.PublicCertificateSha256);
            Equal(Fact(scope, "certificate", "thumbprint").ToLowerInvariant(), enrollment.Binding.ThumbprintHex);
            Equal(Fact(scope, "certificate", "private_key"), "NO_PRIVATE_KEY");
        }
        Equal(Fact("master", "signature_validity", "module"), "1:1");
        var signature = Single(facts, row => Text(row, "Kind") == "signature");
        Equal(Text(signature, "Scope"), "master"); Equal(Text(signature, "Key"), enrollment.Binding.ObjectId.ToString(CultureInfo.InvariantCulture));
        Equal(Text(signature, "Value"), "SPVC:" + enrollment.Binding.ThumbprintHex.ToUpperInvariant());
    }

    private static int ValidateReport(JsonElement report, JsonElement package, string phase, SqlAgentActivityNativeReplayHashes hashes,
        Guid deployment, string installedDigest, int sidecarLength)
    {
        Allow(report, "observedAtUtc operation nativeSqlQualificationRequested nativeProcessDaclChanged completed sourceSha256 sourceBefore packageSha256 sourceRoot reviewedRunnerInputs runnerSourceBefore remoteSourceVerified actionResult runnerSourceAfter sourceAfter localEvidenceDirectory qualificationPassed cleanupVerified privateAuthorizationSidecar");
        var copiedSidecar = report.GetProperty("privateAuthorizationSidecar");
        Shape(copiedSidecar, "sha256 bytes originalAuthorizationPreserved localArtifact");
        Equal(HashText(copiedSidecar, "sha256"), hashes.SidecarSha256); Equal(Long(copiedSidecar, "bytes"), sidecarLength);
        Boolean(copiedSidecar, "originalAuthorizationPreserved", true); _ = Text(copiedSidecar, "localArtifact", maximum: 4096);
        Equal(Text(report, "operation"), "Run");
        foreach (string flag in new[] { "nativeSqlQualificationRequested", "completed", "remoteSourceVerified", "qualificationPassed", "cleanupVerified" }) Boolean(report, flag, true);
        Boolean(report, "nativeProcessDaclChanged", false);
        Shape(package, "createdAtUtc archiveSha256 fileCount inputs head status");
        Equal(HashText(report, "packageSha256"), HashText(package, "archiveSha256"));
        int count = Int(package, "fileCount"); if (count is < 1 or > 10000) throw Invalid();
        var inputs = package.GetProperty("inputs"); if (inputs.ValueKind != JsonValueKind.Object || inputs.EnumerateObject().Count() != count) throw Invalid();
        foreach (var input in inputs.EnumerateObject())
        {
            if (input.Name.Length is < 1 or > 512 || input.Name.StartsWith('/') || input.Name.Contains('\\') ||
                input.Name.Split('/').Any(part => part is "" or "." or "..")) throw Invalid();
            _ = Hex(input.Value.GetString() ?? throw Invalid(), 64);
        }
        foreach (string name in RequiredInputs) _ = HashText(inputs, name);
        foreach (string key in new[] { "sourceBefore", "sourceAfter" }) Inventory(report.GetProperty(key), count, exact: false);
        foreach (string key in new[] { "runnerSourceBefore", "runnerSourceAfter" }) Inventory(report.GetProperty(key), RunnerNames.Length, exact: true);
        var action = report.GetProperty("actionResult");
        Allow(action, "observedAtUtc testBuildExitCode buildExitCode runnerExitCode nativeTestsExecuted normalQualificationPassed fallbackRecoveryAttempted cleanupVerified success sourceRoot runnerSourceSnapshot runnerSourceSha256 testBuildStdoutPath testBuildStderrPath testAssemblyPath testAssemblySha256 buildStdoutPath buildStderrPath runnerAssemblySha256 testAssemblyStableBeforeLaunch resultsRoot runnerLaunchStarted firstProcess runner stderrNonEmpty firstProcessIdentityVerified workerTerminationFenceVerified signedTrx deniedTrx restoredTrx normalCleanupVerified postRunAudit PSComputerName RunspaceId PSShowComputerName");
        foreach (string flag in new[] { "normalQualificationPassed", "cleanupVerified", "success", "testAssemblyStableBeforeLaunch", "runnerLaunchStarted", "firstProcessIdentityVerified", "workerTerminationFenceVerified", "normalCleanupVerified" }) Boolean(action, flag, true);
        Boolean(action, "fallbackRecoveryAttempted", false); Boolean(action, "stderrNonEmpty", false);
        foreach (string key in new[] { "testBuildExitCode", "buildExitCode", "runnerExitCode" }) Equal(Int(action, key), 0);
        Equal(Int(action, "nativeTestsExecuted"), 4); _ = HashText(action, "testAssemblySha256"); _ = HashText(action, "runnerAssemblySha256");
        var reviewed = Rows(report, "reviewedRunnerInputs", RunnerNames.Length); if (reviewed.Length != RunnerNames.Length) throw Invalid();
        var runnerHashes = action.GetProperty("runnerSourceSha256"); Shape(runnerHashes, string.Join(' ', RunnerNames));
        foreach (string name in RunnerNames)
        {
            var item = Single(reviewed, row => Text(row, "path") == name); Shape(item, "path digest");
            Equal(HashText(item, "digest"), HashText(runnerHashes, name));
        }
        var scriptHashes = report.GetProperty("sourceSha256"); Shape(scriptHashes, "guest-native-run.ps1 inspect-native-sql.ps1 invoke-native-sql.ps1");
        foreach (var script in scriptHashes.EnumerateObject()) _ = Hex(script.Value.GetString() ?? throw Invalid(), 64);
        var process = action.GetProperty("firstProcess");
        Allow(process, "stderrLength processStartedUtc stderrJoined exitConfirmed stdoutLength processExitedUtc metadataParsed processId timedOut stdoutSha256 metadata stdoutJoined exitCode stderrSha256 stderrNonEmpty");
        foreach (string flag in new[] { "stderrJoined", "exitConfirmed", "metadataParsed", "stdoutJoined" }) Boolean(process, flag, true);
        Boolean(process, "timedOut", false); Boolean(process, "stderrNonEmpty", false); Equal(Int(process, "exitCode"), 0);
        Positive(Int(process, "processId")); _ = HashText(process, "stdoutSha256"); _ = HashText(process, "stderrSha256");
        var runner = process.GetProperty("metadata");
        if (!JsonNode.DeepEquals(JsonNode.Parse(runner.GetRawText()), JsonNode.Parse(action.GetProperty("runner").GetRawText()))) throw Invalid();
        Allow(runner, "schemaVersion pipeClientIdentityVerified existingServiceAdmissionChanged testContract runnerInvocationId runnerProcessId runnerProcessStartedUtc workerTerminationFenceVersion workerLaunchPhaseCount workerTerminationConfirmed sqlRecoveryBlockedWorkerTerminationUnconfirmed serverWebRootArgumentPresent collectorConnectionPresent serverConnectionPresent fingerprintKeyValid selectedEndpointValid persistedHostBindingValid cpuCapabilityPresent monitorMatchesCollectorService monitorIdentityResolved monitorProfilePresent preflightOnly dbaConnectionId trialOperation deploymentId qualificationEnvironmentComplete installationPolicyVersion journalSchemaVersion enrollmentEvidenceSha256 enrollmentEvidenceFormat trustedIdentityFromValidatedBaseline serviceStopped trxSha256 serviceCreated exactNativeCasesVerified actualServiceMonitorIdentityVerified success stdoutArtifact nativeTestsSkipped workerReceiptReceived trxArtifact nativeExecutionMayHaveStarted descendantCleanupVerified stderrArtifact nativeLaunchStarted nativeTestsPassed serviceStartAccepted serviceDeletionVerified childExitCode temporaryServiceName nativeTestsExecuted timedOut serviceDeleteRequested diagnosticOnly nativeChildProcessStartedUtc nativeChildProcessId directChildExited signedPhase trustedMetadataFromValidatedInstalledReceipt deniedPhase deniedPostimageDigestSource explicitDenyScope explicitDenyQualified attachedInvalidSignatureQualified restoredPhase restoredSignedCollectorQualified trialResult authorizationSidecarArtifact authorizationSidecarSha256 authorizationSidecarSchemaVersion authorizationSidecarBytes authorizationSidecarWrittenBeforeMonitorLaunch authorizationSidecarReadbackVerified ownServiceStopRequested");
        Equal(Text(runner, "testContract"), "agent-production-explicit-deny-original-authorization-v1"); Equal(Int(runner, "schemaVersion"), 1);
        Equal(Int(process, "processId"), Int(runner, "runnerProcessId"));
        var started = ProcessUtc(process, "processStartedUtc"); var exited = ProcessUtc(process, "processExitedUtc");
        Equal(started, ProcessUtc(runner, "runnerProcessStartedUtc")); if (exited < started) throw Invalid();
        Equal(Id(runner, "deploymentId"), deployment); Equal(Int(runner, "installationPolicyVersion"), 2); Equal(Int(runner, "journalSchemaVersion"), 4);
        Equal(HashText(runner, "enrollmentEvidenceSha256"), installedDigest);
        Equal(Text(runner, "enrollmentEvidenceFormat"), "System.Text.Json default serialization of validated installed TrialSnapshot");
        Equal(HashText(runner, "authorizationSidecarSha256"), hashes.SidecarSha256); Equal(Int(runner, "authorizationSidecarSchemaVersion"), 1);
        Equal(Int(runner, "authorizationSidecarBytes"), sidecarLength);
        foreach (string flag in new[] { "workerTerminationConfirmed", "exactNativeCasesVerified", "actualServiceMonitorIdentityVerified", "success", "explicitDenyQualified", "restoredSignedCollectorQualified", "trustedIdentityFromValidatedBaseline", "trustedMetadataFromValidatedInstalledReceipt", "authorizationSidecarWrittenBeforeMonitorLaunch", "authorizationSidecarReadbackVerified" }) Boolean(runner, flag, true);
        foreach (string flag in new[] { "existingServiceAdmissionChanged", "sqlRecoveryBlockedWorkerTerminationUnconfirmed", "preflightOnly", "attachedInvalidSignatureQualified", "diagnosticOnly", "timedOut" }) Boolean(runner, flag, false);
        Equal(Int(runner, "workerTerminationFenceVersion"), 1); Equal(Int(runner, "workerLaunchPhaseCount"), 3);
        if (runner.TryGetProperty("ownServiceStopRequested", out _)) Boolean(runner, "ownServiceStopRequested", true);
        Equal(Int(runner, "nativeTestsExecuted"), 4); Equal(Int(runner, "nativeTestsPassed"), 4); Equal(Int(runner, "nativeTestsSkipped"), 0);
        Equal(Text(runner, "explicitDenyScope"), "msdb.syssessions.select.certificate-user");
        var trial = runner.GetProperty("trialResult"); Shape(trial, "DeploymentId QualificationPassed CleanupVerified FailureCode");
        Equal(Id(trial, "DeploymentId"), deployment); Boolean(trial, "QualificationPassed", true); Boolean(trial, "CleanupVerified", true);
        if (trial.GetProperty("FailureCode").ValueKind != JsonValueKind.Null) throw Invalid();
        foreach (var (name, cases) in new[] { ("signed", new[] { CollectorCase }), ("denied", DeniedCases), ("restored", new[] { CollectorCase }) })
        {
            ValidatePhase(runner.GetProperty(name + "Phase"), cases.Length, started, exited);
            var trx = action.GetProperty(name + "Trx"); Shape(trx, "exactCasesPassed passed skipped executed sha256 expectedCases");
            Boolean(trx, "exactCasesPassed", true); Equal(Int(trx, "passed"), cases.Length); Equal(Int(trx, "executed"), cases.Length); Equal(Int(trx, "skipped"), 0);
            var actualCases = Rows(trx, "expectedCases", 2).Select(item => item.GetString()).ToArray();
            if (!actualCases.SequenceEqual(cases)) throw Invalid();
            Equal(HashText(trx, "sha256"), HashText(runner.GetProperty(name + "Phase"), "trxSha256"));
            if (name == phase) Equal(HashText(trx, "sha256"), hashes.TrxSha256);
        }
        if (PhaseTrxNames.Select(key => HashText(action.GetProperty(key), "sha256")).Distinct(StringComparer.Ordinal).Count() != 3) throw Invalid();
        var audit = action.GetProperty("postRunAudit"); Shape(audit, "observation qualificationServices existingServices agentServiceSnapshot qualificationProcesses observedAtUtc");
        if (Rows(audit, "qualificationServices", 0).Length != 0 || Rows(audit, "qualificationProcesses", 0).Length != 0) throw Invalid();
        var agents = Rows(audit, "agentServiceSnapshot", 1); if (agents.Length != 1) throw Invalid();
        Shape(agents[0], "Name State"); _ = Text(agents[0], "Name"); Equal(Text(agents[0], "State"), "Stopped");
        foreach (var service in Rows(audit, "existingServices", 32)) { Shape(service, "Name State"); _ = Text(service, "Name"); _ = Text(service, "State"); }
        return count;
    }

    private static void Inventory(JsonElement value, int count, bool exact)
    {
        Allow(value, "verifiedFileCount allInputsUnchanged exactFileSet PSComputerName RunspaceId PSShowComputerName");
        Equal(Int(value, "verifiedFileCount"), count); Boolean(value, "allInputsUnchanged", true);
        if (exact) Boolean(value, "exactFileSet", true);
    }
    private static void ValidatePhase(JsonElement value, int count, DateTimeOffset runnerStarted, DateTimeOffset runnerExited)
    {
        Allow(value, "nativeTestsExecuted nativeTestsPassed nativeTestsSkipped exactNativeCasesVerified actualServiceMonitorIdentityVerified pipeClientIdentityVerified trxArtifact trxSha256 serviceStopped serviceDeletionVerified directChildExited childExitCode nativeLaunchStarted nativeChildProcessId nativeChildProcessStartedUtc nativeExecutionMayHaveStarted workerReceiptReceived timedOut temporaryServiceName workerFailedStage workerExceptionChain stdoutArtifact stderrArtifact exceptionType failedStage exceptionChain success descendantCleanupVerified diagnosticOnly serviceCreated serviceDeleteRequested serviceStartAccepted ownServiceStopRequested ownedServiceTreeKillRequested serviceStopExceptionType serviceStopNativeErrorCode serviceDeleteExceptionType serviceDeleteNativeErrorCode serviceDeletionCheckNativeErrorCode cleanupExceptionType ownedTreeKillRequested");
        Equal(Int(value, "nativeTestsExecuted"), count); Equal(Int(value, "nativeTestsPassed"), count); Equal(Int(value, "nativeTestsSkipped"), 0); Equal(Int(value, "childExitCode"), 0);
        foreach (string flag in new[] { "exactNativeCasesVerified", "actualServiceMonitorIdentityVerified", "pipeClientIdentityVerified", "serviceStopped", "serviceDeletionVerified", "directChildExited", "workerReceiptReceived", "nativeLaunchStarted", "nativeExecutionMayHaveStarted", "serviceCreated", "serviceStartAccepted", "serviceDeleteRequested", "success" }) Boolean(value, flag, true);
        Boolean(value, "timedOut", false); Boolean(value, "diagnosticOnly", false);
        Positive(Int(value, "nativeChildProcessId")); var childStarted = ProcessUtc(value, "nativeChildProcessStartedUtc");
        if (childStarted < runnerStarted || childStarted > runnerExited) throw Invalid();
        _ = HashText(value, "trxSha256");
        foreach (string key in new[] { "workerFailedStage", "workerExceptionChain", "exceptionType", "failedStage", "exceptionChain", "serviceStopExceptionType", "serviceDeleteExceptionType", "cleanupExceptionType" })
            if (value.TryGetProperty(key, out var error) && error.ValueKind != JsonValueKind.Null) throw Invalid();
    }

    private static string ParseTrx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumTrxBytes });
        var trx = XDocument.Load(reader); XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        if (trx.Root?.Name != ns + "TestRun") throw Invalid();
        Equal(Single(trx.Descendants(ns + "ResultSummary")).Attribute("outcome")?.Value, "Completed");
        var counters = Single(trx.Descendants(ns + "Counters"));
        foreach (string name in new[] { "total", "executed", "passed" }) Equal(counters.Attribute(name)?.Value, "1");
        foreach (string name in new[] { "failed", "notExecuted", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "disconnected", "warning", "completed", "inProgress", "pending" })
            if (counters.Attribute(name) is { } counter) Equal(counter.Value, "0");
        Equal(counters.Attribute("failed")?.Value, "0"); Equal(counters.Attribute("notExecuted")?.Value, "0");
        var result = Single(trx.Descendants(ns + "UnitTestResult")); Equal(result.Attribute("testName")?.Value, CollectorCase); Equal(result.Attribute("outcome")?.Value, "Passed");
        var definition = Single(trx.Descendants(ns + "UnitTest")); var method = Single(definition.Elements(ns + "TestMethod"));
        Equal(definition.Attribute("name")?.Value, CollectorCase);
        int separator = CollectorCase.LastIndexOf('.');
        Equal(method.Attribute("className")?.Value, CollectorCase[..separator]); Equal(method.Attribute("name")?.Value, CollectorCase[(separator + 1)..]);
        var entry = Single(trx.Descendants(ns + "TestEntry")); var execution = Single(definition.Elements(ns + "Execution"));
        var testId = TrxId(result, "testId"); var executionId = TrxId(result, "executionId");
        Equal(TrxId(definition, "id"), testId); Equal(TrxId(entry, "testId"), testId);
        Equal(TrxId(entry, "executionId"), executionId); Equal(TrxId(execution, "id"), executionId);
        if (trx.Descendants(ns + "ErrorInfo").Any()) throw Invalid();
        var lines = trx.Descendants(ns + "StdOut").SelectMany(row => row.Value.Split('\n'))
            .Where(line => line.TrimStart().StartsWith(Marker, StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1 || !result.Descendants(ns + "StdOut").Any(row => row.Value.Contains(lines[0], StringComparison.Ordinal))) throw Invalid();
        return lines[0].Trim()[Marker.Length..];
    }
    private static Guid TrxId(XElement value, string name)
    {
        var text = value.Attribute(name)?.Value;
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D")) throw Invalid();
        return id;
    }

    private static (CollectorRunId RunId, CollectorExecutionResult Result) ParseReceipt(JsonElement value, SqlAgentModuleEnrollment enrollment, DateTimeOffset nowUtc)
    {
        Shape(value, "schemaVersion diagnosticOnly actualMonitorCollector repositoryResolverQualified productionAdmitted serviceSessionAssociationQualified nativeQualification targetId targetRevision runId collectorId collectorManifestVersion outputSchemaVersion outcome reason accounting loss observation");
        Equal(Int(value, "schemaVersion"), 1); Boolean(value, "diagnosticOnly", true); Boolean(value, "actualMonitorCollector", true);
        Boolean(value, "repositoryResolverQualified", false); Flags(value);
        Equal(Id(value, "targetId"), enrollment.TargetId.Value); Equal(Long(value, "targetRevision"), enrollment.TargetRevision.Value);
        var runId = new CollectorRunId(Id(value, "runId")); Equal(Text(value, "collectorId"), "sql-agent.activity-observations");
        Equal(Int(value, "collectorManifestVersion"), 1); Equal(Int(value, "outputSchemaVersion"), 1);
        var observation = value.GetProperty("observation");
        Shape(observation, "sourceSchemaVersion enrollmentRevision enrollmentId evidenceSha256 before after jobs sourceRowsRead truncated productionAdmitted serviceSessionAssociationQualified nativeQualification");
        Flags(observation); Equal(Int(observation, "sourceSchemaVersion"), 1);
        Equal(Long(observation, "enrollmentRevision"), enrollment.Revision); Equal(Id(observation, "enrollmentId"), enrollment.EnrollmentId);
        Equal(HashText(observation, "evidenceSha256"), enrollment.EvidenceSha256);
        var before = Header(observation.GetProperty("before")); var after = Header(observation.GetProperty("after"));
        if (!enrollment.IsActiveAt(before.SourceObservedAtUtc) || !enrollment.IsActiveAt(after.SourceObservedAtUtc) ||
            before.SourceObservedAtUtc < nowUtc.AddMinutes(-5) || after.SourceObservedAtUtc > nowUtc.AddMinutes(5)) throw Invalid();
        // Native cases require stopped Agent. Preserve historical local dates even when reversed.
        Equal(before.Service.MatchingServiceCount, 1); Equal(after.Service.MatchingServiceCount, 1);
        Equal(before.Service.Status, 1); Equal(after.Service.Status, 1);
        var jobs = Rows(observation, "jobs", SqlAgentActivityCapture.MaximumJobs).Select(job =>
        {
            Shape(job, "jobId sessionId requestedLocal queuedLocal startExecutionLocal stopExecutionLocal lastExecutedStepId lastExecutedStepLocal");
            return new SqlAgentActivityJob(Id(job, "jobId"), Int(job, "sessionId"), Local(job, "requestedLocal"), Local(job, "queuedLocal"),
                Local(job, "startExecutionLocal"), Local(job, "stopExecutionLocal"), NullableInt(job, "lastExecutedStepId"), Local(job, "lastExecutedStepLocal"));
        }).ToArray();
        var capture = new SqlAgentActivityCapture(enrollment.TargetId, enrollment.TargetRevision, runId, before, jobs, after,
            Int(observation, "sourceRowsRead"), Bool(observation, "truncated"));
        bool partial = capture.Truncated;
        Equal(Text(value, "outcome"), partial ? "partial" : "succeeded"); Equal(Text(value, "reason"), partial ? "source_row_limit" : "completed");
        var accounting = value.GetProperty("accounting"); Shape(accounting, "sourceRowsRead outputItemsProduced responseBytes outputBytes");
        var counted = new CollectorRunAccounting(Int(accounting, "sourceRowsRead"), Int(accounting, "outputItemsProduced"), Int(accounting, "responseBytes"), Int(accounting, "outputBytes"));
        var loss = value.GetProperty("loss"); Shape(loss, "kind minimumLostItems countIsExact minimumLostBytes");
        Equal(Text(loss, "kind"), partial ? "source_row_limit" : "none");
        var lost = new CollectorLossEvidence(partial ? CollectorLossKind.SourceRowLimit : CollectorLossKind.None,
            Int(loss, "minimumLostItems"), Bool(loss, "countIsExact"), Int(loss, "minimumLostBytes"));
        var payload = new CollectorPayload(sqlAgentModuleCapture: new SqlAgentModuleCaptureResult(capture, enrollment));
        var outcome = partial ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded;
        SqlAgentActivityObservationsOutputValidation.Validate(payload, enrollment.TargetId, enrollment.TargetRevision, runId, outcome, counted, lost);
        return (runId, new(enrollment.TargetId, enrollment.TargetRevision, new("sql-agent.activity-observations"), 1, 1, outcome,
            partial ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed, payload, counted, lost));
    }
    private static SqlAgentActivityHeader Header(JsonElement value)
    {
        Shape(value, "sourceObservedAtUtc service session");
        var service = value.GetProperty("service"); Shape(service, "matchingServiceCount status processId startupAtUtc");
        var session = value.GetProperty("session"); Shape(session, "sessionId agentStartLocal");
        return new(Utc(value, "sourceObservedAtUtc"), new(Int(service, "matchingServiceCount"), NullableInt(service, "status"), NullableInt(service, "processId"),
            service.GetProperty("startupAtUtc").ValueKind == JsonValueKind.Null ? null : Utc(service, "startupAtUtc")),
            new(NullableInt(session, "sessionId"), Local(session, "agentStartLocal")));
    }
    private static void Flags(JsonElement value) { Boolean(value, "productionAdmitted", false); Boolean(value, "serviceSessionAssociationQualified", false); Equal(Text(value, "nativeQualification"), "Absent"); }
    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = File.OpenRead(path); if (stream.Length is <= 0 || stream.Length > maximum) throw Invalid();
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); if (stream.ReadByte() != -1) throw Invalid(); return bytes;
    }
    private static void CheckHash(byte[] bytes, int maximum, string expected)
    { if (bytes is null || bytes.Length is <= 0 || bytes.Length > maximum) throw Invalid(); Equal(Hash(bytes), Hex(expected, 64)); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static JsonDocument ParseJson(byte[] bytes)
    {
        // Windows report writers may prefix a single UTF-8 BOM. Artifact accounting and
        // digest checks above retain every original byte, including this encoding marker.
        int prefix = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, prefix, bytes.Length - prefix), new JsonDocumentOptions { MaxDepth = 32 });
        try { Duplicates(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void Duplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); Duplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Duplicates(item);
    }
    private static void Shape(JsonElement value, string fields)
    { Allow(value, fields); if (value.EnumerateObject().Count() != fields.Split(' ').Length) throw Invalid(); }
    private static void Allow(JsonElement value, string fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var allowed = fields.Split(' ').ToHashSet(StringComparer.Ordinal);
        if (value.EnumerateObject().Any(item => !allowed.Contains(item.Name))) throw Invalid();
    }
    private static JsonElement[] Rows(JsonElement value, string name, int maximum)
    { var rows = value.GetProperty(name); if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > maximum) throw Invalid(); return rows.EnumerateArray().ToArray(); }
    private static T Single<T>(IEnumerable<T> values) { var items = values.Take(2).ToArray(); if (items.Length != 1) throw Invalid(); return items[0]; }
    private static T Single<T>(IEnumerable<T> values, Func<T, bool> predicate) => Single(values.Where(predicate));
    private static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw Invalid(); }
    private static void Positive(int value) { if (value <= 0) throw Invalid(); }
    private static void Scope(JsonElement value, string name) { if (Text(value, name) is not ("server" or "master" or "msdb")) throw Invalid(); }
    private static string Text(JsonElement value, string name, bool allowEmpty = false, int maximum = 512)
    { var text = value.GetProperty(name).GetString(); if (text is null || text.Length > maximum || !allowEmpty && string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) throw Invalid(); return text; }
    private static string Hex(string value, int length)
    { if (value is null || value.Length != length || value.All(c => c == '0') || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw Invalid(); return value; }
    private static string HashText(JsonElement value, string name) => Hex(Text(value, name), 64);
    private static Guid Id(JsonElement value, string name)
    { string text = Text(value, name); if (!Guid.TryParseExact(text, "D", out Guid id) || id == Guid.Empty || text != id.ToString("D")) throw Invalid(); return id; }
    private static int Int(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static long Long(JsonElement value, string name) => value.GetProperty(name).GetInt64();
    private static int? NullableInt(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Int(value, name);
    private static bool Bool(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static void Boolean(JsonElement value, string name, bool expected) => Equal(Bool(value, name), expected);
    private static DateTimeOffset Utc(JsonElement value, string name, bool allowOffsetSuffix = false)
    {
        string text = Text(value, name); string format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        if (allowOffsetSuffix && text.EndsWith("+00:00", StringComparison.Ordinal)) format = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";
        if (!DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ||
            parsed.Offset != TimeSpan.Zero || parsed.ToString(format, CultureInfo.InvariantCulture) != text) throw Invalid();
        return parsed;
    }
    private static DateTimeOffset ProcessUtc(JsonElement value, string name)
    {
        // Default JSON DateTime serialization trims fractional zeros. This applies only
        // to diagnostic process clocks; original authorization/source precision is strict.
        string text = Text(value, name);
        int fraction = text.Length == 20 ? 0 : text.Length - 21;
        if (fraction is < 0 or > 7) throw Invalid();
        string format = "yyyy-MM-dd'T'HH:mm:ss" + (fraction == 0 ? "" : "." + new string('f', fraction)) + "'Z'";
        if (!DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ||
            parsed.Offset != TimeSpan.Zero || parsed.ToString(format, CultureInfo.InvariantCulture) != text) throw Invalid();
        return parsed;
    }
    private static DateTime? Local(JsonElement value, string name)
    {
        if (value.GetProperty(name).ValueKind == JsonValueKind.Null) return null;
        const string format = "yyyy-MM-dd'T'HH:mm:ss.fffffff"; string text = Text(value, name);
        if (!DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Kind != DateTimeKind.Unspecified || parsed.ToString(format, CultureInfo.InvariantCulture) != text) throw Invalid(); return parsed;
    }
    private static string SidHex(string sid)
    {
        var parts = sid.Split('-'); byte[] bytes = new byte[8 + (parts.Length - 3) * 4]; bytes[0] = 1; bytes[1] = checked((byte)(parts.Length - 3));
        ulong authority = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
        for (int index = 0; index < 6; index++) bytes[7 - index] = (byte)(authority >> (index * 8));
        for (int index = 3; index < parts.Length; index++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (index - 3) * 4), uint.Parse(parts[index], CultureInfo.InvariantCulture));
        return Convert.ToHexString(bytes);
    }
    private static bool IsContractError(Exception error) => error is InvalidDataException or JsonException or XmlException or ArgumentException or
        InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or IOException or UnauthorizedAccessException;
    private static InvalidDataException Invalid() => new(Failure);
}
