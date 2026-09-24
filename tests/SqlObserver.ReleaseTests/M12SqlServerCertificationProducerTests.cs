using System.Diagnostics;

namespace SqlObserver.ReleaseTests;

public sealed class M12SqlServerCertificationProducerTests
{
    [Fact]
    public void ProducerHasExactCaseMappingAndClosedPassiveContract()
    {
        string root = FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "tools", "run-m12-sqlserver-certification.ps1"));
        Assert.Contains("m12-sqlserver-2019-passive", source, StringComparison.Ordinal);
        Assert.Contains("m12-sqlserver-2022-passive", source, StringComparison.Ordinal);
        Assert.Contains("m12-sqlserver-2025-passive", source, StringComparison.Ordinal);
        Assert.Contains("Major = 15", source, StringComparison.Ordinal);
        Assert.Contains("Major = 16", source, StringComparison.Ordinal);
        Assert.Contains("Major = 17", source, StringComparison.Ordinal);
        Assert.Contains("release-windows-server-2022", source, StringComparison.Ordinal);
        Assert.Contains("release-windows-server-2025", source, StringComparison.Ordinal);
        Assert.Contains("RequiresM12SqlServerRelease", source, StringComparison.Ordinal);
        Assert.Contains("sqlserver-nonmutation-evidence", source, StringComparison.Ordinal);
        Assert.Contains("passiveMutation", source, StringComparison.Ordinal);
        Assert.Contains("nonmutation", source, StringComparison.Ordinal);
        Assert.Contains("M12-SQLSERVER-PRODUCER-HOST", source, StringComparison.Ordinal);
        Assert.Contains("function Resolve-M12CanonicalRoot", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12TrustedTree $requestedFull", source, StringComparison.Ordinal);
        Assert.Contains("a5310947a8ce8cae80df2b21a6e42dfd231f69cdaf8f9377c36b0956d047ad64", source, StringComparison.Ordinal);
        Assert.Contains("5539bba4fe0139b92aadbcd6203526cb3377b689d732e68c70aff60bac843406", source, StringComparison.Ordinal);
        Assert.Contains("function Read-M12ClosedJson", source, StringComparison.Ordinal);
        Assert.Contains("JsonDocument", source, StringComparison.Ordinal);
        Assert.Contains("expectedMachineNames = @('schemaVersion','caseId','environment','observedMajorVersion','observedProductVersion','platform','transportEncrypted','authenticationScheme','isSysAdmin'", source, StringComparison.Ordinal);
        Assert.Contains("Read-M12LockedBytes", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12JsonTypesBytes", source, StringComparison.Ordinal);
        Assert.Contains("Open-M12ExecutableHandle", source, StringComparison.Ordinal);
        Assert.Contains("ProcessIds", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12ObservedPidsExited", source, StringComparison.Ordinal);
        Assert.Contains("$discoveredPids", source, StringComparison.Ordinal);
        Assert.Contains("$stableEmptySweeps", source, StringComparison.Ordinal);
        Assert.Contains("[Collections.Generic.Queue[int]]::new()", source, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance Win32_Process", source, StringComparison.Ordinal);
        Assert.Contains("Open-M12ExecutableHandle", source, StringComparison.Ordinal);
        Assert.Contains("$executableHandle.Stream.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("snapshotEncoding", source, StringComparison.Ordinal);
        Assert.Contains("M12OutputIdentities", source, StringComparison.Ordinal);
        Assert.Contains("Remove-M12RawTestDirectory", source, StringComparison.Ordinal);
        Assert.Contains("function Remove-M12SafeDescendants", source, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $full -Force -ErrorAction Stop", source, StringComparison.Ordinal);
        Assert.Contains("Remove-M12SafeDescendants $i.FullName $full", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-M12SafeTree $child", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-Recurse", source, StringComparison.Ordinal);
        Assert.Contains("$RunId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLOBSERVER_RELEASE_MCP", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApprovedCurrentProtocol", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApprovedCatalogDigest", source, StringComparison.Ordinal);
        string liveTest = File.ReadAllText(Path.Combine(root, "tests", "SqlObserver.IntegrationTests.SqlServer", "M12SqlServerPassiveCertificationTests.cs"));
        Assert.Contains("FrameSnapshotComponent", liveTest, StringComparison.Ordinal);
        Assert.Contains("memory_partition_mode", liveTest, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT name,enabled FROM sys.server_event_sessions", liveTest, StringComparison.Ordinal);
        Assert.Contains("target_id", liveTest, StringComparison.Ordinal);
        Assert.Contains("predicate_xml", liveTest, StringComparison.Ordinal);
        Assert.Contains("field_value", liveTest, StringComparison.Ordinal);
        Assert.Contains("unsupportedTargetCount", liveTest, StringComparison.Ordinal);
        Assert.Contains("BundleChecksum", liveTest, StringComparison.Ordinal);
        Assert.Contains("ValidateSelectedAssets", liveTest, StringComparison.Ordinal);
        Assert.Contains("GetQuery(major)", liveTest, StringComparison.Ordinal);
        Assert.Contains(" GRANT ", liveTest, StringComparison.Ordinal);
        Assert.Contains(" DENY ", liveTest, StringComparison.Ordinal);
        Assert.Contains("START EVENT SESSION", liveTest, StringComparison.Ordinal);
        Assert.Contains("SET NOCOUNT ON;\\n", liveTest, StringComparison.Ordinal);
        Assert.Contains("TOP (@maximum_rows)", liveTest, StringComparison.Ordinal);
        Assert.Contains("TOP (@probe_rows)", liveTest, StringComparison.Ordinal);
        Assert.Contains("TOP (@scan_rows)", liveTest, StringComparison.Ordinal);
        Assert.Contains("RECONFIGURE", liveTest, StringComparison.Ordinal);
        Assert.Contains("PHYSICAL_NAME", liveTest, StringComparison.Ordinal);
        Assert.Contains("OPENQUERY", liveTest, StringComparison.Ordinal);
        Assert.Contains("SelectedProductionAssetsAreBoundedAndPassiveForEveryMajor", liveTest, StringComparison.Ordinal);
        Assert.Contains("SelectedAssetValidatorRejectsMutationAndUnboundedQueries", liveTest, StringComparison.Ordinal);
        Assert.Contains("CollectorOperationalMode.Passive", liveTest, StringComparison.Ordinal);
        Assert.Contains("collectorEvidence", liveTest, StringComparison.Ordinal);
        Assert.Contains("transportEncrypted", liveTest, StringComparison.Ordinal);
        Assert.Contains("isSysAdmin", liveTest, StringComparison.Ordinal);
        Assert.Contains("msdb.dbo.sysschedules", liveTest, StringComparison.Ordinal);
        Assert.DoesNotContain("job_id,name,enabled FROM msdb.dbo.sysjobschedules", liveTest, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT name,enabled FROM sys.server_event_sessions", liveTest, StringComparison.Ordinal);
        Assert.DoesNotContain("target_name FROM sys.server_event_session_targets", liveTest, StringComparison.Ordinal);
        Assert.DoesNotContain("event_name FROM sys.server_event_session_events", liveTest, StringComparison.Ordinal);
        Assert.DoesNotContain("column_type FROM sys.server_event_session_fields", liveTest, StringComparison.Ordinal);
        Assert.DoesNotContain("database_id,actual_state", liveTest, StringComparison.Ordinal);
        Assert.Contains("SnapshotJobStepsQuery", liveTest, StringComparison.Ordinal);
        Assert.Contains("os_run_priority,command_hash", liveTest, StringComparison.Ordinal);
        Assert.DoesNotContain("os_run_priority,enabled", liveTest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("os_run_priority,enabled,command_hash", liveTest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SnapshotAlertsQuery", liveTest, StringComparison.Ordinal);
        Assert.Contains("notification_message_hash", liveTest, StringComparison.Ordinal);
        Assert.DoesNotContain("notification_message_id FROM msdb.dbo.sysalerts", liveTest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("next_run_date FROM msdb.dbo.sysjobsteps", liveTest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SnapshotMsdbProjectionsUseClosedPersistentAllowlist", liveTest, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(expected, actual)", liveTest, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($build,$verify)", source, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($verify,$final)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAllText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NegativeDesktopInvocationProducesNoCandidateOutput()
    {
        string root = FindRoot();
        string script = Path.Combine(root, "tools", "run-m12-sqlserver-certification.ps1");
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(pwsh)) pwsh = "pwsh";
        ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-RepositoryRoot", root, "-Profile", "Release", "-CaseId", "m12-sqlserver-2022-passive" }) start.ArgumentList.Add(arg);
        start.Environment["SQLOBSERVER_VALIDATION_PROFILE"] = "Release";
        start.Environment["SQLOBSERVER_RELEASE_SQLSERVER_ENVIRONMENT"] = "release-windows-server-2022";
        start.Environment["SQLOBSERVER_RELEASE_SQLSERVER"] = "Server=DESKTOP-IORRV3E;Integrated Security=true;Encrypt=true;TrustServerCertificate=false";
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Equal("M12-SQLSERVER-PRODUCER-HOST", output.Trim());
        Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
    }

    private static string FindRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName;
        return current ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
