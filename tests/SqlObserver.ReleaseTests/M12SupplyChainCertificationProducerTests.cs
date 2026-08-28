using System.Diagnostics;

namespace SqlObserver.ReleaseTests;

public sealed class M12SupplyChainCertificationProducerTests
{
    [Theory]
    [InlineData("7.4", false)]
    [InlineData("7.5", true)]
    [InlineData("7.6.4", true)]
    public void PowerShellHostPolicyUsesSemanticMinimum(string versionText, bool expected)
    {
        Version version = Version.Parse(versionText);
        Assert.Equal(expected, version >= new Version(7, 5));
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        Assert.Contains("Assert-M12PowerShellVersion", source, StringComparison.Ordinal);
        Assert.Contains("-lt [Version]'7.5'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractOnlyRejectsTamperedPinnedInput()
    {
        string root = FindRoot(); string path = Path.Combine(root, "release/certification/m12-sbom-inputs.v1.json"); string original = File.ReadAllText(path);
        try { File.WriteAllText(path, original.Replace("m12-sbom-inputs", "m12-sbom-inputs-tampered", StringComparison.Ordinal)); Assert.NotEqual(0, Run(root, "-ContractOnly")); }
        finally { File.WriteAllText(path, File.ReadAllText(path).Replace("m12-sbom-inputs-tampered-tampered", "m12-sbom-inputs", StringComparison.Ordinal).Replace("m12-sbom-inputs-tampered", "m12-sbom-inputs", StringComparison.Ordinal)); }
    }

    [Fact]
    public void ContractOnlyRejectsTamperedExternalAssetManifest()
    {
        string root = FindRoot(); string path = Path.Combine(root, "release/certification/m12-supply-chain-contract.v1.assets.sha256"); string original = File.ReadAllText(path);
        try { File.WriteAllText(path, original.Replace("m12-sbom.v1.schema.json", "m12-sbom.v1.schema.json-tampered", StringComparison.Ordinal)); Assert.NotEqual(0, Run(root, "-ContractOnly")); }
        finally { File.WriteAllText(path, File.ReadAllText(path).Replace("m12-sbom.v1.schema.json-tampered", "m12-sbom.v1.schema.json", StringComparison.Ordinal)); }
    }

    [Fact]
    public void ReleaseEnvironmentOverridesFailClosedBeforeBuild()
    {
        string root = FindRoot();
        Assert.NotEqual(0, RunWithEnvironment(root, new Dictionary<string, string?> { ["DOTNET_ROOT"] = "C:\\untrusted", ["NODE_OPTIONS"] = "--require=C:\\untrusted.js", ["npm_config_registry"] = "https://untrusted.invalid" }));
        Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
    }

    [Fact]
    public void ProducerRefusesNonCanonicalRootAndNeverPublishes()
    {
        string root = FindRoot(); string fake = Path.Combine(Path.GetTempPath(), "m12-supply-chain-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fake);
        try { Assert.NotEqual(0, Run(root, "-RepositoryRoot", fake)); Assert.False(Directory.Exists(Path.Combine(fake, "TestResults"))); }
        finally { Directory.Delete(fake, true); }
    }

    [Fact]
    public void LocalProfileAndUnsupportedEnvironmentFailBeforePublication()
    {
        string root = FindRoot();
        Assert.NotEqual(0, Run(root, "-Profile", "Local"));
        Assert.NotEqual(0, RunWithEnvironment(root, new Dictionary<string, string?> { ["SQLOBSERVER_RELEASE_SUPPLY_CHAIN_ENVIRONMENT"] = "local-windows" }));
        Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
    }

    [Fact]
    public void ProducerSourceHasContainmentAndClosedPublicationTerms()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        Assert.Contains("Assert-M12NoDescendants", source, StringComparison.Ordinal); Assert.Contains("Assert-M12OutputFileIdentity", source, StringComparison.Ordinal); Assert.Contains("FileShare]::None", source, StringComparison.Ordinal); Assert.Contains("m12-supply-chain-contract.v1.assets.sha256", source, StringComparison.Ordinal); Assert.Contains("m12-sbom-provenance.json", source, StringComparison.Ordinal); Assert.Contains("generate-m12-sbom.mjs", source, StringComparison.Ordinal); Assert.Contains("--deps", source, StringComparison.Ordinal); Assert.Contains("--pnpm-list", source, StringComparison.Ordinal); Assert.Contains("--web-catalog", source, StringComparison.Ordinal); Assert.Contains("LiveReleaseSbomIsCompleteDeterministicAndCommitBound", source, StringComparison.Ordinal); Assert.Contains("inProgress", source, StringComparison.Ordinal); Assert.Contains("pending", source, StringComparison.Ordinal); Assert.DoesNotContain("WriteAllText", source, StringComparison.Ordinal); Assert.DoesNotContain("return $null", source, StringComparison.Ordinal); Assert.DoesNotContain("Get-Command git", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerBindsLiveTestToBothSbomsAndCreateNewResult()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "SQLOBSERVER_M12_SBOM_PATH", "SQLOBSERVER_M12_COMMIT_SHA", "SQLOBSERVER_M12_SBOM_SECOND_PATH", "SQLOBSERVER_M12_RESULT_PATH", "Assert-M12MachineResult", "FileMode]::CreateNew", "Assert-M12OutputTree" }) Assert.Contains(marker, source, StringComparison.Ordinal);
        string validate = File.ReadAllText(Path.Combine(FindRoot(), "tools/validate.ps1"));
        Assert.Contains("Category!=RequiresM12SupplyChainRelease", validate, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerUsesExactClosedGraphAndCanonicalPurls()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "Get-M12CanonicalPurl", "canonicalRoot", "edgeMap.Count-ne$refs.Count", "dependsOn|ref", "bom-ref|name|purl|type|version", "Assert-M12AssetPins", "Assert-M12SbomTypes", "CompareOrdinal" }) Assert.Contains(marker, source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerWiresExactClosedTrxShapeIntoLiveAssertion()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        int assertion = source.IndexOf("function Assert-M12Trx(", StringComparison.Ordinal);
        Assert.True(assertion >= 0);
        string liveAssertion = source[assertion..];
        Assert.Contains("Assert-M12TrxShape $locked.Bytes $ExpectedTestName", liveAssertion, StringComparison.Ordinal);
        foreach (string marker in new[] { "Assert-M12TrxAttributes", "Assert-M12TrxElement", "DtdProcessing]::Prohibit", "XmlResolver", "XmlNodeType]::Comment", "XmlNodeType]::ProcessingInstruction", "XmlNodeType]::CDATA", "TestDefinitions", "TestEntries", "TestLists", "runDeploymentRoot", "relativeResultsDirectory" }) Assert.Contains(marker, source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerProjectClosureUsesActiveStackCycleDefense()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        int closure = source.IndexOf("function Get-M12ExpectedManifestPaths", StringComparison.Ordinal);
        Assert.True(closure >= 0);
        string closureSource = source[closure..];
        foreach (string marker in new[] { "HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)", "$active.Contains($Current)", "Fail 'CONTRACT'", "$active.Add($Current)", "$active.Remove($Current)", "$completed.Contains($Current)", "$visit" }) Assert.Contains(marker, closureSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerPublicationReleasesQuarantineHandlesAndRechecksIdentityContent()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        Assert.Contains("$locked=Read-M12LockedBytes $entry.FullName $Root $MaximumJsonBytes", source, StringComparison.Ordinal);
        Assert.Contains("$locked.Bytes.Length-ne$expected.Bytes.Length", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12SameIdentity $expected.Identity $locked.Identity", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12SameIdentity $expected.Identity $after.Identity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$held=[Collections.Generic.List[IO.FileStream]]::new()", source, StringComparison.Ordinal);
        Assert.Contains("$catalogHandle=Open-M12ExecutableHandle $catalogGenerator", source, StringComparison.Ordinal);
        Assert.Contains("$catalogSnapshot=Read-M12LockedBytes $catalogGenerator $Root", source, StringComparison.Ordinal);
        Assert.Contains("$catalogAfter=Read-M12LockedBytes $catalogGenerator $Root", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12NoAlternateDataStreams", source, StringComparison.Ordinal);
        Assert.Contains("-Stream *", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractOnlyRejectsNtfsAlternateDataStream()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = FindRoot();
        string target = Path.Combine(root, "release/certification/m12-sbom-inputs.v1.json");
        string stream = target + ":m12-adversarial-data";
        try
        {
            using (FileStream ads = new(stream, FileMode.CreateNew, FileAccess.Write, FileShare.None)) ads.WriteByte(0x41);
            Assert.NotEqual(0, Run(root, "-ContractOnly"));
        }
        finally { File.Delete(stream); }
    }

    [Fact]
    public void ContractOnlyRejectsNtfsAlternateDataStreamOnAuthorityDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = FindRoot();
        string directory = Path.Combine(root, "release/certification");
        string stream = directory + ":m12-directory-adversarial-data";
        try
        {
            using (FileStream ads = new(stream, FileMode.CreateNew, FileAccess.Write, FileShare.None)) ads.WriteByte(0x41);
            Assert.NotEqual(0, Run(root, "-ContractOnly"));
        }
        finally { File.Delete(stream); }
    }

    private static int Run(string root, params string[] extra)
    {
        ProcessStartInfo start = new("pwsh") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"), "-RepositoryRoot", root, "-Profile", "Release", "-CaseId", "m12-sbom" }.Concat(extra)) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!; process.WaitForExit(); return process.ExitCode;
    }
    private static int RunWithEnvironment(string root, IDictionary<string, string?> values)
    {
        ProcessStartInfo start = new("pwsh") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (KeyValuePair<string, string?> pair in values) start.Environment[pair.Key] = pair.Value;
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"), "-RepositoryRoot", root, "-Profile", "Release", "-CaseId", "m12-sbom" }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!; process.WaitForExit(); return process.ExitCode;
    }
    private static string FindRoot() { string? current = AppContext.BaseDirectory; while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName; return current ?? throw new DirectoryNotFoundException(); }
}
