using Npgsql;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.SecurityTests;

public sealed class PostgreSqlDataSourceFactoryTests
{
    [Fact]
    public async Task FactoryForcesProviderDetailOffAndSuppressesPersistedSecrets()
    {
        await using NpgsqlDataSource dataSource = PostgreSqlDataSourceFactory.Create(
            "Host=localhost;Database=sqlobserver;Username=service;Password=test-only;Include Error Detail=true;Persist Security Info=true;Timezone=America/New_York",
            "SqlObserver.Tests");
        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.False(settings.IncludeErrorDetail);
        Assert.False(settings.PersistSecurityInfo);
        Assert.Equal("SqlObserver.Tests", settings.ApplicationName);
        Assert.Equal("UTC", settings.Timezone);
    }

    [Fact]
    public void FactoryRejectsUnboundedConfiguration()
    {
        string oversized = new('x', PostgreSqlDataSourceFactory.MaximumConfigurationLength + 1);

        Assert.Throws<ArgumentException>(() =>
            PostgreSqlDataSourceFactory.Create(oversized, "SqlObserver.Tests"));
    }
}
