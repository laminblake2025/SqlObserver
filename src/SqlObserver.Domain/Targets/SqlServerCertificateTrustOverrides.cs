namespace SqlObserver.Domain.Targets;

/// <summary>An administrator-managed exception for one exact target and TCP endpoint.</summary>
public sealed record SqlServerCertificateTrustOverride
{
    public SqlServerCertificateTrustOverride(Guid targetId, string host, int tcpPort)
    {
        if (targetId == Guid.Empty) throw new ArgumentException("A certificate trust exception requires a target ID.", nameof(targetId));
        _ = new SqlServerEndpoint(new SqlServerHostName(host), null, tcpPort);
        TargetId = targetId;
        Host = host;
        TcpPort = tcpPort;
    }
    public Guid TargetId { get; }
    public string Host { get; }
    public int TcpPort { get; }
}

public sealed class SqlServerCertificateTrustOverrides
{
    public const string ConfigurationSection = "SqlObserver:SqlServerCertificateTrustOverrides";
    public const string TrustedCapability = "transport.tls-trusted-by-operator";
    public static SqlServerCertificateTrustOverrides None { get; } = new([]);
    private readonly Dictionary<Guid, SqlServerCertificateTrustOverride> entries;

    public SqlServerCertificateTrustOverrides(IReadOnlyList<SqlServerCertificateTrustOverride> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > 32) throw new ArgumentException("At most 32 certificate trust exceptions are allowed.", nameof(entries));
        this.entries = entries.ToDictionary(entry => entry.TargetId);
    }

    public SqlServerConnectionPolicy Apply(Guid targetId, SqlServerConnectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        bool trusted = entries.TryGetValue(targetId, out var entry) &&
            policy.Endpoint.InstanceName is null && policy.Endpoint.TcpPort == entry.TcpPort &&
            string.Equals(policy.Endpoint.HostName.Value, entry.Host, StringComparison.OrdinalIgnoreCase);
        return new SqlServerConnectionPolicy(policy.Endpoint, policy.ConnectTimeout, policy.CertificateHostName, trusted);
    }
}
