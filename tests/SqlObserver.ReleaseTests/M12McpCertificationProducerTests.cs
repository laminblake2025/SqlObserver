using System.Diagnostics;

namespace SqlObserver.ReleaseTests;

public sealed class M12McpCertificationProducerTests
{
    [Fact]
    public void ProducerIsExplicitlyPendingAndUsesOnlyTheIgnoredRunRoot()
    {
        string root = FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "tools", "run-m12-mcp-certification.ps1"));
        Assert.Contains("m12-mcp-protocol", source, StringComparison.Ordinal);
        Assert.Contains("m12-mcp-harness", source, StringComparison.Ordinal);
        Assert.Contains("mcp-protocol-evidence", source, StringComparison.Ordinal);
        Assert.Contains("TestResults/m12", source, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($build, $verify)", source, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($verify, $final)", source, StringComparison.Ordinal);
        Assert.Contains(".pending-", source, StringComparison.Ordinal);
        Assert.Contains(".verify-", source, StringComparison.Ordinal);
        Assert.Contains("Remove-SafeTree", source, StringComparison.Ordinal);
        Assert.Contains("ReparsePoint", source, StringComparison.Ordinal);
        Assert.Contains("Assert-NoDescendants", source, StringComparison.Ordinal);
        Assert.Contains("M12SuspendedProcess", source, StringComparison.Ordinal);
        Assert.Contains("CreateSuspended", source, StringComparison.Ordinal);
        Assert.Contains("ResumeThread", source, StringComparison.Ordinal);
        Assert.Contains("KillOnClose", source, StringComparison.Ordinal);
        Assert.Contains("TerminateJobObject", source, StringComparison.Ordinal);
        Assert.Contains("WaitForZero", source, StringComparison.Ordinal);
        Assert.Contains("M12OutputFile", source, StringComparison.Ordinal);
        Assert.Contains("FileMode]::CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("FileShare]::None", source, StringComparison.Ordinal);
        Assert.Contains("Flush($true)", source, StringComparison.Ordinal);
        Assert.Contains("GetFileInformationByHandle", source, StringComparison.Ordinal);
        Assert.Contains("Assert-OutputFileIdentity", source, StringComparison.Ordinal);
        Assert.Contains("Assert-PublishedJson", source, StringComparison.Ordinal);
        Assert.Contains("unitTest.name", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAllText", source, StringComparison.Ordinal);
        Assert.Contains("m12-mcp-protocol-contract.v1.json", source, StringComparison.Ordinal);
        Assert.Contains("$ApprovedCatalogDigest", source, StringComparison.Ordinal);
        Assert.Contains("$ApprovedServerVersion", source, StringComparison.Ordinal);
        Assert.Contains("$ApprovedCurrentProtocol", source, StringComparison.Ordinal);
        Assert.Contains("$ApprovedDownlevelProtocol", source, StringComparison.Ordinal);
        Assert.Contains("$ApprovedContractSchemaSha256", source, StringComparison.Ordinal);
        Assert.Contains("Test-Json", source, StringComparison.Ordinal);
        Assert.Contains("src/SqlObserver.McpStdio/bin/Release/net10.0/SqlObserver.McpStdio.exe", source, StringComparison.Ordinal);
        Assert.Contains("--filter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProducerUsesOrdinalEnvironmentIdentityAndStrictStreamBounds()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools", "run-m12-mcp-certification.ps1"));
        Assert.Contains("-cne 'release-windows-server-2022'", source, StringComparison.Ordinal);
        Assert.Contains("-cne 'release-windows-server-2025'", source, StringComparison.Ordinal);
        Assert.Contains("$MaximumOutput", source, StringComparison.Ordinal);
        Assert.Contains("$MaximumError", source, StringComparison.Ordinal);
        Assert.Contains("ReadAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEndAsync", source, StringComparison.Ordinal);
        Assert.Contains("Kill($true)", source, StringComparison.Ordinal);
        Assert.Contains("M12-MCP-PRODUCER-HOST", source, StringComparison.Ordinal);
        Assert.Contains("OSArchitecture", source, StringComparison.Ordinal);
        Assert.Contains("ProcessArchitecture", source, StringComparison.Ordinal);
        Assert.Contains("Architecture]::X64", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerBindsAttestationRunIdToContainingLabDirectory()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools", "run-m12-mcp-certification.ps1"));
        Assert.Contains("$runIdFromPath = $full.Substring($labRoot.Length, 36)", source, StringComparison.Ordinal);
        Assert.Contains("$object.GetProperty('runId').GetString() -cne $runIdFromPath", source, StringComparison.Ordinal);
        Assert.Contains("-cnotmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\\\denied-target\\.json$'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerRefusesNonCanonicalRootWithoutCandidateOutput()
    {
        string root = FindRoot();
        string fakeRoot = Path.Combine(Path.GetTempPath(), "m12-mcp-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeRoot);
        try
        {
            ProcessResult result = RunProducer(root, fakeRoot);
            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("fake", result.Stdout + result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(fakeRoot, "TestResults")));
            Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
        }
        finally { Directory.Delete(fakeRoot, recursive: true); }
    }

    [Fact]
    public void ProducerPreflightRejectsUnsafeEndpointWithoutWritingRunDirectory()
    {
        string root = FindRoot();
        string fakeRoot = Path.Combine(Path.GetTempPath(), "m12-mcp-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeRoot);
        try
        {
            ProcessResult result = RunProducer(root, fakeRoot, new Dictionary<string, string?>
            {
                ["SQLOBSERVER_RELEASE_MCP_ENDPOINT"] = "https://user:secret@example.test/mcp?x=1",
                ["SQLOBSERVER_RELEASE_MCP_ENVIRONMENT"] = "release-windows-server-2025",
                ["SQLOBSERVER_VALIDATION_PROFILE"] = "Release",
                ["SQLOBSERVER_RELEASE_MCP_STDIO_EXE"] = Path.Combine(root, "src", "SqlObserver.McpStdio", "bin", "Release", "net10.0", "SqlObserver.McpStdio.exe")
            });
            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("secret", result.Stdout + result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(fakeRoot, "TestResults")));
        }
        finally { Directory.Delete(fakeRoot, recursive: true); }
    }

    private static ProcessResult RunProducer(string root, string requestedRoot, Dictionary<string, string?>? variables = null)
    {
        string script = Path.Combine(root, "tools", "run-m12-mcp-certification.ps1");
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(pwsh)) pwsh = "pwsh";
        ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script); start.ArgumentList.Add("-RepositoryRoot"); start.ArgumentList.Add(requestedRoot);
        if (variables is not null) foreach ((string key, string? value) in variables) start.Environment[key] = value;
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd(); string stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private static string FindRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName;
        return current ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
