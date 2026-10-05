using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace SqlObserver.AgentActivityTrial;

public sealed record TrialVisibilityJob(Guid JobId,int AgentSessionId,DateTime? RequestedLocal,DateTime? QueuedLocal,
    DateTime? StartExecutionLocal,DateTime? StopExecutionLocal,int? LastExecutedStepId,DateTime? LastExecutedStepLocal);
public sealed record TrialVisibilityRow(TrialVisibilityJob Source,bool ForeignOwner);
public sealed record TrialVisibilitySnapshot(TrialIdentity Identity,Guid ConnectionId,int SessionId,
    DateTimeOffset StartedAtUtc,DateTimeOffset CompletedAtUtc,int AgentSessionId,DateTime AgentStartedLocal,
    IReadOnlyList<TrialVisibilityRow> Rows);
public sealed record TrialVisibilityCapture(DateTimeOffset ObservedFromUtc,DateTimeOffset ObservedToUtc,
    int? BeforeAgentSessionId,DateTime? BeforeAgentStartedLocal,int? AfterAgentSessionId,DateTime? AfterAgentStartedLocal,
    IReadOnlyList<TrialVisibilityJob> Jobs,int SourceRowsRead,bool Truncated);
public sealed record TrialVisibilityResult(int ComparedRows,int ForeignOwnerRows,bool ExactMatch,bool StableSession,bool BracketContained);

/// <summary>Read-only DBA witness for one bounded, observed fixture. Raw snapshots and job identities are private.
/// A differing owner SID does not identify another human or exclude group membership. Matching snapshots cannot exclude change-and-revert.</summary>
public static class TrialVisibilityWitness
{
    public const int MaximumRows=512;
    private static readonly TimeSpan Budget=TimeSpan.FromSeconds(10);

