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

    [Fact]
    public void QueryPerformanceMigrationQualifiesPlpgsqlColumnReferences()
    {
        string root = FindRoot();
        string migration = File.ReadAllText(Path.Combine(
            root,
            "database",
            "migrations",
            "0011_query_performance.sql"));

        Assert.Contains(
            "SELECT baseline.cumulative_executions,baseline.cumulative_cpu_ms",
            migration,
            StringComparison.Ordinal);
        Assert.Contains("FROM candidates AS candidate", migration, StringComparison.Ordinal);
        Assert.Contains("SELECT candidate.database_id,candidate.query_fingerprint", migration, StringComparison.Ordinal);
        Assert.Equal(
            4,
            Regex.Count(
                migration,
                @"JOIN events\.query_performance_run r ON r\.collection_run_id=o\.collection_run_id",
                RegexOptions.CultureInvariant));
        Assert.DoesNotContain(
            "JOIN events.query_performance_run r USING(collection_run_id)",
            migration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SELECT cumulative_executions,cumulative_cpu_ms",
            migration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AlertingMigrationDefinesCanonicalEvidenceOverloadBeforeCompatibilityWrapper()
    {
        string root = FindRoot();
        string migration = File.ReadAllText(Path.Combine(
            root,
            "database",
            "migrations",
            "0012_alerts_maintenance_notifications.sql"));
        const string canonicalOverload =
            "CREATE OR REPLACE FUNCTION alerting.canonical_evidence_sha256(" +
            "p_target_id uuid, p_rule_id uuid, p_source_kind text";
        const string compatibilityWrapper =
            "CREATE OR REPLACE FUNCTION alerting.canonical_evidence_sha256(" +
            "p_target_id uuid, p_rule_id uuid, p_observed_at timestamptz";

        int canonicalIndex = migration.IndexOf(canonicalOverload, StringComparison.Ordinal);
        int wrapperIndex = migration.IndexOf(compatibilityWrapper, StringComparison.Ordinal);

        Assert.True(canonicalIndex >= 0, "Canonical evidence overload is missing.");
        Assert.True(wrapperIndex > canonicalIndex, "Compatibility wrapper must follow its dependency.");
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
