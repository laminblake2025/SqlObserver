using System.Reflection;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class ObservationTargetContractTests
{
    [Fact]
    public void TargetIdentityAndDisplayNameEnforceRepositoryBounds()
    {
        string maximumKey = "a" + new string('b', ObservationTargetKey.MaximumLength - 1);
        var key = new ObservationTargetKey(maximumKey);
        var display = new ObservationTargetDisplayName(new string('é', 256));

        Assert.Equal(maximumKey, key.Value);
        Assert.Equal(512, System.Text.Encoding.UTF8.GetByteCount(display.Value));
        Assert.Throws<ArgumentException>(() => new ObservationTargetKey("A-not-normalized"));
        Assert.Throws<ArgumentException>(() => new ObservationTargetKey("target;Encrypt=false"));
        Assert.Throws<ArgumentException>(() => new ObservationTargetKey(maximumKey + "x"));
        Assert.Throws<ArgumentException>(() => new ObservationTargetDisplayName(new string('é', 257)));
        Assert.Throws<ArgumentException>(() => new ObservationTargetDisplayName("unsafe\rname"));
    }

    [Fact]
    public void EndpointRequiresExactlyOneStructuredAddressSelector()
    {
        var host = new SqlServerHostName("sql01.example.test");
        var named = new SqlServerEndpoint(host, new SqlServerInstanceName("REPORTING"));
        var tcp = new SqlServerEndpoint(host, tcpPort: 1433);

        Assert.Equal("REPORTING", named.InstanceName?.Value);
        Assert.Equal(1433, tcp.TcpPort);
        Assert.Throws<ArgumentException>(() => new SqlServerEndpoint(host));
        Assert.Throws<ArgumentException>(() => new SqlServerEndpoint(
            host,
            new SqlServerInstanceName("REPORTING"),
            1433));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlServerEndpoint(host, tcpPort: 0));
        Assert.Throws<ArgumentException>(() => new SqlServerHostName("sql01;Password=p"));
        Assert.Throws<ArgumentException>(() => new SqlServerCertificateHostName("*.example.test"));
    }

    [Fact]
    public void ConnectionPolicyCanOnlyExpressIntegratedValidatedTls()
    {
        var policy = new SqlServerConnectionPolicy(
            new SqlServerEndpoint(new SqlServerHostName("sql01.example.test"), tcpPort: 1433),
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(10)),
            new SqlServerCertificateHostName("sql01.example.test"));

        Assert.Equal(SqlServerAuthenticationMode.WindowsIntegrated, policy.AuthenticationMode);
        Assert.Equal(
            SqlServerTransportSecurity.EncryptAndValidateCertificate,
            policy.TransportSecurity);
        Assert.Equal("sql01.example.test", policy.CertificateHostName?.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlServerConnectTimeout(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlServerConnectTimeout(TimeSpan.FromSeconds(31)));

        PropertyInfo[] properties = typeof(SqlServerConnectionPolicy).GetProperties();
        Assert.DoesNotContain(properties, static property =>
            property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TargetSnapshotEnforcesRevisionLifecycleAndRepositoryTime()
    {
        DateTimeOffset created = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        ObservationTarget active = CreateTarget(
            ObservationTargetLifecycle.Active,
            created,
            created.AddSeconds(1),
            created.AddSeconds(2));
        ObservationTarget retired = CreateTarget(
            ObservationTargetLifecycle.Retired,
            created,
            created.AddSeconds(1),
            created.AddSeconds(2),
            created.AddSeconds(2));

        Assert.Equal(1, active.Revision.Value);
        Assert.Equal(created.AddSeconds(1), active.DiscoveryRequestedAtUtc);
        Assert.NotNull(retired.RetiredAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObservationTargetRevision(0));
        Assert.Throws<ArgumentException>(() => CreateTarget(
            ObservationTargetLifecycle.Active,
            created,
            created.AddTicks(1),
            created.AddSeconds(2)));
        Assert.Throws<ArgumentException>(() => CreateTarget(
            ObservationTargetLifecycle.Active,
            created,
            created.AddSeconds(3),
            created.AddSeconds(2)));
        Assert.Throws<ArgumentException>(() => CreateTarget(
            ObservationTargetLifecycle.Retired,
            created,
            created,
            created));
    }

    [Fact]
    public void AuthorizationScopeIsDefensiveAndDenyByDefault()
    {
        var first = new MonitoredInstanceId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var second = new MonitoredInstanceId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        MonitoredInstanceId[] source = [first];
        TargetAuthorizationScope scope = TargetAuthorizationScope.ForTargets(source);
        source[0] = second;
        var disabled = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-100"),
            AuthorizationPrincipalState.Disabled,
            [ApplicationRole.TargetAdministrator],
            TargetAuthorizationScope.ForAllTargets());

        Assert.True(scope.Contains(first));
        Assert.False(scope.Contains(second));
        Assert.False(TargetAuthorizationScope.None().Contains(first));
        Assert.False(disabled.HasRole(ApplicationRole.TargetAdministrator));
        Assert.False(disabled.CanAccess(first));
        Assert.Throws<ArgumentException>(() => TargetAuthorizationScope.ForTargets([first, first]));
        Assert.Throws<ArgumentException>(() => new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-100"),
            AuthorizationPrincipalState.Active,
            [ApplicationRole.Viewer, ApplicationRole.Viewer],
            scope));
        Assert.Throws<ArgumentException>(() => new ActorSecurityIdentifier("DOMAIN\\operator"));
    }

    private static ObservationTarget CreateTarget(
        ObservationTargetLifecycle lifecycle,
        DateTimeOffset createdAtUtc,
        DateTimeOffset discoveryRequestedAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? retiredAtUtc = null) =>
        new(
            new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            new ObservationTargetKey("target-a"),
            new ObservationTargetDisplayName("Target A"),
            new SqlServerConnectionPolicy(
                new SqlServerEndpoint(new SqlServerHostName("sql01.example.test"), tcpPort: 1433),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))),
            lifecycle,
            new ObservationTargetRevision(1),
            createdAtUtc,
            discoveryRequestedAtUtc,
            updatedAtUtc,
            retiredAtUtc);
}
