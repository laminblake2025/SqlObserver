using System.Globalization;

namespace SqlObserver.UnitTests;

public sealed class MigrationPreambleTests
{
    // Earlier applied migrations are immutable. Enforce the convention from
    // the first forward migration after the repository review onward.
    private const int FirstEnforcedMigration = 117;

    [Fact]
    public void NewTransactionalMigrationsSetMigratorRoleAndLockBudgetsBeforeSql()
    {
        string migrations = Path.Combine(FindRoot(), "database", "migrations");
        string[] names = File.ReadAllLines(Path.Combine(migrations, "checksums.sha256"))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
            .Where(name => int.Parse(name[..4], CultureInfo.InvariantCulture) >= FirstEnforcedMigration)
            .ToArray();
        Assert.NotEmpty(names);

        foreach (string name in names)
        {
            string[] lines = File.ReadAllLines(Path.Combine(migrations, name));
            if (lines[0].StartsWith("-- sqlobserver:nontransactional-index=", StringComparison.Ordinal) ||
                lines[0].StartsWith("-- sqlobserver:partitioned-concurrent-index=", StringComparison.Ordinal))
                continue; // The concurrent-index runner forbids transaction-local settings.

            string[] preamble = lines
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))
                .Take(4)
                .ToArray();
            string[] expected = [
                "SET LOCAL ROLE sqlobserver_migrator;",
                "SET LOCAL lock_timeout = '5s';",
                "SET LOCAL statement_timeout = '5min';",
                "SET LOCAL TIME ZONE 'UTC';",
            ];
            Assert.True(preamble.SequenceEqual(expected, StringComparer.Ordinal),
                $"{name} must begin with the migrator role, lock timeout, statement timeout, and UTC setting.");
        }
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "database", "migrations", "checksums.sha256")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