    public static async Task<TrialVisibilitySnapshot> CaptureAsync(SqlConnection connection,TrialBaseline baseline,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(baseline);
        token.ThrowIfCancellationRequested();
        AgentActivityTrialSql.ValidateBaselineRestored(baseline,baseline);
        RequireConnection(connection);
        Guid connectionId=connection.ClientConnectionId;
        long started=Stopwatch.GetTimestamp();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Budget);
        try
        {
            await using var command=new SqlCommand(Query,connection) { CommandTimeout=10 };
            command.Parameters.Add("@monitor",SqlDbType.NVarChar,128).Value=baseline.MonitorPrincipal;
            command.Parameters.Add("@monitor_sid",SqlDbType.VarBinary,85).Value=Convert.FromHexString(baseline.Snapshot.Monitor.SidHex);
            command.Parameters.Add("@monitor_login_id",SqlDbType.Int).Value=baseline.Snapshot.Monitor.LoginId;
            TrialVisibilitySnapshot result;
            await using(var reader=await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
            {
                Shape(reader,HeaderNames,HeaderTypes);
                if(!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
                for(int i=0;i<13;i++) if(reader.IsDBNull(i)) throw Invalid();
                var identity=new TrialIdentity(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
                    reader.GetInt32(4),reader.GetInt32(5),reader.GetString(6),reader.GetGuid(7),reader.GetGuid(8));
                if(identity!=baseline.Snapshot.Identity) throw Invalid();
                int sqlSession=reader.GetInt32(9),agentSession=reader.GetInt32(11);
                DateTimeOffset from=Utc(reader.GetDateTime(10));
                DateTime agentStart=reader.GetDateTime(12);
                if(await reader.ReadAsync(deadline.Token).ConfigureAwait(false) || !await reader.NextResultAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
                Shape(reader,RowNames,RowTypes);
                var rows=new List<TrialVisibilityRow>();
                while(await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
                {
                    // A 513th row is lookahead evidence only. This exact fixture gate refuses it.
                    if(rows.Count==MaximumRows || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(8)) throw Invalid();
                    rows.Add(new(new(reader.GetGuid(0),reader.GetInt32(1),Local(reader,2),Local(reader,3),Local(reader,4),Local(reader,5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6),Local(reader,7)),reader.GetBoolean(8)));
                }
                if(!await reader.NextResultAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
                Shape(reader,FooterNames,FooterTypes);
                if(!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
                for(int i=0;i<4;i++) if(reader.IsDBNull(i)) throw Invalid();
                DateTimeOffset to=Utc(reader.GetDateTime(0));
                if(reader.GetInt32(1)!=agentSession || reader.GetDateTime(2)!=agentStart || reader.GetInt32(3)!=sqlSession ||
                    await reader.ReadAsync(deadline.Token).ConfigureAwait(false) || await reader.NextResultAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
                result=new(identity,connectionId,sqlSession,from,to,agentSession,agentStart,rows.AsReadOnly());
            }
            deadline.Token.ThrowIfCancellationRequested();
            RequireConnection(connection);
            if(connection.ClientConnectionId!=connectionId || Stopwatch.GetElapsedTime(started)>=Budget) throw Invalid();
            ValidateSnapshot(result);
            return result;
        }
        catch(SqlException error) { throw new TrialSqlMetadataException("visibility_witness",error); }
        catch(OperationCanceledException) when(!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The bounded visibility witness deadline expired."); }
    }

    /// <summary>The receipt parser must separately attest target/revision/run, enrollment hash and successful native test identity.</summary>
    public static TrialVisibilityResult ValidateComparison(TrialVisibilitySnapshot before,TrialVisibilitySnapshot after,TrialVisibilityCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ValidateSnapshot(before); ValidateSnapshot(after);
        if(before.Identity!=after.Identity || before.ConnectionId!=after.ConnectionId || before.SessionId!=after.SessionId ||
            before.AgentSessionId!=after.AgentSessionId || before.AgentStartedLocal!=after.AgentStartedLocal ||
            !before.Rows.SequenceEqual(after.Rows)) throw Invalid();
        if(!ValidUtc(capture.ObservedFromUtc) || !ValidUtc(capture.ObservedToUtc) || capture.ObservedFromUtc>capture.ObservedToUtc ||
            capture.ObservedToUtc-capture.ObservedFromUtc>Budget || before.CompletedAtUtc>capture.ObservedFromUtc ||
            capture.ObservedToUtc>after.StartedAtUtc) throw Invalid();
        if(capture.BeforeAgentSessionId!=before.AgentSessionId || capture.AfterAgentSessionId!=before.AgentSessionId ||
            capture.BeforeAgentStartedLocal!=before.AgentStartedLocal || capture.AfterAgentStartedLocal!=before.AgentStartedLocal ||
            !ValidLocal(capture.BeforeAgentStartedLocal) || !ValidLocal(capture.AfterAgentStartedLocal) ||
            capture.Jobs is null || capture.Truncated || capture.SourceRowsRead!=before.Rows.Count || capture.Jobs.Count!=before.Rows.Count) throw Invalid();
        ValidateJobs(capture.Jobs,before.AgentSessionId);
        if(!capture.Jobs.SequenceEqual(before.Rows.Select(x=>x.Source))) throw Invalid();
        return new(before.Rows.Count,before.Rows.Count(x=>x.ForeignOwner),true,true,true);
    }

    public static void ValidateSnapshot(TrialVisibilitySnapshot snapshot)
    {
        if(snapshot is null || snapshot.Identity is null || snapshot.ConnectionId==Guid.Empty || snapshot.SessionId<=0 ||
            snapshot.Identity.MajorVersion!=17 || snapshot.Identity.EngineEdition is not (2 or 3) || snapshot.Identity.PathSeparator!="\\" ||
            snapshot.Identity.MasterDatabaseGuid==Guid.Empty || snapshot.Identity.MsdbDatabaseGuid==Guid.Empty ||
            string.IsNullOrWhiteSpace(snapshot.Identity.ServerName) || string.IsNullOrWhiteSpace(snapshot.Identity.MachineName) ||
            snapshot.Identity.InstanceName is null || string.IsNullOrWhiteSpace(snapshot.Identity.ProductVersion) ||
            !ValidUtc(snapshot.StartedAtUtc) || !ValidUtc(snapshot.CompletedAtUtc) || snapshot.StartedAtUtc>snapshot.CompletedAtUtc ||
            snapshot.CompletedAtUtc-snapshot.StartedAtUtc>Budget || snapshot.AgentSessionId<=0 || !ValidLocal(snapshot.AgentStartedLocal) ||
            snapshot.Rows is null || snapshot.Rows.Count is <1 or >MaximumRows || snapshot.Rows.Any(x=>x is null || x.Source is null) ||
            !snapshot.Rows.Any(x=>x.ForeignOwner)) throw Invalid();
        ValidateJobs(snapshot.Rows.Select(x=>x.Source).ToArray(),snapshot.AgentSessionId);
    }

    private static void ValidateJobs(IReadOnlyList<TrialVisibilityJob> jobs,int session)
    {
        if(jobs.Count is <1 or >MaximumRows) throw Invalid();
        string? previous=null;
        foreach(var row in jobs)
        {
            if(row is null || row.JobId==Guid.Empty || row.AgentSessionId!=session || row.LastExecutedStepId is <0 ||
                !ValidOptionalLocal(row.RequestedLocal) || !ValidOptionalLocal(row.QueuedLocal) || !ValidOptionalLocal(row.StartExecutionLocal) ||
                !ValidOptionalLocal(row.StopExecutionLocal) || !ValidOptionalLocal(row.LastExecutedStepLocal)) throw Invalid();
            string key=row.JobId.ToString("D");
            if(previous is not null && string.CompareOrdinal(previous,key)>=0) throw Invalid();
            previous=key;
        }
    }

    private static bool ValidUtc(DateTimeOffset value) => value.Offset==TimeSpan.Zero && value.Year>=1753;
    private static bool ValidLocal(DateTime? value) => value is {Kind:DateTimeKind.Unspecified,Year:>=1753};
    private static bool ValidOptionalLocal(DateTime? value) => value is null || ValidLocal(value);
    private static DateTimeOffset Utc(DateTime value) => value.Kind!=DateTimeKind.Local && value.Year>=1753
        ? new(DateTime.SpecifyKind(value,DateTimeKind.Utc)) : throw Invalid();
    private static DateTime? Local(SqlDataReader reader,int ordinal)
    {
        DateTime? value=reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
        return ValidOptionalLocal(value) ? value : throw Invalid();
    }
    private static void RequireConnection(SqlConnection connection)
    {
        if(connection.State!=ConnectionState.Open || connection.ClientConnectionId==Guid.Empty || connection.Database!="master") throw Invalid();
        var settings=new SqlConnectionStringBuilder(connection.ConnectionString);
        if(!settings.IntegratedSecurity || settings.TrustServerCertificate || settings.ConnectRetryCount!=0 || settings.Pooling || settings.MultipleActiveResultSets ||
            settings.Encrypt==SqlConnectionEncryptOption.Optional || !settings.DataSource.StartsWith("tcp:",StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(settings.HostNameInCertificate) || settings.InitialCatalog!="master") throw Invalid();
    }
    private static void Shape(SqlDataReader reader,string[] names,Type[] types)
    {
        if(reader.FieldCount!=names.Length) throw Invalid();
        for(int i=0;i<names.Length;i++) if(reader.GetName(i)!=names[i] || reader.GetFieldType(i)!=types[i]) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("The bounded visibility witness does not establish an exact stable fixture comparison.");
    private static readonly string[] HeaderNames=["server_name","machine_name","instance_name","product_version","major_version","engine_edition","path_separator","master_guid","msdb_guid","sql_session_id","observed_utc","agent_session_id","agent_start_local"];
    private static readonly Type[] HeaderTypes=[typeof(string),typeof(string),typeof(string),typeof(string),typeof(int),typeof(int),typeof(string),typeof(Guid),typeof(Guid),typeof(int),typeof(DateTime),typeof(int),typeof(DateTime)];
    private static readonly string[] RowNames=["job_id","agent_session_id","requested_local","queued_local","start_execution_local","stop_execution_local","last_executed_step_id","last_executed_step_local","foreign_owner"];
    private static readonly Type[] RowTypes=[typeof(Guid),typeof(int),typeof(DateTime),typeof(DateTime),typeof(DateTime),typeof(DateTime),typeof(int),typeof(DateTime),typeof(bool)];
    private static readonly string[] FooterNames=["observed_utc","agent_session_id","agent_start_local","sql_session_id"];
    private static readonly Type[] FooterTypes=[typeof(DateTime),typeof(int),typeof(DateTime),typeof(int)];

    public const string Query="""
        SET NOCOUNT ON;
        IF IS_SRVROLEMEMBER(N'sysadmin')<>1 OR IS_SRVROLEMEMBER(N'sysadmin') IS NULL OR
          CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))<>17 OR
          CONVERT(int,SERVERPROPERTY('EngineEdition')) NOT IN(2,3) OR CONVERT(nvarchar(1),SERVERPROPERTY('PathSeparator'))<>N'\'
          THROW 51000,'Unsupported visibility witness connection.',1;
        IF NOT EXISTS(SELECT 1 FROM master.sys.server_principals WHERE
          name COLLATE Latin1_General_100_BIN2=@monitor COLLATE Latin1_General_100_BIN2 AND sid=@monitor_sid AND principal_id=@monitor_login_id AND
          type='U' AND is_disabled=0 AND IS_SRVROLEMEMBER(N'sysadmin',name)=0)
          THROW 51000,'Visibility witness monitor identity changed.',1;
        DECLARE @started datetime2(7)=SYSUTCDATETIME(),@before_session int,@before_start datetime;
        SELECT TOP(1) @before_session=session_id,@before_start=agent_start_date FROM msdb.dbo.syssessions ORDER BY session_id DESC;
        SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) AS server_name,
          CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) AS machine_name,
          COALESCE(CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),N'') AS instance_name,
          CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')) AS product_version,
          CONVERT(int,SERVERPROPERTY('ProductMajorVersion')) AS major_version,
          CONVERT(int,SERVERPROPERTY('EngineEdition')) AS engine_edition,
          CONVERT(nvarchar(1),SERVERPROPERTY('PathSeparator')) AS path_separator,
          (SELECT database_guid FROM master.sys.database_recovery_status WHERE database_id=1) AS master_guid,
          (SELECT database_guid FROM master.sys.database_recovery_status WHERE database_id=4) AS msdb_guid,
          CONVERT(int,@@SPID) AS sql_session_id,@started AS observed_utc,@before_session AS agent_session_id,@before_start AS agent_start_local;
        SELECT TOP(513) a.job_id AS job_id,a.session_id AS agent_session_id,
          a.run_requested_date AS requested_local,a.queued_date AS queued_local,a.start_execution_date AS start_execution_local,
          a.stop_execution_date AS stop_execution_local,a.last_executed_step_id AS last_executed_step_id,
          a.last_executed_step_date AS last_executed_step_local,
          CONVERT(bit,CASE WHEN j.owner_sid IS NULL OR DATALENGTH(j.owner_sid)=0 THEN NULL WHEN j.owner_sid=@monitor_sid THEN 0 ELSE 1 END) AS foreign_owner
        FROM msdb.dbo.sysjobactivity AS a LEFT JOIN msdb.dbo.sysjobs AS j ON j.job_id=a.job_id
        WHERE a.session_id=@before_session
        ORDER BY CONVERT(char(36),a.job_id) COLLATE Latin1_General_100_BIN2;
        DECLARE @after_session int,@after_start datetime;
        SELECT TOP(1) @after_session=session_id,@after_start=agent_start_date FROM msdb.dbo.syssessions ORDER BY session_id DESC;
        SELECT SYSUTCDATETIME() AS observed_utc,@after_session AS agent_session_id,@after_start AS agent_start_local,CONVERT(int,@@SPID) AS sql_session_id;
        """;
}
