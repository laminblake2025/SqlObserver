using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class CapabilityProfileContractTests
{
    [Fact]
    public void SupportedProfileRequiresKerberosValidatedTlsAndNonSysadmin()
    {
        CapabilityProfile profile = CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false);

        Assert.True(profile.IsUsable);
        Assert.Equal("capability.connection", profile.CollectorId.Value);
        Assert.Equal(1, profile.CollectorManifestVersion);
        Assert.Equal(1, profile.OutputSchemaVersion);
        Assert.False(profile.IsSysAdmin);
        Assert.Throws<ArgumentException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Ntlm,
            transportEncrypted: true,
            isSysAdmin: false));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: false,
            isSysAdmin: false));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: true));
    }

    [Fact]
    public void NtlmIsVisibleOnlyAsExplicitDegradedFallback()
    {
        CapabilityProfile profile = CreateProfile(
            CapabilityDiscoveryOutcome.Degraded,
            CapabilityDiscoveryReason.AuthenticationSchemeFallback,
            SqlServerAuthenticationScheme.Ntlm,
            transportEncrypted: true,
            isSysAdmin: false);

        Assert.True(profile.IsUsable);
        Assert.Equal(SqlServerAuthenticationScheme.Ntlm, profile.AuthenticationScheme);
        Assert.Throws<ArgumentException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Degraded,
            CapabilityDiscoveryReason.OptionalCapabilityUnavailable,
            SqlServerAuthenticationScheme.Ntlm,
            transportEncrypted: true,
            isSysAdmin: false));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Degraded,
            CapabilityDiscoveryReason.AuthenticationSchemeFallback,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false));
    }

    [Theory]
    [InlineData(CapabilityDiscoveryReason.ExcessivePrivilege, true, true, SqlServerAuthenticationScheme.Kerberos)]
    [InlineData(CapabilityDiscoveryReason.TransportNotEncrypted, false, false, SqlServerAuthenticationScheme.Kerberos)]
    [InlineData(CapabilityDiscoveryReason.AuthenticationSchemeMismatch, false, true, SqlServerAuthenticationScheme.SqlAuthentication)]
    public void UnsafeConnectionEvidenceIsSecurityPolicyRejected(
        CapabilityDiscoveryReason reason,
        bool isSysAdmin,
        bool transportEncrypted,
        SqlServerAuthenticationScheme authenticationScheme)
    {
        CapabilityProfile profile = CreateProfile(
            CapabilityDiscoveryOutcome.SecurityPolicyRejected,
            reason,
            authenticationScheme,
            transportEncrypted,
            isSysAdmin);

        Assert.False(profile.IsUsable);
        Assert.Equal(reason, profile.Reason);
    }

    [Fact]
    public void ConnectionFailureDoesNotInventServerOrSecurityEvidence()
    {
        CapabilityProfile profile = CreateProfile(
            CapabilityDiscoveryOutcome.Unreachable,
            CapabilityDiscoveryReason.NetworkUnreachable,
            SqlServerAuthenticationScheme.Unknown,
            transportEncrypted: false,
            isSysAdmin: false,
            includeIdentity: false);

        Assert.Null(profile.ServerIdentity);
        Assert.False(profile.IsUsable);
        Assert.Throws<ArgumentException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Unreachable,
            CapabilityDiscoveryReason.NetworkUnreachable,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false,
            includeIdentity: false));
    }

    [Fact]
    public void EvidenceIsBoundedUniqueAndDefensivelyCopied()
    {
        var connection = new CapabilityEvidence(
            new CapabilityId("connection"),
            CapabilityAvailability.Available,
            CapabilityEvidenceReason.Verified);
        var permission = new PermissionEvidence(
            new SqlServerPermissionId("server.connect"),
            PermissionEvidenceScope.Server,
            PermissionEvidenceOutcome.Granted);
        CapabilityEvidence[] capabilities = [connection];
        PermissionEvidence[] permissions = [permission];
        CapabilityProfile profile = CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false,
            capabilities,
            permissions);
        capabilities[0] = new CapabilityEvidence(
            new CapabilityId("changed"),
            CapabilityAvailability.Available,
            CapabilityEvidenceReason.Verified);
        permissions[0] = new PermissionEvidence(
            new SqlServerPermissionId("changed"),
            PermissionEvidenceScope.Server,
            PermissionEvidenceOutcome.Denied);

        Assert.Same(connection, profile.Capabilities[0]);
        Assert.Same(permission, profile.Permissions[0]);
        Assert.Throws<ArgumentException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            true,
            false,
            [connection, connection],
            [permission]));
        Assert.Throws<ArgumentException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            true,
            false,
            [connection],
            [permission, permission]));
    }

    [Fact]
    public void ProfileEnforcesPersistencePrecisionAndAttemptBounds()
    {
        Assert.Throws<ArgumentException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            true,
            false,
            checkedAtUtc: UtcNow.AddTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            true,
            false,
            evidenceBytes: CapabilityProfile.MaximumEvidenceBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateProfile(
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            true,
            false,
            discoveryDuration: CapabilityProfile.MaximumDiscoveryDuration + TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityProfileRefreshInterval(TimeSpan.Zero));
    }

    private static readonly DateTimeOffset UtcNow =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static CapabilityProfile CreateProfile(
        CapabilityDiscoveryOutcome outcome,
        CapabilityDiscoveryReason reason,
        SqlServerAuthenticationScheme authenticationScheme,
        bool transportEncrypted,
        bool isSysAdmin,
        IReadOnlyList<CapabilityEvidence>? capabilities = null,
        IReadOnlyList<PermissionEvidence>? permissions = null,
        bool includeIdentity = true,
        TimeSpan? discoveryDuration = null,
        int evidenceBytes = 256,
        DateTimeOffset? checkedAtUtc = null) =>
        new(
            new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            new ObservationTargetRevision(1),
            new CollectorId("capability.connection"),
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            includeIdentity
                ? new SqlServerIdentity(
                    new SqlServerVersion(16, 0, 1000, 6),
                    new SqlServerEditionName("Enterprise Edition"),
                    SqlServerEngineEdition.Enterprise,
                    SqlServerPlatform.Windows)
                : null,
            outcome,
            reason,
            authenticationScheme,
            transportEncrypted,
            isSysAdmin,
            capabilities ??
            [
                new CapabilityEvidence(
                    new CapabilityId("connection"),
                    CapabilityAvailability.Available,
                    CapabilityEvidenceReason.Verified),
            ],
            permissions ??
            [
                new PermissionEvidence(
                    new SqlServerPermissionId("server.connect"),
                    PermissionEvidenceScope.Server,
                    PermissionEvidenceOutcome.Granted),
            ],
            discoveryDuration ?? TimeSpan.FromMilliseconds(20),
            evidenceBytes,
            checkedAtUtc ?? UtcNow,
            (checkedAtUtc ?? UtcNow).AddHours(1));
}
