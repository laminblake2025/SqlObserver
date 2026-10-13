using SqlObserver.Domain.Capabilities;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class SqlServer2014DiscoveryTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(15)]
    public void LegacyStatePermissionVersionsCanBeDiscovered(int major)
    {
        var result = SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(major, 2,
            SqlServerPlatform.Windows, SqlServerAuthenticationScheme.Kerberos, true, false, true, false);
        Assert.Equal(CapabilityDiscoveryOutcome.Supported, result.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.Verified, result.Reason);
        var denied = SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(major, 2,
            SqlServerPlatform.Windows, SqlServerAuthenticationScheme.Kerberos, true, false, false, false);
        Assert.Equal(CapabilityDiscoveryReason.RequiredPermissionMissing, denied.Reason);
        var sysadmin = SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(major, 2,
            SqlServerPlatform.Windows, SqlServerAuthenticationScheme.Kerberos, true, true, true, false);
        Assert.Equal(CapabilityDiscoveryReason.ExcessivePrivilege, sysadmin.Reason);
        var unencrypted = SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(major, 2,
            SqlServerPlatform.Windows, SqlServerAuthenticationScheme.Kerberos, false, false, true, false);
        Assert.Equal(CapabilityDiscoveryReason.TransportNotEncrypted, unencrypted.Reason);
        var linux = SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(major, 2,
            SqlServerPlatform.Linux, SqlServerAuthenticationScheme.Kerberos, true, false, true, false);
        Assert.Equal(CapabilityDiscoveryReason.UnsupportedPlatform, linux.Reason);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(18)]
    public void VersionsWithoutExactCoverageRemainUnsupported(int major)
    {
        var result = SqlServerCapabilityDiscoveryPort.ClassifyConnectedEvidence(major, 2,
            SqlServerPlatform.Windows, SqlServerAuthenticationScheme.Kerberos, true, false, true, false);
        Assert.Equal(CapabilityDiscoveryOutcome.Unsupported, result.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.UnsupportedVersion, result.Reason);
    }
}
