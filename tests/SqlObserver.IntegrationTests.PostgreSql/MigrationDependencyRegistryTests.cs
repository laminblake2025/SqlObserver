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

    [Fact]
    public void DeadlockCommitFunctionGrantsUseTheDeclaredArgumentSignature()
    {
        string root = FindRoot();
        string migration = File.ReadAllText(Path.Combine(
            root,
            "database",
            "migrations",
            "0010_deadlocks_extended_events.sql"));
        string compact = Regex.Replace(migration, @"\s+", string.Empty);
        const string signature =
            "uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea," +
            "text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer," +
            "text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[]";
        string functionReference = $"ONFUNCTIONcontrol.commit_deadlock_collection_run({signature})";

        Assert.Equal(2, Regex.Count(
            compact,
            Regex.Escape(functionReference),
            RegexOptions.CultureInvariant));
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
