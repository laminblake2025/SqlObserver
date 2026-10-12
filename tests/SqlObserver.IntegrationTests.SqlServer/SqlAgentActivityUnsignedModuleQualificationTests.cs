using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Infrastructure.SqlServer;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Read-only negative cases after separately attested, committed signature removal.
/// The monitor does not verify module definition/signature metadata or alter any SQL state.</summary>
[Trait("Category", "RequiresSqlServer")]
[Trait("Category", "RequiresSqlAgentActivityUnsignedModule")]
public sealed class SqlAgentActivityUnsignedModuleQualificationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] CanonicalBound = ["513"];
    private static readonly string[] InvalidBounds = ["NULL", "512", "514"];

    [Fact]
    public Task ActualMonitorUnsignedModuleRequiresPermissionDeniedWithoutSourceRows() => Safely(async token =>
    {
        using var context = await PrepareAsync(token);
        await VerifyAsync(context, CanonicalBound, 229, "unsigned_module_permission_denied", token);
    });

    [Fact]
    public Task ActualMonitorUnsignedModulePreservesNullAndNoncanonicalBoundsRejection() => Safely(async token =>
    {
        using var context = await PrepareAsync(token);
        await VerifyAsync(context, InvalidBounds, 50001, "unsigned_module_invalid_bounds", token);
    });

    private async Task VerifyAsync(Context context, string[] bounds, int expectedError, string scenario, CancellationToken token)
    {
        // Nested execution makes statement-level permission errors catchable by the outer batch.
        // Each fixed call must return only its one-int error envelope. Partial module rows,
        // unexpected success, any other error and any extra result set fail the test.
        string calls = string.Join("\n", bounds.Select(value =>
            "BEGIN TRY EXEC sys.sp_executesql N'EXEC master.sqlobserver_agent.capture_v1 @maximum_rows=" + value +
            ";'; SELECT CONVERT(int,0); END TRY BEGIN CATCH SELECT CONVERT(int,ERROR_NUMBER()); END CATCH;"));
        string query = IdentityPrefix(context) + BaselineSql + calls + BaselineSql + IdentitySql;
        long started = Stopwatch.GetTimestamp();
        await using var session = await new SqlAgentActivitySource().OpenAsync(context.Request, query, TimeSpan.FromSeconds(10), token);
        session.RequireConnection();
        var reader = session.Reader;
        var before = await IdentityAsync(reader, token);
        var permissionsBefore = await BaselineAsync(reader, "before", token);
        for (int index = 0; index < bounds.Length; index++)
        {
            Assert.True(await reader.NextResultAsync(token));
            int observedError = (await IntegersAsync(reader, 1, token))[0];
            Emit("module_error", new { scenario, ordinal = index + 1, observedError, expectedError });
            Assert.Equal(expectedError, observedError);
        }
        var permissionsAfter = await BaselineAsync(reader, "after", token);
        Assert.Equal(permissionsBefore, permissionsAfter);
        Assert.True(await reader.NextResultAsync(token));
        var after = await IdentityAsync(reader, token);
        Assert.Equal(before.Connection, after.Connection);
        Assert.Equal(before.Session, after.Session);
        Assert.True(after.ObservedUtc >= before.ObservedUtc);
        Assert.False(await reader.NextResultAsync(token));
        session.RequireConnection();
        token.ThrowIfCancellationRequested();
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
        Emit(scenario, new
        {
            actualMonitorIdentityVerified = true, ownedIntegratedTlsSessionStable = true,
            originalDiscoveryProfilePreserved = true, rejectedCount = bounds.Length, expectedErrorNumber = expectedError,
            directSecurityStateGranted = false, directSessionsSelectGranted = false, directActivitySelectGranted = true,
            directSessionsSelectErrorBefore = 229, directSessionsSelectErrorAfter = 229, directPermissionsUnchanged = true,
            sourceResultSetsObserved = 0, moduleDefinitionEnrollmentMatched = true, monitorDefinitionVerified = false,
            signatureVerifiedByMonitor = false, databaseDefinitionAndSignatureVerification = "separate_DBA_evidence_required",
            attachedInvalidSignatureQualified = false, explicitDenyQualified = false, signatureRecoveryQualified = false,
            productionAdmitted = false, serviceSessionAssociationQualified = false, elapsedQualified = false, longRunningQualified = false,
        });
    }

    private async Task<int[]> BaselineAsync(ISqlAgentActivityResultReader reader, string boundary, CancellationToken token)
    {
        Assert.True(await reader.NextResultAsync(token));
        int[] permissions = await IntegersAsync(reader, 3, token);
        Emit("direct_permissions", new { boundary, securityState = permissions[0], sessionsSelect = permissions[1], activitySelect = permissions[2] });
        Assert.Equal(0, permissions[0]);
        Assert.Equal(0, permissions[1]);
        Assert.Equal(1, permissions[2]);
        Assert.True(await reader.NextResultAsync(token));
        int error = (await IntegersAsync(reader, 1, token))[0];
        Emit("direct_sessions_denial", new { boundary, errorNumber = error });
        Assert.Equal(229, error);
        return permissions;
    }

    private static async Task Safely(Func<CancellationToken, Task> action)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await action(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            throw new XunitException("Unsigned Agent module trial failed; type=" + error.GetType().Name +
                (error is SqlException sql ? "; SQL number=" + sql.Number : "") + "; private details withheld.");
        }
    }

    private void Emit(string stage, object evidence) => output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, stage, evidence }, Json));

    private static async Task<Context> PrepareAsync(CancellationToken token)
    {
        _ = SqlAgentUnsignedModuleEnrollment.Load(Environment.GetEnvironmentVariable);
        var lab = PairedCpuQualificationLabContract.Load(Environment.GetEnvironmentVariable);
        Assert.Equal(17, lab.ExpectedSqlMajor);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(identity.User?.Value == lab.ExpectedWindowsSid, "Actual monitor token mismatch; identity withheld.");
        var profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(lab.DiscoveryRequest(), token);
        Assert.Equal(lab.TargetId, profile.TargetId); Assert.Equal(lab.TargetRevision, profile.TargetRevision);
        Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded);
        Assert.Equal(17, profile.ServerIdentity!.Version.Major); Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.True(profile.TransportEncrypted); Assert.False(profile.IsSysAdmin);
        Assert.True(profile.AuthenticationScheme is SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm);
        string source = SqlServerAgentActivityCaptureAssetCatalog.LoadEmbedded().GetQuery(17);
        Assert.Equal(SqlAgentModuleEnrollment.ExpandedSourceHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant());
        byte[] sid = new byte[identity.User!.BinaryLength];
        identity.User.GetBinaryForm(sid, 0);
        return new(lab.ExecutionRequest(profile), sid);
    }

    private sealed record Context(CollectorExecutionRequest Request, byte[] Sid) : IDisposable
    {
        public void Dispose() => CryptographicOperations.ZeroMemory(Sid);
    }

    private static string IdentitySql => SqlServerCpuTargetWitnessReader.LoadMonitorIdentityQuery().Replace("CONVERT(bit,", "CONVERT(int,", StringComparison.Ordinal);
    private static string IdentityPrefix(Context context) => "DECLARE @expected_sid varbinary(68)=0x" + Convert.ToHexString(context.Sid) + ";\n" + IdentitySql;

    private static async Task<(Guid Connection, int Session, DateTime ObservedUtc)> IdentityAsync(ISqlAgentActivityResultReader reader, CancellationToken token)
    {
        Assert.Equal(12, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
        Assert.Equal(typeof(DateTime), reader.GetFieldType(0)); Assert.False(reader.IsDBNull(0));
        DateTime observed = reader.GetDateTime(0); Assert.NotEqual(DateTimeKind.Local, observed.Kind);
        for (int index = 1; index <= 8; index++)
        {
            Assert.Equal(typeof(int), reader.GetFieldType(index));
            Assert.False(reader.IsDBNull(index)); Assert.Equal(1, reader.GetInt32(index));
        }
        Assert.Equal(typeof(Guid), reader.GetFieldType(9)); Assert.False(reader.IsDBNull(9));
        Guid connection = reader.GetGuid(9); Assert.NotEqual(Guid.Empty, connection);
        Assert.Equal(typeof(int), reader.GetFieldType(10)); Assert.False(reader.IsDBNull(10));
        int session = reader.GetInt32(10); Assert.True(session > 0);
        Assert.Equal(typeof(int), reader.GetFieldType(11)); Assert.False(reader.IsDBNull(11));
        Assert.Equal(1, reader.GetInt32(11)); Assert.False(await reader.ReadAsync(token));
        return (connection, session, observed);
    }

    private static async Task<int[]> IntegersAsync(ISqlAgentActivityResultReader reader, int width, CancellationToken token)
    {
        Assert.Equal(width, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
        int[] values = new int[width];
        for (int index = 0; index < width; index++)
        {
            Assert.Equal(typeof(int), reader.GetFieldType(index)); Assert.False(reader.IsDBNull(index));
            values[index] = reader.GetInt32(index);
        }
        Assert.False(await reader.ReadAsync(token));
        return values;
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
}
