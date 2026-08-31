using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SqlObserver.ReleaseTests;

public sealed class M12CertificationVerifierTests
{
    [Fact]
    public void AcceptsClosedJsonEvidenceSuccessShapeThroughVerifier()
    {
        JsonObject manifest = ValidManifest();
        JsonObject firstLane = (JsonObject)((JsonArray)manifest["lanes"]!)[0]!;
        JsonObject firstCase = (JsonObject)((JsonArray)firstLane["cases"]!)[0]!;
        JsonObject firstEvidence = (JsonObject)((JsonArray)firstCase["evidence"]!)[0]!;
        string evidencePath = Path.Combine(FindRoot(), firstEvidence["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
        using JsonDocument evidence = JsonDocument.Parse(File.ReadAllText(evidencePath));
        Assert.Equal(["caseId", "status", "executions", "skipped", "notRun", "failed", "runId", "commitSha", "environmentId"], evidence.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, Run(manifest));
    }
    private static readonly string[] CountNames = ["skipped", "notRun", "failed"];
    [Fact] public void AcceptsCompleteLocalManifest() => Assert.Equal(0, Run(ValidManifest()));
    [Fact] public void ValidationUsesTheExplicitNineProjectListForReleaseAndLocal()
    {
        string text = File.ReadAllText(Path.Combine(FindRoot(), "tools", "validate.ps1"));
        int start = text.IndexOf("$testProjectsToRun", StringComparison.Ordinal);
        int end = text.IndexOf("foreach ($testProject", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string block = text[start..end];
        Assert.DoesNotContain("$solutionPath", block, StringComparison.Ordinal);
        string[] projects = ["tests/SqlObserver.UnitTests/SqlObserver.UnitTests.csproj", "tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj", "tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj", "tests/SqlObserver.ApiContractTests/SqlObserver.ApiContractTests.csproj", "tests/SqlObserver.SecurityTests/SqlObserver.SecurityTests.csproj", "tests/SqlObserver.PerformanceTests/SqlObserver.PerformanceTests.csproj", "tests/SqlObserver.EndToEndTests/SqlObserver.EndToEndTests.csproj", "tests/SqlObserver.McpContractTests/SqlObserver.McpContractTests.csproj", "tests/SqlObserver.ReleaseTests/SqlObserver.ReleaseTests.csproj"];
        foreach (string project in projects) Assert.Equal(1, block.Split(project, StringSplitOptions.None).Length - 1);
    }
    [Fact] public void AcceptsDistinctInstancesOfSameIdentity()
    {
        JsonObject manifest = ValidManifest();
        ((JsonArray)manifest["environments"]!).Add(new JsonObject { ["environmentId"] = "local-windows-2", ["identity"] = "local-windows", ["os"] = "Windows 11", ["architecture"] = "x64", ["facts"] = new JsonObject { ["os"] = "Windows 11", ["architecture"] = "x64" } });
        Assert.Equal(0, Run(manifest));
    }
    [Fact] public void MatrixAnchorRequiresExactlyTwentyLanesAndThirtyFiveCases()
    {
        string output = RunMatrixOnly(out int exitCode);
        Assert.Equal(0, exitCode);
        Assert.Contains("\"lanes\":20", output);
        Assert.Contains("\"cases\":35", output);
        Assert.Contains("sqlobserver-m12-policy-v1", output);
    }
    [Fact] public void RejectsUnknownManifestProperty() => AssertReject(m => m["unexpected"] = true);
    [Fact] public void RejectsWrongManifestSchema() => AssertReject(m => m["$schema"] = "m12-certification-manifest.v0.schema.json");
    [Fact] public void RejectsEmptyArtifacts() => AssertReject(m => m["artifacts"] = new JsonArray());
    [Fact] public void RejectsEmptyEvidence() => AssertReject(m => FirstCase(m)["evidence"] = new JsonArray());
    [Fact] public void RejectsStaleCommit() => AssertReject(m => m["commitSha"] = new string('0', 40));
    [Fact] public void RejectsExpectedCommitThatIsNotCurrentHead() => Assert.Equal(1, Run(ValidManifest(), "Local", new string('0', 40)));
    [Fact] public void AcceptsBoundTrxPassedEvidence() => Assert.Equal(0, Run(WithXmlEvidence(ValidManifest(), "trx", "<TestRun sqlobserverRunId=\"{RUN}\" sqlobserverCommitSha=\"{COMMIT}\" sqlobserverEnvironmentId=\"local-windows-1\"><Results><UnitTestResult testName=\"m12-repository-contract\" outcome=\"Passed\" /></Results></TestRun>")));
    [Fact] public void RejectsTrxUnknownOutcome() => AssertReject(m => WithXmlEvidence(m, "trx", "<TestRun sqlobserverRunId=\"{RUN}\" sqlobserverCommitSha=\"{COMMIT}\" sqlobserverEnvironmentId=\"local-windows-1\"><Results><UnitTestResult testName=\"m12-repository-contract\" outcome=\"Inconclusive\" /></Results></TestRun>"));
    [Fact] public void AcceptsBoundJunitEvidence() => Assert.Equal(0, Run(WithXmlEvidence(ValidManifest(), "junit", "<testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\" sqlobserverRunId=\"{RUN}\" sqlobserverCommitSha=\"{COMMIT}\" sqlobserverEnvironmentId=\"local-windows-1\"><testcase name=\"m12-repository-contract\" /></testsuite>")));
    [Fact] public void RejectsJunitTotalsMismatch() => AssertReject(m => WithXmlEvidence(m, "junit", "<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"0\" sqlobserverRunId=\"{RUN}\" sqlobserverCommitSha=\"{COMMIT}\" sqlobserverEnvironmentId=\"local-windows-1\"><testcase name=\"m12-repository-contract\" /></testsuite>"));
    [Fact] public void RejectsWrongArtifactHash() => AssertReject(m => m["artifacts"]![0]! ["sha256"] = new string('0', 64));
    [Fact] public void RejectsArbitraryClaimedProductDigest() => AssertReject(m => m["artifacts"]![0]! ["productSha256"] = new string('a', 64));
    [Fact] public void AcceptsPhysicalProductBoundToArtifactAndSidecar() => Assert.Equal(0, Run(AddPhysicalProduct(ValidManifest())));
    [Fact]
    public void RejectsMissingPhysicalProduct()
    {
        JsonObject manifest = ValidManifest();
        JsonObject artifact = Artifact(manifest, 0);
        artifact["productId"] = "m12-missing-product";
        UpdateProvenance(artifact, provenance => provenance["productId"] = "m12-missing-product");
        Assert.Equal(1, Run(manifest));
    }
    [Fact]
    public void RejectsWrongPhysicalProductHash()
    {
        JsonObject manifest = AddPhysicalProduct(ValidManifest());
        JsonObject product = ((JsonArray)manifest["products"]!)[0]!.AsObject();
        product["sha256"] = new string('0', 64);
        Assert.Equal(1, Run(manifest));
    }
    [Fact]
    public void RejectsClaimedProductWithoutMatchingSidecarBinding()
    {
        JsonObject manifest = AddPhysicalProduct(ValidManifest());
        JsonObject artifact = Artifact(manifest, 0);
        UpdateProvenance(artifact, provenance => provenance["productId"] = "m12-other-product");
        Assert.Equal(1, Run(manifest));
    }
    [Fact]
    public void RejectsPartialProductBinding()
    {
        JsonObject manifest = AddPhysicalProduct(ValidManifest());
        JsonObject artifact = Artifact(manifest, 0);
        string path = ProvenancePath(artifact);
        JsonObject payload = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        payload.Remove("productId");
        File.WriteAllText(path, payload.ToJsonString());
        ((JsonObject)artifact["provenance"]!)["sha256"] = Hash(path);
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void RejectsPhysicalProductHeldOpenByWriterWithoutSharing()
    {
        Assert.True(OperatingSystem.IsWindows(), "The sharing-mode contract is a Windows filesystem contract.");
        JsonObject manifest = AddPhysicalProduct(ValidManifest());
        string path = ((JsonObject)((JsonArray)manifest["products"]!)[0]!) ["path"]!.GetValue<string>();
        string fullPath = Path.Combine(FindRoot(), path.Replace('/', Path.DirectorySeparatorChar));
        int result;
        using (FileStream writer = new(fullPath, FileMode.Open, FileAccess.Write, FileShare.None)) { result = RunVerifierOnly(manifest); }
        CleanupFixtures();
        Assert.Equal(1, result);
    }

    [Fact]
    public void RejectsPhysicalProductAlternateDataStreamPath()
    {
        Assert.True(OperatingSystem.IsWindows(), "Alternate data streams are an NTFS filesystem contract.");
        JsonObject manifest = AddPhysicalProduct(ValidManifest());
        JsonObject product = ((JsonArray)manifest["products"]!)[0]!.AsObject();
        string streamPath = Path.Combine(FindRoot(), product["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar)) + ":m12-proof.txt";
        File.WriteAllText(streamPath, "alternate stream\n");
        product["path"] = RelativeToRoot(streamPath);
        product["sha256"] = Hash(streamPath);
        Assert.Equal(1, Run(manifest));
    }
    [Fact]
    public void RejectsUnreferencedPhysicalProduct()
    {
        JsonObject manifest = ValidManifest();
        AddPhysicalProduct(manifest, bindArtifact: false);
        Assert.Equal(1, Run(manifest));
    }
    [Fact] public void RejectsMissingProvenanceSidecar() => AssertReject(m => File.Delete(ProvenancePath(Artifact(m, 0))));
    [Fact] public void RejectsWrongProvenanceReferenceHash() => AssertReject(m => ((JsonObject)Artifact(m, 0)["provenance"]!)["sha256"] = new string('0', 64));
    [Fact] public void RejectsUnknownArtifactKind() => AssertReject(m => m["artifacts"]![0]! ["kind"] = "arbitrary-output");
    [Fact] public void RejectsMismatchedProvenance() => AssertReject(m => UpdateProvenance(Artifact(m, 0), p => p["artifactSize"] = 999));
    [Fact] public void RejectsReusedArtifactAcrossCases() => AssertReject(m => ((JsonArray)((JsonObject)((JsonArray)m["lanes"]!)[1]!["cases"]![0]!) ["artifactIds"]!)[0] = "m12-local-repository-contract");
    [Fact] public void RejectsUnreferencedArtifact() => AssertReject(m => ((JsonArray)m["artifacts"]!).RemoveAt(0));
    [Fact] public void RejectsArtifactAlternateDataStreamPath() => AssertReject(m => m["artifacts"]![0]! ["path"] = "release/certification/file.txt:secret");
    [Fact]
    public void AcceptsDirectBurnExeArtifact()
    {
        JsonObject manifest = ValidManifest();
        JsonObject artifact = Artifact(manifest, 0);
        string originalPath = ArtifactPath(manifest, 0);
        string exePath = Path.ChangeExtension(originalPath, ".exe");
        File.Move(originalPath, exePath);
        artifact["path"] = RelativeToRoot(exePath);
        SetArtifactHashAndSize(artifact, exePath);
        Assert.Equal(0, Run(manifest));
    }
    [Fact] public void RejectsStaleProvenance() => AssertReject(m => UpdateProvenance(Artifact(m, 0), p => p["createdAtUtc"] = DateTimeOffset.UtcNow.AddDays(-2).ToString("O")));
    [Fact] public void RejectsZeroExecutions() => AssertReject(m => FirstCase(m)["executions"] = 0);
    [Fact] public void RejectsSkippedNotRunOrFailedCounts() =>
        Assert.All(CountNames, name => AssertReject(m => FirstCase(m)[name] = 1));
    [Fact] public void RejectsDuplicateLaneAndCaseEvidence() =>
        AssertReject(m => ((JsonArray)m["lanes"]![0]! ["cases"]!)[0]! ["evidence"] = new JsonArray(
            ((JsonArray)((JsonArray)m["lanes"]![0]! ["cases"]!)[0]! ["evidence"]!)[0]!.DeepClone(),
            ((JsonArray)((JsonArray)m["lanes"]![0]! ["cases"]!)[0]! ["evidence"]!)[0]!.DeepClone()));
    [Fact] public void RejectsCanonicalPathAliasDuplicateEvidence() => AssertReject(m =>
    {
        JsonObject evidence = (JsonObject)((JsonArray)FirstCase(m)["evidence"]!)[0]!;
        string path = (string)evidence["path"]!;
        ((JsonArray)FirstCase(m)["evidence"]!).Add(new JsonObject
        {
            ["path"] = $"./{path}", ["sha256"] = evidence["sha256"]!.GetValue<string>(), ["format"] = evidence["format"]!.GetValue<string>(),
            ["runId"] = evidence["runId"]!.GetValue<string>(), ["commitSha"] = evidence["commitSha"]!.GetValue<string>(), ["environmentId"] = "local-windows-1"
        });
    });
    [Fact] public void RejectsUnknownEnvironmentIdentity() => AssertReject(m => ((JsonArray)m["environments"]!)[0]! ["identity"] = "unknown-host-class");
    [Fact] public void RejectsWrongSemanticEnvironmentFact() => AssertReject(m => ((JsonObject)((JsonArray)m["environments"]!)[0]!["facts"]!)["os"] = "Windows Server 2025");
    [Fact] public void RejectsEvidencePathTraversal() => AssertReject(m => FirstCase(m)["evidence"]![0]! ["path"] = "../outside.txt");
    [Fact] public void RejectsMissingRequiredLaneAndCase() => AssertReject(m => ((JsonArray)m["lanes"]!).RemoveAt(0));
    [Fact] public void RejectsReleaseProfileWithoutExternalCertification()
    {
        JsonObject manifest = ValidManifest();
        manifest["profile"] = "Release";
        manifest["result"]!["releaseEvidence"] = true;
        JsonObject environment = (JsonObject)((JsonArray)manifest["environments"]!)[0]!;
        environment["identity"] = "release-windows-server-2022";
        JsonObject artifact = (JsonObject)((JsonArray)manifest["artifacts"]!)[0]!;
        artifact["environmentId"] = "local-windows-1";
        foreach (JsonNode? laneNode in (JsonArray)manifest["lanes"]!)
        {
            JsonObject lane = (JsonObject)laneNode!;
            foreach (JsonNode? caseNode in (JsonArray)lane["cases"]!)
            {
                JsonObject testCase = (JsonObject)caseNode!;
            testCase["environmentId"] = "local-windows-1";
                foreach (JsonNode? evidenceNode in (JsonArray)testCase["evidence"]!) ((JsonObject)evidenceNode!)["environmentId"] = "local-windows-1";
            }
        }
        Assert.Equal(1, Run(manifest, "Release"));
    }

    [Fact]
    public void RejectsArtifactHeldOpenByWriterWithoutSharing()
    {
        Assert.True(OperatingSystem.IsWindows(), "The sharing-mode contract is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        string path = ArtifactPath(manifest, 0);
        int result;
        using (FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            result = RunVerifierOnly(manifest);
        }
        CleanupFixtures();
        Assert.Equal(1, result);
    }

    [Fact]
    public void RejectsEvidenceHeldOpenByWriterWithoutSharing()
    {
        Assert.True(OperatingSystem.IsWindows(), "The sharing-mode contract is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        string path = EvidencePath(manifest, 0);
        int result;
        using (FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            result = RunVerifierOnly(manifest);
        }
        CleanupFixtures();
        Assert.Equal(1, result);
    }

    [Fact, SupportedOSPlatform("windows")]
    public void RejectsArtifactWithLockedByteRange()
    {
        Assert.True(OperatingSystem.IsWindows(), "The byte-range locking contract is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        string path = ArtifactPath(manifest, 0);
        int result;
        using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            locked.Lock(0, 1);
            try { result = RunVerifierOnly(manifest); }
            finally { locked.Unlock(0, 1); }
        }
        CleanupFixtures();
        Assert.Equal(1, result);
    }

    [Fact]
    public void RejectsHardLinkAliasByWindowsFileIdentity()
    {
        Assert.True(OperatingSystem.IsWindows(), "Hard-link identity is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        JsonObject first = Artifact(manifest, 0);
        JsonObject second = Artifact(manifest, 1);
        string firstPath = ArtifactPath(manifest, 0);
        string aliasPath = Path.Combine(Path.GetDirectoryName(firstPath)!, "hard-link-alias.txt");
        Assert.True(CreateHardLink(aliasPath, firstPath), $"Could not create hard link ({Marshal.GetLastWin32Error()}).");
        second["path"] = RelativeToRoot(aliasPath);
        SetArtifactHashAndSize(second, aliasPath);
        int result = Run(manifest);
        Assert.Equal(1, result);
    }

    [Fact]
    public void RejectsSingleArtifactWithUnreferencedHardLinkAlias()
    {
        Assert.True(OperatingSystem.IsWindows(), "Hard-link identity is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        string artifactPath = ArtifactPath(manifest, 0);
        string aliasPath = Path.Combine(Path.GetDirectoryName(artifactPath)!, "unreferenced-artifact-hard-link.txt");
        Assert.True(CreateHardLink(aliasPath, artifactPath), $"Could not create hard link ({Marshal.GetLastWin32Error()}).");
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void RejectsProvenanceSidecarHardLink()
    {
        Assert.True(OperatingSystem.IsWindows(), "Hard-link identity is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        JsonObject first = Artifact(manifest, 0);
        JsonObject second = Artifact(manifest, 1);
        string aliasPath = Path.Combine(Path.GetDirectoryName(ProvenancePath(first))!, "provenance-hard-link.json");
        Assert.True(CreateHardLink(aliasPath, ProvenancePath(first)), $"Could not create hard link ({Marshal.GetLastWin32Error()}).");
        JsonObject reference = (JsonObject)second["provenance"]!;
        reference["path"] = RelativeToRoot(aliasPath);
        reference["sha256"] = Hash(aliasPath);
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void RejectsSingleProvenanceSidecarWithUnreferencedHardLinkAlias()
    {
        Assert.True(OperatingSystem.IsWindows(), "Hard-link identity is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        string provenancePath = ProvenancePath(Artifact(manifest, 0));
        string aliasPath = Path.Combine(Path.GetDirectoryName(provenancePath)!, "unreferenced-provenance-hard-link.json");
        Assert.True(CreateHardLink(aliasPath, provenancePath), $"Could not create hard link ({Marshal.GetLastWin32Error()}).");
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void RejectsProvenanceSidecarHeldOpenByWriterWithoutSharing()
    {
        Assert.True(OperatingSystem.IsWindows(), "The sharing-mode contract is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        string path = ProvenancePath(Artifact(manifest, 0));
        int result;
        using (FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            result = RunVerifierOnly(manifest);
        }
        CleanupFixtures();
        Assert.Equal(1, result);
    }

    [Fact, SupportedOSPlatform("windows")]
    public void RejectsProvenanceSidecarWithLockedByteRange()
    {
        Assert.True(OperatingSystem.IsWindows(), "The byte-range locking contract is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        string path = ProvenancePath(Artifact(manifest, 0));
        int result;
        using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            locked.Lock(0, 1);
            try { result = RunVerifierOnly(manifest); }
            finally { locked.Unlock(0, 1); }
        }
        CleanupFixtures();
        Assert.Equal(1, result);
    }

    [Fact]
    public void RejectsActualNtfsAlternateDataStream()
    {
        Assert.True(OperatingSystem.IsWindows(), "Alternate data streams are an NTFS filesystem contract.");
        JsonObject manifest = ValidManifest();
        JsonObject artifact = Artifact(manifest, 0);
        string basePath = ArtifactPath(manifest, 0);
        string streamPath = basePath + ":m12-proof.txt";
        File.WriteAllText(streamPath, "actual NTFS alternate data stream\n");
        artifact["path"] = RelativeToRoot(streamPath);
        SetArtifactHashAndSize(artifact, streamPath);
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void RejectsProvenanceSidecarAlternateDataStream()
    {
        Assert.True(OperatingSystem.IsWindows(), "Alternate data streams are an NTFS filesystem contract.");
        JsonObject manifest = ValidManifest();
        JsonObject reference = (JsonObject)Artifact(manifest, 0)["provenance"]!;
        string streamPath = ProvenancePath(Artifact(manifest, 0)) + ":m12-proof.txt";
        File.WriteAllText(streamPath, "actual NTFS alternate data stream\n");
        reference["path"] = RelativeToRoot(streamPath);
        reference["sha256"] = Hash(streamPath);
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void RejectsReparsePointAncestor()
    {
        Assert.True(OperatingSystem.IsWindows(), "The reparse-point contract is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        string targetDirectory = Path.GetDirectoryName(ArtifactPath(manifest, 0))!;
        string certificationRoot = Path.Combine(FindRoot(), "release", "certification");
        string linkName = $".m12-reparse-{Guid.NewGuid():N}";
        string linkDirectory = Path.Combine(certificationRoot, linkName);
        Assert.True(CreateReparseDirectory(linkDirectory, targetDirectory), "A symlink or junction is required to exercise fail-closed ancestor handling.");
        string fileName = Path.GetFileName(ArtifactPath(manifest, 0));
        Artifact(manifest, 0)["path"] = $"release/certification/{linkName}/{fileName}";
        int result;
        try { result = RunVerifierOnly(manifest); }
        finally
        {
            try { Directory.Delete(linkDirectory); } catch (DirectoryNotFoundException) { }
            CleanupFixtures();
        }
        Assert.Equal(1, result);
    }

    [Fact]
    public void RejectsProvenanceSidecarReparsePointAncestor()
    {
        Assert.True(OperatingSystem.IsWindows(), "The reparse-point contract is a Windows filesystem contract.");
        JsonObject manifest = ValidManifest();
        string targetDirectory = Path.GetDirectoryName(ProvenancePath(Artifact(manifest, 0)))!;
        string certificationRoot = Path.Combine(FindRoot(), "release", "certification");
        string linkName = $".m12-reparse-{Guid.NewGuid():N}";
        string linkDirectory = Path.Combine(certificationRoot, linkName);
        Assert.True(CreateReparseDirectory(linkDirectory, targetDirectory), "A symlink or junction is required to exercise fail-closed ancestor handling.");
        string fileName = Path.GetFileName(ProvenancePath(Artifact(manifest, 0)));
        ((JsonObject)Artifact(manifest, 0)["provenance"]!)["path"] = $"release/certification/{linkName}/{fileName}";
        int result;
        try { result = RunVerifierOnly(manifest); }
        finally
        {
            try { Directory.Delete(linkDirectory); } catch (DirectoryNotFoundException) { }
            CleanupFixtures();
        }
        Assert.Equal(1, result);
    }

    [Fact]
    public void RejectsSyntheticExternalCaseWithWrongProducer()
    {
        JsonObject baseline = SyntheticAllImplementedReleaseManifest();
        BindSyntheticInstallerProduct(manifest: baseline);
        (int baselineExit, string baselineOutput) = RunWithOutput(baseline, "Release");
        Assert.Equal(1, baselineExit);
        Assert.Contains("M12-EVIDENCE-CASE", baselineOutput, StringComparison.Ordinal);
        JsonObject manifest = SyntheticAllImplementedReleaseManifest();
        BindSyntheticInstallerProduct(manifest);
        JsonObject artifact = SyntheticExternalCaseArtifact(manifest, "m12-mcp-protocol");
        artifact["producerId"] = "m12-external-producer";
        UpdateProvenance(artifact, provenance => provenance["producerId"] = "m12-external-producer");
        (int exitCode, string output) = RunWithOutput(manifest, "Release");
        Assert.Equal(1, exitCode);
        Assert.Contains("M12-EVIDENCE-ARTIFACT", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsSyntheticExternalCaseWithWrongArtifactKind()
    {
        JsonObject baseline = SyntheticAllImplementedReleaseManifest();
        BindSyntheticInstallerProduct(manifest: baseline);
        (int baselineExit, string baselineOutput) = RunWithOutput(baseline, "Release");
        Assert.Equal(1, baselineExit);
        Assert.Contains("M12-EVIDENCE-CASE", baselineOutput, StringComparison.Ordinal);
        JsonObject manifest = SyntheticAllImplementedReleaseManifest();
        BindSyntheticInstallerProduct(manifest);
        JsonObject artifact = SyntheticExternalCaseArtifact(manifest, "m12-mcp-protocol");
        artifact["kind"] = "external-installer-bundle";
        UpdateProvenance(artifact, provenance => provenance["kind"] = "external-installer-bundle");
        (int exitCode, string output) = RunWithOutput(manifest, "Release");
        Assert.Equal(1, exitCode);
        Assert.Contains("M12-EVIDENCE-ARTIFACT", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsSyntheticReleaseCasesBoundToDifferentProducts()
    {
        JsonObject manifest = SyntheticAllImplementedReleaseManifest();
        foreach (string caseId in new[] { "m12-installer-install", "m12-installer-upgrade", "m12-installer-recovery", "m12-installer-uninstall" })
            BindSyntheticProduct(manifest, SyntheticExternalCaseArtifact(manifest, caseId), "m12-shared-product");
        BindSyntheticProduct(manifest, SyntheticExternalCaseArtifact(manifest, "m12-installer-upgrade"), "m12-different-product");
        JsonArray lanes = (JsonArray)manifest["lanes"]!;
        JsonNode installerLane = lanes.Single(lane => lane!["laneId"]!.GetValue<string>() == "installer")!;
        lanes.Remove(installerLane);
        lanes.Insert(0, installerLane);
        (int exitCode, string output) = RunWithOutput(manifest, "Release");
        Assert.Equal(1, exitCode);
        Assert.Contains("M12-EVIDENCE-ARTIFACT", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsExactArtifactSizeUpperBoundary()
    {
        JsonObject manifest = ValidManifest();
        string path = ArtifactPath(manifest, 0);
        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) stream.SetLength(268_435_456);
        SetArtifactHashAndSize(Artifact(manifest, 0), path);
        Assert.Equal(0, Run(manifest));
    }

    [Fact]
    public void RejectsArtifactOneByteOverSizeUpperBoundary()
    {
        JsonObject manifest = ValidManifest();
        string path = ArtifactPath(manifest, 0);
        using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) stream.SetLength(268_435_457);
        SetArtifactHashAndSize(Artifact(manifest, 0), path);
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void AcceptsGeneratedAtInsideCurrentWindow()
    {
        JsonObject manifest = ValidManifest();
        SetGeneratedAt(manifest, DateTimeOffset.UtcNow.AddMinutes(4));
        Assert.Equal(0, Run(manifest));
    }

    [Fact]
    public void RejectsGeneratedAtMoreThanFiveMinutesInFuture()
    {
        JsonObject manifest = ValidManifest();
        SetGeneratedAt(manifest, DateTimeOffset.UtcNow.AddMinutes(6));
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void RejectsGeneratedAtInsideWindowWhenFilesAreOutsideThatRunWindow()
    {
        JsonObject manifest = ValidManifest();
        SetGeneratedAt(manifest, DateTimeOffset.UtcNow.AddHours(-23).AddMinutes(-59));
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void RejectsGeneratedAtOlderThanTwentyFourHours()
    {
        JsonObject manifest = ValidManifest();
        SetGeneratedAt(manifest, DateTimeOffset.UtcNow.AddHours(-24).AddSeconds(-1));
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void RejectsEveryProvenanceToArtifactBindingMismatch()
    {
        string[] fields = ["artifactId", "kind", "producerId", "artifactSha256", "artifactSize", "commitSha", "runId", "environmentId"];
        foreach (string field in fields)
        {
            string selected = field;
            AssertReject(manifest =>
            {
                UpdateProvenance(Artifact(manifest, 0), provenance => provenance[selected] = selected switch
                {
                    "artifactId" => "m12-local-not-the-artifact",
                    "kind" => "not-the-kind",
                    "producerId" => "not-the-producer",
                    "artifactSha256" => new string('0', 64),
                    "artifactSize" => 999L,
                    "commitSha" => new string('0', 40),
                    "runId" => Guid.NewGuid().ToString(),
                    "environmentId" => "not-the-environment",
                    _ => throw new InvalidOperationException()
                });
            });
        }
    }

    [Fact]
    public void RejectsEvidenceToCaseBindingMismatch()
    {
        // Each mutation uses its own manifest/fixture so the evidence hash is
        // recomputed over the actual bytes that the verifier reads.
        foreach (string field in new[] { "caseId", "runId", "commitSha", "environmentId" })
        {
            JsonObject manifest = ValidManifest();
            JsonObject evidence = (JsonObject)FirstCase(manifest)["evidence"]![0]!;
            string evidencePath = EvidencePath(manifest, 0);
            JsonObject payload = (JsonObject)JsonNode.Parse(File.ReadAllText(evidencePath))!;
            payload[field] = field switch
            {
                "caseId" => "m12-release-build",
                "runId" => Guid.NewGuid().ToString(),
                "commitSha" => new string('0', 40),
                "environmentId" => "not-the-environment",
                _ => throw new InvalidOperationException()
            };
            File.WriteAllText(evidencePath, payload.ToJsonString());
            evidence["sha256"] = Hash(evidencePath);
            try { Assert.Equal(1, RunVerifierOnly(manifest)); }
            finally { CleanupFixtures(); }
        }
    }

    [Fact]
    public void RejectsUnknownArtifactReference()
    {
        AssertReject(manifest => ((JsonArray)FirstCase(manifest)["artifactIds"]!)[0] = "m12-local-unknown-artifact");
    }

    [Fact]
    public void RejectsAdditionalUnreferencedArtifact()
    {
        JsonObject manifest = ValidManifest();
        JsonObject source = Artifact(manifest, 0);
        string fixture = Path.GetDirectoryName(ArtifactPath(manifest, 0))!;
        string extraPath = Path.Combine(fixture, "unreferenced.txt");
        File.WriteAllText(extraPath, "unreferenced artifact\n");
        string hash = Hash(extraPath);
        long size = new FileInfo(extraPath).Length;
        JsonObject extra = (JsonObject)source.DeepClone();
        extra["artifactId"] = "m12-local-unreferenced-artifact";
        extra["path"] = RelativeToRoot(extraPath);
        extra["sha256"] = hash;
        extra["size"] = size;
        string sidecarPath = Path.Combine(fixture, "unreferenced-provenance.json");
        File.WriteAllText(sidecarPath, new JsonObject
        {
            ["artifactId"] = extra["artifactId"]!.GetValue<string>(), ["kind"] = extra["kind"]!.GetValue<string>(), ["producerId"] = extra["producerId"]!.GetValue<string>(),
            ["artifactSha256"] = hash, ["artifactSize"] = size, ["commitSha"] = extra["commitSha"]!.GetValue<string>(), ["runId"] = extra["runId"]!.GetValue<string>(),
            ["environmentId"] = extra["environmentId"]!.GetValue<string>(), ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        }.ToJsonString());
        extra["provenance"] = new JsonObject { ["path"] = RelativeToRoot(sidecarPath), ["sha256"] = Hash(sidecarPath) };
        ((JsonArray)manifest["artifacts"]!).Add(extra);
        Assert.Equal(1, Run(manifest));
    }

    [Fact]
    public void SyntheticAllImplementedReleaseManifestBindsAllThirtyFiveCasesToUniqueInstances()
    {
        try
        {
            JsonObject manifest = SyntheticAllImplementedReleaseManifest();
            JsonArray lanes = (JsonArray)manifest["lanes"]!;
            JsonArray environments = (JsonArray)manifest["environments"]!;
            var cases = lanes.SelectMany(lane => ((JsonArray)((JsonObject)lane!)["cases"]!).Select(node => (JsonObject)node!)).ToArray();
            Assert.Equal(35, cases.Length);
            Assert.Equal(35, cases.Select(testCase => testCase["caseId"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(35, cases.Select(testCase => testCase["environmentId"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(35, environments.Count);
            Assert.All(cases, testCase => Assert.Equal("passed", testCase["status"]!.GetValue<string>()));
            Assert.Equal("Release", manifest["profile"]!.GetValue<string>());
            Assert.True(manifest["result"]!["releaseEvidence"]!.GetValue<bool>());

            // Assert the contradictory requirements are represented by separate
            // executable environment instances rather than flattened facts.
            JsonObject Facts(string caseId) => (JsonObject)environments.Single(environment =>
                ((JsonObject)environment!)["environmentId"]!.GetValue<string>() == cases.Single(testCase => testCase["caseId"]!.GetValue<string>() == caseId)["environmentId"]!.GetValue<string>())!["facts"]!;
            Assert.Equal(15, Facts("m12-sqlserver-2019-passive")["sqlServerVersion"]!.GetValue<int>());
            Assert.Equal(16, Facts("m12-sqlserver-2022-passive")["sqlServerVersion"]!.GetValue<int>());
            Assert.Equal(17, Facts("m12-sqlserver-2025-passive")["sqlServerVersion"]!.GetValue<int>());
            Assert.Equal("minimum", Facts("m12-postgresql-min-patch")["postgresqlPatch"]!.GetValue<string>());
            Assert.Equal("current", Facts("m12-postgresql-current-patch")["postgresqlPatch"]!.GetValue<string>());
            Assert.Equal("Edge", Facts("m12-edge-browser")["browser"]!.GetValue<string>());
            Assert.Equal("Chrome", Facts("m12-chrome-browser")["browser"]!.GetValue<string>());
            Assert.Equal("install", Facts("m12-installer-install")["installer"]!.GetValue<string>());
            Assert.Equal("upgrade", Facts("m12-installer-upgrade")["installer"]!.GetValue<string>());
            Assert.Equal("report", Facts("m12-report-contract")["reportKind"]!.GetValue<string>());
            Assert.Equal("export", Facts("m12-export-contract")["reportKind"]!.GetValue<string>());
        }
        finally { CleanupFixtures(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static bool CreateHardLink(string alias, string existing) => CreateHardLink(alias, existing, IntPtr.Zero);

    private static bool CreateReparseDirectory(string linkDirectory, string targetDirectory)
    {
        try { Directory.CreateSymbolicLink(linkDirectory, targetDirectory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (Directory.Exists(linkDirectory) && (File.GetAttributes(linkDirectory) & FileAttributes.ReparsePoint) != 0) return true;
        if (!OperatingSystem.IsWindows()) return false;
        var start = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(linkDirectory);
        start.ArgumentList.Add(targetDirectory);
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(linkDirectory) && (File.GetAttributes(linkDirectory) & FileAttributes.ReparsePoint) != 0;
    }

    private static JsonObject Artifact(JsonObject manifest, int index) => (JsonObject)((JsonArray)manifest["artifacts"]!)[index]!;

    private static JsonObject SyntheticExternalCaseArtifact(JsonObject manifest, string caseId)
    {
        JsonObject testCase = ((JsonArray)manifest["lanes"]!).SelectMany(lane => ((JsonArray)((JsonObject)lane!) ["cases"]!).Select(node => (JsonObject)node!)).Single(test => test["caseId"]!.GetValue<string>() == caseId);
        string artifactId = ((JsonArray)testCase["artifactIds"]!)[0]!.GetValue<string>();
        return ((JsonArray)manifest["artifacts"]!).Select(node => (JsonObject)node!).Single(artifact => artifact["artifactId"]!.GetValue<string>() == artifactId);
    }

    private static JsonObject AddPhysicalProduct(JsonObject manifest, string productId = "m12-tested-product", bool bindArtifact = true)
    {
        JsonObject artifact = Artifact(manifest, 0);
        string fixture = Path.GetDirectoryName(ArtifactPath(manifest, 0))!;
        string productPath = Path.Combine(fixture, $"{productId}.exe");
        File.WriteAllText(productPath, "signed Burn bootstrapper product bytes\n");
        JsonArray products = manifest["products"] as JsonArray ?? new JsonArray();
        manifest["products"] = products;
        products.Add(new JsonObject
        {
            ["productId"] = productId,
            ["path"] = RelativeToRoot(productPath),
            ["sha256"] = Hash(productPath),
            ["size"] = new FileInfo(productPath).Length,
            ["runId"] = manifest["runId"]!.GetValue<string>(),
            ["commitSha"] = manifest["commitSha"]!.GetValue<string>(),
            ["environmentId"] = "local-windows-1"
        });
        if (bindArtifact)
        {
            artifact["productId"] = productId;
            UpdateProvenance(artifact, provenance => provenance["productId"] = productId);
        }
        return manifest;
    }

    private static void BindSyntheticProduct(JsonObject manifest, JsonObject artifact, string productId)
    {
        JsonArray products = manifest["products"] as JsonArray ?? new JsonArray();
        manifest["products"] = products;
        if (!products.Any(product => product!["productId"]!.GetValue<string>() == productId))
        {
            string fixture = Path.GetDirectoryName(ArtifactPath(manifest, 0))!;
            string productPath = Path.Combine(fixture, $"{productId}.msi");
            File.WriteAllText(productPath, $"synthetic physical product {productId}\n");
            products.Add(new JsonObject
            {
                ["productId"] = productId, ["path"] = RelativeToRoot(productPath), ["sha256"] = Hash(productPath),
                ["size"] = new FileInfo(productPath).Length, ["runId"] = manifest["runId"]!.GetValue<string>(),
                ["commitSha"] = manifest["commitSha"]!.GetValue<string>(), ["environmentId"] = artifact["environmentId"]!.GetValue<string>()
            });
        }
        artifact["productId"] = productId;
        UpdateProvenance(artifact, provenance => provenance["productId"] = productId);
    }

    private static void BindSyntheticInstallerProduct(JsonObject manifest)
    {
        foreach (string caseId in new[] { "m12-installer-install", "m12-installer-upgrade", "m12-installer-recovery", "m12-installer-uninstall" })
            BindSyntheticProduct(manifest, SyntheticExternalCaseArtifact(manifest, caseId), "m12-shared-product");
    }

    private static string ArtifactPath(JsonObject manifest, int index) =>
        Path.Combine(FindRoot(), Artifact(manifest, index)["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));

    private static string EvidencePath(JsonObject manifest, int index) =>
        Path.Combine(FindRoot(), ((JsonObject)((JsonArray)FirstCase(manifest)["evidence"]!)[index]!) ["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));

    private static string RelativeToRoot(string path) => Path.GetRelativePath(FindRoot(), path).Replace(Path.DirectorySeparatorChar, '/');

    private static void SetArtifactHashAndSize(JsonObject artifact, string path)
    {
        string hash = Hash(path);
        long size = new FileInfo(path).Length;
        artifact["sha256"] = hash;
        artifact["size"] = size;
        UpdateProvenance(artifact, payload =>
        {
            payload["artifactSha256"] = hash;
            payload["artifactSize"] = size;
        });
    }

    private static void SetGeneratedAt(JsonObject manifest, DateTimeOffset generatedAt)
    {
        string text = generatedAt.ToUniversalTime().ToString("O");
        manifest["generatedAtUtc"] = text;
        foreach (JsonNode? artifactNode in (JsonArray)manifest["artifacts"]!)
            UpdateProvenance((JsonObject)artifactNode!, payload => payload["createdAtUtc"] = text);
    }

    private static string ProvenancePath(JsonObject artifact) =>
        Path.Combine(FindRoot(), ((JsonObject)artifact["provenance"]!)["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));

    private static void UpdateProvenance(JsonObject artifact, Action<JsonObject> update)
    {
        JsonObject reference = (JsonObject)artifact["provenance"]!;
        string path = ProvenancePath(artifact);
        JsonObject payload = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        update(payload);
        File.WriteAllText(path, payload.ToJsonString());
        reference["sha256"] = Hash(path);
    }

    private static int RunVerifierOnly(JsonObject manifest)
    {
        string root = FindRoot();
        string path = Path.Combine(root, "release", "certification", $".m12-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        try
        {
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-File"); start.ArgumentList.Add(Path.Combine(root, "tools", "verify-test-results.ps1"));
            start.ArgumentList.Add("-ManifestPath"); start.ArgumentList.Add(path); start.ArgumentList.Add("-Profile"); start.ArgumentList.Add("Local"); start.ArgumentList.Add("-RepositoryRoot"); start.ArgumentList.Add(root);
            using Process process = Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        finally { File.Delete(path); }
    }

    private static void CleanupFixtures()
    {
        string certificationRoot = Path.Combine(FindRoot(), "release", "certification");
        foreach (string fixture in Directory.GetDirectories(certificationRoot, ".m12-test-*"))
        {
            try { Directory.Delete(fixture, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static JsonObject SyntheticAllImplementedReleaseManifest()
    {
        string root = FindRoot();
        string fixtureName = $".m12-test-{Guid.NewGuid():N}";
        string fixture = Path.Combine(root, "release", "certification", fixtureName);
        Directory.CreateDirectory(fixture);
        string commit = Git(root, "rev-parse", "HEAD").Trim();
        string runId = Guid.NewGuid().ToString();
        string generatedAt = DateTimeOffset.UtcNow.ToString("O");
        JsonObject matrix = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(root, "release", "certification", "m12-certification-matrix.v1.json")))!;
        var environments = new JsonArray();
        var artifacts = new JsonArray();
        var lanes = new JsonArray();
        int number = 0;
        foreach (JsonNode? laneNode in (JsonArray)matrix["lanes"]!)
        {
            JsonObject matrixLane = (JsonObject)laneNode!;
            var cases = new JsonArray();
            foreach (JsonNode? matrixCaseNode in (JsonArray)matrixLane["cases"]!)
            {
                JsonObject matrixCase = (JsonObject)matrixCaseNode!;
                string caseId = matrixCase["caseId"]!.GetValue<string>();
                JsonObject requirement = (JsonObject)matrixCase["environment"]!;
                string identity = ((JsonArray)requirement["identities"]!).Select(node => node!.GetValue<string>()).First(value => value.StartsWith("release-windows-server-", StringComparison.Ordinal));
                string environmentId = $"synthetic-{number:00}-{caseId[4..]}";
                string os = identity.EndsWith("2022", StringComparison.Ordinal) ? "Windows Server 2022" : "Windows Server 2025";
                var facts = new JsonObject { ["os"] = os, ["architecture"] = "x64" };
                JsonObject predicates = (JsonObject)requirement["factPredicates"]!;
                foreach (KeyValuePair<string, JsonNode?> predicate in predicates) facts[predicate.Key] = predicate.Value?.DeepClone();
                foreach (JsonNode? requiredNode in (JsonArray)requirement["requiredFacts"]!)
                {
                    string required = requiredNode!.GetValue<string>();
                    if (facts[required] is null) facts[required] = SyntheticFact(required);
                }
                environments.Add(new JsonObject { ["environmentId"] = environmentId, ["identity"] = identity, ["os"] = os, ["architecture"] = "x64", ["facts"] = facts });

                string artifactId = $"m12-synthetic-{number:00}";
                string artifactPath = Path.Combine(fixture, $"artifact-{number:00}.txt");
                File.WriteAllText(artifactPath, $"synthetic evidence for {caseId}\n");
                string relativeArtifact = $"release/certification/{fixtureName}/artifact-{number:00}.txt";
                string artifactHash = Hash(artifactPath);
                long artifactSize = new FileInfo(artifactPath).Length;
                string artifactKind = ((JsonArray)matrixCase["artifactKinds"]!)[0]!.GetValue<string>();
                string artifactProducer = matrixCase["producerId"]!.GetValue<string>();
                string provenanceName = $"provenance-{number:00}.json";
                string provenancePath = Path.Combine(fixture, provenanceName);
                File.WriteAllText(provenancePath, new JsonObject { ["artifactId"] = artifactId, ["kind"] = artifactKind, ["producerId"] = artifactProducer, ["artifactSha256"] = artifactHash, ["artifactSize"] = artifactSize, ["commitSha"] = commit, ["runId"] = runId, ["environmentId"] = environmentId, ["createdAtUtc"] = generatedAt }.ToJsonString());
                artifacts.Add(new JsonObject
                {
                    ["artifactId"] = artifactId, ["kind"] = artifactKind, ["producerId"] = artifactProducer,
                    ["path"] = relativeArtifact, ["sha256"] = artifactHash, ["size"] = artifactSize, ["runId"] = runId, ["commitSha"] = commit, ["environmentId"] = environmentId,
                    ["provenance"] = new JsonObject { ["path"] = $"release/certification/{fixtureName}/{provenanceName}", ["sha256"] = Hash(provenancePath) }
                });
                string evidencePath = Path.Combine(fixture, $"evidence-{number:00}.json");
                var evidencePayload = new JsonObject { ["caseId"] = caseId, ["status"] = "passed", ["executions"] = 1, ["skipped"] = 0, ["notRun"] = 0, ["failed"] = 0, ["runId"] = runId, ["commitSha"] = commit, ["environmentId"] = environmentId };
                File.WriteAllText(evidencePath, evidencePayload.ToJsonString());
                string relativeEvidence = $"release/certification/{fixtureName}/evidence-{number:00}.json";
                cases.Add(new JsonObject
                {
                    ["caseId"] = caseId, ["environmentId"] = environmentId, ["status"] = "passed", ["executions"] = 1, ["skipped"] = 0, ["notRun"] = 0, ["failed"] = 0,
                    ["artifactIds"] = new JsonArray(artifactId), ["evidence"] = new JsonArray(new JsonObject { ["path"] = relativeEvidence, ["sha256"] = Hash(evidencePath), ["format"] = "json", ["runId"] = runId, ["commitSha"] = commit, ["environmentId"] = environmentId })
                });
                number++;
            }
            lanes.Add(new JsonObject { ["laneId"] = matrixLane["laneId"]!.GetValue<string>(), ["cases"] = cases });
        }
        return new JsonObject
        {
            ["$schema"] = "m12-certification-manifest.v1.schema.json", ["schemaVersion"] = 1, ["matrixId"] = "sqlobserver-m12", ["profile"] = "Release", ["commitSha"] = commit,
            ["generatedAtUtc"] = generatedAt, ["runId"] = runId, ["environments"] = environments, ["artifacts"] = artifacts, ["lanes"] = lanes,
            ["result"] = new JsonObject { ["releaseEvidence"] = true, ["missing"] = 0, ["unavailable"] = 0, ["skipped"] = 0, ["notRun"] = 0, ["failed"] = 0 }
        };
    }

    private static JsonValue SyntheticFact(string name) => name switch
    {
        "windowsServerVersion" => JsonValue.Create("2022"),
        "postgresqlVersion" => JsonValue.Create("18"),
        "postgresqlPatch" => JsonValue.Create("current"),
        "sqlServerVersion" => JsonValue.Create(16),
        _ => JsonValue.Create(true)
    };

    private static JsonObject ValidManifest()
    {
        string root = FindRoot();
        string work = Path.Combine(root, "release", "certification", $".m12-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        string commit = Git(root, "rev-parse", "HEAD").Trim();
        string runId = Guid.NewGuid().ToString();
        var lanes = new JsonArray();
        var artifacts = new JsonArray();
        foreach (string laneId in new[] { "repository-contract", "release-build", "unit", "api", "security", "performance", "end-to-end", "frontend" })
        {
            string caseId = laneId switch
            {
                "repository-contract" => "m12-repository-contract",
                "release-build" => "m12-release-build",
                "unit" => "m12-unit-tests",
                "api" => "m12-api-contract-tests",
                "security" => "m12-security-tests",
                "performance" => "m12-performance-tests",
                "end-to-end" => "m12-e2e-composition",
                _ => "m12-frontend"
            };
            string evidenceName = $"evidence-{laneId}.json";
            string artifactName = $"artifact-{laneId}.txt";
            string artifactPath = Path.Combine(work, artifactName);
            File.WriteAllText(artifactPath, $"release-test-artifact-{laneId}\n");
            string relativeArtifact = $"release/certification/{Path.GetFileName(work)}/{artifactName}";
            string artifactHash = Hash(artifactPath);
            string artifactId = $"m12-local-{laneId}";
            string provenanceName = $"provenance-{laneId}.json";
            string provenancePath = Path.Combine(work, provenanceName);
            var provenancePayload = new JsonObject { ["artifactId"] = artifactId, ["kind"] = "local-validation-bundle", ["producerId"] = "m12-local-validation", ["artifactSha256"] = artifactHash, ["artifactSize"] = new FileInfo(artifactPath).Length, ["commitSha"] = commit, ["runId"] = runId, ["environmentId"] = "local-windows-1", ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("O") };
            File.WriteAllText(provenancePath, provenancePayload.ToJsonString());
            artifacts.Add(new JsonObject { ["artifactId"] = artifactId, ["kind"] = "local-validation-bundle", ["producerId"] = "m12-local-validation", ["path"] = relativeArtifact, ["sha256"] = artifactHash, ["size"] = new FileInfo(artifactPath).Length, ["runId"] = runId, ["commitSha"] = commit, ["environmentId"] = "local-windows-1", ["provenance"] = new JsonObject { ["path"] = $"release/certification/{Path.GetFileName(work)}/{provenanceName}", ["sha256"] = Hash(provenancePath) } });
            var evidencePayload = new JsonObject
            {
                ["caseId"] = caseId, ["status"] = "passed", ["executions"] = 1,
                ["skipped"] = 0, ["notRun"] = 0, ["failed"] = 0,
                ["runId"] = runId, ["commitSha"] = commit, ["environmentId"] = "local-windows-1"
            };
            File.WriteAllText(Path.Combine(work, evidenceName), evidencePayload.ToJsonString());
            string relativeEvidence = $"release/certification/{Path.GetFileName(work)}/{evidenceName}";
            string evidenceHash = Hash(Path.Combine(root, relativeEvidence));
            lanes.Add(new JsonObject
            {
                ["laneId"] = laneId,
                ["cases"] = new JsonArray(new JsonObject
                {
                    ["caseId"] = caseId, ["environmentId"] = "local-windows-1", ["artifactIds"] = new JsonArray($"m12-local-{laneId}"), ["status"] = "passed", ["executions"] = 1,
                    ["skipped"] = 0, ["notRun"] = 0, ["failed"] = 0,
                    ["evidence"] = new JsonArray(new JsonObject
                    {
                        ["path"] = relativeEvidence, ["sha256"] = evidenceHash, ["format"] = "json",
                        ["runId"] = runId, ["commitSha"] = commit, ["environmentId"] = "local-windows-1"
                    })
                })
            });
        }
        return new JsonObject
        {
            ["$schema"] = "m12-certification-manifest.v1.schema.json", ["schemaVersion"] = 1,
            ["matrixId"] = "sqlobserver-m12", ["profile"] = "Local", ["commitSha"] = commit,
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["runId"] = runId,
            ["environments"] = new JsonArray(new JsonObject { ["environmentId"] = "local-windows-1", ["identity"] = "local-windows", ["os"] = "Windows 11", ["architecture"] = "x64", ["facts"] = new JsonObject { ["os"] = "Windows 11", ["architecture"] = "x64" } }),
            ["artifacts"] = artifacts,
            ["lanes"] = lanes,
            ["result"] = new JsonObject { ["releaseEvidence"] = false, ["missing"] = 0, ["unavailable"] = 0, ["skipped"] = 0, ["notRun"] = 0, ["failed"] = 0 }
        };
    }

    private static JsonObject FirstCase(JsonObject manifest) => (JsonObject)((JsonArray)manifest["lanes"]!)[0]! ["cases"]![0]!;

    private static void AssertReject(Action<JsonObject> mutation)
    {
        JsonObject manifest = ValidManifest();
        mutation(manifest);
        Assert.Equal(1, Run(manifest));
    }

    private static JsonObject WithXmlEvidence(JsonObject manifest, string format, string template)
    {
        JsonObject evidence = (JsonObject)((JsonArray)FirstCase(manifest)["evidence"]!)[0]!;
        string path = Path.Combine(FindRoot(), ((string)evidence["path"]!).Replace('/', Path.DirectorySeparatorChar));
        string run = manifest["runId"]!.GetValue<string>(); string commit = manifest["commitSha"]!.GetValue<string>();
        File.WriteAllText(path, template.Replace("{RUN}", run, StringComparison.Ordinal).Replace("{COMMIT}", commit, StringComparison.Ordinal));
        evidence["format"] = format; evidence["sha256"] = Hash(path); return manifest;
    }

    private static int Run(JsonObject manifest, string profile = "Local", string? expectedCommit = null)
    {
        string root = FindRoot();
        string path = Path.Combine(root, "release", "certification", $".m12-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        try
        {
            return RunVerifierProcess(path, profile, expectedCommit);
        }
        finally
        {
            File.Delete(path);
            CleanupFixtures();
        }
    }

    private static (int ExitCode, string Output) RunWithOutput(JsonObject manifest, string profile)
    {
        string root = FindRoot();
        string path = Path.Combine(root, "release", "certification", $".m12-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        try { return RunVerifierProcessWithOutput(path, profile); }
        finally { File.Delete(path); CleanupFixtures(); }
    }

    private static int RunVerifierProcess(string manifestPath, string profile, string? expectedCommit = null)
    {
        string root = FindRoot();
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(Path.Combine(root, "tools", "verify-test-results.ps1"));
        start.ArgumentList.Add("-ManifestPath"); start.ArgumentList.Add(manifestPath); start.ArgumentList.Add("-Profile"); start.ArgumentList.Add(profile); start.ArgumentList.Add("-RepositoryRoot"); start.ArgumentList.Add(root);
        if (expectedCommit is not null) { start.ArgumentList.Add("-ExpectedCommitSha"); start.ArgumentList.Add(expectedCommit); }
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static (int ExitCode, string Output) RunVerifierProcessWithOutput(string manifestPath, string profile)
    {
        string root = FindRoot();
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(Path.Combine(root, "tools", "verify-test-results.ps1"));
        start.ArgumentList.Add("-ManifestPath"); start.ArgumentList.Add(manifestPath); start.ArgumentList.Add("-Profile"); start.ArgumentList.Add(profile); start.ArgumentList.Add("-RepositoryRoot"); start.ArgumentList.Add(root);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error);
    }

    private static string RunMatrixOnly(out int exitCode)
    {
        string root = FindRoot();
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(Path.Combine(root, "tools", "verify-test-results.ps1")); start.ArgumentList.Add("-RepositoryRoot"); start.ArgumentList.Add(root); start.ArgumentList.Add("-MatrixOnly");
        using Process process = Process.Start(start)!; string output = process.StandardOutput.ReadToEnd(); process.WaitForExit(); exitCode = process.ExitCode; return output;
    }

    private static string FindRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName;
        return current ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!; string output = process.StandardOutput.ReadToEnd(); process.WaitForExit(); return output;
    }
}
