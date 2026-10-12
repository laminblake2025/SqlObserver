using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Explicit isolated module trial. No production adapter, direct permission grant,
/// signature attestation, running-job admission or service/session qualification is inferred.</summary>
[Trait("Category", "RequiresSqlServer")]
[Trait("Category", "RequiresSqlAgentActivitySignedModule")]
public sealed class SqlAgentActivitySignedModuleQualificationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] InvalidBounds = ["NULL", "512", "514"];
    private const string ModuleExec = "EXEC master.sqlobserver_agent.capture_v1 @maximum_rows=@maximum_rows;";

    [Fact]
    public Task ActualMonitorCapturesSignedModuleThroughProductionParser() => Safely(async token =>
    {
        var context = await PrepareAsync(token);
        string query = IdentityPrefix(context) + BaselineSql + ModuleExec + IdentitySql;
        long started = Stopwatch.GetTimestamp();
        await using var session = await new SqlAgentActivitySource().OpenAsync(context.Request, query, TimeSpan.FromSeconds(10), token);
        session.RequireConnection(); var reader = session.Reader;
        var before = await IdentityAsync(reader, token);
        Assert.True(await reader.NextResultAsync(token));
        int[] permissions = await IntegersAsync(reader, 3, token);
        Assert.Equal(0, permissions[0]); Assert.Equal(0, permissions[1]);
        Assert.InRange(permissions[2], -1, 1);
        Assert.True(await reader.NextResultAsync(token));
        Assert.Equal(229, (await IntegersAsync(reader, 1, token))[0]);
        Assert.True(await reader.NextResultAsync(token));
        // Only the module's three result sets are passed to the production parser.
        // The two identity envelopes are inspected independently and never become source rows.
        var window = new CaptureWindow(reader);
        var capture = await SqlServerAgentActivityCaptureReader.ParseAsync(context.Request, window, token);
        Assert.Equal(3, window.CompletedSets);
        Assert.True(await reader.NextResultAsync(token));
        var after = await IdentityAsync(reader, token);
        Assert.False(await reader.NextResultAsync(token));
        session.RequireConnection(); Assert.Equal(before, after);
        token.ThrowIfCancellationRequested(); Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
        Assert.Equal(context.Request.TargetId, capture.TargetId); Assert.Equal(context.Request.TargetRevision, capture.TargetRevision);
        Assert.Equal(context.Request.RunId, capture.RunId);
        var evaluation = SqlAgentActivityEvaluator.Evaluate(capture);
        Assert.False(capture.ProductionAdmitted); Assert.False(capture.ServiceSessionAssociationQualified);
        Assert.Equal(SqlAgentActivityNativeQualification.Absent, capture.NativeQualification);
        Assert.All(evaluation.Jobs, job => { Assert.Null(job.ElapsedSeconds); Assert.Null(job.StartedAtUtc); Assert.Null(job.LongRunning); });
        // This explicitly enrolled trial uses the already stopped Agent; it never changes its state.
        Assert.Equal(1, capture.Before.Service.MatchingServiceCount); Assert.Equal(1, capture.After.Service.MatchingServiceCount);
        Assert.Equal(1, capture.Before.Service.Status); Assert.Equal(1, capture.After.Service.Status);
        Assert.Equal(SqlAgentActivityGap.ServiceNotRunning, evaluation.Gap);
        Assert.All(evaluation.Jobs, job => Assert.Equal(SqlAgentActivityCandidateState.Unknown, job.CandidateState));
        Emit("signed_module_capture", new { actualMonitorIdentityVerified = true, ownedIntegratedTlsSessionStable = true,
            originalDiscoveryProfilePreserved = true, directSecurityStateGranted = permissions[0] == 1,
            directSessionsSelectGranted = permissions[1] == 1, directActivitySelectGranted = permissions[2] == 1,
            directSessionsSelectError = 229, capture.SourceRowsRead, retainedRows = capture.Jobs.Count, capture.Truncated,
            gap = evaluation.Gap.ToString(), moduleDefinitionEnrollmentMatched = true, monitorDefinitionVerified = false,
            signatureVerifiedByMonitor = false, databaseDefinitionAndSignatureVerification = "separate_DBA_evidence_required",
            fullForeignJobVisibilityQualified = false, productionAdmitted = false, serviceSessionAssociationQualified = false,
            elapsedQualified = false, longRunningQualified = false });
        byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, diagnosticOnly = true,
            sourceAssetVersion = 2, moduleDefinitionEnrollmentHash = context.Enrollment.DefinitionHash,
            databaseDefinitionVerifiedByMonitor = false, capture }, Json);
        Assert.True(receipt.Length <= 32768, "Private module receipt exceeds the approved evidence bound.");
        output.WriteLine("SQL_AGENT_MODULE_PRIVATE_RECEIPT_V1:" + Encoding.UTF8.GetString(receipt));
    });

    [Fact]
    public Task ActualMonitorRejectsNullAndNoncanonicalModuleBounds() => Safely(async token =>
    {
        var context = await PrepareAsync(token);
        string query = IdentityPrefix(context) + string.Join("\n", InvalidBounds.Select(value =>
            "BEGIN TRY EXEC master.sqlobserver_agent.capture_v1 @maximum_rows=" + value + "; SELECT CONVERT(int,0); END TRY BEGIN CATCH SELECT CONVERT(int,ERROR_NUMBER()); END CATCH;")) + IdentitySql;
        await using var session = await new SqlAgentActivitySource().OpenAsync(context.Request, query, TimeSpan.FromSeconds(10), token);
        session.RequireConnection(); var before = await IdentityAsync(session.Reader, token);
        foreach (int _ in Enumerable.Range(0, 3))
        { Assert.True(await session.Reader.NextResultAsync(token)); Assert.Equal(50001, (await IntegersAsync(session.Reader, 1, token))[0]); }
        Assert.True(await session.Reader.NextResultAsync(token)); Assert.Equal(before, await IdentityAsync(session.Reader, token));
        Assert.False(await session.Reader.NextResultAsync(token)); session.RequireConnection();
        Emit("invalid_bounds", new { rejectedCount = 3, expectedErrorNumber = 50001, ownedSessionStable = true, moduleCaptureExecuted = false });
    });

    [Fact]
    public Task PreCancelledModuleSourceExecutesNoCommand() => Safely(async token =>
    {
        var context = await PrepareAsync(token);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var session = await new SqlAgentActivitySource().OpenAsync(context.Request, ModuleExec, TimeSpan.FromSeconds(10), cancelled.Token);
        });
        Emit("pre_cancelled", new { moduleCommandExecuted = false, productionGenericSourceOpenCancelled = true,
            productionModuleAdapterQualified = false, discoveryPreviouslyCompleted = true });
    });

    private static async Task Safely(Func<CancellationToken, Task> action)
    {
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await action(deadline.Token); deadline.Token.ThrowIfCancellationRequested(); }
        catch (Exception error)
        { throw new XunitException("Signed Agent module trial failed; type=" + error.GetType().Name + (error is SqlException sql ? "; SQL number=" + sql.Number : "") + "; private details withheld."); }
    }
    private void Emit(string stage, object evidence) => output.WriteLine(JsonSerializer.Serialize(new { stage, evidence }, Json));

    private static async Task<Context> PrepareAsync(CancellationToken token)
    {
        var enrollment = SqlAgentModuleEnrollment.Load(Environment.GetEnvironmentVariable);
        var lab = PairedCpuQualificationLabContract.Load(Environment.GetEnvironmentVariable);
        Assert.Equal(17, lab.ExpectedSqlMajor);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(identity.User?.Value == lab.ExpectedWindowsSid, "Actual monitor token mismatch; identity withheld.");
        byte[] sid = new byte[identity.User!.BinaryLength]; identity.User.GetBinaryForm(sid, 0);
        var profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(lab.DiscoveryRequest(), token);
        Assert.Equal(lab.TargetId, profile.TargetId); Assert.Equal(lab.TargetRevision, profile.TargetRevision);
        Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded);
        Assert.Equal(17, profile.ServerIdentity!.Version.Major); Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.True(profile.TransportEncrypted); Assert.False(profile.IsSysAdmin);
        Assert.True(profile.AuthenticationScheme is SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm);
        string source = SqlServerAgentActivityCaptureAssetCatalog.LoadEmbedded().GetQuery(17);
        Assert.Equal(SqlAgentModuleEnrollment.ExpandedSourceHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant());
        return new(lab.ExecutionRequest(profile), sid, enrollment);
    }
    private sealed record Context(CollectorExecutionRequest Request, byte[] Sid, SqlAgentModuleEnrollment Enrollment);
    private static string IdentitySql => SqlServerCpuTargetWitnessReader.LoadMonitorIdentityQuery().Replace("CONVERT(bit,", "CONVERT(int,", StringComparison.Ordinal);
    private static string IdentityPrefix(Context context) => "DECLARE @expected_sid varbinary(68)=0x" + Convert.ToHexString(context.Sid) + ";\n" + IdentitySql;
    private static async Task<(Guid Connection, int Session)> IdentityAsync(ISqlAgentActivityResultReader reader, CancellationToken token)
    {
        Assert.Equal(12, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
        for (int index = 1; index <= 8; index++) Assert.Equal(1, reader.GetInt32(index));
        var result = (reader.GetGuid(9), reader.GetInt32(10)); Assert.NotEqual(Guid.Empty, result.Item1); Assert.True(result.Item2 > 0);
        Assert.Equal(1, reader.GetInt32(11)); Assert.False(await reader.ReadAsync(token)); return result;
    }
    private static async Task<int[]> IntegersAsync(ISqlAgentActivityResultReader reader, int width, CancellationToken token)
    {
        Assert.Equal(width, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
        int[] values = Enumerable.Range(0, width).Select(reader.GetInt32).ToArray(); Assert.False(await reader.ReadAsync(token)); return values;
    }
    private const string BaselineSql = """
        USE msdb;
        SELECT CONVERT(int,COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW SERVER SECURITY STATE'),-1)),
          CONVERT(int,COALESCE(HAS_PERMS_BY_NAME(N'dbo.syssessions',N'OBJECT',N'SELECT'),-1)),
          CONVERT(int,COALESCE(HAS_PERMS_BY_NAME(N'dbo.sysjobactivity',N'OBJECT',N'SELECT'),-1));
        BEGIN TRY
          EXEC sys.sp_executesql N'DECLARE @ignored_session int; SELECT TOP (1) @ignored_session=session_id FROM msdb.dbo.syssessions;';
          SELECT CONVERT(int,0);
        END TRY BEGIN CATCH SELECT CONVERT(int,ERROR_NUMBER()); END CATCH;
        USE master;
        """;

    private sealed class CaptureWindow(ISqlAgentActivityResultReader inner) : ISqlAgentActivityResultReader
    {
        public int CompletedSets { get; private set; }
        public int FieldCount => inner.FieldCount;
        public string GetName(int ordinal) => inner.GetName(ordinal);
        public Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public DateTimeOffset GetDateTimeOffset(int ordinal) => inner.GetDateTimeOffset(ordinal);
        public ValueTask<bool> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public ValueTask<bool> NextResultAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ++CompletedSets == 3 ? ValueTask.FromResult(false) : inner.NextResultAsync(token); }
    }
}
