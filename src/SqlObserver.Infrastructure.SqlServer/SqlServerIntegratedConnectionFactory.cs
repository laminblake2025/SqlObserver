using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Targets;

namespace SqlObserver.Infrastructure.SqlServer;

internal interface ISqlServerConnectionFactory
{
    ValueTask<SqlConnection> OpenConnectionAsync(
        SqlServerConnectionPolicy policy,
        CancellationToken cancellationToken);
}

/// <summary>Builds only the product-owned, Windows-integrated target connection policy.</summary>
internal sealed class SqlServerIntegratedConnectionFactory : ISqlServerConnectionFactory
{
    internal const string ApplicationName = "SqlObserver.CapabilityDiscovery";
    internal const string CollectionApplicationName = "SqlObserver.CollectionEngine";

    private readonly string _applicationName;

    public SqlServerIntegratedConnectionFactory()
        : this(ApplicationName)
    {
    }

    internal SqlServerIntegratedConnectionFactory(string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        _applicationName = applicationName;
    }

    public async ValueTask<SqlConnection> OpenConnectionAsync(
        SqlServerConnectionPolicy policy,
        CancellationToken cancellationToken)
    {
        SqlConnection connection = CreateConnection(policy, _applicationName);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static SqlConnection CreateConnection(SqlServerConnectionPolicy policy) =>
        CreateConnection(policy, ApplicationName);

    internal static SqlConnection CreateConnection(
        SqlServerConnectionPolicy policy,
        string applicationName)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        SqlServerEndpoint endpoint = policy.Endpoint;
        string dataSource = endpoint switch
        {
            { InstanceName: not null, TcpPort: null } =>
                string.Concat(endpoint.HostName.Value, "\\", endpoint.InstanceName.Value),
            { InstanceName: null, TcpPort: not null } => string.Create(
                CultureInfo.InvariantCulture,
                $"tcp:{endpoint.HostName.Value},{endpoint.TcpPort.Value}"),
            _ => throw new InvalidOperationException(
                "A SQL Server endpoint must contain exactly one routing selector."),
        };

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = false,
            ApplicationName = applicationName,
            ConnectTimeout = Math.Min(
                SqlServerCapabilityAssetCatalog.ConnectTimeoutSeconds,
                checked((int)Math.Floor(policy.ConnectTimeout.Value.TotalSeconds))),
            PersistSecurityInfo = false,
            MultipleActiveResultSets = false,
            Enlist = false,
            Pooling = true,
        };

        if (policy.CertificateHostName is not null)
        {
            builder.HostNameInCertificate = policy.CertificateHostName.Value;
        }

        return new SqlConnection(builder.ConnectionString);
    }
}
