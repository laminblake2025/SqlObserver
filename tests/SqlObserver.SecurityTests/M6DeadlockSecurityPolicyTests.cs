using SqlObserver.Server;

namespace SqlObserver.SecurityTests;

public sealed class M6DeadlockSecurityPolicyTests
{
    [Fact]
    public void DeadlockSurfaceContainsNoRawOrProviderFields()
    {
        string[] names = typeof(DeadlockDetailResponse).Assembly.GetTypes().Where(static type => type.Namespace == "SqlObserver.Server" && type.Name.Contains("Deadlock", StringComparison.Ordinal)).SelectMany(static type => type.GetProperties()).Select(static property => property.Name).ToArray();
        Assert.DoesNotContain(names, static name => name.Contains("Xml", StringComparison.OrdinalIgnoreCase) || name.Contains("Sql", StringComparison.OrdinalIgnoreCase) || name.Contains("Query", StringComparison.OrdinalIgnoreCase) || name.Contains("Path", StringComparison.OrdinalIgnoreCase) || name.Contains("Host", StringComparison.OrdinalIgnoreCase) || name.Contains("Login", StringComparison.OrdinalIgnoreCase) || name.Contains("Application", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeadlockRetentionIsEnvelopeOnlyAndServiceRolesHaveNoBaseTableMutationPath()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0010_deadlocks_extended_events.sql"));
        string migration = File.ReadAllText(path);
        Assert.Contains("ON DELETE CASCADE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("envelope_cascade", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REVOKE ALL ON TABLE events.deadlock_summary, events.deadlock_participant, events.deadlock_relation FROM PUBLIC, sqlobserver_server, sqlobserver_collector", migration, StringComparison.OrdinalIgnoreCase);
    }
}
