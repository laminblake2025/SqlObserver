using System.Text.Json;

namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Synthetic policy inventories only. These tests do not qualify native metadata visibility.</summary>
public sealed class TrialMetadataPolicyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MetadataPolicyAddsOnlyTwoExactGrantsAndLegacyStillRefusesThem(bool temporaryMonitor)
    {
        var (baseline, legacy) = TrialFixture.Create(temporaryMonitor);
        var installed = MetadataImage(legacy);
        AgentActivityTrialSql.ValidateInstalled(baseline, legacy);
        AgentActivityTrialSql.ValidateInstalled(baseline, installed, TrialInstallationPolicy.MetadataVerificationV2);
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, installed));
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, legacy, TrialInstallationPolicy.MetadataVerificationV2));
        Assert.Equal(legacy.Objects, installed.Objects);
        Assert.Equal(legacy.Facts, installed.Facts);
        Assert.Equal(legacy.PackagePermissions.Count + 2, installed.PackagePermissions.Count);
        Assert.Equal(legacy.MonitorPermissions.Count + 2, installed.MonitorPermissions.Count);
        AgentActivityTrialSql.ValidateBaselineRestored(baseline, baseline);
    }

    [Theory]
    [InlineData(1)] [InlineData(25)]
    public void EitherMissingMetadataGrantRefusesTheNewPolicy(int grantClass)
    {
        var (baseline, legacy) = TrialFixture.Create();
        var installed = MetadataImage(legacy);
        installed = installed with {
            PackagePermissions = installed.PackagePermissions.Where(x => x.Name != "VIEW DEFINITION" || x.Class != grantClass).ToArray(),
            MonitorPermissions = installed.MonitorPermissions.Where(x => x.Name != "VIEW DEFINITION" || x.Class != grantClass).ToArray()
        };
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, installed, TrialInstallationPolicy.MetadataVerificationV2));
    }

    [Fact]
    public void OriginalExecuteGrantMustStillBeVisibleInBothInventories()
    {
        var (baseline, legacy) = TrialFixture.Create();
        var installed = MetadataImage(legacy);
        installed = installed with { MonitorPermissions = installed.MonitorPermissions.Where(x => x.Name != "EXECUTE").ToArray() };
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, installed, TrialInstallationPolicy.MetadataVerificationV2));
    }

    [Theory]
    [InlineData("EXECUTE", 1)] [InlineData("EXECUTE", 999)]
    [InlineData("VIEW DEFINITION", 1)] [InlineData("VIEW DEFINITION", 999)]
    public void ModuleGrantsRequireTheExactRecordedOwnerRatherThanDboOrAnArbitraryGrantor(string permission, int wrongGrantor)
    {
        var (baseline, legacy) = TrialFixture.Create();
        var installed = MetadataImage(legacy);
        int owner = installed.Objects.Single(x => x.Kind == "owner_user").Id;
        Assert.Equal(owner, Assert.Single(installed.PackagePermissions, x => x.Class == 1 && x.Scope == "master" && x.Name == permission).GrantorId);
        TrialGrant Change(TrialGrant grant) => grant.Scope == "master" && grant.Class == 1 && grant.Name == permission
            ? grant with { GrantorId = wrongGrantor } : grant;
        var changed = installed with {
            PackagePermissions = TrialFixture.Order(installed.PackagePermissions.Select(Change)),
            MonitorPermissions = TrialFixture.Order(installed.MonitorPermissions.Select(Change))
        };
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, changed, TrialInstallationPolicy.MetadataVerificationV2));
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateUnsigned(baseline, installed, WithoutSignature(changed), TrialInstallationPolicy.MetadataVerificationV2));
    }

    [Fact]
    public void CertificateGrantCannotBorrowTheModuleOwnersGrantorIdentity()
    {
        var (baseline, legacy) = TrialFixture.Create();
        var installed = MetadataImage(legacy);
        int owner = installed.Objects.Single(x => x.Kind == "owner_user").Id;
        TrialGrant Change(TrialGrant grant) => grant.Class == 25 ? grant with { GrantorId = owner } : grant;
        installed = installed with {
            PackagePermissions = TrialFixture.Order(installed.PackagePermissions.Select(Change)),
            MonitorPermissions = TrialFixture.Order(installed.MonitorPermissions.Select(Change))
        };
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, installed, TrialInstallationPolicy.MetadataVerificationV2));
    }

    [Fact]
    public void TemporaryMappingStillAllowsOnlyItsCapturedImplicitConnectVariant()
    {
        var (baseline, legacy) = TrialFixture.Create(missingMasterMapping: true);
        var installed = MetadataImage(legacy);
        bool IsMonitorConnect(TrialGrant grant) => grant.Scope == "master" && grant.Name == "CONNECT";
        var withoutImplicitConnect = installed with {
            PackagePermissions = installed.PackagePermissions.Where(x => !IsMonitorConnect(x)).ToArray(),
            MonitorPermissions = installed.MonitorPermissions.Where(x => !IsMonitorConnect(x)).ToArray()
        };
        AgentActivityTrialSql.ValidateInstalled(baseline, withoutImplicitConnect, TrialInstallationPolicy.MetadataVerificationV2);
        var widened = installed with {
            PackagePermissions = TrialFixture.Order(installed.PackagePermissions.Select(x => IsMonitorConnect(x) ? x with { State = "W" } : x)),
            MonitorPermissions = TrialFixture.Order(installed.MonitorPermissions.Select(x => IsMonitorConnect(x) ? x with { State = "W" } : x))
        };
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, widened, TrialInstallationPolicy.MetadataVerificationV2));
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateExactSnapshot(installed, withoutImplicitConnect));
    }

    [Theory]
    [InlineData("scope")] [InlineData("grantee")] [InlineData("grantor")] [InlineData("class")]
    [InlineData("object")] [InlineData("minor")] [InlineData("permission")] [InlineData("grant-option")]
    [InlineData("deny")] [InlineData("duplicate")] [InlineData("monitor-missing")] [InlineData("broad-definition")]
    public void MetadataGrantDriftCannotBeHiddenByOriginalPermissionSubtraction(string change)
    {
        var (baseline, legacy) = TrialFixture.Create();
        var installed = MetadataImage(legacy);
        var grant = installed.PackagePermissions.Single(x => x.Name == "VIEW DEFINITION" && x.Class == 25);
        var changed = change switch {
            "scope" => grant with { Scope = "msdb" },
            "grantee" => grant with { GranteeId = grant.GranteeId + 1 },
            "grantor" => grant with { GrantorId = 2 },
            "class" => grant with { Class = 1 },
            "object" => grant with { MajorId = grant.MajorId + 1 },
            "minor" => grant with { MinorId = 1 },
            "permission" => grant with { Name = "CONTROL" },
            "grant-option" => grant with { State = "W" },
            "deny" => grant with { State = "D" },
            "broad-definition" => grant with { Class = 0, MajorId = 0 },
            _ => grant
        };
        installed = change switch {
            "duplicate" => installed with { PackagePermissions = TrialFixture.Order(installed.PackagePermissions.Append(grant)) },
            "monitor-missing" => installed with { MonitorPermissions = installed.MonitorPermissions.Where(x => x != grant).ToArray() },
            _ => installed with {
                PackagePermissions = TrialFixture.Order(installed.PackagePermissions.Select(x => x == grant ? changed : x)),
                MonitorPermissions = TrialFixture.Order(installed.MonitorPermissions.Select(x => x == grant ? changed : x))
            }
        };
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, installed, TrialInstallationPolicy.MetadataVerificationV2));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnsignedTransitionPreservesTheSelectedPolicyAndCannotReclassifyIt(bool temporaryMonitor)
    {
        var (baseline, legacy) = TrialFixture.Create(temporaryMonitor);
        var signed = MetadataImage(legacy);
        var unsignedSnapshot = WithoutSignature(signed);
        AgentActivityTrialSql.ValidateUnsigned(baseline, signed, unsignedSnapshot, TrialInstallationPolicy.MetadataVerificationV2);
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateUnsigned(baseline, signed, unsignedSnapshot));
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateUnsigned(baseline, legacy, WithoutSignature(legacy), TrialInstallationPolicy.MetadataVerificationV2));
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateUnsigned(baseline, signed, WithoutSignature(legacy), TrialInstallationPolicy.MetadataVerificationV2));
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateUnsigned(baseline, unsignedSnapshot, unsignedSnapshot, TrialInstallationPolicy.MetadataVerificationV2));
    }

    [Theory]
    [InlineData("definition")] [InlineData("owner")] [InlineData("identity")]
    [InlineData("signature")] [InlineData("monitor-marker")]
    public void NewPolicyDoesNotLoosenExistingOwnershipOrProvenanceChecks(string change)
    {
        var (baseline, legacy) = TrialFixture.Create(missingMasterMapping: true);
        var installed = MetadataImage(legacy);
        installed = change switch {
            "definition" => installed with { Facts = TrialFixture.Order(installed.Facts.Select(x => x.Kind == "definition" && x.Key == "sha256" ? x with { Value = new string('b',64) } : x)) },
            "owner" => installed with { Objects = TrialFixture.Order(installed.Objects.Select(x => x.Kind == "module" ? x with { OwnerId = 1 } : x)) },
            "identity" => installed with { Monitor = installed.Monitor with { SidHex = "05060708" } },
            "signature" => WithoutSignature(installed),
            _ => installed with { Facts = installed.Facts.Where(x => x.Kind != "monitor_marker").ToArray() }
        };
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, installed, TrialInstallationPolicy.MetadataVerificationV2));
    }

    [Fact]
    public void PreexistingMonitorGrantsRemainExactAndCannotBeSubtractedAsPackageMetadata()
    {
        var (baseline, legacy) = TrialFixture.Create();
        var original = new TrialGrant("master", legacy.Monitor.MasterUserId, 1, 1, 999, 0, "VIEW DEFINITION", "G");
        baseline = new(baseline.Snapshot with { MonitorPermissions = [original] });
        var installed = MetadataImage(legacy) with {
            MonitorPermissions = TrialFixture.Order(MetadataImage(legacy).MonitorPermissions.Append(original))
        };
        AgentActivityTrialSql.ValidateInstalled(baseline, installed, TrialInstallationPolicy.MetadataVerificationV2);
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline,
            installed with { MonitorPermissions = installed.MonitorPermissions.Where(x => x != original).ToArray() }, TrialInstallationPolicy.MetadataVerificationV2));
        var changedBaseline = new TrialBaseline(baseline.Snapshot with { MonitorPermissions = [] });
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateBaselineRestored(baseline, changedBaseline));
        Assert.Throws<TrialObjectsPresentException>(() => AgentActivityTrialSql.ValidateBaselineRestored(baseline, new(installed)));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(3)] [InlineData(int.MaxValue)]
    public async Task UnknownPolicyIsRejectedBeforeAnySqlExecution(int value)
    {
        var (baseline, signed) = TrialFixture.Create();
        var invalid = (TrialInstallationPolicy)value;
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, signed, invalid));
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateUnsigned(baseline, signed, WithoutSignature(signed), invalid));
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentActivityTrialSql.CaptureInstalledAsync(null!, null!, baseline, CancellationToken.None, invalid));
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentActivityTrialSql.RemoveAsync(null!, null!, baseline, signed, CancellationToken.None, invalid));
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentActivityTrialSql.RemoveSignatureAsync(null!, null!, baseline, signed, CancellationToken.None, invalid));
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentActivityTrialSql.CaptureUnsignedAsync(null!, null!, baseline, signed, CancellationToken.None, invalid));
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentActivityTrialSql.RemoveUnsignedAsync(null!, null!, baseline, signed, WithoutSignature(signed), CancellationToken.None, invalid));
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentActivityTrialSql.InstallAsync(null!, null!, baseline, [], CancellationToken.None, invalid));
    }

    [Fact]
    public void VersionedPolicyManifestPinsExactModuleAndTwoAdditionalMetadataGrants()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SqlObserver.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools/provisioning/sql-agent-activity/trial/installation-policy.v2.json")));
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("installationPolicyVersion").GetInt32());
        Assert.Equal(3, root.GetProperty("requiredJournalSchemaVersion").GetInt32());
        Assert.Equal(7, root.GetProperty("explicitGrantCount").GetInt32());
        Assert.Equal(AgentActivityTrialSql.ModuleUtf8Sha256, root.GetProperty("moduleUtf8Sha256").GetString());
        Assert.Equal(AgentActivityTrialSql.DefinitionUtf16Sha256, root.GetProperty("definitionUtf16Sha256").GetString());
        var grants = root.GetProperty("grants").EnumerateArray().ToArray();
        Assert.Equal(7, grants.Length);
        var metadata = grants.Where(x => x.GetProperty("permission").GetString() == "VIEW DEFINITION").ToArray();
        Assert.Equal(2, metadata.Length);
        Assert.All(metadata, x => {
            Assert.Equal("master", x.GetProperty("database").GetString());
            Assert.Equal("enrolled_monitor", x.GetProperty("recipient").GetString());
        });
        Assert.Equal(["CERTIFICATE::SqlObserverAgentActivityCertificateV1", "OBJECT::sqlobserver_agent.capture_v1"],
            metadata.Select(x => x.GetProperty("securable").GetString()).Order(StringComparer.Ordinal));
        Assert.All(grants.Where(x => x.GetProperty("securable").GetString() == "OBJECT::sqlobserver_agent.capture_v1"),
            x => Assert.Equal("SqlObserverAgentActivitySchemaOwnerV1", x.GetProperty("grantor").GetString()));
        Assert.Equal("dbo", Assert.Single(metadata, x => x.GetProperty("securable").GetString()!.StartsWith("CERTIFICATE::", StringComparison.Ordinal))
            .GetProperty("grantor").GetString());
    }

    private static TrialSnapshot MetadataImage(TrialSnapshot legacy)
    {
        int module = legacy.Objects.Single(x => x.Kind == "module").Id;
        int owner = legacy.Objects.Single(x => x.Kind == "owner_user").Id;
        TrialGrant[] grants = [
            new("master", legacy.Monitor.MasterUserId, owner, 1, module, 0, "VIEW DEFINITION", "G"),
            new("master", legacy.Monitor.MasterUserId, 1, 25, legacy.Objects.Single(x => x.Kind == "master_certificate").Id, 0, "VIEW DEFINITION", "G")
        ];
        TrialGrant ObjectGrantor(TrialGrant grant) => grant.Scope == "master" && grant.Class == 1 && grant.MajorId == module && grant.Name == "EXECUTE"
            ? grant with { GrantorId = owner } : grant;
        return legacy with {
            PackagePermissions = TrialFixture.Order(legacy.PackagePermissions.Select(ObjectGrantor).Concat(grants)),
            MonitorPermissions = TrialFixture.Order(legacy.MonitorPermissions.Select(ObjectGrantor).Concat(grants))
        };
    }

    private static TrialSnapshot WithoutSignature(TrialSnapshot signed) => signed with {
        Facts = TrialFixture.Order(signed.Facts.Where(x => x.Kind != "signature").Select(x =>
            x.Kind == "signature_validity" ? x with { Value = "0:0" } : x))
    };
}
