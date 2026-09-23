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
        Assert.Equal(4, contract.RootElement.GetProperty("reportKinds").GetArrayLength());
        Assert.Equal(4, contract.RootElement.GetProperty("sections").GetArrayLength());
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
            ["database/migrations/checksums.sha256"] = "aab7ecfcc2a1e3265e6485f443d3c76c6e5e03df65c6388d2de5b61cb9c6d973",
            ["src/SqlObserver.Reporting/ReportContracts.cs"] = "95613db9340aba8120066a88c5a7062c5f6377c64d08c3d8a1d1fc2c43eb5af8",
            ["src/SqlObserver.Reporting/ReportRendering.cs"] = "6c89ce15c5463b8e56bc72cf78f28f979579e69719e36ef64b40f32b0ce9de61",
            ["src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlReportRepository.cs"] = "6dce0d1b5dc6bbd930367059153872e5df3eaa38aeeb31650b0fdb1afd73b8ee",
            ["src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlReportAuditPort.cs"] = "9e7c8a3af79809f955f8fa7042d095a950a016c43a318bf26676caa3f1c16b74",
            ["tests/SqlObserver.IntegrationTests.PostgreSql/M12ReportsCertificationTests.cs"] = "253b2c8d9381fa20493486cda27da05b43a6cea86f760acd0f65b25912a7198d",
            ["tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj"] = "1e42d8ee02bc687988cc17b44f6200f4bf04dc48d1c93b7e298b195e6c521910"
        };
        JsonElement assets = contract.RootElement.GetProperty("assetPins");
        Assert.Equal(expectedAssets.Count, assets.EnumerateObject().Count());
        foreach ((string relative, string expectedHash) in expectedAssets)
        {
            Assert.Equal(expectedHash, assets.GetProperty(relative).GetString());
            Assert.Equal(expectedHash, Hash(Path.Combine(root, relative)));
        }
        string expected = $"{Hash(contractPath)}  m12-reports-contract.v1.json\n{Hash(schemaPath)}  m12-reports-contract.v1.schema.json\n";
        Assert.Equal(expected, File.ReadAllText(pinPath));
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
        foreach (string marker in new[] { "m12-reports-exports", "m12-report-contract", "m12-export-contract", "m12-reports-harness", "reports-exports-evidence", "LiveReleaseReportsExportsRepresentativeVolumeIsBounded", "LiveReleaseReportContractIsSnapshotScopedAndAudited", "LiveReleaseExportContractIsInertAndFormulaSafe", "Reports=$true", "Exports=$true", "ReportKind='report'", "ReportKind='export'", "e861400591c82000d9bb329c52a983680c3c2a0fc2f66fa93e37472be8b49986", "1c2726afe570ff7a8db169afc6469196d3c27ad73d33b2f5702de5130b31d64d", "1647cdaa465f1e216a87f8d47c50575ba7fdc5c43198285b8bdc5fa453ad02b0", "ea27ee076b86cd541d0834d2f729b83e737eb56dd3ceea943482dee3def084e8", "80defd27f366b29b46e48bba9d9a7eec2934a41a2aaf5ca38a79ab979e4354ad", "253b2c8d9381fa20493486cda27da05b43a6cea86f760acd0f65b25912a7198d", "1e42d8ee02bc687988cc17b44f6200f4bf04dc48d1c93b7e298b195e6c521910", "detailedWindowDays=7", "trendWindowDays=31", "VerifyFull", "Assert-ReportsConnection", "ConnectionOnly", "ContractOnly", "/t:Rebuild", "--output", "raw", "Assert-M12CleanTree", "Invoke-M12GitStatus", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_TERMINAL_PROMPT", "--no-optional-locks", "Assert-M12TrustedTree", "Read-M12LockedBytes", "FileShare]::None", "FileMode]::CreateNew", "Flush($true)", "ReadAsync", "M12SuspendedProcess", "KillOnClose", "ProcessIds", "Assert-M12NoDescendants", "Read-M12ReportsTrx", "XmlResolver", "DocumentType", "UnitTestResult", "Counters", "expectedCounters", "TestCount=$trxResult.TestCount", "testCount-ne$ExpectedTestCount", "executed=1", "passed=1", "notExecuted=0", "warning=0", "Assert-M12ObservedPidsExited", "$stableEmptySweeps", "Get-CimInstance Win32_Process", "Remove-M12RawArtifactsDirectory", "[IO.Directory]::Move($build,$verify)", "[IO.Directory]::Move($verify,$final)", "M12-REPORTS-PRODUCER-HOST" }) Assert.Contains(marker, source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEndAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAllText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Command dotnet", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Diagnostics.Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-build", source, StringComparison.Ordinal);
        string validator = File.ReadAllText(Path.Combine(FindRoot(), "tools/validate.ps1"));
        Assert.Contains("6b3031b17b3ebc25bfef1968e2058543e3fec12ec78427fd5d9c548f7ce08144", validator, StringComparison.Ordinal);
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
