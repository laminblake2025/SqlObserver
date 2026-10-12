using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SqlObserver.ReleaseTests;

public sealed class M12ReportsCertificationProducerTests
{
    [Fact]
    public void ReportsContractAndPinsAreClosedAndMatchProductSources()
    {
        string root = FindRoot();
        string contractPath = Path.Combine(root, "release/certification/m12-reports-contract.v1.json");
        string schemaPath = Path.Combine(root, "release/certification/m12-reports-contract.v1.schema.json");
        string pinPath = Path.Combine(root, "release/certification/m12-reports-contract.v1.sha256");
        using JsonDocument contract = JsonDocument.Parse(File.ReadAllBytes(contractPath));
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(1, contract.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("m12-reports-exports", contract.RootElement.GetProperty("contractId").GetString());
        Assert.Equal(5, contract.RootElement.GetProperty("reportKinds").GetArrayLength());
        Assert.Equal(6, contract.RootElement.GetProperty("sections").GetArrayLength());
        Assert.Equal("saved-investigation", contract.RootElement.GetProperty("reportKinds")[4].GetString());
        Assert.Equal("preserve-unicode-and-crlf-tab", contract.RootElement.GetProperty("encoding").GetProperty("savedNotes").GetString());
        Assert.Equal("json-string-parts-ordered-by-packet-and-part", contract.RootElement.GetProperty("encoding").GetProperty("savedEvidence").GetString());
        Assert.Equal(200, contract.RootElement.GetProperty("bounds").GetProperty("pageRows").GetInt32());
        Assert.Equal(2_000, contract.RootElement.GetProperty("bounds").GetProperty("htmlRows").GetInt32());
        Assert.Equal(10_000, contract.RootElement.GetProperty("bounds").GetProperty("materializationRows").GetInt32());
        Assert.Equal(7, contract.RootElement.GetProperty("bounds").GetProperty("detailedWindowDays").GetInt32());
        Assert.Equal(31, contract.RootElement.GetProperty("bounds").GetProperty("trendWindowDays").GetInt32());
        Assert.True(contract.RootElement.GetProperty("encoding").GetProperty("formulaNeutralization").GetBoolean());
        string[] expectedCaseIds = ["m12-reports-exports", "m12-report-contract", "m12-export-contract"];
        string[] expectedTests = ["LiveReleaseReportsExportsRepresentativeVolumeIsBounded", "LiveReleaseReportContractIsSnapshotScopedAndAudited", "LiveReleaseExportContractIsInertAndFormulaSafe"];
        for (int i = 0; i < expectedCaseIds.Length; i++)
        {
            JsonElement mapping = contract.RootElement.GetProperty("caseMappings")[i];
            Assert.Equal(expectedCaseIds[i], mapping.GetProperty("caseId").GetString());
            Assert.Equal(expectedTests[i], mapping.GetProperty("testName").GetString());
            Assert.Equal("reports|exports|reportKind", string.Join('|', mapping.GetProperty("facts").EnumerateObject().Select(static property => property.Name)));
        }
        var expectedAssets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["database/migrations/0021_reports_exports.sql"] = "e861400591c82000d9bb329c52a983680c3c2a0fc2f66fa93e37472be8b49986",
            ["database/migrations/0022_runtime_startup_repairs.sql"] = "1c2726afe570ff7a8db169afc6469196d3c27ad73d33b2f5702de5130b31d64d",
            ["database/migrations/0023_report_expiry_lock_privilege.sql"] = "1647cdaa465f1e216a87f8d47c50575ba7fdc5c43198285b8bdc5fa453ad02b0",
            ["database/migrations/0024_report_materialization_column_binding.sql"] = "8786730998c3e120664a046519796a24afb8851d41fc5689e66b917aacd068a6",
            ["database/migrations/0025_report_run_scoped_read.sql"] = "dd397e02f0faa079befc1a9a804fd0b82eceacb9598a12c48cf9c9a98b3d88e6",
            ["database/migrations/checksums.sha256"] = "b69dfafa038e9ec335fca6b23af5c4ef41b18533a4c2ac49a338077387f2f010",
            ["src/SqlObserver.Reporting/ReportContracts.cs"] = "f22cb37ebc9fba15fa01d1215e902a38b5e43f02c494585f06bcac0214b89dd0",
            ["src/SqlObserver.Reporting/ReportRendering.cs"] = "08bba74ded22122a4a08f03dbfd6a254a7b786b352a3c3390a000a64552a824c",
            ["src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlReportRepository.cs"] = "61e1b6a3f5b66a9583089572ee2ebfe2fb0e7fa08a50ff6c23450441561e7865",
            ["src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlReportAuditPort.cs"] = "9e7c8a3af79809f955f8fa7042d095a950a016c43a318bf26676caa3f1c16b74",
            ["tests/SqlObserver.IntegrationTests.PostgreSql/M12ReportsCertificationTests.cs"] = "6596309e5ff693b47c6952f8f3be12314c14e15bc601e588705b3ac990e64cdd",
            ["tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj"] = "e7aa4a9e68a5ccc512ea141c7f34e25cd963c30c41d3be5a6455ee639293d938",
            ["database/migrations/0288_saved_incident_workspace.sql"] = "6e04b0c7d6ae18827b273a2742dce33369ef0c6d11d7e2a461a3a4814cd7b2e3",
            ["database/migrations/0289_saved_incident_listing.sql"] = "91f8a79793e3ba7fa36199a676a46ce26664af0b3d9e531fc8ee792c021daf12",
            ["database/migrations/0290_saved_investigation_reports.sql"] = "61fd92379f9a0e95bf8affabf43865b225913c8dd11bfffe73ff7289bf00012b",
            ["src/SqlObserver.Application/Ports/SavedIncidentPorts.cs"] = "a7c7b6990802f04c3bf799693cd3c91ba39e95a2980cf9343fff3735fbf165c6",
            ["src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlSavedIncidentPort.cs"] = "8a355aed69c8479eb3be51a5a541900d13c4104336f84c99809f8eaa1e17c87c",
            ["database/migrations/0291_report_materialization_row_budget.sql"] = "599eed19372b3654a07561a63ad041455b55cdd1f1f821074c1ce22e3436a4c4",
        };
        JsonElement assets = contract.RootElement.GetProperty("assetPins");
        Assert.Equal(expectedAssets.Count, assets.EnumerateObject().Count());
        JsonElement assetSchema = schema.RootElement.GetProperty("properties").GetProperty("assetPins");
        Assert.Equal(expectedAssets.Keys, assetSchema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(expectedAssets.Keys, assetSchema.GetProperty("properties").EnumerateObject().Select(static item => item.Name));
        foreach ((string relative, string expectedHash) in expectedAssets)
        {
            Assert.Equal(expectedHash, assets.GetProperty(relative).GetString());
            Assert.Equal(expectedHash, Hash(Path.Combine(root, relative)));
        }
        string expected = $"{Hash(contractPath)}  m12-reports-contract.v1.json\n{Hash(schemaPath)}  m12-reports-contract.v1.schema.json\n";
        Assert.Equal(expected, File.ReadAllText(pinPath));
    }

    [Fact]
    public void ProducerRequiresSavedInvestigationEvidenceForEveryCase()
    {
        string root = FindRoot();
        string script = Path.Combine(Path.GetTempPath(), $"sqlobserver-report-facts-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, """
            param([string]$ProducerPath)
            Set-StrictMode -Version Latest
            $ErrorActionPreference='Stop'
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($ProducerPath,[ref]$tokens,[ref]$errors)
            if ($errors.Count) { throw 'Producer parse failure' }
            foreach ($name in @('Fail-M12','Assert-M12FactShape','Assert-LiveReportsResult')) {
                $function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true)
                if ($null-eq$function) { throw 'Missing producer function' }
                . ([scriptblock]::Create($function.Extent.Text))
            }
            $cases=@(
                @{Id='m12-reports-exports';Kind=$null;Reports=$true;Exports=$true;Facts=[ordered]@{reports=$true;exports=$true;reportKind=$null;definitions=5;sections=@('health','performance','incidents','capacity','investigation','saved-evidence');page200=$true;html2000=$true;export10000=$true;responseBytes=1048576;materializationBytes=8388608;testCount=1;savedInvestigation=$true;observedPostgreSqlVersion=18}},
                @{Id='m12-report-contract';Kind='report';Reports=$true;Exports=$false;Facts=[ordered]@{reports=$true;exports=$false;reportKind='report';snapshotScoped=$true;audited=$true;idempotentReplay=$true;targetIsolation=$true;roles=$true;testCount=1;savedInvestigation=$true;observedPostgreSqlVersion=18}},
                @{Id='m12-export-contract';Kind='export';Reports=$false;Exports=$true;Facts=[ordered]@{reports=$false;exports=$true;reportKind='export';inertHtml=$true;rfc4180=$true;formulaNeutralization=$true;formulaSentinels=4;testCount=1;savedInvestigation=$true;observedPostgreSqlVersion=18}}
            )
            foreach ($entry in $cases) {
                $CaseId=$entry.Id
                $case=[pscustomobject]@{Test='proof';ReportKind=$entry.Kind;Reports=$entry.Reports;Exports=$entry.Exports}
                $result=[pscustomobject][ordered]@{schemaVersion=1;caseId=$CaseId;status='passed';testName='proof';testCount=1;facts=[pscustomobject]$entry.Facts}
                [void](Assert-LiveReportsResult $result $case 1)
                foreach ($mutation in @('missing','false','string','extra')) {
                    $facts=[ordered]@{}; foreach ($key in $entry.Facts.Keys) { $facts[$key]=$entry.Facts[$key] }
                    switch ($mutation) {
                        'missing' { $facts.Remove('savedInvestigation') }
                        'false' { $facts.savedInvestigation=$false }
                        'string' { $facts.savedInvestigation='true' }
                        'extra' { $facts.unexpected=$true }
                    }
                    $result.facts=[pscustomobject]$facts
                    $rejected=$false
                    try { [void](Assert-LiveReportsResult $result $case 1) } catch { $rejected=$true }
                    if (-not$rejected) { throw "Producer accepted $mutation evidence" }
                }
            }
            """);
        try
        {
            string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            if (!File.Exists(pwsh)) pwsh = "pwsh";
            ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, Path.Combine(root, "tools/run-m12-reports-certification.ps1") }) start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
            Assert.Equal(string.Empty, output.Trim());
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public void ProducerExecutesContractValidationAgainstPinnedFiles()
    {
        string root = FindRoot();
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"); if (!File.Exists(pwsh)) pwsh = "pwsh";
        ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tools/run-m12-reports-certification.ps1"), "-RepositoryRoot", root, "-Profile", "Release", "-CaseId", "m12-reports-exports", "-ContractOnly" }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!; string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.Equal(0, process.ExitCode); Assert.Equal(string.Empty, output.Trim());
    }

    [Fact]
    public void IsolatedRebuildCliShapeSucceedsWithoutRestore()
    {
        string root = FindRoot();
        string project = Path.Combine(root, "tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj");
        string outputDirectory = Path.Combine(Path.GetTempPath(), "m12-reports-cli-" + Guid.NewGuid().ToString("N"));
        string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"); if (!File.Exists(dotnet)) dotnet = "dotnet";
        try
        {
            ProcessStartInfo start = new(dotnet) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string arg in new[] { "test", project, "-c", "Release", "--no-restore", "/t:Rebuild", "--output", outputDirectory, "--filter", "FullyQualifiedName=SqlObserver.IntegrationTests.PostgreSql.M12ReportsCertificationTests.DoesNotExist", "--list-tests" }) start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!; string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("NETSDK1004", output, StringComparison.Ordinal);
            Assert.DoesNotContain("MSB1001", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, true);
        }
    }

    [Fact]
    public void ProducerConnectionValidationRejectsAmbiguousOrWeakTopology()
    {
        string root = FindRoot();
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"); if (!File.Exists(pwsh)) pwsh = "pwsh";
        static (int ExitCode, string Output) Run(string pwsh, string root, string connection)
        {
            ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tools/run-m12-reports-certification.ps1"), "-RepositoryRoot", root, "-Profile", "Release", "-CaseId", "m12-reports-exports", "-ConnectionOnly" }) start.ArgumentList.Add(arg);
            start.Environment["SQLOBSERVER_RELEASE_POSTGRES"] = connection;
            using Process process = Process.Start(start)!; string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit(); return (process.ExitCode, output.Trim());
        }
        (int validExit, string validOutput) = Run(pwsh, root, "Host=release-db-a,release-db-b;Database=reports;Ssl Mode=VerifyFull");
        Assert.Equal(0, validExit); Assert.Equal(string.Empty, validOutput);
        (int invalidExit, string invalidOutput) = Run(pwsh, root, "Host=release-db-a,,release-db-b;Database=reports;Ssl Mode=VerifyFull");
        Assert.NotEqual(0, invalidExit); Assert.Equal("M12-REPORTS-PRODUCER-INPUT", invalidOutput);
        (int weakExit, string weakOutput) = Run(pwsh, root, "Host=release-db-a,release-db-b;Database=reports;Ssl Mode=Require");
        Assert.NotEqual(0, weakExit); Assert.Equal("M12-REPORTS-PRODUCER-INPUT", weakOutput);
        (int loopbackExit, string loopbackOutput) = Run(pwsh, root, "Host=127.0.0.1,release-db-b;Database=reports;Ssl Mode=VerifyFull");
        Assert.NotEqual(0, loopbackExit); Assert.Equal("M12-REPORTS-PRODUCER-INPUT", loopbackOutput);
        (int singleHostExit, string singleHostOutput) = Run(pwsh, root, "Host=release-db-a;Database=reports;Ssl Mode=VerifyFull");
        Assert.NotEqual(0, singleHostExit); Assert.Equal("M12-REPORTS-PRODUCER-INPUT", singleHostOutput);
        (int extraKeyExit, string extraKeyOutput) = Run(pwsh, root, "Host=release-db-a,release-db-b;Database=reports;Ssl Mode=VerifyFull;Application Name=cert");
        Assert.NotEqual(0, extraKeyExit); Assert.Equal("M12-REPORTS-PRODUCER-INPUT", extraKeyOutput);
        (int channelBindingExit, string channelBindingOutput) = Run(pwsh, root, "Host=release-db-a,release-db-b;Database=reports;Ssl Mode=VerifyFull;Channel Binding=require");
        Assert.NotEqual(0, channelBindingExit); Assert.Equal("M12-REPORTS-PRODUCER-INPUT", channelBindingOutput);
        (int revocationExit, string revocationOutput) = Run(pwsh, root, "Host=release-db-a,release-db-b;Database=reports;Ssl Mode=VerifyFull;Check Certificate Revocation=true");
        Assert.NotEqual(0, revocationExit); Assert.Equal("M12-REPORTS-PRODUCER-INPUT", revocationOutput);
        (int portExit, string portOutput) = Run(pwsh, root, "Host=release-db-a,release-db-b;Database=reports;Ssl Mode=VerifyFull;Port=0");
        Assert.NotEqual(0, portExit); Assert.Equal("M12-REPORTS-PRODUCER-INPUT", portOutput);
    }

    [Fact]
    public void ProducerHasPendingOnlyCaseMapAndPublicationHardening()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-reports-certification.ps1"));
        foreach (string marker in new[] { "m12-reports-exports", "m12-report-contract", "m12-export-contract", "m12-reports-harness", "reports-exports-evidence", "LiveReleaseReportsExportsRepresentativeVolumeIsBounded", "LiveReleaseReportContractIsSnapshotScopedAndAudited", "LiveReleaseExportContractIsInertAndFormulaSafe", "Reports=$true", "Exports=$true", "ReportKind='report'", "ReportKind='export'", "e861400591c82000d9bb329c52a983680c3c2a0fc2f66fa93e37472be8b49986", "1c2726afe570ff7a8db169afc6469196d3c27ad73d33b2f5702de5130b31d64d", "1647cdaa465f1e216a87f8d47c50575ba7fdc5c43198285b8bdc5fa453ad02b0", "0c8e8d075c513ff973a91a584090ac8cc683c40abc0ad89a47fc4473593c5c22", "cbb3780fb0833ca4c9b8d633cc72ff45200aac336b5a0790860b12e167a07266", "6596309e5ff693b47c6952f8f3be12314c14e15bc601e588705b3ac990e64cdd", "e7aa4a9e68a5ccc512ea141c7f34e25cd963c30c41d3be5a6455ee639293d938", "detailedWindowDays=7", "trendWindowDays=31", "VerifyFull", "Assert-ReportsConnection", "ConnectionOnly", "ContractOnly", "/t:Rebuild", "--output", "raw", "Assert-M12CleanTree", "Invoke-M12GitStatus", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_TERMINAL_PROMPT", "--no-optional-locks", "Assert-M12TrustedTree", "Read-M12LockedBytes", "FileShare]::None", "FileMode]::CreateNew", "Flush($true)", "ReadAsync", "M12SuspendedProcess", "KillOnClose", "ProcessIds", "Assert-M12NoDescendants", "Read-M12ReportsTrx", "XmlResolver", "DocumentType", "UnitTestResult", "Counters", "expectedCounters", "TestCount=$trxResult.TestCount", "testCount-ne$ExpectedTestCount", "executed=1", "passed=1", "notExecuted=0", "warning=0", "Assert-M12ObservedPidsExited", "$stableEmptySweeps", "Get-CimInstance Win32_Process", "Remove-M12RawArtifactsDirectory", "[IO.Directory]::Move($build,$verify)", "[IO.Directory]::Move($verify,$final)", "M12-REPORTS-PRODUCER-HOST" }) Assert.Contains(marker, source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEndAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAllText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Command dotnet", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Diagnostics.Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-build", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?<!\w)(return|throw)(?=[\$\[])", source);
        string validator = File.ReadAllText(Path.Combine(FindRoot(), "tools/validate.ps1"));
        Assert.Contains("6c5adc1242fa64c87d23b12d619781476860331a291078daedb4a1ee02395c6b", validator, StringComparison.Ordinal);
        Assert.Contains("var builder = new StringBuilder(\"\\\"\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new StringBuilder(\"\\\\\\\"\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentWorkstationInvocationFailsWithoutCandidateOutput()
    {
        string root = FindRoot(); string output = Path.Combine(root, "TestResults", "m12", "candidate");
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"); if (!File.Exists(pwsh)) pwsh = "pwsh";
        ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tools/run-m12-reports-certification.ps1"), "-RepositoryRoot", root, "-Profile", "Release", "-CaseId", "m12-reports-exports" }) start.ArgumentList.Add(arg);
        start.Environment["SQLOBSERVER_VALIDATION_PROFILE"] = "Release"; start.Environment["SQLOBSERVER_RELEASE_REPORTS_ENVIRONMENT"] = "release-windows-server-2022"; start.Environment["SQLOBSERVER_RELEASE_POSTGRES"] = "Host=127.0.0.1;Database=admin";
        using Process process = Process.Start(start)!; string text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.NotEqual(0, process.ExitCode); Assert.Equal("M12-REPORTS-PRODUCER-HOST", text.Trim()); Assert.False(Directory.Exists(output));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string FindRoot() { string? path = AppContext.BaseDirectory; while (path is not null && !File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName; return path ?? throw new DirectoryNotFoundException(); }
}
