using Npgsql;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Guards the profile boundary without starting Docker.</summary>
public sealed class PostgreSql18FixtureContractTests
{
    [Fact]
    public void ReleaseSelectsItsOwnConnectionAndDisablesPoolingAndErrorDetails()
    {
        string? resolved = PostgreSql18Fixture.ResolveExternalConnection(
            "Release", "Host=localhost;Database=local", "Host=release-db;Database=release;Pooling=true;Include Error Detail=true");
        var connection = new NpgsqlConnectionStringBuilder(Assert.IsType<string>(resolved));
        Assert.Equal("release-db", connection.Host);
        Assert.Equal("release", connection.Database);
        Assert.False(connection.Pooling);
        Assert.False(connection.IncludeErrorDetail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ReleaseRejectsMissingConnectionEvenWhenLocalIsConfigured(string? releaseConnection)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgreSql18Fixture.ResolveExternalConnection(
            "release", "Host=localhost;Database=local", releaseConnection));
        Assert.Contains("is required for Release PostgreSQL evidence", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Release", "Host=release-db")]
    [InlineData("Release", "Database=release")]
    [InlineData("Local", "Host=localhost")]
    [InlineData("Local", "Database=local")]
    public void ExternalConnectionsRequireHostAndDatabase(string profile, string selected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgreSql18Fixture.ResolveExternalConnection(profile, selected, selected));
        Assert.Contains("must specify Host and Database", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Release")]
    public void MalformedConnectionsFailInsteadOfFallingBack(string profile)
    {
        const string malformed = "Host=localhost;Database=local;UnknownSetting=invalid";
        var error = Assert.Throws<InvalidOperationException>(() => PostgreSql18Fixture.ResolveExternalConnection(profile, malformed, malformed));
        Assert.Contains("is malformed", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("::1")]
    public void LocalAcceptsOnlyExplicitLoopbackServiceEndpoints(string host)
    {
        string? resolved = PostgreSql18Fixture.ResolveExternalConnection(
            "Local", $"Host={host};Port=55432;Database=local;Pooling=true", "Host=release-db;Database=release");
        var connection = new NpgsqlConnectionStringBuilder(Assert.IsType<string>(resolved));
        Assert.Equal(host, connection.Host);
        Assert.Equal(55432, connection.Port);
        Assert.Equal("local", connection.Database);
        Assert.False(connection.Pooling);
    }

    [Theory]
    [InlineData("database.example.test")]
    [InlineData("127.0.0.1,database.example.test")]
    [InlineData("/var/run/postgresql")]
    public void LocalRejectsRemoteMultiHostAndSocketEndpoints(string host)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgreSql18Fixture.ResolveExternalConnection(
            "Local", $"Host={host};Database=local", null));
        Assert.Contains("single loopback host", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Local")]
    public void UnconfiguredLocalUsesTestcontainersWithoutReadingRelease(string? profile)
    {
        Assert.Null(PostgreSql18Fixture.ResolveExternalConnection(profile, null, "Host=release-db;Database=release"));
    }
}
