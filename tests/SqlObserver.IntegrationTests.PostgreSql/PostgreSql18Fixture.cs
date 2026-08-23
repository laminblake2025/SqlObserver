using Npgsql;
using System.Security.Cryptography;
using Testcontainers.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSql18CollectionDefinition : ICollectionFixture<PostgreSql18Fixture>
{
    public const string Name = "PostgreSQL 18.4 repository";
}

public sealed class PostgreSql18Fixture : IAsyncLifetime
{
    public const string Image = "postgres:18.4-bookworm@sha256:7e6103cf85f88f7a0eddb3ec0b1ba8940eba098ed118ade25a729ca9daee5568";

    private readonly PostgreSqlContainer _container;

    public PostgreSql18Fixture()
    {
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("sqlobserver_test_admin")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
    }

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<RepositoryTestDatabase> CreateDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        string databaseName = $"sqlobserver_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE \"{databaseName}\" TEMPLATE template0 ENCODING 'UTF8';",
                connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName,
            IncludeErrorDetail = false,
            ApplicationName = "SqlObserver.PostgreSqlIntegrationTests",
        };
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString.ConnectionString);
        return new RepositoryTestDatabase(
            _container,
            databaseName,
            connectionString.ConnectionString,
            dataSource);
    }
}

public sealed class RepositoryTestDatabase : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;
    private readonly string _adminConnectionString;

    internal RepositoryTestDatabase(
        PostgreSqlContainer container,
        string databaseName,
        string adminConnectionString,
        NpgsqlDataSource dataSource)
    {
        _container = container;
        _adminConnectionString = adminConnectionString;
        DatabaseName = databaseName;
        DataSource = dataSource;
    }

    public string DatabaseName { get; }

    public NpgsqlDataSource DataSource { get; }

    public NpgsqlDataSource CreateCollectorDataSource() =>
        CreateRoleDataSource("sqlobserver_collector");

    public NpgsqlDataSource CreateServerDataSource() =>
        CreateRoleDataSource("sqlobserver_server");

    private NpgsqlDataSource CreateRoleDataSource(string role)
    {
        string setRoleSql = role switch
        {
            "sqlobserver_collector" => "SET ROLE sqlobserver_collector;",
            "sqlobserver_server" => "SET ROLE sqlobserver_server;",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        var connectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Pooling = false,
        };
        var builder = new NpgsqlDataSourceBuilder(connectionString.ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            connection =>
            {
                using var command = new NpgsqlCommand(setRoleSql, connection);
                command.ExecuteNonQuery();
            },
            async connection =>
            {
                await using var command = new NpgsqlCommand(setRoleSql, connection);
                await command.ExecuteNonQueryAsync();
            });
        return builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE);",
            connection);
        await command.ExecuteNonQueryAsync();
        GC.SuppressFinalize(this);
    }
}
