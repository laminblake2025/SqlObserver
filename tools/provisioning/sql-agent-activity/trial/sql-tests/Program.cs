using SqlObserver.AgentActivityTrial;

var (baseline,installed) = TrialFixture.Create();
int passed = 0;
void Pass(string name,Action test) { test(); passed++; Console.WriteLine("PASS "+name); }
void Refuse(string name,Action test) => Pass(name,()=> {
    try { test(); } catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Expected safe refusal: "+name);
});
TrialSnapshot Fact(string kind,string key,string value) => installed with {
    Facts = TrialFixture.Order(installed.Facts.Select(x=>x.Kind==kind && x.Key==key ? x with { Value=value } : x))
};
Pass("valid synthetic postimage",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed));
Pass("baseline restored",()=>AgentActivityTrialSql.ValidateBaselineRestored(baseline,baseline));
Pass("exact postimage",()=>AgentActivityTrialSql.ValidateExactSnapshot(installed,installed));
Refuse("module altered",()=>AgentActivityTrialSql.ValidateInstalled(baseline,Fact("definition","sha256",new string('0',64))));
Refuse("execute as owner",()=>AgentActivityTrialSql.ValidateInstalled(baseline,Fact("definition","execute_as","-2")));
Refuse("signature invalid",()=>AgentActivityTrialSql.ValidateInstalled(baseline,Fact("signature_validity","module","1:0")));
Refuse("deployment marker mismatch",()=>AgentActivityTrialSql.ValidateInstalled(baseline,Fact("marker","SqlObserver.AgentActivity.DeploymentId",Guid.Empty.ToString("D"))));
Refuse("private key retained",()=>AgentActivityTrialSql.ValidateInstalled(baseline,Fact("certificate","private_key","ENCRYPTED_BY_PASSWORD")));
Refuse("missing certificate evidence",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with { Facts=installed.Facts.Where(x=>x.Key!="thumbprint").ToArray() }));
Refuse("unexpected consumers",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with { Facts=TrialFixture.Order(installed.Facts.Append(new("master","unexpected_dependency","99","other"))) }));
Refuse("extra permission",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with { PackagePermissions=TrialFixture.Order(installed.PackagePermissions.Append(new("server",64,1,100,0,0,"CONTROL SERVER","G"))) }));
Refuse("grant option",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with { PackagePermissions=TrialFixture.Order(installed.PackagePermissions.Select(x=>x.Name=="EXECUTE" ? x with {State="W"} : x)) }));
Refuse("monitor baseline grant changed",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with { MonitorPermissions=TrialFixture.Order(installed.MonitorPermissions.Append(new("server",50,1,100,0,0,"VIEW SERVER STATE","G"))) }));
Refuse("owner changed",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with { Objects=TrialFixture.Order(installed.Objects.Select(x=>x.Kind=="module" ? x with {OwnerId=1} : x)) }));
Refuse("object id drift",()=>AgentActivityTrialSql.ValidateExactSnapshot(installed,installed with { Objects=TrialFixture.Order(installed.Objects.Select(x=>x.Kind=="module" ? x with {Id=999} : x)) }));
Refuse("grantor drift",()=>AgentActivityTrialSql.ValidateExactSnapshot(installed,installed with { PackagePermissions=TrialFixture.Order(installed.PackagePermissions.Select(x=>x with {GrantorId=2})) }));
Refuse("mapping drift",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with {Monitor=installed.Monitor with {SidHex="05060708"}}));
Refuse("wrong instance",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with {Identity=installed.Identity with {MasterDatabaseGuid=Guid.NewGuid()}}));
Refuse("duplicate inventory",()=>AgentActivityTrialSql.ValidateExactSnapshot(installed,installed with {Facts=TrialFixture.Order(installed.Facts.Append(installed.Facts[0]))}));
Refuse("noncanonical order",()=>AgentActivityTrialSql.ValidateExactSnapshot(installed,installed with {Facts=installed.Facts.Reverse().ToArray()}));
Refuse("null malformed row",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with {Facts=[null!]}));
Refuse("null malformed identity",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with {Identity=null!}));
Refuse("oversized evidence",()=>AgentActivityTrialSql.ValidateInstalled(baseline,Fact("definition","sha256",new string('a',40000))));
Refuse("restored mapping differs",()=>AgentActivityTrialSql.ValidateBaselineRestored(baseline,new(baseline.Snapshot with {Monitor=baseline.Snapshot.Monitor with {MasterUserId=99}})));
Pass("objects present typed recovery signal",()=> {
    try { AgentActivityTrialSql.ValidateBaselineRestored(baseline,new(installed)); }
    catch (TrialObjectsPresentException) { return; }
    throw new InvalidOperationException("Expected typed objects-present refusal.");
});
if(args.Length != 1) throw new ArgumentException("Supply pinned capture-v1.sql path.");
var bytes=File.ReadAllBytes(args[0]);
Pass("exact module bytes",()=>AgentActivityTrialSql.ValidateModule(bytes));
Refuse("module byte drift",()=>AgentActivityTrialSql.ValidateModule([..bytes,(byte)' ']));
void ParseRefuses(string name,string json) => Pass(name,()=> {
    try { AgentActivityTrialSql.ParseMetadata<TrialMonitor>(json); }
    catch(TrialMetadataException error)
    {
        if(error.RecordTypeName!="TrialMonitor" || error.JsonLength!=json.Length || error.InnerException is not null ||
            error.ToString().Contains("PRIVATE_SENTINEL",StringComparison.Ordinal))
            throw new InvalidOperationException("Metadata diagnostic did not preserve the privacy contract.");
        return;
    }
    throw new InvalidOperationException("Expected metadata parse refusal.");
});
ParseRefuses("empty metadata", "");
ParseRefuses("whitespace metadata", "  \r\n ");
ParseRefuses("null metadata", "null");
ParseRefuses("malformed private metadata", "{\"PRIVATE_SENTINEL\":");
ParseRefuses("wrong metadata scalar", "{\"Principal\":\"PRIVATE_SENTINEL\",\"LoginId\":\"PRIVATE_SENTINEL\"}");
ParseRefuses("multiple exact mappings refused", "{\"Principal\":\"PRIVATE_SENTINEL\"},{\"Principal\":\"PRIVATE_SENTINEL\"}");
Pass("readable exact monitor metadata",()=> {
    var value=AgentActivityTrialSql.ParseMetadata<TrialMonitor>("{\"Principal\":\"TEST\\\\Monitor\",\"SidHex\":\"01020304\",\"LoginId\":50,\"MasterUserId\":51}");
    if(value!=baseline.Snapshot.Monitor) throw new InvalidOperationException("Exact readable mapping was changed.");
});
Pass("readable empty inventory",()=> {
    if(AgentActivityTrialSql.ParseMetadata<TrialFact[]>("[]").Length!=0) throw new InvalidOperationException("Unexpected metadata row.");
});
Pass("malformed inventory has typed diagnostic",()=> {
    try { AgentActivityTrialSql.ParseMetadata<TrialFact[]>("[PRIVATE_SENTINEL"); }
    catch(TrialMetadataException error) when(error.RecordTypeName=="TrialFact[]" && error.JsonLength==17 && error.InnerException is null) { return; }
    throw new InvalidOperationException("Expected bounded inventory diagnostic.");
});
var (missingBaseline,ownedUser)=TrialFixture.Create(missingMasterMapping:true);
Pass("absent user baseline valid",()=>AgentActivityTrialSql.ValidateBaselineRestored(missingBaseline,missingBaseline));
Pass("owned user with implicit CONNECT",()=>AgentActivityTrialSql.ValidateInstalled(missingBaseline,ownedUser));
Pass("owned user without explicit CONNECT catalog row",()=>AgentActivityTrialSql.ValidateInstalled(missingBaseline,ownedUser with {
    PackagePermissions=ownedUser.PackagePermissions.Where(x=>!(x.Scope=="master" && x.Name=="CONNECT")).ToArray(),
    MonitorPermissions=ownedUser.MonitorPermissions.Where(x=>x.Name!="CONNECT").ToArray()
}));
Refuse("owned user marker missing",()=>AgentActivityTrialSql.ValidateInstalled(missingBaseline,ownedUser with {
    Facts=ownedUser.Facts.Where(x=>x.Kind!="monitor_marker").ToArray()
}));
Refuse("owned user marker foreign",()=>AgentActivityTrialSql.ValidateInstalled(missingBaseline,ownedUser with {
    Facts=TrialFixture.Order(ownedUser.Facts.Select(x=>x.Kind=="monitor_marker" ? x with {Value=Guid.Empty.ToString("D")} : x))
}));
Refuse("implicit CONNECT has foreign grantor",()=>AgentActivityTrialSql.ValidateInstalled(missingBaseline,ownedUser with {
    PackagePermissions=TrialFixture.Order(ownedUser.PackagePermissions.Select(x=>x.Scope=="master" && x.Name=="CONNECT" ? x with {GrantorId=2} : x))
}));
Refuse("owned user permissions disagree",()=>AgentActivityTrialSql.ValidateInstalled(missingBaseline,ownedUser with {MonitorPermissions=[]}));
Refuse("owned user additional role",()=>AgentActivityTrialSql.ValidateInstalled(missingBaseline,ownedUser with {
    Facts=TrialFixture.Order(ownedUser.Facts.Append(new("master","monitor_role","10","51")))
}));
Refuse("zero baseline contradicts mapping",()=>AgentActivityTrialSql.ValidateBaselineRestored(missingBaseline,new(missingBaseline.Snapshot with {
    Facts=TrialFixture.Order(missingBaseline.Snapshot.Facts.Append(new("master","monitor_mapping","51","TEST\\Monitor:U:WINDOWS:01020304")))
})));
Pass("residual temporary user is typed presence",()=> {
    var residual=ownedUser with {Objects=ownedUser.Objects.Where(x=>x.Kind=="monitor_user").ToArray(),PackagePermissions=[],MonitorPermissions=[],
        Facts=TrialFixture.Order(ownedUser.Facts.Where(x=>x.Kind is "monitor_mapping" or "monitor_marker" or "base_object"))};
    try { AgentActivityTrialSql.ValidateBaselineRestored(missingBaseline,new(residual)); }
    catch(TrialObjectsPresentException) { return; }
    throw new InvalidOperationException("Residual owned monitor user was silently adopted.");
});
Pass("zero SQL fragments normalize empty list",()=> {
    if(AgentActivityTrialSql.ParseMetadataList<TrialObject>("",0).Length!=0)
        throw new InvalidOperationException("Zero-row list was not preserved.");
});
foreach(var malformed in new[]{"", " \r\n ", "[", "null"})
    Pass("returned malformed list fragment refused "+malformed.Length,()=> {
        try { AgentActivityTrialSql.ParseMetadataList<TrialObject>(malformed,1); }
        catch(TrialMetadataException) { return; }
        throw new InvalidOperationException("A returned invalid fragment was normalized.");
    });
