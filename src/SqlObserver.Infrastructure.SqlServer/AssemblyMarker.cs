using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SqlObserver.IntegrationTests.SqlServer")]
[assembly: InternalsVisibleTo("SqlObserver.IntegrationTests.PostgreSql")]

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Identifies the SQL Server infrastructure adapter assembly.</summary>
public static class AssemblyMarker
{
}
