using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlObserver.ReleaseTests;

public sealed class M12RunbooksCertificationTests
{
    [Fact]
    public void ContractAssetsDescribeExactRunbookInventory()
    {
        string root = FindRoot();
        string catalog = File.ReadAllText(Path.Combine(root, "release/certification/m12-runbooks-catalog.v1.json"));
        Assert.Contains("m12-release-preflight.md", catalog, StringComparison.Ordinal);
        Assert.Contains("m12-supply-chain-certification.md", catalog, StringComparison.Ordinal);
        Assert.Contains("m12-candidate-evidence-verification.md", catalog, StringComparison.Ordinal);
        Assert.Contains("m12-failed-run-quarantine-and-escalation.md", catalog, StringComparison.Ordinal);
        string[] runbooks = Directory.GetFiles(Path.Combine(root, "docs/runbooks"), "m12-*.md"); Assert.Equal(4, runbooks.Length);
        foreach (string file in runbooks) Assert.Equal(12, File.ReadLines(file).Count(line => line.StartsWith("## ", StringComparison.Ordinal)));
    }

    [Fact]
    public void RunbookPublicationReleasesHeldEvidenceBeforeMoveAndKeepsTamperChecks()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        Assert.Contains("Close-M12RunbooksEvidence $held $raw", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12RunbooksHeld $held $Root;Close-M12RunbooksEvidence $held $raw;Restore-M12Environment", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Move($evidence,(Join-Path $build 'm12-runbooks.json'))", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12SameIdentity $expected.Identity $current.Identity", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunbookPredecessorUsesImmutableLicenseEvidenceInsteadOfReopeningPackageRoots()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        Assert.Contains("function Assert-M12PublishedEvidenceSource", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$PublishedEvidenceOnly", source, StringComparison.Ordinal);
        Assert.Contains("-SbomRunId $SbomRunId -PublishedEvidenceOnly", source, StringComparison.Ordinal);
        Assert.Contains("if($PublishedEvidenceOnly){Assert-M12PublishedEvidenceSource $component}else{Assert-M12EvidenceSource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'m12-licenses' {[void](Assert-M12PublishedLicenseArtifacts $dir $Root $SbomPath $Commit $runId $Environment 'SqlObserver.ReleaseTests.M12LicenseCertificationTests.LiveReleaseLicenseEvidenceIsCompleteDeterministicAndSbomBound' ([Environment]::GetEnvironmentVariable('NUGET_PACKAGES'))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunbookHeldRevalidationScansDirectoryAncestorsForLateAds()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        Assert.Contains("function Assert-M12NoAlternateDataStreamsTree", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12NoAlternateDataStreamsTree $item.Path $Root", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12NoAlternateDataStreamsTree $Path $Root", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunbookHeldFilesRevalidateExistingStreamsAndRetainOwnerIdentity()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        Assert.Contains("function Assert-M12RunbooksHeldFile", source, StringComparison.Ordinal);
        Assert.Contains("function Get-M12RunbooksHeldFileSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("$firstEvidence=Get-M12RunbooksHeldFileSnapshot $held $evidence $Root", source, StringComparison.Ordinal);
        Assert.Contains("$secondSnapshot=Get-M12RunbooksHeldFileSnapshot $held $secondEvidence $Root", source, StringComparison.Ordinal);
        Assert.Contains("function Open-M12RunbooksMoveSource", source, StringComparison.Ordinal);
        Assert.Contains("if($stream.Length-lt2-or$stream.Length-gt$MaximumJsonBytes)", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Move($evidence,(Join-Path $build 'm12-runbooks.json'));$moveSource.Stream.Position=0;$movedIdentity=[M12OutputFile]::Read($moveSource.Stream);Assert-M12SameIdentity $moveSource.Identity $movedIdentity;$moveSource.Stream.Dispose();$moveSource.Stream=$null;$snap=Read-M12LockedBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.File]::Move($evidence,(Join-Path $build 'm12-runbooks.json'));$snap=Read-M12LockedBytes", source, StringComparison.Ordinal);
        Assert.Contains("$moveSource=Open-M12RunbooksMoveSource $evidence $Root", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12RunbooksOwnedBuild $owner $build $Root;[IO.File]::Move($evidence", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12RunbooksOwnedBuild $owner $build $Root;Remove-M12SafeDescendants $raw $Root", source, StringComparison.Ordinal);
        Assert.Contains("$beforeArtifact=$before['m12-runbooks.json'];if($beforeArtifact.Hash-cne$snap.Hash-or$beforeArtifact.Bytes.Length-ne$snap.Bytes.Length", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12SameIdentity $snap.Identity $beforeArtifact.Identity;Assert-M12RunbooksOwnedBuild", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$firstEvidence=Read-M12LockedBytes $evidence $Root", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$secondSnapshot=Read-M12LockedBytes $secondEvidence $Root", source, StringComparison.Ordinal);
        Assert.Contains("$stream.Position=0", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12RunbooksHeldFile $item $Root", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12SameIdentity $owner ([M12ExclusiveDirectory]::ReadIdentity($verify))", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12SameIdentity $owner ([M12ExclusiveDirectory]::ReadIdentity($final))", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12RunbooksOwnedBuild $owner $build $Root;Assert-M12RunbooksOutputRoot $outputRoot $outputRootState.Identity $Root;[IO.Directory]::Move($build,$verify);$cleanupTarget=$verify;", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12RunbooksOwnedBuild $owner $verify $Root;Assert-M12RunbooksOutputRoot $outputRoot $outputRootState.Identity $Root;[IO.Directory]::Move($verify,$final);$cleanupTarget=$final;", source, StringComparison.Ordinal);
        Assert.Contains("$identity=$owner;Assert-M12SameIdentity $owner ([M12ExclusiveDirectory]::ReadIdentity($cleanupTarget))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractOnlyValidatesWithoutPublishing()
    {
        string root = FindRoot(); string source = File.ReadAllText(Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "m12-runbooks", "m12-runbooks-contract.v1.json", "m12-runbooks-catalog.v1.json", "m12-runbooks-inputs.v1.schema.json", "m12-runbooks-evidence.v1.schema.json", "generate-m12-runbooks-evidence.mjs", "m12-runbooks.json", "m12-runbooks-test-evidence.json", "m12-runbooks-provenance.json", "LiveReleaseRunbooksAreClosedVersionedAndExercised", "RequiresM12SupplyChainRelease", "FileMode]::CreateNew", "pending-", "verify-", "quarantine" }) Assert.Contains(marker, source, StringComparison.Ordinal);
        Assert.Equal(0, Run(root, "m12-runbooks", "-ContractOnly")); Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
    }

    [Fact]
    public void RunbookProcedureCannotBeUsedAsCommandInput()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/generate-m12-runbooks-evidence.mjs"));
        Assert.Contains("never", File.ReadAllText(Path.Combine(FindRoot(), "docs/runbooks/m12-supply-chain-certification.md")), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("execFile", source, StringComparison.Ordinal); Assert.DoesNotContain("spawn", source, StringComparison.Ordinal); Assert.Contains("procedureIds", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "RequiresM12SupplyChainRelease")]
    public void LiveReleaseRunbooksAreClosedVersionedAndExercised()
    {
        string evidencePath = RequiredEnvironment("SQLOBSERVER_M12_RUNBOOKS_EVIDENCE_PATH");
        string resultPath = RequiredEnvironment("SQLOBSERVER_M12_RUNBOOKS_RESULT_PATH");
        string commit = RequiredEnvironment("SQLOBSERVER_M12_COMMIT_SHA");
        string environment = RequiredEnvironment("SQLOBSERVER_M12_ENVIRONMENT");
        using JsonDocument evidence = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
        JsonElement root = evidence.RootElement;
        AssertExactEvidence(root, commit, environment);
        string json = JsonSerializer.Serialize(new { caseId = "m12-runbooks", commitSha = commit, result = "passed", schemaVersion = 1 });
        using FileStream stream = new(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
        writer.Write(json); writer.Write('\n'); writer.Flush(); stream.Flush(true);
    }

#pragma warning disable CA1861
    private static void AssertExactEvidence(JsonElement root, string commit, string environment)
    {
        AssertPropertyOrder(root, new[] { "$schema", "schemaVersion", "caseId", "producerId", "kind", "result", "commitSha", "environmentId", "runId", "catalogSha256", "matrixSha256", "documents", "prerequisites" });
        Assert.Equal("m12-runbooks-evidence.v1.schema.json", root.GetProperty("$schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32()); Assert.Equal("m12-runbooks", root.GetProperty("caseId").GetString());
        Assert.Equal("m12-supply-chain-harness", root.GetProperty("producerId").GetString()); Assert.Equal("supply-chain-evidence", root.GetProperty("kind").GetString()); Assert.Equal("passed", root.GetProperty("result").GetString());
        Assert.Equal(commit, root.GetProperty("commitSha").GetString()); Assert.Contains(environment, new[] { "release-windows-server-2022", "release-windows-server-2025" }); Assert.Equal(environment, root.GetProperty("environmentId").GetString());
        AssertUuid(root.GetProperty("runId").GetString()); AssertSha(root.GetProperty("catalogSha256").GetString()); Assert.Equal("8b87625c2a56ea07b1dfe826201557e54891341803a75f2a77d2296509f07aac", root.GetProperty("matrixSha256").GetString());
        JsonElement docs = root.GetProperty("documents"); Assert.Equal(JsonValueKind.Array, docs.ValueKind); Assert.Equal(4, docs.GetArrayLength());
        string[] ids = { "m12-release-preflight", "m12-supply-chain-certification", "m12-candidate-evidence-verification", "m12-failed-run-quarantine-and-escalation" };
        string[] paths = { "docs/runbooks/m12-release-preflight.md", "docs/runbooks/m12-supply-chain-certification.md", "docs/runbooks/m12-candidate-evidence-verification.md", "docs/runbooks/m12-failed-run-quarantine-and-escalation.md" };
        string[][] procedures = { new[] { "matrix-only", "contract-only", "clean-trusted-tree" }, new[] { "fixed-prerequisite-order", "three-file-publication", "held-inputs" }, new[] { "three-file-inventory", "sidecar-binding", "locked-read" }, new[] { "owned-cleanup", "pending-verify-quarantine", "never-delete-unowned" } };
        for (int i = 0; i < 4; i++) { JsonElement doc = docs[i]; AssertPropertyOrder(doc, new[] { "documentId", "path", "sha256", "size", "sectionIds", "procedureIds" }); Assert.Equal(ids[i], doc.GetProperty("documentId").GetString()); Assert.Equal(paths[i], doc.GetProperty("path").GetString()); AssertSha(doc.GetProperty("sha256").GetString()); Assert.InRange(doc.GetProperty("size").GetInt64(), 1, 131072); AssertArray(doc.GetProperty("sectionIds"), new[] { "status", "scope", "supported-versions-and-environment", "prerequisites", "required-role", "blast-radius", "procedure", "verification", "failure-recovery", "evidence-and-utc-timestamps", "escalation-conditions", "explicit-exclusions" }); AssertArray(doc.GetProperty("procedureIds"), procedures[i]); }
        JsonElement prerequisites = root.GetProperty("prerequisites"); Assert.Equal(JsonValueKind.Array, prerequisites.ValueKind); Assert.Equal(4, prerequisites.GetArrayLength()); var runs = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 4; i++) { JsonElement item = prerequisites[i]; AssertPropertyOrder(item, new[] { "caseId", "runId" }); Assert.Equal(new[] { "m12-sbom", "m12-licenses", "m12-vulnerability-scan", "m12-provenance" }[i], item.GetProperty("caseId").GetString()); string? run = item.GetProperty("runId").GetString(); AssertUuid(run); Assert.True(runs.Add(run!)); }
    }

    private static void AssertPropertyOrder(JsonElement value, string[] expected) { Assert.Equal(JsonValueKind.Object, value.ValueKind); Assert.Equal(expected, value.EnumerateObject().Select(property => property.Name).ToArray()); }
    private static void AssertArray(JsonElement value, string[] expected) { Assert.Equal(JsonValueKind.Array, value.ValueKind); Assert.Equal(expected, value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()); }
    private static void AssertSha(string? value) { Assert.NotNull(value); Assert.Matches(new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant), value!); }
    private static void AssertUuid(string? value) { Assert.NotNull(value); Assert.Matches(new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.CultureInvariant), value!); }
#pragma warning restore CA1861

    [Fact]
    public void LiveContractBindsExactTestAndPrerequisites()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "m12-sbom|m12-licenses|m12-vulnerability-scan|m12-provenance", "SQLOBSERVER_M12_RUNBOOKS_INPUTS_PATH", "SQLOBSERVER_M12_RUNBOOKS_EVIDENCE_PATH", "SQLOBSERVER_M12_RUNBOOKS_RESULT_PATH", "m12-runbooks.trx", "same commit", "same environment" }) Assert.Contains(marker, source, StringComparison.OrdinalIgnoreCase);
    }

    private static int Run(string root, string caseId, params string[] extra)
    {
        ProcessStartInfo start = new("pwsh") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"), "-RepositoryRoot", root, "-Profile", "Release", "-CaseId", caseId }.Concat(extra)) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!; process.WaitForExit(); return process.ExitCode;
    }
    private static string FindRoot() { string? current = AppContext.BaseDirectory; while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName; return current ?? throw new DirectoryNotFoundException(); }
    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing {name}");
}
