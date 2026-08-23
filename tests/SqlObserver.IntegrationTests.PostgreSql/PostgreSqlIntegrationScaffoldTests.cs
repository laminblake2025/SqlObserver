namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class PostgreSqlIntegrationScaffoldTests
{
#pragma warning disable xUnit1004 // This boundary is deliberately pending a real PostgreSQL implementation.
    [Fact(Skip = "Requires the future PostgreSQL runtime adapter and an isolated database.")]
    public void PostgreSqlRoundTripContract()
    {
    }
#pragma warning restore xUnit1004
}
