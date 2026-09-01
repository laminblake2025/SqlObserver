using System.Text.RegularExpressions;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class MigrationDependencyRegistryTests
{
    [Fact]
    public void ScheduledDependencyRegistryExcludesDiscoveryOnlyCapabilityPrerequisites()
    {
        string root = FindRoot();
        string migrationDirectory = Path.Combine(root, "database", "migrations");
        var statementPattern = new Regex(
            @"INSERT\s+INTO\s+control\.collector_dependency\b.*?;",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Match[] statements = Directory
            .EnumerateFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .SelectMany(path => statementPattern.Matches(File.ReadAllText(path)).Cast<Match>())
            .ToArray();

        Assert.NotEmpty(statements);
        Assert.All(
            statements,
            statement => Assert.DoesNotContain(
                "'capability.connection'",
                statement.Value,
                StringComparison.Ordinal));
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx")))
        {
            path = Directory.GetParent(path)?.FullName
                ?? throw new DirectoryNotFoundException("Repository root was not found.");
        }

        return path;
    }
}
