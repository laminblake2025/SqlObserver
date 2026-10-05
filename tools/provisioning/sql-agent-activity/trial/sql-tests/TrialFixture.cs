using System.Text.Json;
namespace SqlObserver.AgentActivityTrial;

// Synthetic metadata only. This fixture does not represent a native SQL qualification.
public static class TrialFixture
{
    public static (TrialBaseline Baseline, TrialSnapshot Installed) Create(bool missingMasterMapping=false)
    {
        var identity = new TrialIdentity("test\\sql17", "test", "sql17", "17.0.1.0", 17, 3, "\\",
            Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var monitor = new TrialMonitor("TEST\\Monitor", "01020304", 50, 51);
        var deployment = Guid.Parse("33333333-3333-3333-3333-333333333333");
        TrialFact[] baseFacts = [new("msdb","base_object","syssessions","100"),new("msdb","base_object","sysjobactivity","101")];
        var baseline = new TrialBaseline(new(identity, missingMasterMapping ? monitor with {MasterUserId=0} : monitor, deployment, [], [], [], Order(baseFacts)));
        TrialObject[] objects = [
            new("owner_user","master","SqlObserverAgentActivitySchemaOwnerV1",60,1,"S","NONE"),
            new("schema","master","sqlobserver_agent",61,60,"SCHEMA",""),
            new("module","master","capture_v1",62,60,"P",""),
            new("master_certificate","master","SqlObserverAgentActivityCertificateV1",63,1,"CERTIFICATE",""),
            new("certificate_login","server","SqlObserverAgentActivityCertificateLoginV1",64,1,"C","ABCD:0:"),
            new("msdb_certificate","msdb","SqlObserverAgentActivityCertificateV1",65,1,"CERTIFICATE",""),
            new("certificate_user","msdb","SqlObserverAgentActivityCertificateUserV1",66,1,"C","ABCD")
        ];
        TrialGrant[] permissions = [
            new("server",64,1,100,0,0,"VIEW SERVER SECURITY STATE","G"),
            new("msdb",66,1,0,0,0,"CONNECT","G"),
            new("msdb",66,1,1,100,0,"SELECT","G"),
            new("msdb",66,1,1,101,0,"SELECT","G"),
            new("master",51,1,1,62,0,"EXECUTE","G")
        ];
        TrialFact[] facts = [..baseFacts,
            new("master","definition","sha256",AgentActivityTrialSql.DefinitionUtf16Sha256),
            new("master","definition","execute_as","CALLER"), new("master","definition","ansi","1:1"),
            new("master","marker","SqlObserver.AgentActivity.DeploymentId",deployment.ToString("D")),
            new("master","certificate","private_key","NO_PRIVATE_KEY"),new("msdb","certificate","private_key","NO_PRIVATE_KEY"),
            new("master","certificate","public_sha256",new string('a',64)),new("msdb","certificate","public_sha256",new string('a',64)),
            new("master","certificate","thumbprint",new string('B',40)),new("msdb","certificate","thumbprint",new string('B',40)),
            new("master","certificate","sid","ABCD"),new("master","signature_validity","module","1:1"),
            new("master","signature","62","SPVC:"+new string('B',40))
        ];
        if(missingMasterMapping)
        {
            objects=[..objects,new("monitor_user","master",monitor.Principal,monitor.MasterUserId,null,"U",monitor.SidHex+":WINDOWS")];
            permissions=[..permissions,new("master",monitor.MasterUserId,1,0,0,0,"CONNECT","G")];
            facts=[..facts,
                new("master","monitor_mapping",monitor.MasterUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),monitor.Principal+":U:WINDOWS:"+monitor.SidHex),
                new("master","monitor_marker","SqlObserver.AgentActivity.DeploymentId",deployment.ToString("D"))];
        }
        var installed = new TrialSnapshot(identity, monitor, deployment, Order(objects), Order(permissions),
            Order(permissions.Where(x=>x.Scope=="master")), Order(facts));
        return (baseline,installed);
    }

    public static T[] Order<T>(IEnumerable<T> rows) => rows.OrderBy(x=>JsonSerializer.Serialize(x),StringComparer.Ordinal).ToArray();
}
