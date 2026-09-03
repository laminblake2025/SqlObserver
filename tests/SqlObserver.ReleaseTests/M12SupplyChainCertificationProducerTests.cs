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

    [Theory]
    [InlineData("run-m12-supply-chain-certification.ps1")]
    [InlineData("run-m12-sqlserver-certification.ps1")]
    [InlineData("run-m12-reports-certification.ps1")]
    [InlineData("run-m12-mcp-certification.ps1")]
    [InlineData("run-m12-observability-certification.ps1")]
    public void ReleaseHostCaptionChecksCallTheStringInstanceMethod(string scriptName)
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools", scriptName));
        Assert.Contains(".StartsWith(", source, StringComparison.Ordinal);
        Assert.Contains("Microsoft Windows Server 2025", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[String]::StartsWith(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePreflightUsesTheSingleLinkGitEntryPointAndDoesNotLeakGitScrubState()
    {
        string root = FindRoot();
        foreach (string scriptName in new[] { "run-m12-supply-chain-certification.ps1", "run-m12-reports-certification.ps1", "run-m12-observability-certification.ps1" })
        {
            string source = File.ReadAllText(Path.Combine(root, "tools", scriptName));
            Assert.Contains("Git/bin/git.exe", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Git/cmd/git.exe", source, StringComparison.Ordinal);
        }

        string supplyChain = File.ReadAllText(Path.Combine(root, "tools", "run-m12-supply-chain-certification.ps1"));
        int trustedTreeStart = supplyChain.IndexOf("function Assert-M12TrustedTree", StringComparison.Ordinal);
        int trustedTreeEnd = supplyChain.IndexOf("function Get-M12ExpectedManifestPaths", trustedTreeStart, StringComparison.Ordinal);
        Assert.True(trustedTreeStart >= 0 && trustedTreeEnd > trustedTreeStart);
        Assert.DoesNotContain("$env:GIT_", supplyChain[trustedTreeStart..trustedTreeEnd], StringComparison.Ordinal);

        string observability = File.ReadAllText(Path.Combine(root, "tools", "run-m12-observability-certification.ps1"));
        Assert.Contains("$PSVersionTable.PSVersion-lt[Version]'7.5'", observability, StringComparison.Ordinal);
        Assert.DoesNotContain("$PSVersionTable.PSVersion.Minor-ne 5", observability, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveProcessExitCodesComeFromTheOwnedNativeHandleAndGitScrubbingIsExact()
    {
        string root = FindRoot();
        foreach (string scriptName in new[]
        {
            "run-m12-supply-chain-certification.ps1",
            "run-m12-sqlserver-certification.ps1",
            "run-m12-reports-certification.ps1",
            "run-m12-mcp-certification.ps1",
            "run-m12-observability-certification.ps1"
        })
        {
            string source = File.ReadAllText(Path.Combine(root, "tools", scriptName));
            Assert.Contains("GetExitCodeProcess", source, StringComparison.Ordinal);
            Assert.Contains("public int ExitCode", source, StringComparison.Ordinal);
            Assert.DoesNotContain("$process.ExitCode", source, StringComparison.Ordinal);
            int boundedStart = source.IndexOf("function Invoke-M12BoundedProcess", StringComparison.Ordinal);
            if (boundedStart >= 0)
            {
                int boundedEnd = source.IndexOf("function ", boundedStart + 10, StringComparison.Ordinal);
                if (boundedEnd < 0) boundedEnd = source.Length;
                Assert.DoesNotContain("$p.ExitCode", source[boundedStart..boundedEnd], StringComparison.Ordinal);
            }
        }

        foreach (string scriptName in new[]
        {
            "run-m12-supply-chain-certification.ps1",
            "run-m12-reports-certification.ps1",
            "run-m12-observability-certification.ps1"
        })
        {
            string source = File.ReadAllText(Path.Combine(root, "tools", scriptName));
            string helperName = scriptName == "run-m12-reports-certification.ps1" ? "function Invoke-M12GitStatus" : "function Invoke-M12GitCommand";
            int start = source.IndexOf(helperName, StringComparison.Ordinal);
            int end = source.IndexOf("function ", start + 10, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start);
            string gitHelper = source[start..end];
            Assert.DoesNotContain("'include.path='", gitHelper, StringComparison.Ordinal);
            Assert.DoesNotContain("SetEnvironmentVariable($name,$null)", gitHelper, StringComparison.Ordinal);
        }

        string supplyChain = File.ReadAllText(Path.Combine(root, "tools", "run-m12-supply-chain-certification.ps1"));
        Assert.Contains("'COREPACK_ENABLE_DOWNLOAD_PROMPT'", supplyChain, StringComparison.Ordinal);
        Assert.Contains("COREPACK_ENABLE_DOWNLOAD_PROMPT='0'", supplyChain, StringComparison.Ordinal);
        Assert.Contains("nodejs/node_modules/corepack/dist/corepack.js", supplyChain, StringComparison.Ordinal);
        Assert.Contains("@($pnpmScript,'pnpm','--version') $webRoot", supplyChain, StringComparison.Ordinal);
        Assert.Contains("'--config.node-linker=hoisted'", supplyChain, StringComparison.Ordinal);
        const string hoistedInstall = "'pnpm','--config.node-linker=hoisted','--dir',$webRoot,'install','--frozen-lockfile','--package-import-method','copy'";
        Assert.Equal(4, supplyChain.Split(hoistedInstall, StringSplitOptions.None).Length - 1);
        const string hoistedBuild = "'pnpm','--config.node-linker=hoisted','--dir',$webRoot,'run','build'";
        Assert.Equal(4, supplyChain.Split(hoistedBuild, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("'install','--frozen-lockfile','--config.node-linker=hoisted'", supplyChain, StringComparison.Ordinal);
        Assert.DoesNotContain("'pnpm','--dir',$webRoot,'run','build'", supplyChain, StringComparison.Ordinal);
        Assert.Contains("(Hash (Join-Path $raw 'sbom-one.json'))-cne(Hash (Join-Path $raw 'sbom-two.json'))", supplyChain, StringComparison.Ordinal);
        Assert.Contains("(Hash $licenseOne)-cne(Hash $licenseTwo)", supplyChain, StringComparison.Ordinal);
        Assert.DoesNotContain("-cne Hash ", supplyChain, StringComparison.Ordinal);
        Assert.Contains("if(-not$script:M12LiveRoot){Assert-M12ToolEnvironmentClean}", supplyChain, StringComparison.Ordinal);
        Assert.DoesNotContain("function Assert-M12CleanTree([string]$Root) { Assert-M12ToolEnvironmentClean", supplyChain, StringComparison.Ordinal);
        Assert.DoesNotContain("nodejs/node_modules/corepack/dist/pnpm.js", supplyChain, StringComparison.Ordinal);
        Assert.DoesNotContain("throw$", supplyChain, StringComparison.Ordinal);

        foreach (string scriptName in new[]
        {
            "run-m12-supply-chain-certification.ps1",
            "run-m12-sqlserver-certification.ps1",
            "run-m12-reports-certification.ps1",
            "run-m12-observability-certification.ps1"
        })
        {
            string source = File.ReadAllText(Path.Combine(root, "tools", scriptName));
            int boundedStart = source.IndexOf("function Invoke-M12BoundedProcess", StringComparison.Ordinal);
            int boundedEnd = source.IndexOf("function ", boundedStart + 10, StringComparison.Ordinal);
            if (boundedEnd < 0) boundedEnd = source.Length;
            string bounded = source[boundedStart..boundedEnd];
            int capture = bounded.IndexOf("$exitCode=if(", StringComparison.Ordinal);
            int terminate = bounded.IndexOf(".Terminate()", capture, StringComparison.Ordinal);
            int wait = bounded.IndexOf(".WaitForZero(2000)", terminate, StringComparison.Ordinal);
            Assert.True(capture >= 0 && terminate > capture && wait > terminate);
            Assert.Contains("ExitCode=$exitCode", bounded, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PnpmListConversionDeserializesTheValidatedBytesAsOneJsonDocument()
    {
        string root = FindRoot();
        string output = Path.Combine(root, "TestResults", "m12", ".pnpm-converter-test-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        const string json = "[{\"name\":\"sql-observer-web\",\"version\":\"0.1.0\",\"dependencies\":{\"react\":{\"version\":\"19.2.8\"},\"react-dom\":{\"version\":\"19.2.8\",\"dependencies\":{\"react\":{\"version\":\"19.2.8\"},\"scheduler\":{\"version\":\"0.27.0\"}}}}}]";
        string command = "$root=$env:M12_CONVERTER_ROOT;$output=$env:M12_CONVERTER_OUTPUT;. (Join-Path $root 'tools/run-m12-supply-chain-certification.ps1') -RepositoryRoot $root -Profile Release -CaseId m12-sbom -FunctionProbe;Convert-M12PnpmList $env:M12_CONVERTER_JSON $root $output";
        try
        {
            ProcessStartInfo start = new("pwsh") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.Environment["M12_CONVERTER_ROOT"] = root;
            start.Environment["M12_CONVERTER_OUTPUT"] = output;
            start.Environment["M12_CONVERTER_JSON"] = json;
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, standardError);
            using System.Text.Json.JsonDocument converted = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(output));
            System.Text.Json.JsonElement packages = converted.RootElement.GetProperty("packages");
            Assert.Equal("react|react-dom|scheduler|sql-observer-web", string.Join('|', packages.EnumerateObject().Select(property => property.Name)));
            Assert.Equal("19.2.8", packages.GetProperty("react-dom").GetProperty("dependencies").GetProperty("react").GetString());
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public void ContractOnlyRejectsTamperedPinnedInput()
    {
        string root = FindRoot(); string path = Path.Combine(root, "release/certification/m12-sbom-inputs.v1.json"); string original = File.ReadAllText(path);
        try { File.WriteAllText(path, original.Replace("m12-sbom-inputs", "m12-sbom-inputs-tampered", StringComparison.Ordinal)); Assert.NotEqual(0, Run(root, "-ContractOnly")); Assert.NotEqual(0, RunCase(root, "m12-vulnerability-scan", "-ContractOnly")); }
        finally { File.WriteAllText(path, File.ReadAllText(path).Replace("m12-sbom-inputs-tampered-tampered", "m12-sbom-inputs", StringComparison.Ordinal).Replace("m12-sbom-inputs-tampered", "m12-sbom-inputs", StringComparison.Ordinal)); }
    }

    [Fact]
    public void ContractOnlyRejectsTamperedExternalAssetManifest()
    {
        string root = FindRoot(); string path = Path.Combine(root, "release/certification/m12-supply-chain-contract.v1.assets.sha256"); string original = File.ReadAllText(path);
        try { File.WriteAllText(path, original.Replace("m12-sbom.v1.schema.json", "m12-sbom.v1.schema.json-tampered", StringComparison.Ordinal)); Assert.NotEqual(0, Run(root, "-ContractOnly")); Assert.NotEqual(0, RunCase(root, "m12-vulnerability-scan", "-ContractOnly")); }
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
    public void LicenseContractOnlyDispatchValidatesWithoutPublishing()
    {
        string root = FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "m12-licenses", "m12-license-contract.v1.json", "generate-m12-license-evidence.mjs", "m12-licenses.json", "m12-licenses-test-evidence.json", "m12-licenses-provenance.json", "LiveReleaseLicenseEvidenceIsCompleteDeterministicAndSbomBound", "SQLOBSERVER_M12_LICENSE_EVIDENCE_PATH", "SQLOBSERVER_M12_LICENSE_SECOND_PATH" }) Assert.Contains(marker, source, StringComparison.Ordinal);
        Assert.Equal(0, RunCase(root, "m12-licenses", "-ContractOnly"));
        Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
    }

    [Fact]
    public void VulnerabilityContractOnlyDispatchValidatesWithoutPublishing()
    {
        string root = FindRoot(); string source = File.ReadAllText(Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "m12-vulnerability-scan", "m12-vulnerability-scan-contract.v1.json", "m12-vulnerability-scan-evidence.v1.schema.json", "generate-m12-vulnerability-scan-evidence.mjs", "'list'", "'package'", "'--vulnerable'", "'--include-transitive'", "'--format','json'", "'--output-version','1'", "'--no-restore'", "'audit'", "'--prod'", "NUGET_HTTP_CACHE_PATH", "reject-all", "FileMode]::CreateNew" }) Assert.Contains(marker, source, StringComparison.Ordinal);
        Assert.Equal(0, RunCase(root, "m12-vulnerability-scan", "-ContractOnly")); Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
    }

    [Fact]
    public void ProvenanceContractOnlyDispatchValidatesWithoutPublishing()
    {
        string root = FindRoot(); string source = File.ReadAllText(Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "m12-provenance", "m12-provenance-contract.v1.json", "m12-provenance-subjects.v1.schema.json", "m12-provenance-evidence.v1.schema.json", "generate-m12-provenance-evidence.mjs", "m12-provenance.json", "m12-provenance-test-evidence.json", "m12-provenance-provenance.json", "LiveReleaseProvenanceIsDeterministicAndBuildBound", "SQLOBSERVER_M12_PROVENANCE_EVIDENCE_PATH", "SQLOBSERVER_M12_PROVENANCE_SECOND_PATH", "SQLOBSERVER_M12_PROVENANCE_INPUT_MANIFEST_PATH", "exact-four-product-build", "full-transitive", "not-claimed" }) Assert.Contains(marker, source, StringComparison.Ordinal);
        Assert.Equal(0, RunCase(root, "m12-provenance", "-ContractOnly")); Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
    }

    [Fact]
    public void RunbooksContractOnlyDispatchValidatesWithoutPublishing()
    {
        string root = FindRoot(); string source = File.ReadAllText(Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "m12-runbooks", "m12-runbooks-contract.v1.json", "m12-runbooks-catalog.v1.json", "m12-runbooks-inputs.v1.schema.json", "m12-runbooks-evidence.v1.schema.json", "generate-m12-runbooks-evidence.mjs", "m12-runbooks.json", "m12-runbooks-test-evidence.json", "m12-runbooks-provenance.json", "LiveReleaseRunbooksAreClosedVersionedAndExercised", "RequiresM12SupplyChainRelease" }) Assert.Contains(marker, source, StringComparison.Ordinal);
        Assert.Equal(0, RunCase(root, "m12-runbooks", "-ContractOnly")); Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
    }

    [Fact]
    public void VulnerabilityLiveRequiresExactReleaseHostAttestationBeforeWork()
    {
        string root = FindRoot(); string source = File.ReadAllText(Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"));
        int helperStart = source.IndexOf("function Assert-M12VulnerabilityReleaseHost", StringComparison.Ordinal);
        int liveStart = source.IndexOf("function Invoke-M12VulnerabilityLive", StringComparison.Ordinal);
        Assert.True(helperStart >= 0 && liveStart > helperStart);
        string helper = source[helperStart..liveStart];
        foreach (string marker in new[] { "release-windows-server-2022", "release-windows-server-2025", "Is64BitOperatingSystem", "Is64BitProcess", "OSArchitecture", "ProcessArchitecture", "Architecture]::X64", "Win32_OperatingSystem", "ProductType", "Windows Server 2022", "Windows Server 2025", "SQLOBSERVER_VALIDATION_PROFILE", "Assert-M12PowerShellVersion" }) Assert.Contains(marker, helper, StringComparison.Ordinal);
        string live = source[liveStart..];
        Assert.Contains("$environment=[Environment]::GetEnvironmentVariable('SQLOBSERVER_RELEASE_SUPPLY_CHAIN_ENVIRONMENT');Assert-M12VulnerabilityReleaseHost $environment;", live, StringComparison.Ordinal);
        Assert.Contains("$canonical=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path);", live, StringComparison.Ordinal);
        Assert.Contains("if([IO.Path]::GetFullPath($Root)-cne$canonical){Fail 'HOST'};", live, StringComparison.Ordinal);
        Assert.Contains("Assert-M12NoReparse $outputRoot $Root;Assert-M12NoAlternateDataStreams $outputRoot $Root;", live, StringComparison.Ordinal);
        Assert.DoesNotContain("if($environment-cnotin@('release-windows-server-2022','release-windows-server-2025')-or[Environment]::GetEnvironmentVariable('SQLOBSERVER_VALIDATION_PROFILE')", live, StringComparison.Ordinal);
    }

    [Fact]
    public void VulnerabilityContractBindsFullSbomAuthorityClosure()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        int start = source.IndexOf("function Assert-M12VulnerabilityContract", StringComparison.Ordinal);
        int end = source.IndexOf("function Assert-Contract", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string contract = source[start..end];
        Assert.Contains("m12-supply-chain-contract.v1.assets.sha256", contract, StringComparison.Ordinal);
        Assert.Contains("ApprovedAssetManifestSha256", contract, StringComparison.Ordinal);
        Assert.Contains("Assert-M12AssetPins", contract, StringComparison.Ordinal);
        Assert.Contains("Assert-Manifest", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void VulnerabilityPublicationCleansOwnedInputsBeforePromotion()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        int live = source.IndexOf("function Invoke-M12VulnerabilityLive", StringComparison.Ordinal);
        int promotion = source.IndexOf("$verify=Join-Path $outputRoot", live, StringComparison.Ordinal);
        int cleanup = source.IndexOf("Exit-M12CleanEnvironment $environmentState", live, StringComparison.Ordinal);
        Assert.True(live >= 0 && cleanup > live && promotion > cleanup);
        Assert.Contains("$environmentState=$null", source[cleanup..promotion], StringComparison.Ordinal);
        int clearTarget = source.IndexOf("$cleanupTarget=$null", promotion, StringComparison.Ordinal);
        Assert.True(clearTarget > promotion);
    }

    [Fact]
    public void SbomPublicationCleansOwnedWebSnapshotBeforeRawInputs()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        int live = source.IndexOf("function Invoke-M12Live", StringComparison.Ordinal);
        int cleanup = source.IndexOf("Exit-M12CleanEnvironment $environmentState;$environmentState=$null;Remove-M12SafeDescendants $raw $Root", live, StringComparison.Ordinal);
        int promotion = source.IndexOf("$verify=Join-Path $outputRoot", cleanup, StringComparison.Ordinal);
        Assert.True(live >= 0 && cleanup > live && promotion > cleanup);
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
    private static int RunCase(string root, string caseId, params string[] extra)
    {
        ProcessStartInfo start = new("pwsh") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1"), "-RepositoryRoot", root, "-Profile", "Release", "-CaseId", caseId }.Concat(extra)) start.ArgumentList.Add(argument);
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
