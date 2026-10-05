using System.Data;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Infrastructure.SqlServer;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Opt-in permission/visibility diagnostics, not activity qualification or admission.
/// No grants, jobs, services, capability profiles or repository state are changed.</summary>
public sealed class SqlAgentActivityNativeReadinessTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlAgentActivityReadiness")]
    public async Task ActualMonitorReportsBoundedAgentReadinessWithoutAdmission()
    {
        string stage = "configuration";
        try
        {
            var contract = PairedCpuQualificationLabContract.Load(Environment.GetEnvironmentVariable);
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            byte[] sid;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                Assert.True(identity.User?.Value == contract.ExpectedWindowsSid, "Actual monitor token mismatch; identity withheld.");
                sid = new byte[identity.User!.BinaryLength]; identity.User.GetBinaryForm(sid, 0);
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CancellationToken token = deadline.Token;
            long started = Stopwatch.GetTimestamp();
            stage = "production_discovery";
            var profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(contract.DiscoveryRequest(), token);
            Assert.Equal(contract.TargetId, profile.TargetId); Assert.Equal(contract.TargetRevision, profile.TargetRevision);
            Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded);
            Assert.NotNull(profile.ServerIdentity); Assert.Equal(contract.ExpectedSqlMajor, profile.ServerIdentity.Version.Major);
            Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
            Assert.True(profile.TransportEncrypted); Assert.False(profile.IsSysAdmin);
            Assert.True(profile.AuthenticationScheme is SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm);
            string stateId = contract.ExpectedSqlMajor == 15 ? "server.view-state" : "server.view-security-state";
            var discovery = new[]
            {
                Discovery(profile, stateId, PermissionEvidenceScope.Server),
                Discovery(profile, "msdb.syssessions.select", PermissionEvidenceScope.Database),
                Discovery(profile, "msdb.sysjobactivity.select", PermissionEvidenceScope.Database),
            };
            Emit("production_discovery", new { requiredPermissions = discovery, profileChanged = false });

            stage = "owned_connection";
            var factory = new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
            await using var connection = await factory.OpenConnectionAsync(contract.Policy, token);
            Guid clientConnection = connection.ClientConnectionId;
            Assert.NotEqual(Guid.Empty, clientConnection);
            stage = "identity_before";
            var before = await AttestAsync(connection, sid, token);

            stage = "actual_permissions";
            string statePermission = contract.ExpectedSqlMajor == 15 ? "VIEW SERVER STATE" : "VIEW SERVER SECURITY STATE";
            var state = await PermissionAsync(connection, stateId,
                "SELECT CONVERT(int,HAS_PERMS_BY_NAME(NULL,NULL,@permission));", statePermission, token);
            var sessions = await PermissionAsync(connection, "msdb.syssessions.select",
                "USE msdb; SELECT CONVERT(int,HAS_PERMS_BY_NAME(N'dbo.syssessions',N'OBJECT',N'SELECT'));", null, token);
            var activity = await PermissionAsync(connection, "msdb.sysjobactivity.select",
                "USE msdb; SELECT CONVERT(int,HAS_PERMS_BY_NAME(N'dbo.sysjobactivity',N'OBJECT',N'SELECT'));", null, token);
            Emit(stage, new { state, sessions, activity });

            stage = "bounded_actual_selects";
            // Actual table reads independently establish only visibility of returned rows.
            // No COUNT(*), job names/IDs, timestamps or table contents leave this process.
            var sessionRead = await ReadIntegersAsync(connection, "syssessions",
                "SELECT TOP (1) session_id FROM msdb.dbo.syssessions ORDER BY session_id DESC;", 1, token);
            var activityRead = await ReadIntegersAsync(connection, "sysjobactivity",
                "SELECT TOP (513) session_id FROM msdb.dbo.sysjobactivity;", 513, token);
            // Match the executable basename rather than localized service display names.
            // Three matches cap ambiguity evidence; neither a service name nor path is returned.
            var serviceRead = await ReadIntegersAsync(connection, "agent_service",
                """
                USE master;
                SELECT TOP (3) CONVERT(int,status) FROM sys.dm_server_services
                CROSS APPLY (SELECT REPLACE(LTRIM(RTRIM(filename)),N'\',N'/') AS path) AS n
                CROSS APPLY (SELECT CASE WHEN LEFT(n.path,1)=N'"' AND RIGHT(n.path,1)=N'"'
                    THEN SUBSTRING(n.path,2,LEN(n.path)-2) ELSE n.path END AS path) AS p
                WHERE LOWER(RIGHT(p.path,CHARINDEX(N'/',REVERSE(p.path)+N'/')-1))
                    COLLATE Latin1_General_100_BIN2=N'sqlagent.exe' AND CHARINDEX(N'"',p.path)=0;
                """, 3, token);
            Emit(stage, new { sessions = sessionRead.Public, activity = activityRead.Public, service = serviceRead.Public,
                serviceState = serviceRead.Outcome != "read_completed" ? "unavailable" : serviceRead.Rows != 1 ? "ambiguous_or_absent" :
                    serviceRead.SingleValue == 4 ? "running" : serviceRead.SingleValue == 1 ? "stopped" : "other",
                serviceSessionAssociationQualified = false, fullJobVisibilityQualified = false });

            stage = "identity_after";
            var after = await AttestAsync(connection, sid, token);
            Assert.True(before == after && connection.ClientConnectionId == clientConnection,
                "Owned physical SQL session changed; private identifiers withheld.");
            token.ThrowIfCancellationRequested();
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30), "Readiness deadline exceeded.");
            Emit("readiness_completed", new { schemaVersion = 1, diagnosticOnly = true, actualMonitorIdentityVerified = true,
                ownedIntegratedTlsSessionVerified = true, beforeAfterSessionStable = true, permissionProbeCount = 3,
                boundedSelectProbeCount = 3, perCommandTimeoutSeconds = 5, totalBudgetSeconds = 30,
                productionAdmitted = false, fullJobVisibilityQualified = false, serviceSessionAssociationQualified = false,
                elapsedQualified = false, longRunningQualified = false });
        }
        catch (Exception failure)
        {
            // SQL text, server/login names, SIDs and exception messages are private.
            throw new XunitException("Agent readiness failed at " + stage + "; exception type=" + failure.GetType().Name + "; private details withheld.");
        }
    }

    private static object Discovery(CapabilityProfile profile, string id, PermissionEvidenceScope scope) => new
    {
        permissionId = id,
        evidencePresent = profile.Permissions.Any(p => p.PermissionId.Value == id && p.Scope == scope),
        granted = profile.Permissions.Any(p => p.PermissionId.Value == id && p.Scope == scope && p.Outcome == PermissionEvidenceOutcome.Granted),
    };

    private static async Task<(Guid Connection, int Session)> AttestAsync(SqlConnection connection, byte[] sid, CancellationToken token)
    {
        await using var command = new SqlCommand(SqlServerCpuTargetWitnessReader.LoadMonitorIdentityQuery(), connection) { CommandTimeout = 5 };
        command.Parameters.Add("@expected_sid", SqlDbType.VarBinary, 68).Value = sid;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal(12, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
        for (int ordinal = 1; ordinal <= 8; ordinal++) Assert.True(!reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal), "Monitor session attestation failed.");
        var result = (reader.GetGuid(9), reader.GetInt32(10));
        Assert.NotEqual(Guid.Empty, result.Item1); Assert.True(result.Item2 > 0);
        Assert.Equal(1, reader.GetInt32(11));
        Assert.False(await reader.ReadAsync(token)); Assert.False(await reader.NextResultAsync(token));
        return result;
    }

    private static async Task<object> PermissionAsync(SqlConnection connection, string id, string sql, string? permission, CancellationToken token)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
            if (permission is not null) command.Parameters.Add("@permission", SqlDbType.NVarChar, 128).Value = permission;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.Equal(1, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
            int? value = reader.IsDBNull(0) ? null : reader.GetInt32(0);
            Assert.True(value is null or 0 or 1); Assert.False(await reader.ReadAsync(token)); Assert.False(await reader.NextResultAsync(token));
            return new { permissionId = id, outcome = "probe_completed", granted = value is null ? (bool?)null : value == 1, permissionErrorNumber = (int?)null };
        }
        catch (SqlException failure) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(failure.Number))
        { return new { permissionId = id, outcome = "permission_denied", granted = (bool?)null, permissionErrorNumber = failure.Number }; }
    }

    private static async Task<ReadResult> ReadIntegersAsync(SqlConnection connection, string source, string sql, int cap, CancellationToken token)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.Equal(1, reader.FieldCount); int rows = 0; int? single = null;
            while (await reader.ReadAsync(token))
            { Assert.True(++rows <= cap); Assert.False(reader.IsDBNull(0)); single = reader.GetInt32(0); }
            Assert.False(await reader.NextResultAsync(token));
            return new(source, "read_completed", rows, cap, rows == 1 ? single : null, null);
        }
        catch (SqlException failure) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(failure.Number))
        { return new(source, "permission_denied", null, cap, null, failure.Number); }
    }
    private sealed record ReadResult(string Source, string Outcome, int? Rows, int Cap, int? SingleValue, int? PermissionErrorNumber)
    {
        internal object Public => new { source = Source, outcome = Outcome, rowsRead = Rows, rowCap = Cap,
            capReached = Rows is null ? (bool?)null : Rows == Cap, permissionErrorNumber = PermissionErrorNumber,
            completeVisibilityProven = false };
    }
    private void Emit(string stage, object evidence) => output.WriteLine(JsonSerializer.Serialize(new { stage, evidence }, JsonOptions));
}
