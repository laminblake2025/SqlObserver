namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Synthetic metadata tests only; no SQL signature or permission is changed by this suite.</summary>
public sealed class TrialSignatureSqlTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OnlyTheExactSingleSignatureRemovalIsAccepted(bool temporaryMonitor)
    {
        var (baseline,signed)=TrialFixture.Create(temporaryMonitor);
        var unsigned=Unsigned(signed);
        AgentActivityTrialSql.ValidateInstalled(baseline,signed);
        AgentActivityTrialSql.ValidateUnsigned(baseline,signed,unsigned);
        Assert.Equal(signed.Objects,unsigned.Objects);
        Assert.Equal(signed.PackagePermissions,unsigned.PackagePermissions);
        Assert.Equal(signed.MonitorPermissions,unsigned.MonitorPermissions);
        Assert.Equal(signed.Facts.Count-1,unsigned.Facts.Count);
        Assert.DoesNotContain(unsigned.Facts,x=>x.Kind=="signature");
        Assert.Equal("0:0",Assert.Single(unsigned.Facts,x=>x.Kind=="signature_validity").Value);
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateInstalled(baseline,unsigned));
    }

    [Theory]
    [InlineData("signed")] [InlineData("invalid")] [InlineData("missing-validity")]
    [InlineData("missing-module")] [InlineData("extra-signature")]
    public void OtherSignatureStatesAreNotTheAuthorizedUnsignedState(string mutation)
    {
        var (baseline,signed)=TrialFixture.Create();
        var unsigned=Unsigned(signed);
        unsigned=mutation switch {
            "signed"=>signed,
            "invalid"=>ChangeFact(unsigned,"signature_validity","module","1:0"),
            "missing-validity"=>unsigned with {Facts=unsigned.Facts.Where(x=>x.Kind!="signature_validity").ToArray()},
            "missing-module"=>unsigned with {Objects=unsigned.Objects.Where(x=>x.Kind!="module").ToArray()},
            _=>unsigned with {Facts=TrialFixture.Order(unsigned.Facts.Append(new("master","signature","999","SPVC:FOREIGN")))}
        };
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateUnsigned(baseline,signed,unsigned));
    }

    [Theory]
    [InlineData("server")] [InlineData("database")] [InlineData("deployment")] [InlineData("monitor-sid")]
    [InlineData("monitor-login-id")] [InlineData("monitor-user-id")] [InlineData("monitor-name")]
    [InlineData("object-id")] [InlineData("object-name")] [InlineData("object-database")]
    [InlineData("object-type")] [InlineData("owner")] [InlineData("extra-object")] [InlineData("missing-object")]
    [InlineData("grant-added")] [InlineData("grant-removed")] [InlineData("grantor")] [InlineData("grant-state")]
    [InlineData("grant-recipient")] [InlineData("monitor-grant")]
    public void UnsignedTransitionCannotAuthorizeAnyOtherIdentityObjectOrGrantChange(string mutation)
    {
        var (baseline,signed)=TrialFixture.Create(missingMasterMapping:true);
        var unsigned=Unsigned(signed);
        unsigned=mutation switch {
            "server"=>unsigned with {Identity=unsigned.Identity with {ServerName="different"}},
            "database"=>unsigned with {Identity=unsigned.Identity with {MasterDatabaseGuid=Guid.NewGuid()}},
            "deployment"=>unsigned with {DeploymentId=Guid.NewGuid()},
            "monitor-sid"=>unsigned with {Monitor=unsigned.Monitor with {SidHex="02030405"}},
            "monitor-login-id"=>unsigned with {Monitor=unsigned.Monitor with {LoginId=999}},
            "monitor-user-id"=>unsigned with {Monitor=unsigned.Monitor with {MasterUserId=999}},
            "monitor-name"=>unsigned with {Monitor=unsigned.Monitor with {Principal="TEST\\Different"}},
            "object-id"=>ChangeModule(unsigned,x=>x with {Id=999}),
            "object-name"=>ChangeModule(unsigned,x=>x with {Name="different"}),
            "object-database"=>ChangeModule(unsigned,x=>x with {Database="msdb"}),
            "object-type"=>ChangeModule(unsigned,x=>x with {Type="V"}),
            "owner"=>ChangeModule(unsigned,x=>x with {OwnerId=1}),
            "extra-object"=>unsigned with {Objects=TrialFixture.Order(unsigned.Objects.Append(new("extra","master","extra",999,null,"P","")))},
            "missing-object"=>unsigned with {Objects=unsigned.Objects.Where(x=>x.Kind!="owner_user").ToArray()},
            "grant-added"=>unsigned with {PackagePermissions=TrialFixture.Order(unsigned.PackagePermissions.Append(new("server",64,1,100,0,0,"CONTROL SERVER","G")))},
            "grant-removed"=>unsigned with {PackagePermissions=unsigned.PackagePermissions.Where(x=>x.Name!="EXECUTE").ToArray()},
            "grantor"=>unsigned with {PackagePermissions=TrialFixture.Order(unsigned.PackagePermissions.Select(x=>x with {GrantorId=2}))},
            "grant-state"=>unsigned with {PackagePermissions=TrialFixture.Order(unsigned.PackagePermissions.Select(x=>x.Name=="EXECUTE" ? x with {State="W"} : x))},
            "grant-recipient"=>unsigned with {PackagePermissions=TrialFixture.Order(unsigned.PackagePermissions.Select(x=>x.Name=="EXECUTE" ? x with {GranteeId=999} : x))},
            _=>unsigned with {MonitorPermissions=[]}
        };
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateUnsigned(baseline,signed,unsigned));
    }

    [Theory]
    [InlineData("definition","sha256","changed")]
    [InlineData("definition","execute_as","-2")]
    [InlineData("definition","ansi","0:1")]
    [InlineData("certificate","public_sha256","changed")]
    [InlineData("certificate","thumbprint","changed")]
    [InlineData("certificate","private_key","ENCRYPTED_BY_PASSWORD")]
    [InlineData("marker","SqlObserver.AgentActivity.DeploymentId","foreign")]
    [InlineData("monitor_marker","SqlObserver.AgentActivity.DeploymentId","foreign")]
    [InlineData("base_object","syssessions","999")]
    public void EveryOtherProvenanceFactRemainsImmutable(string kind,string key,string value)
    {
        var (baseline,signed)=TrialFixture.Create(missingMasterMapping:true);
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateUnsigned(baseline,signed,ChangeFact(Unsigned(signed),kind,key,value)));
    }

    [Fact]
    public void FabricatedUnsignedImageDoesNotSupplyItsOwnSignedProvenance()
    {
        var (baseline,signed)=TrialFixture.Create();
        var unsigned=Unsigned(signed);
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateUnsigned(baseline,unsigned,unsigned));
        var invalidSigned=ChangeFact(signed,"signature_validity","module","1:0");
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateUnsigned(baseline,invalidSigned,unsigned));
        var foreignGrant=signed with {PackagePermissions=TrialFixture.Order(signed.PackagePermissions.Append(new("server",64,1,100,0,0,"CONTROL SERVER","G")))};
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateUnsigned(baseline,foreignGrant,Unsigned(foreignGrant)));
    }

    [Fact]
    public void NoAdditionalSignatureMayBeDeletedUnderTheSingleSignaturePlan()
    {
        var (baseline,signed)=TrialFixture.Create();
        signed=signed with {Facts=TrialFixture.Order(signed.Facts.Append(new("master","signature","62","SPVC:FOREIGN")))};
        Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateUnsigned(baseline,signed,Unsigned(signed)));
    }

    [Fact]
    public void ExtraMissingDuplicateAndNoncanonicalFactsAreRefused()
    {
        var (baseline,signed)=TrialFixture.Create();
        var unsigned=Unsigned(signed);
        TrialSnapshot[] variants=[
            unsigned with {Facts=TrialFixture.Order(unsigned.Facts.Append(new("master","unexpected_dependency","1","foreign")))},
            unsigned with {Facts=unsigned.Facts.Skip(1).ToArray()},
            unsigned with {Facts=TrialFixture.Order(unsigned.Facts.Append(unsigned.Facts[0]))},
            unsigned with {Facts=unsigned.Facts.Reverse().ToArray()}
        ];
        foreach(var variant in variants)
            Assert.Throws<InvalidDataException>(()=>AgentActivityTrialSql.ValidateUnsigned(baseline,signed,variant));
    }

    private static TrialSnapshot Unsigned(TrialSnapshot signed) => signed with {
        Facts=TrialFixture.Order(signed.Facts.Where(x=>x.Kind!="signature").Select(x=>x.Scope=="master" && x.Kind=="signature_validity" && x.Key=="module" ? x with {Value="0:0"} : x))
    };
    private static TrialSnapshot ChangeModule(TrialSnapshot state,Func<TrialObject,TrialObject> change) => state with {
        Objects=TrialFixture.Order(state.Objects.Select(x=>x.Kind=="module" ? change(x) : x))
    };
    private static TrialSnapshot ChangeFact(TrialSnapshot state,string kind,string key,string value) => state with {
        Facts=TrialFixture.Order(state.Facts.Select(x=>x.Kind==kind && x.Key==key ? x with {Value=value} : x))
    };
}
