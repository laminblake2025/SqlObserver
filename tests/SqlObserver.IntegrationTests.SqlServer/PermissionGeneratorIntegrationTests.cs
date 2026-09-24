using System.Diagnostics;
using System.Text;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class PermissionGeneratorIntegrationTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public async Task PermissionPlansAreOfflineDeterministicAndReversible(int majorVersion)
    {
        ScriptResult first = await RunGeneratorAsync(
            majorVersion,
            "CONTOSO\\sqlobserver$",
            "Grant");
        ScriptResult second = await RunGeneratorAsync(
            majorVersion,
            "CONTOSO\\sqlobserver$",
            "Grant");

        Assert.Equal(0, first.ExitCode);
        Assert.Empty(first.StandardError);
        Assert.Equal(first.StandardOutput, second.StandardOutput);
        Assert.DoesNotContain('\r', first.StandardOutput);
        Assert.Contains("SERVERPROPERTY(N'PathSeparator')", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("<> N'\\'", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("IS_SRVROLEMEMBER(N'sysadmin', @sqlobserver_principal)", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("IF @sqlobserver_operation = N'Grant'", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ELSE IF @sqlobserver_operation = N'Remove'", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Removal section", first.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE LOGIN", first.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sp_configure", first.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EVENT SESSION", first.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QUERY_STORE", first.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USE [msdb]", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("CREATE USER", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("SUSER_SNAME(database_principal.sid)", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON OBJECT::[dbo].[backupset]", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON OBJECT::[dbo].[sysjobhistory]", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("IF CONVERT(int, SERVERPROPERTY(N'EngineEdition')) IN (2, 3)", first.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLAgentReaderRole", first.StandardOutput, StringComparison.Ordinal);

        if (majorVersion == 15)
        {
            Assert.Contains("GRANT VIEW SERVER STATE", first.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("REVOKE VIEW SERVER STATE", first.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("ADD MEMBER", first.StandardOutput, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(
                "ALTER SERVER ROLE [##MS_ServerPerformanceStateReader##] ADD MEMBER",
                first.StandardOutput,
                StringComparison.Ordinal);
            Assert.Contains(
                "ALTER SERVER ROLE [##MS_ServerPerformanceStateReader##] DROP MEMBER",
                first.StandardOutput,
                StringComparison.Ordinal);
            Assert.DoesNotContain("GRANT VIEW SERVER STATE", first.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PermissionPlanEscapesButNeverExecutesTheValidatedPrincipal()
    {
        ScriptResult result = await RunGeneratorAsync(
            16,
            "CONTOSO\\O'Brien]svc",
            "Remove");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("N'CONTOSO\\O''Brien]svc'", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[CONTOSO\\O'Brien]]svc]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("N'Remove'", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermissionGeneratorRejectsNonWindowsPrincipalSyntax()
    {
        ScriptResult result = await RunGeneratorAsync(16, "sa", "Grant");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("Windows DOMAIN\\name or user@domain", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(15, "VIEW DATABASE STATE")]
    [InlineData(16, "VIEW DATABASE PERFORMANCE STATE")]
    [InlineData(17, "VIEW DATABASE PERFORMANCE STATE")]
    public async Task QueryStoreOptInMapsOnlyTheNamedDatabaseAndGrantsTheVersionedReadPermission(int major, string permission)
    {
        ScriptResult result = await RunGeneratorAsync(major, "CONTOSO\\sqlobserver$", "Grant", "Accounting");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("USE [Accounting];", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("name = N'Accounting' AND database_id > 4 AND state = 0", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("database_principal.sid <> SUSER_SID(@sqlobserver_principal)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("CREATE USER [CONTOSO\\sqlobserver$] FOR LOGIN [CONTOSO\\sqlobserver$];", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GRANT CONNECT TO [CONTOSO\\sqlobserver$];", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"GRANT {permission} TO [CONTOSO\\sqlobserver$];", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER DATABASE", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE LOGIN", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db_owner", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db_datareader", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT SELECT ALL", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        if (major > 15) Assert.DoesNotContain("GRANT VIEW DATABASE STATE", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryStoreDatabaseIsEscapedAsDataInBothIdentifierAndLiteralContexts()
    {
        ScriptResult result = await RunGeneratorAsync(16, "CONTOSO\\O'Brien]svc", "Grant", "O'Brien] 倉庫; --");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("USE [O'Brien]] 倉庫; --];", Assert.Single(result.StandardOutput.Split('\n'), line => line.StartsWith("USE [O", StringComparison.Ordinal)));
        Assert.Contains("name = N'O''Brien] 倉庫; --'", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("CREATE USER [CONTOSO\\O'Brien]]svc] FOR LOGIN [CONTOSO\\O'Brien]]svc];", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("USE [O'Brien] 倉庫; --];", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("TeMpDb")]
    [InlineData("msdb")]
    [InlineData("model")]
    [InlineData(" Accounting")]
    [InlineData("Accounting\nUSE master")]
    public async Task QueryStoreDatabaseRejectsSystemNamesAndMalformedValuesBeforeProducingSql(string database)
    {
        ScriptResult result = await RunGeneratorAsync(16, "CONTOSO\\sqlobserver$", "Grant", database);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("QueryStoreDatabase must be a trimmed user database name", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryStoreRemovalRequiresGrantProvenanceInsteadOfRevokingExistingDatabaseAccess()
    {
        ScriptResult result = await RunGeneratorAsync(16, "CONTOSO\\sqlobserver$", "Remove", "Accounting");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("QueryStoreDatabase supports Grant only", result.StandardError, StringComparison.Ordinal);
    }

    private static async Task<ScriptResult> RunGeneratorAsync(
        int majorVersion,
        string principal,
        string operation,
        string? queryStoreDatabase = null)
    {
        string repositoryRoot = FindRepositoryRoot();
        string scriptPath = Path.Combine(repositoryRoot, "tools", "generate-permissions.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repositoryRoot,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-SqlServerMajorVersion");
        startInfo.ArgumentList.Add(majorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-Principal");
        startInfo.ArgumentList.Add(principal);
        startInfo.ArgumentList.Add("-Operation");
        startInfo.ArgumentList.Add(operation);
        if (queryStoreDatabase is not null)
        {
            startInfo.ArgumentList.Add("-QueryStoreDatabase");
            startInfo.ArgumentList.Add(queryStoreDatabase);
        }

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("PowerShell permission generator could not be started.");
        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        return new ScriptResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }

    private sealed record ScriptResult(int ExitCode, string StandardOutput, string StandardError);
}
