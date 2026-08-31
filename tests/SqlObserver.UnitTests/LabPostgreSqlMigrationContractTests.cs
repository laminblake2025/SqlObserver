using System.Text.Json;
using SqlObserver.Application.Deployment;
using SqlObserver.Domain.Repository;

namespace SqlObserver.UnitTests;

public sealed class LabPostgreSqlMigrationContractTests
{
    [Fact]
    public void InvocationAcceptsOnlyTheExactLoopbackLabShape()
    {
        string[] accepted =
        [
            "postgres",
            "migrate",
            "--lab-loopback",
            "--allow-loopback-cleartext",
            "--database",
            "sqlobserver",
            "--username",
            "sqlobserver_bootstrap",
        ];

        Assert.True(LabPostgreSqlMigrationInvocation.TryParse(accepted, out var invocation));
        Assert.NotNull(invocation);

        string[][] rejected =
        [
            [],
            [.. accepted, "--password", "secret"],
            [.. accepted, "--host", "db.example.test"],
            ["postgres", "migrate", "--lab-loopback", "--database", "sqlobserver", "--username", "sqlobserver_bootstrap"],
            ["postgres", "migrate", "--lab-loopback", "--allow-loopback-cleartext", "--database", "other", "--username", "sqlobserver_bootstrap"],
            ["postgres", "migrate", "--lab-loopback", "--allow-loopback-cleartext", "--database", "sqlobserver", "--username", "postgres"],
            ["postgres", "execute", "--sql", "select 1"],
        ];

        foreach (string[] value in rejected)
        {
            Assert.False(LabPostgreSqlMigrationInvocation.TryParse(value, out _));
        }
    }

    [Fact]
    public void ResultJsonIsClosedOrderedAndContainsNoEndpointOrCredentialMaterial()
    {
        Assert.True(LabPostgreSqlMigrationInvocation.TryParse(
            ["postgres", "migrate", "--lab-loopback", "--allow-loopback-cleartext", "--database", "sqlobserver", "--username", "sqlobserver_bootstrap"],
            out LabPostgreSqlMigrationInvocation? invocation));
        Assert.NotNull(invocation);

        DateTimeOffset completed = new(2026, 8, 31, 14, 15, 16, TimeSpan.Zero);
        var migration = new MigrationDescriptor(
            new MigrationNumber(1),
            "repository_bootstrap",
            new MigrationChecksum(Enumerable.Repeat((byte)0xAB, MigrationChecksum.RequiredLength).ToArray()),
            isTransactional: true);
        var batch = new MigrationBatchResult(
            [new MigrationExecutionResult(migration, MigrationOutcome.Applied, completed, completed)],
            completed);

        string json = LabPostgreSqlMigrationInvocation.CreateResultJson(batch);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlobserver_bootstrap", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            ["schemaVersion", "command", "status", "appliedCount", "alreadyAppliedCount", "failedCount", "completedAtUtc", "migrations"],
            document.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("succeeded", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("appliedCount").GetInt32());
        JsonElement result = Assert.Single(document.RootElement.GetProperty("migrations").EnumerateArray());
        Assert.Equal("abababababababababababababababababababababababababababababababab", result.GetProperty("sha256").GetString());
    }

    [Fact]
    public void FailureReportContainsOnlyTheClosedFailureCode()
    {
        Assert.True(LabPostgreSqlMigrationInvocation.TryParse(
            ["postgres", "migrate", "--lab-loopback", "--allow-loopback-cleartext", "--database", "sqlobserver", "--username", "sqlobserver_bootstrap"],
            out LabPostgreSqlMigrationInvocation? invocation));
        Assert.NotNull(invocation);

        DateTimeOffset completed = new(2026, 8, 31, 14, 15, 16, TimeSpan.Zero);
        var migration = new MigrationDescriptor(
            new MigrationNumber(2),
            "repository_tables",
            new MigrationChecksum(new byte[MigrationChecksum.RequiredLength]),
            isTransactional: true);
        var batch = new MigrationBatchResult(
            [new MigrationExecutionResult(migration, MigrationOutcome.Failed, completed, completed, "postgresql_23505")],
            completed);

        using JsonDocument document = JsonDocument.Parse(LabPostgreSqlMigrationInvocation.CreateResultJson(batch));
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("failedCount").GetInt32());
        Assert.Equal(
            "postgresql_23505",
            document.RootElement.GetProperty("migrations")[0].GetProperty("failureCode").GetString());
    }

    [Fact]
    public void ProcessBoundaryUsesHiddenInteractiveInputAndNeverEmitsProviderErrors()
    {
        string repositoryRoot = FindRepositoryRoot();
        string host = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SqlObserver.Cli",
            "LabPostgreSqlMigrationHost.cs"));

        Assert.Contains("Console.ReadKey(intercept: true)", host, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", host, StringComparison.Ordinal);
        Assert.Contains("settings[\"SSL Mode\"] = \"Disable\"", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.ReadLine", host, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnvironmentVariable", host, StringComparison.Ordinal);
        Assert.DoesNotContain(".Message", host, StringComparison.Ordinal);
        Assert.DoesNotContain("--password", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--connection", host, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SqlObserver.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
