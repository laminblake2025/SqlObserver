using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace SqlObserver.AgentActivityTrial;

public sealed record RunningJobBaseline(TrialBaseline ModuleBaseline, Guid FixtureId, Guid ConnectionId, int SessionId,
    string OwnerPrincipal, string OwnerSidHex, int OwnerPrincipalId, string DatabaseName, string JobName, Guid ExecutionNonce,
    int ForeignJobCount, int OriginalAgentSessionId, DateTime OriginalAgentStartLocal, string ForeignInventoryJson,
    string ForeignDefinitionsSha256, string PermissionsSha256, string OriginalSessionsSha256, string OriginalActivitySha256);
public sealed record RunningJobOwnership(RunningJobBaseline Baseline, int DatabaseId, Guid DatabaseGuid, string DatabaseMarker,
    string DatabaseDefinitionSha256, Guid JobId, string JobDefinitionSha256, string StepCommandSha256);
public sealed record RunningJobExecutionWitness(Guid FixtureId, Guid JobId, Guid ExecutionNonce, int DatabaseId, Guid DatabaseGuid,
    int? StepSessionId, DateTimeOffset ObservedUtc, DateTimeOffset? StepStartedUtc, DateTime? StepStartedLocal,
    DateTimeOffset? StepStartedOffset, bool Released, DateTimeOffset? StepCompletedUtc, DateTime? StepCompletedLocal,
    DateTimeOffset? StepCompletedOffset, bool LiveOwnedRequest, int AgentSessionId, DateTime AgentStartedLocal,
    TrialVisibilityJob? Activity, bool SuccessfulHistory);

/// <summary>One finite SQL-only fixture. Every SQL action remains on the reviewed DBA physical session.
/// Raw job inventories and witness identities belong only in the protected journal.</summary>
public static class TrialRunningJobWitness
{
    public const int MaximumForeignJobs = 511;
    public const int MaximumInventoryBytes = 4 * 1024 * 1024;
    public const int StepIterations = 1200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task<RunningJobBaseline> CaptureBaselineAsync(SqlConnection connection, TrialBaseline moduleBaseline,
        Guid fixtureId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(moduleBaseline);
        AgentActivityTrialSql.ValidateBaselineRestored(moduleBaseline, moduleBaseline);
        RequireConnection(connection);
        if (fixtureId == Guid.Empty) throw Invalid();
        string database = "SqlObserverRunning_" + fixtureId.ToString("N"), job = "SqlObserver Running " + fixtureId.ToString("N");
        Guid connectionId = connection.ClientConnectionId;
        await using var command = new SqlCommand(BaselineSql, connection) { CommandTimeout = 10 };
        command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = database;
        command.Parameters.Add("@job_name", SqlDbType.NVarChar, 128).Value = job;
        command.Parameters.Add("@monitor_sid", SqlDbType.VarBinary, 85).Value = Convert.FromHexString(moduleBaseline.Snapshot.Monitor.SidHex);
        using JsonDocument document = await ReadJsonAsync(connection, command, connectionId, token).ConfigureAwait(false);
        JsonElement row = document.RootElement;
        TrialIdentity identity = Deserialize<TrialIdentity>(row.GetProperty("Identity"));
        if (identity != moduleBaseline.Snapshot.Identity) throw Invalid();
        int oldSession = row.GetProperty("AgentSessionId").GetInt32();
        DateTime oldStart = row.GetProperty("AgentStartedLocal").GetDateTime();
        int session = row.GetProperty("SessionId").GetInt32();
        var provisional = new RunningJobBaseline(moduleBaseline, fixtureId, connectionId, session,
            row.GetProperty("OwnerPrincipal").GetString()!, row.GetProperty("OwnerSidHex").GetString()!,
            row.GetProperty("OwnerPrincipalId").GetInt32(), database, job, Guid.NewGuid(), row.GetProperty("ForeignJobCount").GetInt32(),
            oldSession, oldStart, "{}", "", "", "", "");
        string inventory = await CaptureInventoryAsync(connection, provisional, Guid.Empty, token).ConfigureAwait(false);
        using JsonDocument state = JsonDocument.Parse(inventory);
        var baseline = provisional with
        {
            ForeignInventoryJson = inventory, ForeignDefinitionsSha256 = DefinitionsDigest(state.RootElement),
            PermissionsSha256 = Digest(state.RootElement.GetProperty("permissions")),
            OriginalSessionsSha256 = Digest(state.RootElement.GetProperty("oldSessions")),
            OriginalActivitySha256 = Digest(state.RootElement.GetProperty("oldActivity")),
        };
        ValidateBaseline(baseline);
        return baseline;
    }

    public static async Task RecheckBaselineAsync(SqlConnection connection, RunningJobBaseline baseline, Guid ownedJobId, CancellationToken token)
    {
        ValidateBaseline(baseline);
        string inventory = await CaptureInventoryAsync(connection, baseline, ownedJobId, token).ConfigureAwait(false);
        using JsonDocument current = JsonDocument.Parse(inventory);
        if (DefinitionsDigest(current.RootElement) != baseline.ForeignDefinitionsSha256 ||
            Digest(current.RootElement.GetProperty("permissions")) != baseline.PermissionsSha256 ||
            Digest(current.RootElement.GetProperty("oldSessions")) != baseline.OriginalSessionsSha256 ||
            Digest(current.RootElement.GetProperty("oldActivity")) != baseline.OriginalActivitySha256) throw Invalid();
    }

    public static async Task<RunningJobOwnership> CreateOwnedSqlFixtureAsync(SqlConnection connection, RunningJobBaseline baseline,
        TrialRunningJobJournal journal, CancellationToken token)
    {
        ValidateBaseline(baseline); RequireJournal(journal, baseline, "prepared");
        await RecheckBaselineAsync(connection, baseline, Guid.Empty, token).ConfigureAwait(false);
        journal.RecordPhase("database_create_pending");
        await ExecuteAsync(connection, baseline, Guid.Empty,
            "IF DB_ID(@db) IS NOT NULL OR EXISTS(SELECT 1 FROM msdb.dbo.sysjobs WHERE name=@job_name) THROW 51000,'Owned name exists.',1;" +
            "EXEC(N'CREATE DATABASE ' + QUOTENAME(@db));", null, token).ConfigureAwait(false);
        string marker = baseline.ModuleBaseline.DeploymentId.ToString("D") + ":" + baseline.FixtureId.ToString("D");
        string bootstrap = BuildDatabaseSql(baseline, marker);
        await ExecuteAsync(connection, baseline, Guid.Empty, "EXEC(@bootstrap);", command =>
            command.Parameters.Add("@bootstrap", SqlDbType.NVarChar, -1).Value = bootstrap, token).ConfigureAwait(false);
        RunningJobOwnership databaseOnly = await ReadOwnershipAsync(connection, baseline, Guid.Empty, marker, token).ConfigureAwait(false);
        journal.Save(journal.Read() with { Phase = "database_created", Ownership = databaseOnly });
        await RecheckBaselineAsync(connection, baseline, Guid.Empty, token).ConfigureAwait(false);
        journal.RecordPhase("job_create_pending");
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        // The actual sp_add_job UUID is captured and journaled before COMMIT; no invented UUID is substituted.
        await using var create = new SqlCommand(JobCreateSql, connection, transaction) { CommandTimeout = 10 };
        AddParameters(create, baseline, Guid.Empty); create.Parameters.Add("@marker", SqlDbType.NVarChar, 256).Value = marker;
        var output = create.Parameters.Add("@new_job", SqlDbType.UniqueIdentifier); output.Direction = ParameterDirection.Output;
        try
        {
            await create.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            RequirePhysicalConnection(connection, baseline);
            if (output.Value is not Guid jobId || jobId == Guid.Empty) throw Invalid();
            string step = BuildStepCommand(baseline, jobId);
            await using var addStep = new SqlCommand(JobStepSql, connection, transaction) { CommandTimeout = 10 };
            addStep.Parameters.Add("@job", SqlDbType.UniqueIdentifier).Value = jobId;
            addStep.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = baseline.DatabaseName;
            addStep.Parameters.Add("@command", SqlDbType.NVarChar, -1).Value = step;
            await addStep.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            RunningJobOwnership ownership = await ReadOwnershipAsync(connection, baseline, jobId, marker, token, transaction).ConfigureAwait(false);
            if (ownership.StepCommandSha256 != TextDigest(step)) throw Invalid();
            journal.Save(journal.Read() with { Ownership = ownership });
            await transaction.CommitAsync(token).ConfigureAwait(false);
            journal.RecordPhase("job_created");
            await RequireOwnedAsync(connection, ownership, token).ConfigureAwait(false);
            return ownership;
        }
        catch (SqlException error) { throw new TrialSqlMetadataException("running_job_create", error); }
    }

