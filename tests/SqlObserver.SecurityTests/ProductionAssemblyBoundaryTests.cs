namespace SqlObserver.SecurityTests;

public sealed class ProductionAssemblyBoundaryTests
{
    private static readonly string[] DatabaseDrivers =
    [
        "Npgsql",
        "Microsoft.Data.SqlClient",
        "System.Data.SqlClient",
    ];

    [Fact]
    public void ServerDoesNotReferenceSqlServerTargetAdapterOrDriver()
    {
        string[] references = GetDirectAssemblyReferences(typeof(Server.ScaffoldEndpoints));

        Assert.DoesNotContain("SqlObserver.Infrastructure.SqlServer", references);
        Assert.DoesNotContain("Microsoft.Data.SqlClient", references);
        Assert.DoesNotContain("System.Data.SqlClient", references);
    }

    [Fact]
    public void McpStdioDoesNotReferenceDatabaseAdaptersOrDrivers()
    {
        string[] references = GetDirectAssemblyReferences(typeof(McpStdio.StdioBridgeScaffold));
        string[] forbidden =
        [
            "SqlObserver.Infrastructure.SqlServer",
            "SqlObserver.Infrastructure.PostgreSql",
            .. DatabaseDrivers,
        ];

        Assert.DoesNotContain(references, forbidden.Contains);
    }

    [Fact]
    public void McpApplicationAssemblyDoesNotReferenceDatabaseAdaptersOrDrivers()
    {
        string[] references = GetDirectAssemblyReferences(typeof(Mcp.AssemblyMarker));
        string[] forbidden =
        [
            "SqlObserver.Infrastructure.SqlServer",
            "SqlObserver.Infrastructure.PostgreSql",
            .. DatabaseDrivers,
        ];

        Assert.DoesNotContain(references, forbidden.Contains);
    }

    private static string[] GetDirectAssemblyReferences(Type productionType) =>
        productionType.Assembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
