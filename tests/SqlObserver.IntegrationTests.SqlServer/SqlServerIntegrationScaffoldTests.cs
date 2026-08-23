namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class SqlServerIntegrationScaffoldTests
{
#pragma warning disable xUnit1004 // This boundary is deliberately pending a real SQL Server implementation.
    [Fact(Skip = "Requires the future SQL Server runtime adapter and an isolated database.")]
    public void SqlServerCollectionContract()
    {
    }
#pragma warning restore xUnit1004
}