    public static async Task StartOwnedJobAsync(SqlConnection connection, RunningJobOwnership ownership,
        TrialRunningJobJournal journal, CancellationToken token)
    {
        RunningJobReceipt receipt = RequireJournal(journal, ownership.Baseline, "agent_running");
        if (receipt.AgentEpoch is null) throw Invalid();
        await RequireOwnedAsync(connection, ownership, token).ConfigureAwait(false);
        await RequireEpochAsync(connection, ownership.Baseline, ownership.JobId, receipt.AgentEpoch, token).ConfigureAwait(false);
        journal.RecordPhase("job_start_pending");
        await ExecuteAsync(connection, ownership.Baseline, ownership.JobId,
            "IF EXISTS(SELECT 1 FROM msdb.dbo.sysjobactivity WHERE job_id=@job AND (run_requested_date IS NOT NULL OR queued_date IS NOT NULL OR start_execution_date IS NOT NULL)) " +
            "OR EXISTS(SELECT 1 FROM msdb.dbo.sysjobhistory WHERE job_id=@job) THROW 51000,'Owned job already requested.',1;" +
            "DECLARE @rc int; EXEC @rc=msdb.dbo.sp_start_job @job_id=@job; IF @rc<>0 THROW 51000,'Owned start failed.',1;", null, token).ConfigureAwait(false);
    }

