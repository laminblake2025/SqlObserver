using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Targets;

/// <summary>A stable, normalized repository key that never contains an endpoint.</summary>
public sealed record ObservationTargetKey
{
    public const int MaximumLength = 256;

    public ObservationTargetKey(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character =>
                character is >= 'a' and <= 'z' ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '.' or '_' or ':' or '-',
            requireLeadingLetter: false);

        if (Value[0] is not (>= 'a' and <= 'z') && !DomainValidation.IsAsciiDigit(Value[0]))
        {
            throw new ArgumentException("A target key must begin with a lowercase ASCII letter or digit.", nameof(value));
        }
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Bounded, untrusted operator-facing target text.</summary>
public sealed record ObservationTargetDisplayName
{
    public const int MaximumUtf8Bytes = 512;

    public ObservationTargetDisplayName(string value)
    {
        Value = DomainValidation.RequireSafeText(value, nameof(value), MaximumUtf8Bytes);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ObservationTargetRevision
{
    public ObservationTargetRevision(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public long Value { get; }

    public ObservationTargetRevision Next()
    {
        return new ObservationTargetRevision(checked(Value + 1));
    }
}

public enum ObservationTargetLifecycle
{
    PendingDiscovery = 1,
    Active = 2,
    Disabled = 3,
    Retired = 4,
}

/// <summary>A bounded DNS, NetBIOS, or IP literal supplied independently from connection syntax.</summary>
public sealed record SqlServerHostName
{
    public const int MaximumLength = 255;

    public SqlServerHostName(string value)
    {
        Value = ValidateHostToken(value, nameof(value), MaximumLength);
    }

    public string Value { get; }

    public override string ToString() => Value;

    internal static string ValidateHostToken(string value, string parameterName, int maximumLength)
    {
        string result = DomainValidation.RequireAsciiToken(
            value,
            parameterName,
            maximumLength,
            static character =>
                DomainValidation.IsAsciiLetter(character) ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '.' or '_' or ':' or '-',
            requireLeadingLetter: false);

        if (!DomainValidation.IsAsciiLetter(result[0]) && !DomainValidation.IsAsciiDigit(result[0]))
        {
            throw new ArgumentException("A host name must begin with an ASCII letter or digit.", parameterName);
        }

        return result;
    }
}

public sealed record SqlServerInstanceName
{
    public const int MaximumLength = 128;

    public SqlServerInstanceName(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character =>
                DomainValidation.IsAsciiLetter(character) ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '_' or '$' or '-',
            requireLeadingLetter: false);

        if (!DomainValidation.IsAsciiLetter(Value[0]) && !DomainValidation.IsAsciiDigit(Value[0]))
        {
            throw new ArgumentException("An instance name must begin with an ASCII letter or digit.", nameof(value));
        }
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record SqlServerCertificateHostName
{
    public const int MaximumLength = SqlServerHostName.MaximumLength;

    public SqlServerCertificateHostName(string value)
    {
        Value = SqlServerHostName.ValidateHostToken(value, nameof(value), MaximumLength);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A structured endpoint; it cannot carry driver keywords or a connection string.</summary>
public sealed class SqlServerEndpoint
{
    public SqlServerEndpoint(
        SqlServerHostName hostName,
        SqlServerInstanceName? instanceName = null,
        int? tcpPort = null)
    {
        ArgumentNullException.ThrowIfNull(hostName);

        if (tcpPort is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(tcpPort));
        }

        if ((instanceName is null) == (tcpPort is null))
        {
            throw new ArgumentException(
                "A SQL Server endpoint must specify exactly one named instance or explicit TCP port.");
        }

        HostName = hostName;
        InstanceName = instanceName;
        TcpPort = tcpPort;
    }

    public SqlServerHostName HostName { get; }

    public SqlServerInstanceName? InstanceName { get; }

    public int? TcpPort { get; }
}

public enum SqlServerAuthenticationMode
{
    WindowsIntegrated = 1,
}

public enum SqlServerTransportSecurity
{
    EncryptAndValidateCertificate = 1,
}

public sealed record SqlServerConnectTimeout
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Maximum = TimeSpan.FromSeconds(30);

    public SqlServerConnectTimeout(TimeSpan value)
    {
        if (value < Minimum || value > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"A target connect timeout must be between {Minimum} and {Maximum}.");
        }

        Value = value;
    }

    public TimeSpan Value { get; }
}

/// <summary>
/// A Windows-integrated, encrypted, certificate-validating target connection policy. It has no
/// password, secret reference, arbitrary driver option, or connection-string surface.
/// </summary>
public sealed class SqlServerConnectionPolicy
{
    public SqlServerConnectionPolicy(
        SqlServerEndpoint endpoint,
        SqlServerConnectTimeout connectTimeout,
        SqlServerCertificateHostName? certificateHostName = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(connectTimeout);
        Endpoint = endpoint;
        ConnectTimeout = connectTimeout;
        CertificateHostName = certificateHostName;
        AuthenticationMode = SqlServerAuthenticationMode.WindowsIntegrated;
        TransportSecurity = SqlServerTransportSecurity.EncryptAndValidateCertificate;
    }

    public SqlServerEndpoint Endpoint { get; }

    public SqlServerConnectTimeout ConnectTimeout { get; }

    public SqlServerCertificateHostName? CertificateHostName { get; }

    public SqlServerAuthenticationMode AuthenticationMode { get; }

    public SqlServerTransportSecurity TransportSecurity { get; }
}

/// <summary>Configuration accepted for a new target before asynchronous discovery.</summary>
public sealed class ObservationTargetRegistration
{
    public ObservationTargetRegistration(
        MonitoredInstanceId targetId,
        ObservationTargetKey key,
        ObservationTargetDisplayName displayName,
        SqlServerConnectionPolicy connectionPolicy)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(connectionPolicy);
        TargetId = targetId;
        Key = key;
        DisplayName = displayName;
        ConnectionPolicy = connectionPolicy;
    }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetKey Key { get; }

    public ObservationTargetDisplayName DisplayName { get; }

    public SqlServerConnectionPolicy ConnectionPolicy { get; }
}

/// <summary>A repository-clock target snapshot protected by an optimistic revision.</summary>
public sealed class ObservationTarget
{
    public ObservationTarget(
        MonitoredInstanceId targetId,
        ObservationTargetKey key,
        ObservationTargetDisplayName displayName,
        SqlServerConnectionPolicy connectionPolicy,
        ObservationTargetLifecycle lifecycle,
        ObservationTargetRevision revision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset discoveryRequestedAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? retiredAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(connectionPolicy);
        ArgumentNullException.ThrowIfNull(revision);

        if (!Enum.IsDefined(lifecycle))
        {
            throw new ArgumentOutOfRangeException(nameof(lifecycle));
        }

        createdAtUtc = DomainValidation.RequireUtcMicrosecondAligned(createdAtUtc, nameof(createdAtUtc));
        discoveryRequestedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(
            discoveryRequestedAtUtc,
            nameof(discoveryRequestedAtUtc));
        updatedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(updatedAtUtc, nameof(updatedAtUtc));

        if (discoveryRequestedAtUtc < createdAtUtc || discoveryRequestedAtUtc > updatedAtUtc)
        {
            throw new ArgumentException(
                "The discovery-request timestamp must fall within the target lifetime.",
                nameof(discoveryRequestedAtUtc));
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("The update timestamp cannot precede target creation.", nameof(updatedAtUtc));
        }

        if (retiredAtUtc is not null)
        {
            retiredAtUtc = DomainValidation.RequireUtcMicrosecondAligned(retiredAtUtc.Value, nameof(retiredAtUtc));
        }

        if ((lifecycle == ObservationTargetLifecycle.Retired) != (retiredAtUtc is not null))
        {
            throw new ArgumentException("A retired target must have, and only a retired target may have, a retirement timestamp.");
        }

        if (retiredAtUtc < createdAtUtc || retiredAtUtc > updatedAtUtc)
        {
            throw new ArgumentException("The retirement timestamp must fall within the target lifetime.", nameof(retiredAtUtc));
        }

        TargetId = targetId;
        Key = key;
        DisplayName = displayName;
        ConnectionPolicy = connectionPolicy;
        Lifecycle = lifecycle;
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
        DiscoveryRequestedAtUtc = discoveryRequestedAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        RetiredAtUtc = retiredAtUtc;
    }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetKey Key { get; }

    public ObservationTargetDisplayName DisplayName { get; }

    public SqlServerConnectionPolicy ConnectionPolicy { get; }

    public ObservationTargetLifecycle Lifecycle { get; }

    public ObservationTargetRevision Revision { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset DiscoveryRequestedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public DateTimeOffset? RetiredAtUtc { get; }
}