Refuse("zero fragments with text refused",()=>AgentActivityTrialSql.ParseMetadataList<TrialObject>("[]",0));
Pass("explicit empty array preserved",()=> {
    if(AgentActivityTrialSql.ParseMetadataList<TrialObject>("[]",1).Length!=0)
        throw new InvalidOperationException("Explicit empty array was not preserved.");
});
foreach(var (kind,type) in new[]{("module","P "),("module","V"),("owner_user","U"),("certificate_login","S"),("certificate_user","S")})
    Refuse("strict canonical type guard "+kind+":"+type,()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with {
        Objects=TrialFixture.Order(installed.Objects.Select(x=>x.Kind==kind ? x with {Type=type} : x))
    }));
Refuse("certificate login automatic CONNECT SQL forbidden",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with {
    PackagePermissions=TrialFixture.Order(installed.PackagePermissions.Append(new("server",64,1,100,0,0,"CONNECT SQL","G")))
}));
Refuse("schema owner automatic CONNECT forbidden",()=>AgentActivityTrialSql.ValidateInstalled(baseline,installed with {
    PackagePermissions=TrialFixture.Order(installed.PackagePermissions.Append(new("master",60,1,0,0,0,"CONNECT","G")))
}));
Console.WriteLine($"Pure SQL-plan tests: {passed} passed; no SQL executed.");
