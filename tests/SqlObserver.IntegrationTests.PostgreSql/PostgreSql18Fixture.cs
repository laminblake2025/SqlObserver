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

    private readonly PostgreSqlContainer? _container;
    private readonly string _adminConnectionString;

    public PostgreSql18Fixture()
    {
        string? externalConnection = ResolveExternalConnection(
            Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE"),
            Environment.GetEnvironmentVariable("SQLOBSERVER_LOCAL_POSTGRES"),
            Environment.GetEnvironmentVariable("SQLOBSERVER_RELEASE_POSTGRES"));
        if (externalConnection is not null)
        {
            _adminConnectionString = externalConnection;
            return;
        }

        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("sqlobserver_test_admin")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        _adminConnectionString = string.Empty;
    }

    // The CI service and local development repository are reached through a
    // published loopback port. Release always requires its own explicit contract;
    // it must never fall back to Local or silently start a disposable container.
    internal static string? ResolveExternalConnection(string? profile, string? localConnection, string? releaseConnection)
    {
        bool releaseProfile = string.Equals(profile, "Release", StringComparison.OrdinalIgnoreCase);
        string settingName = releaseProfile ? "SQLOBSERVER_RELEASE_POSTGRES" : "SQLOBSERVER_LOCAL_POSTGRES";
        string? selected = releaseProfile ? releaseConnection : localConnection;
        if (string.IsNullOrWhiteSpace(selected))
        {
            if (releaseProfile)
                throw new InvalidOperationException("SQLOBSERVER_RELEASE_POSTGRES is required for Release PostgreSQL evidence.");
            return null;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(selected) { Pooling = false, IncludeErrorDetail = false };
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
                throw new InvalidOperationException($"{settingName} must specify Host and Database.");
            if (!releaseProfile && builder.Host is not "127.0.0.1" and not "::1" &&
                !string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLOBSERVER_LOCAL_POSTGRES must use a single loopback host.");
            return builder.ConnectionString;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"{settingName} is malformed.", exception);
        }
    }

    public Task InitializeAsync() => _container is null ? Task.CompletedTask : _container.StartAsync();

    public Task DisposeAsync() => _container is null ? Task.CompletedTask : _container!.DisposeAsync().AsTask();

    public async Task<RepositoryTestDatabase> CreateDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        string databaseName = $"sqlobserver_{Guid.NewGuid():N}";
        string adminConnectionString = _container is null ? _adminConnectionString : _container!.GetConnectionString();
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE \"{databaseName}\" TEMPLATE template0 ENCODING 'UTF8';",
                connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
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
            adminConnectionString,
            dataSource);
    }
}

public sealed class RepositoryTestDatabase : IAsyncDisposable
{
    private readonly PostgreSqlContainer? _container;
    private readonly string _adminConnectionString;
    private readonly string _cleanupConnectionString;

    internal RepositoryTestDatabase(
        PostgreSqlContainer? container,
        string databaseName,
        string adminConnectionString,
        string cleanupConnectionString,
        NpgsqlDataSource dataSource)
    {
        _container = container;
        _adminConnectionString = adminConnectionString;
        _cleanupConnectionString = cleanupConnectionString;
        DatabaseName = databaseName;
        DataSource = dataSource;
    }

    public string DatabaseName { get; }

    public string ConnectionString => _adminConnectionString;

    public NpgsqlDataSource DataSource { get; }

    public NpgsqlDataSource CreateCollectorDataSource(bool initializeEmptyTargetScope = false) =>
        CreateRoleDataSource("sqlobserver_collector", initializeEmptyTargetScope);

    public NpgsqlDataSource CreateServerDataSource() =>
        CreateRoleDataSource("sqlobserver_server");

    private NpgsqlDataSource CreateRoleDataSource(string role, bool initializeEmptyTargetScope = false)
    {
        string setRoleSql = role switch
        {
            "sqlobserver_collector" => "SET ROLE sqlobserver_collector;",
            "sqlobserver_server" => "SET ROLE sqlobserver_server;",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        if (initializeEmptyTargetScope) setRoleSql += "SELECT set_config('sqlobserver.target_scope','',false);";
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

        await using var connection = new NpgsqlConnection(_container is null ? _cleanupConnectionString : _container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE);",
            connection);
        await command.ExecuteNonQueryAsync();
        GC.SuppressFinalize(this);
    }
}
