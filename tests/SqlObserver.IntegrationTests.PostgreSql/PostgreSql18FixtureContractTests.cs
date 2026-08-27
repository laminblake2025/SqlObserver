namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Guards the profile boundary without starting Docker.</summary>
public sealed class PostgreSql18FixtureContractTests
{
    [Fact]
    public void ReleaseFixtureUsesValidatedConnectionAndDoesNotConstructContainer()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/SqlObserver.IntegrationTests.PostgreSql/PostgreSql18Fixture.cs"));
        string source = File.ReadAllText(path);
        Assert.Contains("SQLOBSERVER_RELEASE_POSTGRES", source, StringComparison.Ordinal);
        int releaseBranch = source.IndexOf("string.Equals(profile, \"Release\"", StringComparison.Ordinal);
        int containerConstruction = source.IndexOf("new PostgreSqlBuilder", StringComparison.Ordinal);
        Assert.True(releaseBranch >= 0 && containerConstruction > releaseBranch);
        Assert.Contains("return;", source[releaseBranch..containerConstruction], StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseFixtureRejectsMissingOrMalformedConnection()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/SqlObserver.IntegrationTests.PostgreSql/PostgreSql18Fixture.cs"));
        string source = File.ReadAllText(path);
        Assert.Contains("is required for Release PostgreSQL evidence", source, StringComparison.Ordinal);
        Assert.Contains("is malformed", source, StringComparison.Ordinal);
        Assert.Contains("must specify Host and Database", source, StringComparison.Ordinal);
    }
}
