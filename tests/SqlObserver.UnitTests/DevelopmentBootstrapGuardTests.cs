using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.UnitTests;

public sealed class DevelopmentBootstrapGuardTests
{
    private const string Repository = "Host=127.0.0.1;Database=sqlobserver_dev;Username=postgres;Password=bootstrap-test-only";
    private static readonly string Password = new('a', 64);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("development")]
    public async Task NonDevelopmentEnvironmentIsRejectedBeforeOpeningAnyConnection(string? environment)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PostgreSqlDevelopmentBootstrap.ApplyAsync(environment, Repository, Password, CancellationToken.None));
    }

    [Theory]
    [InlineData("Host=sql.example.invalid;Database=sqlobserver_dev;Username=postgres;Password=secret")]
    [InlineData("Host=localhost;Database=sqlobserver_dev;Username=postgres;Password=secret")]
    [InlineData("Host=127.0.0.1,10.0.0.1;Database=sqlobserver_dev;Username=postgres;Password=secret")]
    [InlineData("Host=127.0.0.1;Database=production;Username=postgres;Password=secret")]
    [InlineData("Host=127.0.0.1;Database=sqlobserver_dev;Username=service;Password=secret")]
    [InlineData("Host=127.0.0.1;Database=sqlobserver_dev;Username=postgres")]
    [InlineData("Host=127.0.0.1;Database=sqlobserver_dev;Username=postgres;Password=secret;Options=-c role=sqlobserver_migrator")]
    [InlineData("not-a-connection-string")]
    public async Task UnsafeConnectionIsRejectedWithoutEchoingConfiguration(string repository)
    {
        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            PostgreSqlDevelopmentBootstrap.ApplyAsync("Development", repository, Password, CancellationToken.None));
        Assert.DoesNotContain(repository, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("01234567890123456789012345678901\n")]
    public void InvalidApplicationPasswordIsRejected(string? password) =>
        Assert.Throws<ArgumentException>(() => PostgreSqlDevelopmentBootstrap.ValidateConfiguration("Development", Repository, password));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void ExplicitLoopbackDevelopmentConfigurationIsAccepted(string address) =>
        PostgreSqlDevelopmentBootstrap.ValidateConfiguration("Development", Repository.Replace("127.0.0.1", address, StringComparison.Ordinal), Password);
}
