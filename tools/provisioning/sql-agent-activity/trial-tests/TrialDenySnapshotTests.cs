namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Pure synthetic inventory checks. These do not establish effective native SQL denial.</summary>
public sealed class TrialDenySnapshotTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void DeniedImageChangesOnlyOwnedSourceGrantStateAndPreservesSignedMetadata(bool missingMapping)
    {
        var (baseline,signed)=TrialDenyFixture.Create(missingMapping);
        var denied=AgentActivityTrialDenySql.ExpectedDenied(baseline,signed);
        AgentActivityTrialDenySql.ValidateDenied(baseline,signed,denied);
        var changed=Assert.Single(denied.PackagePermissions.Except(signed.PackagePermissions));
        var original=Assert.Single(signed.PackagePermissions.Except(denied.PackagePermissions));
        Assert.Equal("msdb",changed.Scope);Assert.Equal("SELECT",changed.Name);Assert.Equal("D",changed.State);
        Assert.Equal(original with {State="D"},changed);Assert.Equal(1,changed.GrantorId);
        Assert.Equal(signed.Facts,denied.Facts);Assert.Equal(signed.Objects,denied.Objects);Assert.Equal(signed.MonitorPermissions,denied.MonitorPermissions);
        Assert.Single(denied.Facts,row=>row.Kind=="signature");Assert.Contains(denied.Facts,row=>row.Kind=="signature_validity" && row.Value=="1:1");
        Assert.Equal(2,denied.PackagePermissions.Count(row=>row.Name=="VIEW DEFINITION"));
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateInstalled(baseline,denied,TrialInstallationPolicy.MetadataVerificationV2));
    }

    [Theory]
    [InlineData("recipient")][InlineData("grantor")][InlineData("class")][InlineData("source")]
    [InlineData("column")][InlineData("permission")][InlineData("state")][InlineData("scope")]
    [InlineData("deleted")][InlineData("extra")][InlineData("duplicate")][InlineData("signature")]
    [InlineData("definition")][InlineData("identity")][InlineData("monitor")][InlineData("object")]
    public void DeniedPostimageRefusesEveryUnrelatedInventoryDelta(string mutation)
    {
        var (baseline,signed)=TrialDenyFixture.Create();var denied=AgentActivityTrialDenySql.ExpectedDenied(baseline,signed);
        denied=TrialDenyFixture.Drift(denied,mutation);
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialDenySql.ValidateDenied(baseline,signed,denied));
    }

    [Theory]
    [InlineData("W")][InlineData("D")][InlineData("R")]
    public void ExistingNoncanonicalSourcePermissionCannotBeAdoptedAsAnOwnedGrant(string state)
    {
        var (baseline,signed)=TrialDenyFixture.Create();
        signed=signed with {PackagePermissions=TrialFixture.Order(signed.PackagePermissions.Select(row=>row.Scope=="msdb" && row.Class==1 && row.MajorId==100?row with {State=state}:row))};
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialDenySql.ExpectedDenied(baseline,signed));
    }

    [Fact]
    public void AForeignSourceGrantorIsRefusedRatherThanImpersonated()
    {
        var (baseline,signed)=TrialDenyFixture.Create();
        signed=signed with {PackagePermissions=TrialFixture.Order(signed.PackagePermissions.Select(row=>row.Scope=="msdb" && row.Class==1 && row.MajorId==100?row with {GrantorId=99}:row))};
        AgentActivityTrialSql.ValidateInstalled(baseline,signed,TrialInstallationPolicy.MetadataVerificationV2);
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialDenySql.ExpectedDenied(baseline,signed));
    }
}

internal static class TrialDenyFixture
{
    internal static (TrialBaseline Baseline,TrialSnapshot Signed) Create(bool missingMapping=false)
    {
        var (baseline,signed)=TrialFixture.Create(missingMapping);
        int owner=signed.Objects.Single(row=>row.Kind=="owner_user").Id;
        int module=signed.Objects.Single(row=>row.Kind=="module").Id;
        int certificate=signed.Objects.Single(row=>row.Kind=="master_certificate").Id;
        TrialGrant[] additions=[new("master",signed.Monitor.MasterUserId,owner,1,module,0,"VIEW DEFINITION","G"),
            new("master",signed.Monitor.MasterUserId,1,25,certificate,0,"VIEW DEFINITION","G")];
        TrialGrant Grantor(TrialGrant row)=>row.Scope=="master" && row.Class==1 && row.MajorId==module && row.Name=="EXECUTE" ? row with {GrantorId=owner}:row;
        signed=signed with {PackagePermissions=TrialFixture.Order(signed.PackagePermissions.Select(Grantor).Concat(additions)),
            MonitorPermissions=TrialFixture.Order(signed.MonitorPermissions.Select(Grantor).Concat(additions))};
        AgentActivityTrialSql.ValidateInstalled(baseline,signed,TrialInstallationPolicy.MetadataVerificationV2);
        return(baseline,signed);
    }
    internal static TrialReceipt Receipt(string phase,bool deniedImage=true)
    {
        var (baseline,signed)=Create();
        bool deny=deniedImage && phase is ("deny_pending" or "denied" or "deny_restore_pending" or "deny_restored" or "remove_pending" or "removed");
        return new(4,baseline.DeploymentId,baseline.MonitorPrincipal,phase,baseline,phase=="prepared"?null:signed,null,
            InstallationPolicyVersion:2,DeniedSnapshot:deny?AgentActivityTrialDenySql.ExpectedDenied(baseline,signed):null);
    }
    internal static TrialSnapshot Drift(TrialSnapshot denied,string mutation)
    {
        var target=denied.PackagePermissions.Single(row=>row.Scope=="msdb" && row.Class==1 && row.MajorId==100);
        TrialGrant changed=mutation switch{
            "recipient"=>target with {GranteeId=999},"grantor"=>target with {GrantorId=999},"class"=>target with {Class=0},
            "source"=>target with {MajorId=101},"column"=>target with {MinorId=1},"permission"=>target with {Name="UPDATE"},
            "state"=>target with {State="G"},"scope"=>target with {Scope="master"},_=>target};
        if(mutation is "recipient" or "grantor" or "class" or "source" or "column" or "permission" or "state" or "scope")
            return denied with {PackagePermissions=TrialFixture.Order(denied.PackagePermissions.Select(row=>row==target?changed:row))};
        return mutation switch{
            "deleted"=>denied with {PackagePermissions=denied.PackagePermissions.Where(row=>row!=target).ToArray()},
            "extra"=>denied with {PackagePermissions=TrialFixture.Order(denied.PackagePermissions.Append(target with {Name="UPDATE"}))},
            "duplicate"=>denied with {PackagePermissions=TrialFixture.Order(denied.PackagePermissions.Append(target))},
            "signature"=>denied with {Facts=denied.Facts.Where(row=>row.Kind!="signature").ToArray()},
            "definition"=>denied with {Facts=TrialFixture.Order(denied.Facts.Select(row=>row.Kind=="definition" && row.Key=="sha256"?row with {Value=new string('b',64)}:row))},
            "identity"=>denied with {Identity=denied.Identity with {MsdbDatabaseGuid=Guid.NewGuid()}},
            "monitor"=>denied with {MonitorPermissions=TrialFixture.Order(denied.MonitorPermissions.Append(new("msdb",denied.Monitor.MasterUserId,1,1,100,0,"SELECT","D")))},
            "object"=>denied with {Objects=TrialFixture.Order(denied.Objects.Select(row=>row.Kind=="certificate_user"?row with {Id=999}:row))},
            _=>throw new ArgumentOutOfRangeException(nameof(mutation))};
    }
}