    public static async Task<RunningJobExecutionWitness> ReadExecutionWitnessAsync(SqlConnection connection,
        RunningJobOwnership ownership, CancellationToken token)
    {
        await RequireOwnedAsync(connection, ownership, token).ConfigureAwait(false);
        string query = BuildExecutionSql(ownership);
        await using var command = new SqlCommand(GuardSql + query, connection) { CommandTimeout = 10 };
        AddParameters(command, ownership.Baseline, ownership.JobId);
        using JsonDocument document = await ReadJsonAsync(connection, command, ownership.Baseline.ConnectionId, token).ConfigureAwait(false);
        JsonElement row = document.RootElement;
        DateTimeOffset? Utc(string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : AsUtc(row.GetProperty(name).GetDateTime());
        DateTime? Local(string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetDateTime();
        DateTimeOffset? Offset(string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetDateTimeOffset();
        var witness = new RunningJobExecutionWitness(ownership.Baseline.FixtureId, ownership.JobId, ownership.Baseline.ExecutionNonce,
            ownership.DatabaseId, ownership.DatabaseGuid, row.GetProperty("StepSessionId").ValueKind == JsonValueKind.Null ? null : row.GetProperty("StepSessionId").GetInt32(),
            AsUtc(row.GetProperty("ObservedUtc").GetDateTime()), Utc("StepStartedUtc"), Local("StepStartedLocal"), Offset("StepStartedOffset"),
            row.GetProperty("Released").GetBoolean(), Utc("StepCompletedUtc"), Local("StepCompletedLocal"), Offset("StepCompletedOffset"),
            row.GetProperty("LiveOwnedRequest").GetBoolean(), row.GetProperty("AgentSessionId").GetInt32(), row.GetProperty("AgentStartedLocal").GetDateTime(),
            row.GetProperty("Activity").ValueKind == JsonValueKind.Null ? null : Deserialize<TrialVisibilityJob>(row.GetProperty("Activity")),
            row.GetProperty("SuccessfulHistory").GetBoolean());
        ValidateExecutionWitness(ownership, witness);
        return witness;
    }

    public static async Task ReleaseOwnedJobAsync(SqlConnection connection, RunningJobOwnership ownership,
        TrialRunningJobJournal journal, bool monitorTerminationConfirmed, CancellationToken token)
    {
        if (!monitorTerminationConfirmed) throw Invalid();
        RunningJobReceipt receipt = RequireJournal(journal, ownership.Baseline, "running_capture_verified");
        if (receipt.AgentEpoch is null) throw Invalid();
        await RequireEpochAsync(connection, ownership.Baseline, ownership.JobId, receipt.AgentEpoch, token).ConfigureAwait(false);
        RunningJobExecutionWitness execution = await ReadExecutionWitnessAsync(connection, ownership, token).ConfigureAwait(false);
        RequireRunning(ownership, execution);
        journal.RecordPhase("release_pending", execution: execution);
        string sql = "EXEC(N'USE " + Quote(ownership.Baseline.DatabaseName) + "; UPDATE dbo.RunningJobWitness SET released=1 WHERE singleton=1 " +
            "AND nonce=''' + CONVERT(nvarchar(36),@nonce) + ''' AND job_id=''' + CONVERT(nvarchar(36),@job) + ''' AND released=0 AND completed_utc IS NULL AND step_spid IS NOT NULL; " +
            "IF @@ROWCOUNT<>1 THROW 51000,''Owned release refused.'',1;');";
        await ExecuteAsync(connection, ownership.Baseline, ownership.JobId, sql, null, token).ConfigureAwait(false);
    }

    public static TrialVisibilityResult ValidateRunningComparison(RunningJobOwnership ownership, TrialVisibilitySnapshot before,
        TrialVisibilitySnapshot after, TrialVisibilityCapture capture, RunningJobExecutionWitness executionBefore, RunningJobExecutionWitness executionAfter)
        => ValidateComparison(ownership, before, after, capture, executionBefore, executionAfter, running: true);
    public static TrialVisibilityResult ValidateCompletedComparison(RunningJobOwnership ownership, TrialVisibilitySnapshot before,
        TrialVisibilitySnapshot after, TrialVisibilityCapture capture, RunningJobExecutionWitness executionBefore, RunningJobExecutionWitness executionAfter)
        => ValidateComparison(ownership, before, after, capture, executionBefore, executionAfter, running: false);

    public static async Task CleanupOwnedSqlFixtureAsync(SqlConnection connection, RunningJobOwnership ownership,
        TrialRunningJobJournal journal, bool monitorTerminationConfirmed, CancellationToken token)
    {
        RunningJobReceipt receipt = journal.Read();
        if (!monitorTerminationConfirmed)
        {
            journal.Save(receipt with { Phase = "cleanup_blocked", FailureCode = "monitor_termination_unconfirmed", BlockedFromPhase = receipt.BlockedFromPhase ?? receipt.Phase });
            throw Invalid();
        }
        if (!SameOwnership(receipt.Ownership, ownership) || receipt.Phase is not ("job_created" or "agent_running" or "completed_witness" or "completed_capture_verified" or "database_created")) throw Invalid();
        try
        {
            await RequireOwnedAsync(connection, ownership, token, allowDatabaseOnly: true).ConfigureAwait(false);
            if (ownership.JobId != Guid.Empty)
            {
                RunningJobExecutionWitness? execution = null;
                if (receipt.Phase == "job_created" && receipt.AgentEpoch is null)
                {
                    await ExecuteAsync(connection, ownership.Baseline, ownership.JobId,
                        "IF EXISTS(SELECT 1 FROM msdb.dbo.sysjobactivity WHERE job_id=@job AND (run_requested_date IS NOT NULL OR queued_date IS NOT NULL OR start_execution_date IS NOT NULL)) " +
                        "OR EXISTS(SELECT 1 FROM msdb.dbo.sysjobhistory WHERE job_id=@job) OR EXISTS(SELECT 1 FROM sys.dm_exec_requests WHERE database_id=DB_ID(@db)) " +
                        "OR EXISTS(SELECT 1 FROM " + Quote(ownership.Baseline.DatabaseName) + ".dbo.RunningJobWitness WHERE step_spid IS NOT NULL OR started_utc IS NOT NULL OR released<>0 OR completed_utc IS NOT NULL) " +
                        "THROW 51000,'Owned never-started proof failed.',1;", null, token).ConfigureAwait(false);
                }
                else
                {
                    execution = await ReadExecutionWitnessAsync(connection, ownership, token).ConfigureAwait(false);
                    ValidateCleanupExecution(ownership, execution);
                }
                journal.RecordPhase("job_delete_pending", execution: execution);
                await ExecuteAsync(connection, ownership.Baseline, ownership.JobId,
                    "IF EXISTS(SELECT 1 FROM msdb.dbo.sysjobactivity WHERE job_id=@job AND (run_requested_date IS NOT NULL OR queued_date IS NOT NULL OR start_execution_date IS NOT NULL) AND stop_execution_date IS NULL) " +
                    "OR EXISTS(SELECT 1 FROM sys.dm_exec_requests WHERE database_id=DB_ID(@db)) OR " +
                    "EXISTS(SELECT 1 FROM sys.dm_exec_sessions s JOIN " + Quote(ownership.Baseline.DatabaseName) + ".dbo.RunningJobWitness w ON s.session_id=w.step_spid) " +
                    "THROW 51000,'Owned execution not ended.',1;" +
                    "DECLARE @rc int; EXEC @rc=msdb.dbo.sp_delete_job @job_id=@job,@delete_unused_schedule=0,@delete_history=0; " +
                    "IF @rc<>0 OR EXISTS(SELECT 1 FROM msdb.dbo.sysjobs WHERE job_id=@job) THROW 51000,'Owned delete not acknowledged.',1;", null, token).ConfigureAwait(false);
                journal.RecordPhase("job_absent");
            }
            await RecheckBaselineAsync(connection, ownership.Baseline, ownership.JobId, token).ConfigureAwait(false);
            await RequireDatabaseAsync(connection, ownership, token).ConfigureAwait(false);
            journal.RecordPhase("database_drop_pending");
            await ExecuteAsync(connection, ownership.Baseline, ownership.JobId,
                "IF EXISTS(SELECT 1 FROM sys.dm_exec_sessions WHERE database_id=DB_ID(@db)) OR " +
                "EXISTS(SELECT 1 FROM sys.dm_exec_requests WHERE database_id=DB_ID(@db)) OR " +
                "EXISTS(SELECT 1 FROM msdb.dbo.sysjobs WHERE job_id=@job OR name=@job_name) THROW 51000,'Owned database is in use.',1;" +
                "EXEC(N'DROP DATABASE ' + QUOTENAME(@db)); IF DB_ID(@db) IS NOT NULL THROW 51000,'Owned drop not acknowledged.',1;", null, token).ConfigureAwait(false);
            journal.RecordPhase("database_absent");
            await RecheckBaselineAsync(connection, ownership.Baseline, ownership.JobId, token).ConfigureAwait(false);
            // Agent stop belongs to the coordinator's held OS epoch; this method never controls SCM.
            if (receipt.AgentEpoch is null) journal.RecordPhase("cleanup_verified");
        }
        catch
        {
            RunningJobReceipt current = journal.Read();
            journal.Save(current with { Phase = "cleanup_blocked", FailureCode = "owned_sql_cleanup_unconfirmed", BlockedFromPhase = current.BlockedFromPhase ?? current.Phase });
            throw;
        }
    }

    public static void ValidateBaseline(RunningJobBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        AgentActivityTrialSql.ValidateBaselineRestored(baseline.ModuleBaseline, baseline.ModuleBaseline);
        if (baseline.FixtureId == Guid.Empty || baseline.ConnectionId == Guid.Empty || baseline.SessionId <= 0 || baseline.ExecutionNonce == Guid.Empty ||
            baseline.DatabaseName != "SqlObserverRunning_" + baseline.FixtureId.ToString("N") || baseline.JobName != "SqlObserver Running " + baseline.FixtureId.ToString("N") ||
            string.IsNullOrWhiteSpace(baseline.OwnerPrincipal) || baseline.OwnerPrincipal.Length > 128 || baseline.OwnerPrincipal.Any(char.IsControl) ||
            baseline.OwnerPrincipalId <= 0 || baseline.OwnerSidHex.Length is < 2 or > 170 || baseline.OwnerSidHex.Length % 2 != 0 ||
            baseline.OwnerSidHex.Any(c => !Uri.IsHexDigit(c)) || string.Equals(baseline.OwnerSidHex, baseline.ModuleBaseline.Snapshot.Monitor.SidHex, StringComparison.OrdinalIgnoreCase) ||
            baseline.ForeignJobCount is < 0 or > MaximumForeignJobs || baseline.OriginalAgentSessionId <= 0 ||
            baseline.OriginalAgentStartLocal.Kind != DateTimeKind.Unspecified || baseline.OriginalAgentStartLocal.Year < 1753 ||
            Encoding.UTF8.GetByteCount(baseline.ForeignInventoryJson) is <= 0 or > MaximumInventoryBytes) throw Invalid();
        using JsonDocument inventory = JsonDocument.Parse(baseline.ForeignInventoryJson, new JsonDocumentOptions { MaxDepth = 32 });
        RequireKeys(inventory.RootElement, InventoryKeys);
        if (inventory.RootElement.GetProperty("jobs").GetArrayLength() != baseline.ForeignJobCount) throw Invalid();
        if (DefinitionsDigest(inventory.RootElement) != baseline.ForeignDefinitionsSha256 ||
            Digest(inventory.RootElement.GetProperty("permissions")) != baseline.PermissionsSha256 ||
            Digest(inventory.RootElement.GetProperty("oldSessions")) != baseline.OriginalSessionsSha256 ||
            Digest(inventory.RootElement.GetProperty("oldActivity")) != baseline.OriginalActivitySha256) throw Invalid();
    }

    public static void ValidateOwnership(RunningJobOwnership ownership, bool allowDatabaseOnly = false)
    {
        ArgumentNullException.ThrowIfNull(ownership); ValidateBaseline(ownership.Baseline);
        if (ownership.DatabaseId <= 4 || ownership.DatabaseGuid == Guid.Empty ||
            ownership.DatabaseMarker != ownership.Baseline.ModuleBaseline.DeploymentId.ToString("D") + ":" + ownership.Baseline.FixtureId.ToString("D") ||
            !ValidHash(ownership.DatabaseDefinitionSha256) || ownership.JobId == Guid.Empty && (!allowDatabaseOnly || ownership.JobDefinitionSha256 != "" || ownership.StepCommandSha256 != "") ||
            ownership.JobId != Guid.Empty && (!ValidHash(ownership.JobDefinitionSha256) || ownership.StepCommandSha256 != TextDigest(BuildStepCommand(ownership.Baseline, ownership.JobId)))) throw Invalid();
    }

    public static void ValidateExecutionWitness(RunningJobOwnership ownership, RunningJobExecutionWitness witness)
    {
        ValidateOwnership(ownership);
        if (witness is null || witness.FixtureId != ownership.Baseline.FixtureId || witness.JobId != ownership.JobId ||
            witness.ExecutionNonce != ownership.Baseline.ExecutionNonce || witness.DatabaseId != ownership.DatabaseId || witness.DatabaseGuid != ownership.DatabaseGuid ||
            witness.ObservedUtc.Offset != TimeSpan.Zero || witness.ObservedUtc.Year < 1753 || witness.StepSessionId is <= 0 ||
            witness.StepSessionId == ownership.Baseline.SessionId ||
            witness.AgentSessionId <= ownership.Baseline.OriginalAgentSessionId || witness.AgentStartedLocal.Kind != DateTimeKind.Unspecified || witness.AgentStartedLocal.Year < 1753 ||
            witness.StepStartedLocal is { Kind: not DateTimeKind.Unspecified } || witness.StepCompletedLocal is { Kind: not DateTimeKind.Unspecified } ||
            witness.StepStartedLocal is { Year: < 1753 } || witness.StepCompletedLocal is { Year: < 1753 } ||
            witness.StepStartedOffset is { Year: < 1753 } || witness.StepCompletedOffset is { Year: < 1753 } ||
            witness.StepStartedUtc is { Year: < 1753 } || witness.StepCompletedUtc is { Year: < 1753 } ||
            witness.StepStartedUtc is { Offset: var startOffset } && startOffset != TimeSpan.Zero ||
            witness.StepCompletedUtc is { Offset: var stopOffset } && stopOffset != TimeSpan.Zero ||
            (witness.StepSessionId is null) != (witness.StepStartedUtc is null) ||
            (witness.StepStartedUtc is null) != (witness.StepStartedLocal is null) || (witness.StepStartedUtc is null) != (witness.StepStartedOffset is null) ||
            (witness.StepCompletedUtc is null) != (witness.StepCompletedLocal is null) || (witness.StepCompletedUtc is null) != (witness.StepCompletedOffset is null) ||
            witness.StepCompletedUtc is not null && (witness.StepStartedUtc is null || !witness.Released) ||
            witness.Activity is { } activity && (activity.JobId != ownership.JobId || activity.AgentSessionId != witness.AgentSessionId)) throw Invalid();
        // The three independently sampled clocks are evidence. They are never subtracted to infer job duration.
    }

    /// <summary>Preliminary cleanup eligibility only. Deletion also independently requires the recorded SQL SPID/request to be absent.</summary>
    public static void ValidateCleanupExecution(RunningJobOwnership ownership, RunningJobExecutionWitness execution)
    {
        ValidateExecutionWitness(ownership, execution);
        if (execution.LiveOwnedRequest || execution.Activity is { StopExecutionLocal: null } activity &&
            (activity.RequestedLocal is not null || activity.QueuedLocal is not null || activity.StartExecutionLocal is not null) ||
            execution.StepStartedUtc is not null && (execution.StepCompletedUtc is null || !execution.SuccessfulHistory)) throw Invalid();
    }

    public static string BuildStepCommand(RunningJobBaseline baseline, Guid jobId)
    {
        if (baseline.FixtureId == Guid.Empty || baseline.ExecutionNonce == Guid.Empty || jobId == Guid.Empty) throw Invalid();
        return "SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 2000;\n" +
            "DECLARE @nonce uniqueidentifier='" + baseline.ExecutionNonce.ToString("D") + "',@job uniqueidentifier='" + jobId.ToString("D") + "';\n" +
            "UPDATE dbo.RunningJobWitness SET step_spid=@@SPID,started_utc=SYSUTCDATETIME(),started_local=SYSDATETIME(),started_offset=SYSDATETIMEOFFSET() " +
            "WHERE singleton=1 AND nonce=@nonce AND job_id=@job AND step_spid IS NULL AND released=0;\n" +
            "IF @@ROWCOUNT<>1 THROW 51000,'Owned step identity refused.',1;\n" +
            "DECLARE @iteration int=0,@released bit=0; WHILE @iteration<1200 BEGIN\n" +
            "SELECT @released=released FROM dbo.RunningJobWitness WHERE singleton=1 AND nonce=@nonce AND job_id=@job AND step_spid=@@SPID;\n" +
            "IF @released=1 BREAK; WAITFOR DELAY '00:00:00.250'; SET @iteration+=1; END;\n" +
            "IF @released<>1 THROW 51000,'Owned bounded step expired.',1;\n" +
            "UPDATE dbo.RunningJobWitness SET completed_utc=SYSUTCDATETIME(),completed_local=SYSDATETIME(),completed_offset=SYSDATETIMEOFFSET() " +
            "WHERE singleton=1 AND nonce=@nonce AND job_id=@job AND step_spid=@@SPID AND released=1 AND completed_utc IS NULL;\n" +
            "IF @@ROWCOUNT<>1 THROW 51000,'Owned completion refused.',1;";
    }

    private static TrialVisibilityResult ValidateComparison(RunningJobOwnership ownership, TrialVisibilitySnapshot before,
        TrialVisibilitySnapshot after, TrialVisibilityCapture capture, RunningJobExecutionWitness executionBefore,
        RunningJobExecutionWitness executionAfter, bool running)
    {
        ValidateOwnership(ownership);
        TrialVisibilityResult result = TrialVisibilityWitness.ValidateComparison(before, after, capture);
        if (before.Identity != ownership.Baseline.ModuleBaseline.Snapshot.Identity || before.ConnectionId != ownership.Baseline.ConnectionId ||
            before.SessionId != ownership.Baseline.SessionId || !before.Rows.Any(row => row.Source.JobId == ownership.JobId && row.ForeignOwner)) throw Invalid();
        ValidateExecutionWitness(ownership, executionBefore); ValidateExecutionWitness(ownership, executionAfter);
        if (executionBefore.AgentSessionId != before.AgentSessionId || executionAfter.AgentSessionId != after.AgentSessionId ||
            executionBefore.AgentStartedLocal != before.AgentStartedLocal || executionAfter.AgentStartedLocal != after.AgentStartedLocal ||
            executionBefore.ObservedUtc > before.StartedAtUtc || executionAfter.ObservedUtc < after.CompletedAtUtc ||
            executionBefore.StepSessionId != executionAfter.StepSessionId || executionBefore.StepStartedUtc != executionAfter.StepStartedUtc ||
            executionBefore.StepStartedLocal != executionAfter.StepStartedLocal || executionBefore.StepStartedOffset != executionAfter.StepStartedOffset ||
            executionBefore.StepCompletedUtc != executionAfter.StepCompletedUtc || executionBefore.StepCompletedLocal != executionAfter.StepCompletedLocal ||
            executionBefore.StepCompletedOffset != executionAfter.StepCompletedOffset) throw Invalid();
        TrialVisibilityJob source = capture.Jobs.Single(row => row.JobId == ownership.JobId);
        if (source != executionBefore.Activity || source != executionAfter.Activity) throw Invalid();
        if (running) { RequireRunning(ownership, executionBefore); RequireRunning(ownership, executionAfter); }
        else { RequireCompleted(ownership, executionBefore); RequireCompleted(ownership, executionAfter); }
        return result;
    }

    private static void RequireRunning(RunningJobOwnership ownership, RunningJobExecutionWitness execution)
    {
        ValidateExecutionWitness(ownership, execution);
        if (execution.StepSessionId is null || execution.StepSessionId == ownership.Baseline.SessionId || !execution.LiveOwnedRequest || execution.Released ||
            execution.StepStartedUtc is null || execution.StepCompletedUtc is not null || execution.SuccessfulHistory ||
            execution.Activity is not { StartExecutionLocal: not null, StopExecutionLocal: null }) throw Invalid();
    }
    private static void RequireCompleted(RunningJobOwnership ownership, RunningJobExecutionWitness execution)
    {
        ValidateExecutionWitness(ownership, execution);
        if (execution.StepSessionId is null || execution.LiveOwnedRequest || !execution.Released || execution.StepCompletedUtc is null ||
            !execution.SuccessfulHistory || execution.Activity is not { StartExecutionLocal: not null, StopExecutionLocal: not null }) throw Invalid();
    }

    private static async Task RequireOwnedAsync(SqlConnection connection, RunningJobOwnership ownership, CancellationToken token, bool allowDatabaseOnly = false)
    {
        ValidateOwnership(ownership, allowDatabaseOnly);
        await RecheckBaselineAsync(connection, ownership.Baseline, ownership.JobId, token).ConfigureAwait(false);
        RunningJobOwnership current = await ReadOwnershipAsync(connection, ownership.Baseline, ownership.JobId, ownership.DatabaseMarker, token).ConfigureAwait(false);
        if (!SameOwnership(current, ownership)) throw Invalid();
    }
    private static async Task RequireDatabaseAsync(SqlConnection connection, RunningJobOwnership ownership, CancellationToken token)
    {
        RunningJobOwnership current = await ReadOwnershipAsync(connection, ownership.Baseline, Guid.Empty, ownership.DatabaseMarker, token).ConfigureAwait(false);
        if (current.DatabaseId != ownership.DatabaseId || current.DatabaseGuid != ownership.DatabaseGuid || current.DatabaseDefinitionSha256 != ownership.DatabaseDefinitionSha256) throw Invalid();
    }

    private static async Task RunningGuardedEpochAsync(SqlConnection connection, RunningJobBaseline baseline, Guid ownedJobId, RunningJobAgentEpoch epoch, CancellationToken token)
    {
        string expectedService = baseline.ModuleBaseline.Snapshot.Identity.InstanceName.Length == 0 ? "SQLSERVERAGENT" : "SQLAgent$" + baseline.ModuleBaseline.Snapshot.Identity.InstanceName;
        if (epoch.ServiceName != expectedService || epoch.ProcessId <= 0 || epoch.ProcessStartedUtc.Offset != TimeSpan.Zero ||
            epoch.ServiceStartupUtc.Offset != TimeSpan.Zero || epoch.AgentSessionId <= baseline.OriginalAgentSessionId) throw Invalid();
        await ExecuteAsync(connection, baseline, ownedJobId, "IF NOT EXISTS(SELECT 1 FROM sys.dm_server_services WHERE " +
            "servicename=N'SQL Server Agent ('+COALESCE(CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),N'MSSQLSERVER')+N')' " +
            "AND status=4 AND process_id=@agent_pid AND last_startup_time=@startup) OR " +
            "(SELECT MAX(session_id) FROM msdb.dbo.syssessions)<>@agent_session OR NOT EXISTS(SELECT 1 FROM msdb.dbo.syssessions " +
            "WHERE session_id=@agent_session AND agent_start_date=@agent_start) THROW 51000,'Owned epoch changed.',1;", command =>
        {
            command.Parameters.Add("@agent_pid", SqlDbType.Int).Value = epoch.ProcessId;
            command.Parameters.Add("@startup", SqlDbType.DateTimeOffset).Value = epoch.ServiceStartupUtc;
            command.Parameters.Add("@agent_session", SqlDbType.Int).Value = epoch.AgentSessionId;
            command.Parameters.Add("@agent_start", SqlDbType.DateTime).Value = epoch.AgentStartedLocal;
        }, token).ConfigureAwait(false);
    }
    private static Task RequireEpochAsync(SqlConnection connection, RunningJobBaseline baseline, Guid ownedJobId, RunningJobAgentEpoch epoch, CancellationToken token)
        => RunningGuardedEpochAsync(connection, baseline, ownedJobId, epoch, token);

    private static async Task<RunningJobOwnership> ReadOwnershipAsync(SqlConnection connection, RunningJobBaseline baseline,
        Guid jobId, string marker, CancellationToken token, SqlTransaction? transaction = null)
    {
        RequirePhysicalConnection(connection, baseline);
        string sql = GuardSql + BuildOwnershipSql(baseline);
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 10 };
        AddParameters(command, baseline, jobId); command.Parameters.Add("@marker", SqlDbType.NVarChar, 256).Value = marker;
        using JsonDocument document = await ReadJsonAsync(connection, command, baseline.ConnectionId, token).ConfigureAwait(false);
        JsonElement row = document.RootElement;
        if (row.GetProperty("OwnerSidHex").GetString() != baseline.OwnerSidHex || row.GetProperty("Marker").GetString() != marker) throw Invalid();
        string databaseHash = Digest(row.GetProperty("DatabaseDefinition"));
        string jobHash = jobId == Guid.Empty ? "" : Digest(row.GetProperty("JobDefinition"));
        string stepHash = jobId == Guid.Empty ? "" : TextDigest(row.GetProperty("Command").GetString()!);
        return new(baseline, row.GetProperty("DatabaseId").GetInt32(), row.GetProperty("DatabaseGuid").GetGuid(), marker, databaseHash, jobId, jobHash, stepHash);
    }

    private static async Task<string> CaptureInventoryAsync(SqlConnection connection, RunningJobBaseline baseline, Guid ownedJobId, CancellationToken token)
    {
        RequirePhysicalConnection(connection, baseline);
        await using var command = new SqlCommand(GuardSql + InventorySql, connection) { CommandTimeout = 10 };
        AddParameters(command, baseline, ownedJobId);
        using JsonDocument document = await ReadJsonAsync(connection, command, baseline.ConnectionId, token).ConfigureAwait(false);
        RequireKeys(document.RootElement, InventoryKeys);
        return document.RootElement.GetRawText();
    }
    private static async Task ExecuteAsync(SqlConnection connection, RunningJobBaseline baseline, Guid jobId, string sql,
        Action<SqlCommand>? configure, CancellationToken token)
    {
        RequirePhysicalConnection(connection, baseline);
        await using var command = new SqlCommand(GuardSql + "IF @@TRANCOUNT<>0 THROW 51000,'Autocommit required.',1;" + sql, connection) { CommandTimeout = 10 };
        AddParameters(command, baseline, jobId); configure?.Invoke(command);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false); }
        catch (SqlException error) { throw new TrialSqlMetadataException("running_job_action", error); }
        RequirePhysicalConnection(connection, baseline);
    }
    private static async Task<JsonDocument> ReadJsonAsync(SqlConnection connection, SqlCommand command, Guid connectionId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (reader.FieldCount != 1 || reader.GetFieldType(0) != typeof(string) || !await reader.ReadAsync(deadline.Token).ConfigureAwait(false) || reader.IsDBNull(0)) throw Invalid();
            string json = reader.GetString(0);
            if (Encoding.UTF8.GetByteCount(json) > MaximumInventoryBytes || await reader.ReadAsync(deadline.Token).ConfigureAwait(false) ||
                await reader.NextResultAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
            RequireConnection(connection); if (connection.ClientConnectionId != connectionId) throw Invalid();
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (SqlException error) { throw new TrialSqlMetadataException("running_job_inventory", error); }
    }
    private static void AddParameters(SqlCommand command, RunningJobBaseline baseline, Guid jobId)
    {
        command.Parameters.Add("@spid", SqlDbType.Int).Value = baseline.SessionId;
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 128).Value = baseline.OwnerPrincipal;
        command.Parameters.Add("@owner_sid", SqlDbType.VarBinary, 85).Value = Convert.FromHexString(baseline.OwnerSidHex);
        command.Parameters.Add("@owner_id", SqlDbType.Int).Value = baseline.OwnerPrincipalId;
        command.Parameters.Add("@job", SqlDbType.UniqueIdentifier).Value = jobId;
        command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = baseline.DatabaseName;
        command.Parameters.Add("@job_name", SqlDbType.NVarChar, 128).Value = baseline.JobName;
        command.Parameters.Add("@nonce", SqlDbType.UniqueIdentifier).Value = baseline.ExecutionNonce;
        command.Parameters.Add("@old_session", SqlDbType.Int).Value = baseline.OriginalAgentSessionId;
        command.Parameters.Add("@foreign_count", SqlDbType.Int).Value = baseline.ForeignJobCount;
        var identity = baseline.ModuleBaseline.Snapshot.Identity;
        command.Parameters.Add("@server", SqlDbType.NVarChar, 128).Value = identity.ServerName;
        command.Parameters.Add("@machine", SqlDbType.NVarChar, 128).Value = identity.MachineName;
        command.Parameters.Add("@instance", SqlDbType.NVarChar, 128).Value = identity.InstanceName;
        command.Parameters.Add("@version", SqlDbType.NVarChar, 128).Value = identity.ProductVersion;
        command.Parameters.Add("@master_guid", SqlDbType.UniqueIdentifier).Value = identity.MasterDatabaseGuid;
        command.Parameters.Add("@msdb_guid", SqlDbType.UniqueIdentifier).Value = identity.MsdbDatabaseGuid;
    }
    private static RunningJobReceipt RequireJournal(TrialRunningJobJournal journal, RunningJobBaseline baseline, string phase)
    {
        ArgumentNullException.ThrowIfNull(journal);
        RunningJobReceipt receipt = journal.Read();
        if (receipt.Phase != phase || JsonSerializer.Serialize(receipt.Baseline, JsonOptions) != JsonSerializer.Serialize(baseline, JsonOptions)) throw Invalid();
        return receipt;
    }
    private static void RequireConnection(SqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open || connection.Database != "master" || connection.ClientConnectionId == Guid.Empty) throw Invalid();
        var settings = new SqlConnectionStringBuilder(connection.ConnectionString);
        if (!settings.IntegratedSecurity || settings.TrustServerCertificate || settings.ConnectRetryCount != 0 || settings.Pooling ||
            settings.MultipleActiveResultSets || settings.Encrypt == SqlConnectionEncryptOption.Optional || settings.InitialCatalog != "master" ||
            !settings.DataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(settings.HostNameInCertificate)) throw Invalid();
    }
    private static void RequirePhysicalConnection(SqlConnection connection, RunningJobBaseline baseline)
    { RequireConnection(connection); if (connection.ClientConnectionId != baseline.ConnectionId) throw Invalid(); }
    private static string DefinitionsDigest(JsonElement inventory) => TextDigest(string.Join('\n', DefinitionKeys.Select(name => inventory.GetProperty(name).GetRawText())));
    private static string Digest(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
    private static string TextDigest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(value)));
    private static bool ValidHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool SameOwnership(RunningJobOwnership? first, RunningJobOwnership second) => first is not null && JsonSerializer.Serialize(first, JsonOptions) == JsonSerializer.Serialize(second, JsonOptions);
    private static T Deserialize<T>(JsonElement value) where T : class => JsonSerializer.Deserialize<T>(value.GetRawText(), JsonOptions) ?? throw Invalid();
    private static DateTimeOffset AsUtc(DateTime value) => value.Kind != DateTimeKind.Local && value.Year >= 1753 ? new(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : throw Invalid();
    private static string Quote(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private static void RequireKeys(JsonElement value, string[] keys)
    { if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal))) throw Invalid(); }
    private static InvalidDataException Invalid() => new("The finite running-job SQL witness or owned action is invalid; private values withheld.");
    private static readonly string[] InventoryKeys = ["jobs", "steps", "schedules", "attachments", "jobServers", "agentConfiguration", "permissions", "oldSessions", "oldActivity", "foreignHistory"];
    private static readonly string[] DefinitionKeys = ["jobs", "steps", "schedules", "attachments", "jobServers", "agentConfiguration", "foreignHistory"];

    private static string BuildDatabaseSql(RunningJobBaseline baseline, string marker) => "USE " + Quote(baseline.DatabaseName) + ";\n" +
        "ALTER DATABASE " + Quote(baseline.DatabaseName) + " SET RECOVERY SIMPLE; ALTER DATABASE " + Quote(baseline.DatabaseName) + " SET AUTO_CLOSE OFF; " +
        "ALTER DATABASE " + Quote(baseline.DatabaseName) + " SET AUTO_SHRINK OFF; ALTER DATABASE " + Quote(baseline.DatabaseName) + " SET TRUSTWORTHY OFF; " +
        "ALTER DATABASE " + Quote(baseline.DatabaseName) + " SET DB_CHAINING OFF;\n" +
        "EXEC sys.sp_addextendedproperty @name=N'SqlObserver.RunningFixture',@value=N'" + marker + "';\n" +
        "CREATE TABLE dbo.RunningJobWitness(singleton int NOT NULL CONSTRAINT PK_RunningJobWitness PRIMARY KEY CHECK(singleton=1), " +
        "nonce uniqueidentifier NOT NULL,job_id uniqueidentifier NOT NULL,step_spid int NULL,released bit NOT NULL, " +
        "started_utc datetime2(7) NULL,started_local datetime2(7) NULL,started_offset datetimeoffset(7) NULL, " +
        "completed_utc datetime2(7) NULL,completed_local datetime2(7) NULL,completed_offset datetimeoffset(7) NULL);\n" +
        "INSERT dbo.RunningJobWitness(singleton,nonce,job_id,released) VALUES(1,'" + baseline.ExecutionNonce.ToString("D") + "','00000000-0000-0000-0000-000000000000',0);";

    private static string BuildExecutionSql(RunningJobOwnership ownership) => """
        DECLARE @agent int,@agent_start datetime;
        SELECT TOP(1) @agent=session_id,@agent_start=agent_start_date FROM msdb.dbo.syssessions ORDER BY session_id DESC;
        IF @agent<=@old_session OR @agent IS NULL THROW 51000,'New Agent session missing.',1;
        """ + "SELECT (SELECT w.step_spid AS StepSessionId,SYSUTCDATETIME() AS ObservedUtc,w.started_utc AS StepStartedUtc," +
        "w.started_local AS StepStartedLocal,w.started_offset AS StepStartedOffset,w.released AS Released," +
        "w.completed_utc AS StepCompletedUtc,w.completed_local AS StepCompletedLocal,w.completed_offset AS StepCompletedOffset," +
        "CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id " +
        "WHERE r.session_id=w.step_spid AND r.database_id=" + ownership.DatabaseId + " AND s.is_user_process=1) THEN 1 ELSE 0 END) AS LiveOwnedRequest," +
        "@agent AS AgentSessionId,@agent_start AS AgentStartedLocal,JSON_QUERY((SELECT a.job_id AS JobId,a.session_id AS AgentSessionId," +
        "a.run_requested_date AS RequestedLocal,a.queued_date AS QueuedLocal,a.start_execution_date AS StartExecutionLocal," +
        "a.stop_execution_date AS StopExecutionLocal,a.last_executed_step_id AS LastExecutedStepId,a.last_executed_step_date AS LastExecutedStepLocal " +
        "FROM msdb.dbo.sysjobactivity a WHERE a.job_id=@job AND a.session_id=@agent FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) AS Activity," +
        "CONVERT(bit,CASE WHEN (SELECT COUNT(*) FROM msdb.dbo.sysjobhistory WHERE job_id=@job AND step_id=0)=1 " +
        "AND EXISTS(SELECT 1 FROM msdb.dbo.sysjobhistory WHERE job_id=@job AND step_id=0 AND run_status=1) " +
        "AND (SELECT COUNT(*) FROM msdb.dbo.sysjobhistory WHERE job_id=@job AND step_id=1)=1 " +
        "AND EXISTS(SELECT 1 FROM msdb.dbo.sysjobhistory WHERE job_id=@job AND step_id=1 AND run_status=1) THEN 1 ELSE 0 END) AS SuccessfulHistory " +
        "FROM " + Quote(ownership.Baseline.DatabaseName) + ".dbo.RunningJobWitness w WHERE w.singleton=1 AND w.nonce=@nonce AND w.job_id=@job " +
        "FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) AS witness_json;";

    private static string BuildOwnershipSql(RunningJobBaseline baseline) => """
        IF DB_ID(@db)<=4 OR DB_ID(@db) IS NULL OR NOT EXISTS(SELECT 1 FROM sys.databases WHERE name=@db AND owner_sid=@owner_sid AND
          state=0 AND is_read_only=0 AND user_access=0 AND is_auto_close_on=0 AND is_auto_shrink_on=0 AND is_trustworthy_on=0 AND
          is_db_chaining_on=0 AND containment=0 AND recovery_model=3) THROW 51000,'Owned database changed.',1;
        DECLARE @metadata nvarchar(max),@found_marker nvarchar(256);
        """ + "EXEC sys.sp_executesql N'USE " + Quote(baseline.DatabaseName) + "; " +
        "SELECT @found_marker=CONVERT(nvarchar(256),value) FROM sys.extended_properties WHERE class=0 AND name=N''SqlObserver.RunningFixture''; " +
        "IF (SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0)<>1 OR OBJECT_ID(N''dbo.RunningJobWitness'',N''U'') IS NULL " +
        "OR EXISTS(SELECT 1 FROM sys.objects WHERE is_ms_shipped=0 AND type IN(''P'',''V'',''FN'',''IF'',''TF'',''TR'',''AF'',''SN'')) " +
        "THROW 51000,''Owned schema changed.'',1; " +
        "SELECT @metadata=(SELECT JSON_QUERY((SELECT * FROM sys.objects WHERE is_ms_shipped=0 ORDER BY object_id FOR JSON PATH,INCLUDE_NULL_VALUES)) objects," +
        "JSON_QUERY((SELECT * FROM sys.columns WHERE object_id=OBJECT_ID(N''dbo.RunningJobWitness'') ORDER BY column_id FOR JSON PATH,INCLUDE_NULL_VALUES)) columns," +
        "JSON_QUERY((SELECT * FROM sys.indexes WHERE object_id=OBJECT_ID(N''dbo.RunningJobWitness'') ORDER BY index_id FOR JSON PATH,INCLUDE_NULL_VALUES)) indexes," +
        "JSON_QUERY((SELECT * FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N''dbo.RunningJobWitness'') ORDER BY object_id FOR JSON PATH,INCLUDE_NULL_VALUES)) checks," +
        "JSON_QUERY((SELECT * FROM sys.database_permissions ORDER BY class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type FOR JSON PATH,INCLUDE_NULL_VALUES)) permissions," +
        "JSON_QUERY((SELECT * FROM sys.database_principals ORDER BY principal_id FOR JSON PATH,INCLUDE_NULL_VALUES)) principals " +
        "FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);',N'@metadata nvarchar(max) OUTPUT,@found_marker nvarchar(256) OUTPUT',@metadata OUTPUT,@found_marker OUTPUT; " +
        "IF @found_marker IS NULL OR @found_marker<>@marker THROW 51000,'Owned marker changed.',1;\n" +
        "DECLARE @job_metadata nvarchar(max)=NULL,@command nvarchar(max)=NULL; IF @job<>'00000000-0000-0000-0000-000000000000' BEGIN\n" +
        "IF NOT EXISTS(SELECT 1 FROM msdb.dbo.sysjobs WHERE job_id=@job AND name COLLATE Latin1_General_100_BIN2=@job_name COLLATE Latin1_General_100_BIN2 " +
        "AND owner_sid=@owner_sid AND enabled=1 AND start_step_id=1 AND description=@marker AND notify_level_eventlog=0 AND notify_level_email=0 AND notify_level_netsend=0 AND notify_level_page=0 AND delete_level=0) " +
        "OR EXISTS(SELECT 1 FROM msdb.dbo.sysjobschedules WHERE job_id=@job) OR (SELECT COUNT(*) FROM msdb.dbo.sysjobservers WHERE job_id=@job)<>1 " +
        "OR NOT EXISTS(SELECT 1 FROM msdb.dbo.sysjobservers WHERE job_id=@job AND server_id=0) OR (SELECT COUNT(*) FROM msdb.dbo.sysjobsteps WHERE job_id=@job)<>1 " +
        "OR NOT EXISTS(SELECT 1 FROM msdb.dbo.sysjobsteps WHERE job_id=@job AND step_id=1 AND step_name=N'Owned bounded witness' AND subsystem=N'TSQL' " +
        "AND database_name=@db AND database_user_name IS NULL AND retry_attempts=0 AND retry_interval=0 AND on_success_action=1 AND on_fail_action=2 " +
        "AND on_success_step_id=0 AND on_fail_step_id=0 AND flags=0 AND proxy_id=0 AND (output_file_name IS NULL OR output_file_name=N'')) " +
        "THROW 51000,'Owned job definition changed.',1;\n" +
        "SELECT @command=command FROM msdb.dbo.sysjobsteps WHERE job_id=@job AND step_id=1; " +
        "SELECT @job_metadata=(SELECT JSON_QUERY((SELECT * FROM msdb.dbo.sysjobs WHERE job_id=@job FOR JSON PATH,INCLUDE_NULL_VALUES)) job," +
        "JSON_QUERY((SELECT job_id,step_id,step_name,subsystem,command,flags,additional_parameters,cmdexec_success_code,on_success_action,on_success_step_id," +
        "on_fail_action,on_fail_step_id,server,database_name,database_user_name,retry_attempts,retry_interval,os_run_priority,output_file_name,proxy_id,step_uid " +
        "FROM msdb.dbo.sysjobsteps WHERE job_id=@job ORDER BY step_id FOR JSON PATH,INCLUDE_NULL_VALUES)) steps," +
        "JSON_QUERY((SELECT job_id,server_id FROM msdb.dbo.sysjobservers WHERE job_id=@job ORDER BY server_id FOR JSON PATH,INCLUDE_NULL_VALUES)) attachments " +
        "FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER); END;\n" +
        "SELECT (SELECT d.database_id AS DatabaseId,r.database_guid AS DatabaseGuid,LOWER(CONVERT(varchar(170),d.owner_sid,2)) AS OwnerSidHex,@found_marker AS Marker," +
        "JSON_QUERY((SELECT d.name,d.database_id,d.owner_sid,d.collation_name,d.compatibility_level,d.recovery_model,d.user_access,d.is_auto_close_on,d.is_auto_shrink_on," +
        "d.is_trustworthy_on,d.is_db_chaining_on,d.containment,JSON_QUERY(@metadata) schemaMetadata FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) AS DatabaseDefinition," +
        "JSON_QUERY(@job_metadata) AS JobDefinition,@command AS Command FROM sys.databases d JOIN sys.database_recovery_status r ON r.database_id=d.database_id " +
        "WHERE d.name=@db FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) AS ownership_json;";

    private const string JobCreateSql = """
        SET NOCOUNT ON;
        IF @@SPID<>@spid OR IS_SRVROLEMEMBER(N'sysadmin')<>1 OR SUSER_SID()<>@owner_sid OR
          APPLOCK_MODE(N'public',N'SqlObserver.AgentActivityRunningJob',N'Session')<>N'Exclusive' OR
          EXISTS(SELECT 1 FROM msdb.dbo.sysjobs WHERE name=@job_name) OR
          EXISTS(SELECT 1 FROM msdb.dbo.sysjobs WHERE enabled<>0) OR (SELECT COUNT(*) FROM msdb.dbo.sysjobs)<>@foreign_count
          THROW 51000,'Owned creation guard refused.',1;
        DECLARE @rc int;
        EXEC @rc=msdb.dbo.sp_add_job @job_name=@job_name,@enabled=1,@description=@marker,@start_step_id=1,
          @owner_login_name=@owner,@notify_level_eventlog=0,@notify_level_email=0,@notify_level_netsend=0,@notify_level_page=0,
          @delete_level=0,@job_id=@new_job OUTPUT;
        IF @rc<>0 OR @new_job IS NULL THROW 51000,'Owned job create failed.',1;
        DECLARE @update nvarchar(max)=N'USE '+QUOTENAME(@db)+N'; UPDATE dbo.RunningJobWitness SET job_id=@new WHERE singleton=1 AND nonce=@nonce AND
          job_id=''00000000-0000-0000-0000-000000000000'' AND step_spid IS NULL AND released=0; IF @@ROWCOUNT<>1 THROW 51000,''Owned row changed.'',1;';
        EXEC sys.sp_executesql @update,N'@new uniqueidentifier,@nonce uniqueidentifier',@new_job,@nonce;
        """;
    private const string JobStepSql = """
        SET NOCOUNT ON; DECLARE @rc int;
        EXEC @rc=msdb.dbo.sp_add_jobstep @job_id=@job,@step_id=1,@step_name=N'Owned bounded witness',@subsystem=N'TSQL',
          @command=@command,@database_name=@db,@on_success_action=1,@on_fail_action=2,@retry_attempts=0,@retry_interval=0,@flags=0;
        IF @rc<>0 THROW 51000,'Owned step create failed.',1;
        EXEC @rc=msdb.dbo.sp_add_jobserver @job_id=@job,@server_name=N'(LOCAL)';
        IF @rc<>0 THROW 51000,'Owned LOCAL attachment failed.',1;
        """;

    private const string BaselineSql = """
        SET NOCOUNT ON;
        IF @@TRANCOUNT<>0 OR IS_SRVROLEMEMBER(N'sysadmin')<>1 OR IS_SRVROLEMEMBER(N'sysadmin') IS NULL OR
          ORIGINAL_LOGIN()<>SUSER_SNAME() OR CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))<>17 OR
          CONVERT(int,SERVERPROPERTY('EngineEdition')) NOT IN(2,3) OR CONVERT(nvarchar(1),SERVERPROPERTY('PathSeparator'))<>N'\'
          THROW 51000,'Unsupported running-job baseline.',1;
        IF NOT EXISTS(SELECT 1 FROM sys.server_principals WHERE principal_id=SUSER_ID() AND sid=SUSER_SID() AND type IN('U','S') AND is_disabled=0)
          OR SUSER_SID()=@monitor_sid THROW 51000,'Existing DBA owner required.',1;
        IF DB_ID(@db) IS NOT NULL OR EXISTS(SELECT 1 FROM sys.databases WHERE name LIKE N'SqlObserverRunning[_]%') OR
          EXISTS(SELECT 1 FROM msdb.dbo.sysjobs WHERE name=@job_name OR name LIKE N'SqlObserver Running %') OR
          EXISTS(SELECT 1 FROM msdb.dbo.sysjobs WHERE enabled<>0) OR (SELECT COUNT_BIG(*) FROM msdb.dbo.sysjobs)>511 OR
          EXISTS(SELECT 1 FROM msdb.dbo.sysjobactivity WHERE (run_requested_date IS NOT NULL OR queued_date IS NOT NULL OR
            start_execution_date IS NOT NULL) AND stop_execution_date IS NULL) THROW 51000,'Foreign fixture is unsafe.',1;
        IF (SELECT COUNT(*) FROM sys.dm_server_services WHERE
          servicename=N'SQL Server Agent ('+COALESCE(CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),N'MSSQLSERVER')+N')' AND
          status=1 AND (process_id=0 OR process_id IS NULL) AND startup_type IN(2,3) AND is_clustered=N'N')<>1 THROW 51000,'Stopped target Agent required.',1;
        DECLARE @lock int;
        EXEC @lock=sys.sp_getapplock @Resource=N'SqlObserver.AgentActivityRunningJob',@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0;
        IF @lock<0 THROW 51000,'Running-job session lock refused.',1;
        DECLARE @agent int,@agent_start datetime;
        SELECT TOP(1) @agent=session_id,@agent_start=agent_start_date FROM msdb.dbo.syssessions ORDER BY session_id DESC;
        IF @agent IS NULL OR @agent_start IS NULL THROW 51000,'Original Agent history required.',1;
        SELECT (SELECT JSON_QUERY((SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) ServerName,
          CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) MachineName,COALESCE(CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),N'') InstanceName,
          CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')) ProductVersion,CONVERT(int,SERVERPROPERTY('ProductMajorVersion')) MajorVersion,
          CONVERT(int,SERVERPROPERTY('EngineEdition')) EngineEdition,CONVERT(nvarchar(1),SERVERPROPERTY('PathSeparator')) PathSeparator,
          (SELECT database_guid FROM sys.database_recovery_status WHERE database_id=1) MasterDatabaseGuid,
          (SELECT database_guid FROM sys.database_recovery_status WHERE database_id=4) MsdbDatabaseGuid
          FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) Identity,CONVERT(int,@@SPID) SessionId,SUSER_SNAME() OwnerPrincipal,
          LOWER(CONVERT(varchar(170),SUSER_SID(),2)) OwnerSidHex,SUSER_ID() OwnerPrincipalId,@agent AgentSessionId,@agent_start AgentStartedLocal,
          (SELECT COUNT(*) FROM msdb.dbo.sysjobs) ForeignJobCount FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) AS baseline_json;
        """;

    private const string GuardSql = """
        SET NOCOUNT ON;
        IF DB_NAME()<>N'master' OR @@SPID<>@spid OR IS_SRVROLEMEMBER(N'sysadmin')<>1 OR IS_SRVROLEMEMBER(N'sysadmin') IS NULL OR
          ORIGINAL_LOGIN()<>SUSER_SNAME() OR SUSER_SID()<>@owner_sid OR SUSER_ID()<>@owner_id OR
          SUSER_SNAME() COLLATE Latin1_General_100_BIN2<>@owner COLLATE Latin1_General_100_BIN2 OR
          CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))<>17 OR CONVERT(int,SERVERPROPERTY('EngineEdition')) NOT IN(2,3) OR
          CONVERT(nvarchar(1),SERVERPROPERTY('PathSeparator'))<>N'\' OR
          CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) COLLATE Latin1_General_100_BIN2<>@server COLLATE Latin1_General_100_BIN2 OR
          CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) COLLATE Latin1_General_100_BIN2<>@machine COLLATE Latin1_General_100_BIN2 OR
          COALESCE(CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),N'') COLLATE Latin1_General_100_BIN2<>@instance COLLATE Latin1_General_100_BIN2 OR
          CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion'))<>@version OR
          (SELECT database_guid FROM sys.database_recovery_status WHERE database_id=1)<>@master_guid OR
          (SELECT database_guid FROM sys.database_recovery_status WHERE database_id=4)<>@msdb_guid OR
          APPLOCK_MODE(N'public',N'SqlObserver.AgentActivityRunningJob',N'Session')<>N'Exclusive' OR
          NOT EXISTS(SELECT 1 FROM sys.server_principals WHERE principal_id=@owner_id AND sid=@owner_sid AND type IN('U','S') AND is_disabled=0)
          THROW 51000,'Running-job physical owner changed.',1;
        IF (SELECT COUNT(*) FROM msdb.dbo.sysjobs WHERE job_id<>@job)<>@foreign_count OR @foreign_count>511 OR
          EXISTS(SELECT 1 FROM msdb.dbo.sysjobs WHERE job_id<>@job AND enabled<>0) OR
          EXISTS(SELECT 1 FROM msdb.dbo.sysjobactivity WHERE job_id<>@job AND (run_requested_date IS NOT NULL OR queued_date IS NOT NULL OR
            start_execution_date IS NOT NULL) AND stop_execution_date IS NULL) OR
          EXISTS(SELECT 1 FROM sys.databases WHERE name LIKE N'SqlObserverRunning[_]%' AND name<>@db)
          THROW 51000,'Foreign running-job state changed.',1;
        """;

    private const string InventorySql = """
        SELECT (SELECT
          JSON_QUERY((SELECT * FROM msdb.dbo.sysjobs WHERE job_id<>@job ORDER BY CONVERT(char(36),job_id) COLLATE Latin1_General_100_BIN2
            FOR JSON PATH,INCLUDE_NULL_VALUES)) jobs,
          JSON_QUERY((SELECT * FROM msdb.dbo.sysjobsteps WHERE job_id<>@job ORDER BY CONVERT(char(36),job_id) COLLATE Latin1_General_100_BIN2,step_id
            FOR JSON PATH,INCLUDE_NULL_VALUES)) steps,
          JSON_QUERY((SELECT * FROM msdb.dbo.sysschedules ORDER BY schedule_id FOR JSON PATH,INCLUDE_NULL_VALUES)) schedules,
          JSON_QUERY((SELECT job_id,schedule_id FROM msdb.dbo.sysjobschedules WHERE job_id<>@job ORDER BY CONVERT(char(36),job_id) COLLATE Latin1_General_100_BIN2,schedule_id
            FOR JSON PATH,INCLUDE_NULL_VALUES)) attachments,
          JSON_QUERY((SELECT job_id,server_id FROM msdb.dbo.sysjobservers WHERE job_id<>@job ORDER BY CONVERT(char(36),job_id) COLLATE Latin1_General_100_BIN2,server_id
            FOR JSON PATH,INCLUDE_NULL_VALUES)) jobServers,
          JSON_QUERY((SELECT
            JSON_QUERY((SELECT servicename,startup_type,service_account,filename,is_clustered,cluster_nodename FROM sys.dm_server_services ORDER BY servicename FOR JSON PATH,INCLUDE_NULL_VALUES)) services,
            JSON_QUERY((SELECT * FROM msdb.dbo.syscategories ORDER BY category_id FOR JSON PATH,INCLUDE_NULL_VALUES)) categories,
            JSON_QUERY((SELECT * FROM msdb.dbo.sysoperators ORDER BY id FOR JSON PATH,INCLUDE_NULL_VALUES)) operators,
            JSON_QUERY((SELECT * FROM msdb.dbo.sysalerts ORDER BY id FOR JSON PATH,INCLUDE_NULL_VALUES)) alerts,
            JSON_QUERY((SELECT * FROM msdb.dbo.sysproxies ORDER BY proxy_id FOR JSON PATH,INCLUDE_NULL_VALUES)) proxies,
            JSON_QUERY((SELECT * FROM msdb.dbo.sysproxylogin ORDER BY proxy_id,sid FOR JSON PATH,INCLUDE_NULL_VALUES)) proxyLogins,
            JSON_QUERY((SELECT * FROM msdb.dbo.sysproxysubsystem ORDER BY proxy_id,subsystem_id FOR JSON PATH,INCLUDE_NULL_VALUES)) proxySubsystems,
            JSON_QUERY((SELECT * FROM msdb.dbo.syssubsystems ORDER BY subsystem_id FOR JSON PATH,INCLUDE_NULL_VALUES)) subsystems,
            JSON_QUERY((SELECT name,value,value_in_use FROM sys.configurations ORDER BY configuration_id FOR JSON PATH,INCLUDE_NULL_VALUES)) configurations
            FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) agentConfiguration,
          JSON_QUERY((SELECT
            JSON_QUERY((SELECT * FROM master.sys.server_principals ORDER BY principal_id FOR JSON PATH,INCLUDE_NULL_VALUES)) serverPrincipals,
            JSON_QUERY((SELECT * FROM master.sys.server_permissions ORDER BY class,major_id,grantee_principal_id,grantor_principal_id,type FOR JSON PATH,INCLUDE_NULL_VALUES)) serverPermissions,
            JSON_QUERY((SELECT * FROM master.sys.server_role_members ORDER BY role_principal_id,member_principal_id FOR JSON PATH,INCLUDE_NULL_VALUES)) serverRoles,
            JSON_QUERY((SELECT * FROM master.sys.database_principals ORDER BY principal_id FOR JSON PATH,INCLUDE_NULL_VALUES)) masterPrincipals,
            JSON_QUERY((SELECT * FROM master.sys.database_permissions ORDER BY class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type FOR JSON PATH,INCLUDE_NULL_VALUES)) masterPermissions,
            JSON_QUERY((SELECT * FROM master.sys.database_role_members ORDER BY role_principal_id,member_principal_id FOR JSON PATH,INCLUDE_NULL_VALUES)) masterRoles,
            JSON_QUERY((SELECT * FROM msdb.sys.database_principals ORDER BY principal_id FOR JSON PATH,INCLUDE_NULL_VALUES)) msdbPrincipals,
            JSON_QUERY((SELECT * FROM msdb.sys.database_permissions ORDER BY class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type FOR JSON PATH,INCLUDE_NULL_VALUES)) msdbPermissions,
            JSON_QUERY((SELECT * FROM msdb.sys.database_role_members ORDER BY role_principal_id,member_principal_id FOR JSON PATH,INCLUDE_NULL_VALUES)) msdbRoles
            FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER)) permissions,
          JSON_QUERY((SELECT * FROM msdb.dbo.syssessions WHERE session_id<=@old_session ORDER BY session_id FOR JSON PATH,INCLUDE_NULL_VALUES)) oldSessions,
          JSON_QUERY((SELECT * FROM msdb.dbo.sysjobactivity WHERE session_id<=@old_session AND job_id<>@job ORDER BY session_id,CONVERT(char(36),job_id) COLLATE Latin1_General_100_BIN2
            FOR JSON PATH,INCLUDE_NULL_VALUES)) oldActivity,
          JSON_QUERY((SELECT * FROM msdb.dbo.sysjobhistory WHERE job_id<>@job ORDER BY instance_id FOR JSON PATH,INCLUDE_NULL_VALUES)) foreignHistory
        FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) AS inventory_json;
        """;
}
