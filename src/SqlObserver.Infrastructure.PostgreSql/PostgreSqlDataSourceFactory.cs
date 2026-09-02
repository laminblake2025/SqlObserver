using Npgsql;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Builds a repository data source without enabling provider-detail or parameter logging.</summary>
public static class PostgreSqlDataSourceFactory
{
    public const int MaximumConfigurationLength = 8_192;

    public static NpgsqlDataSource Create(string repositoryConfiguration, string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryConfiguration);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        if (repositoryConfiguration.Length > MaximumConfigurationLength)
        {
            throw new ArgumentException(
                "The repository connection configuration is too large.",
                nameof(repositoryConfiguration));
        }

        var settings = new NpgsqlConnectionStringBuilder(repositoryConfiguration)
        {
            ApplicationName = applicationName,
            IncludeErrorDetail = false,
            PersistSecurityInfo = false,
            Timezone = "UTC",
        };

        return NpgsqlDataSource.Create(settings.ConnectionString);
    }
}
