using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;
using Xunit.Abstractions;
using Xunit.Sdk;
using DomainEnrollment = SqlObserver.Domain.Collection.SqlAgentModuleEnrollment;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Opt-in effective-permission failure fixture, not an inference from catalog DENY.
/// The DBA coordinator separately owns the journaled G-to-D-to-G change and cleanup.</summary>
[Trait("Category", "RequiresSqlServer")]
[Trait("Category", "RequiresSqlAgentActivityProductionModule")]
[Trait("Category", "RequiresSqlAgentActivityExplicitDenyModule")]
public sealed class SqlAgentActivityExplicitDenyQualificationTests(ITestOutputHelper output)
{
    [Fact]
    public Task ActualMonitorSignedModuleExplicitDenyRefusesPublicCapture() =>
        new SqlAgentActivityNativeQualificationHarness(output).RunAsync(true, async (context, token) =>
        {
            var resolver = new SqlAgentActivityNativeFixtureResolver(context.Input.Enrollment);
            var reader = new SqlServerAgentActivityModuleCaptureReader(resolver);
            long started = Stopwatch.GetTimestamp();
            var refusal = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(context.Request, token).AsTask());
            Assert.Equal("Agent module SQL permissions are unavailable.", refusal.Message);
            Assert.Null(refusal.InnerException);
            Assert.True(Stopwatch.GetElapsedTime(started) < SqlServerAgentActivityModuleCaptureReader.MaximumBudget);
            // Reaching this fence does not constitute native statement tracing. The independent
            // canonical EXEC below must return only 229, without any source-shaped results.
            Assert.Equal(2, resolver.Calls);
            await SqlAgentActivityNativeQualificationHarness.CanonicalExecRefusalAsync(context.Diagnostics, 513, 229, token);
            context.SafeEvidence = new
            {
                schemaVersion = 1, stage = "public_module_explicit_deny_refusal",
                actualWindowsIdentityVerified = true, publicReaderInvoked = true, publicReaderRefused = true,
                authorizationResolutions = resolver.Calls, canonicalExecError = 229, captureAccepted = false,
                signedMetadataVerifiedBeforeAndAfter = true, scope = SqlAgentActivityExplicitDenyEnrollment.Scope,
                nativeStatementTraceQualified = false, repositoryResolverQualified = false,
                attachedInvalidSignatureQualified = false, productionAdmitted = false,
            };
        });

    [Fact]
    public Task ActualMonitorDeniedModuleRetainsCanonicalBoundRefusal() =>
        new SqlAgentActivityNativeQualificationHarness(output).RunAsync(true, async (context, token) =>
        {
            foreach (int? limit in new int?[] { null, 512, 514 })
                await SqlAgentActivityNativeQualificationHarness.CanonicalExecRefusalAsync(context.Diagnostics, limit, 50001, token);
            context.SafeEvidence = new
            {
                schemaVersion = 1, stage = "public_module_explicit_deny_bound_refusal",
                actualWindowsIdentityVerified = true, signedMetadataVerifiedBeforeAndAfter = true,
                invalidLimits = new object?[] { null, 512, 514 }, canonicalExecError = 50001,
                sourceResultsReturned = false, ddlPerformed = false, productionAdmitted = false,
            };
        });
}

/// <summary>Actual public collector through a finite enrollment resolver, independent of scheduler admission.</summary>
[Trait("Category", "RequiresSqlServer")]
[Trait("Category", "RequiresSqlAgentActivityProductionModule")]
public sealed class SqlAgentActivityCollectorNativeQualificationTests(ITestOutputHelper output)
{
    [Fact]
    public Task ActualMonitorCapturesThroughPublicObservationsCollector() =>
        new SqlAgentActivityNativeQualificationHarness(output).RunAsync(false, async (context, token) =>
        {
            var resolver = new SqlAgentActivityNativeFixtureResolver(context.Input.Enrollment);
            var collector = new SqlAgentActivityObservationsCollector(new SqlServerAgentActivityModuleCaptureReader(resolver));
            long started = Stopwatch.GetTimestamp();
            CollectorExecutionResult result = await collector.CollectAsync(context.Request, token);
            Assert.True(Stopwatch.GetElapsedTime(started) < SqlServerAgentActivityModuleCaptureReader.MaximumBudget);
            Assert.Equal(3, resolver.Calls);
            Assert.True(result.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial);
            Assert.Equal(context.Request.TargetId, result.TargetId); Assert.Equal(context.Request.TargetRevision, result.TargetRevision);
            Assert.Equal("sql-agent.activity-observations", result.CollectorId.Value);
            Assert.Equal(1, result.CollectorManifestVersion); Assert.Equal(1, result.OutputSchemaVersion);
            SqlAgentModuleCaptureResult value = Assert.IsType<SqlAgentModuleCaptureResult>(result.Payload.SqlAgentModuleCapture);
            Assert.Equal(context.Request.RunId, value.Capture.RunId);
            Assert.Equal(context.Input.Enrollment.Revision, value.EnrollmentRevision);
            Assert.Equal(context.Input.Enrollment.EnrollmentId, value.EnrollmentId);
            Assert.Equal(context.Input.Enrollment.EvidenceSha256, value.EvidenceSha256);
            SqlAgentActivityObservationsOutputValidation.Validate(result.Payload, context.Request.TargetId,
                context.Request.TargetRevision, context.Request.RunId, result.Outcome, result.Accounting, result.Loss);
            Assert.Equal(4096 + value.Capture.SourceRowsRead * 768, result.Accounting.ResponseBytes);
            Assert.Equal(4096 + value.Capture.Jobs.Count * 768, result.Accounting.OutputBytes);
            Assert.Equal(1, result.Accounting.OutputItemsProduced);
            Assert.InRange(value.Capture.Jobs.Count, 0, 512); Assert.InRange(value.Capture.SourceRowsRead, 0, 513);
            Assert.Equal(value.Capture.Truncated, result.Outcome == CollectorRunOutcome.Partial);
            Assert.Equal(value.Capture.Truncated ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed, result.Reason);
            Assert.False(value.ProductionAdmitted); Assert.False(value.ServiceSessionAssociationQualified);
            Assert.False(value.Capture.ProductionAdmitted); Assert.False(value.Capture.ServiceSessionAssociationQualified);
            Assert.Equal(SqlObserver.Domain.Collection.SqlAgentActivityNativeQualification.Absent, value.NativeQualification);
            Assert.Equal(SqlObserver.Domain.Collection.SqlAgentActivityNativeQualification.Absent, value.Capture.NativeQualification);
            // This trial requires Agent stopped. It does not qualify running, session association,
            // physical elapsed time, or repository-backed enrollment resolution.
            Assert.Equal(1, value.Capture.Before.Service.MatchingServiceCount);
            Assert.Equal(1, value.Capture.After.Service.MatchingServiceCount);
            Assert.Equal(1, value.Capture.Before.Service.Status); Assert.Equal(1, value.Capture.After.Service.Status);
            var evaluation = SqlAgentActivityEvaluator.Evaluate(value.Capture);
            Assert.Equal(value.Capture.Truncated ? SqlAgentActivityGap.SourceTruncated : SqlAgentActivityGap.ServiceNotRunning, evaluation.Gap);
            Assert.All(evaluation.Jobs, job =>
            {
                Assert.Equal(SqlAgentActivityCandidateState.Unknown, job.CandidateState);
                Assert.Null(job.StartedAtUtc); Assert.Null(job.ElapsedSeconds); Assert.Null(job.LongRunning);
            });
            // The private receipt contains only approved source fields and immutable enrollment
            // provenance. No credentials, endpoint, monitor SID, SQL text or raw metadata enter it.
            context.PrivateReceipt = SqlAgentActivityCollectorNativeReceipt.Serialize(result, context.Request.RunId);
        });
}

internal sealed record SqlAgentActivityNativeContext(SqlAgentProductionModuleQualificationEnrollment Input,
    CollectorExecutionRequest Request, SqlConnection Diagnostics)
{
    internal string? PrivateReceipt { get; set; }
    internal string? DeferredRepositoryReceipt { get; set; }
    internal string? DeferredRunningJobReceipt { get; set; }
    internal object? SafeEvidence { get; set; }
}

internal sealed class SqlAgentActivityNativeFixtureResolver(DomainEnrollment enrollment) : ISqlAgentModuleEnrollmentResolver
{
    internal int Calls { get; private set; }
    public ValueTask<SqlAgentModuleEnrollmentSnapshot?> ResolveAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(enrollment.TargetId, targetId); Assert.Equal(enrollment.TargetRevision, targetRevision);
        Calls++;
        return ValueTask.FromResult<SqlAgentModuleEnrollmentSnapshot?>(new(enrollment.Revision, enrollment));
    }
}

internal sealed class SqlAgentActivityNativeQualificationHarness(ITestOutputHelper output)
{
    internal async Task RunAsync(bool denied, Func<SqlAgentActivityNativeContext, CancellationToken, Task> action)
    {
        string stage = "enrollment";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            CancellationToken token = timeout.Token;
            var input = denied
                ? SqlAgentActivityExplicitDenyEnrollment.Load(Environment.GetEnvironmentVariable).Production
                : SqlAgentActivityNativeFixture.LoadSigned(Environment.GetEnvironmentVariable);
            stage = "actual_monitor_identity";
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? actualSid = identity.User?.Value;
            Assert.True(actualSid == input.Lab.ExpectedWindowsSid && actualSid == input.Enrollment.ExpectedWindowsSid,
                "Actual monitor identity mismatch; values withheld.");
            byte[] sid = new byte[identity.User!.BinaryLength]; identity.User.GetBinaryForm(sid, 0);
            stage = "discovery";
            CapabilityProfile profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(input.Lab.DiscoveryRequest(), token);
            Assert.Equal(input.Lab.TargetId, profile.TargetId); Assert.Equal(input.Lab.TargetRevision, profile.TargetRevision);
            Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded);
            Assert.Equal(17, profile.ServerIdentity!.Version.Major); Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
            Assert.True(profile.ServerIdentity.EngineEdition is SqlServerEngineEdition.Standard or SqlServerEngineEdition.Enterprise);
            Assert.True(profile.IsUsable && profile.TransportEncrypted); Assert.False(profile.IsSysAdmin);
            Assert.True(profile.AuthenticationScheme is SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm);
            Assert.True(input.Enrollment.IsActiveAt(DateTimeOffset.UtcNow));
            Assert.DoesNotContain(profile.Permissions, permission =>
                (permission.PermissionId.Value is "server.view-security-state" or "msdb.syssessions.select") &&
                permission.Outcome == PermissionEvidenceOutcome.Granted);
            var request = input.Lab.ExecutionRequest(profile);
            stage = "diagnostic_before";
            await using var diagnostics = await new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName)
                .OpenConnectionAsync(request.ConnectionPolicy, token);
            Guid clientId = diagnostics.ClientConnectionId; Assert.NotEqual(Guid.Empty, clientId);
            var nativeIdentity = await IdentityAsync(diagnostics, sid, token);
            await DirectPermissionsAsync(diagnostics, token);
            string metadataSql = SqlAgentActivityModuleMetadataProbe.LoadSql();
            var before = await SqlAgentActivityModuleMetadataProbe.ReadAsync(diagnostics, input.Metadata, metadataSql, TimeSpan.FromSeconds(5), token);
            Assert.Equal(SqlAgentModuleSignatureState.SignedVerified, before.State); Assert.True(before.CaptureEligible);
            var context = new SqlAgentActivityNativeContext(input, request, diagnostics);
            stage = "qualification";
            await action(context, token);
            stage = "diagnostic_after";
            var after = await SqlAgentActivityModuleMetadataProbe.ReadAsync(diagnostics, input.Metadata, metadataSql, TimeSpan.FromSeconds(5), token);
            Assert.Equal(SqlAgentModuleSignatureState.SignedVerified, after.State); Assert.True(after.CaptureEligible);
            Assert.True(after.ObservedAtUtc >= before.ObservedAtUtc);
            Assert.Equal(before with { ObservedAtUtc = after.ObservedAtUtc }, after);
            await DirectPermissionsAsync(diagnostics, token);
            Assert.Equal(nativeIdentity, await IdentityAsync(diagnostics, sid, token));
            Assert.Equal(clientId, diagnostics.ClientConnectionId);
            token.ThrowIfCancellationRequested();
            // Emit replayable evidence only after all native metadata/identity bounds pass.
            if (context.SafeEvidence is { } evidence) output.WriteLine(JsonSerializer.Serialize(evidence));
            if (context.PrivateReceipt is { } receipt) output.WriteLine(SqlAgentActivityCollectorNativeReceipt.Marker + receipt);
            if (context.DeferredRepositoryReceipt is { } repositoryReceipt)
                output.WriteLine(SqlAgentActivityRepositoryNativeReceipt.Marker + repositoryReceipt);
            if (context.DeferredRunningJobReceipt is { } runningJobReceipt)
                output.WriteLine(SqlAgentActivityRunningJobNativeReceipt.Marker + runningJobReceipt);
        }
        catch (Exception error)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, stage = "explicit_deny_or_collector_failure", testStage = stage,
                failureType = error.GetType().Name, sqlNumber = error is SqlException sql ? (int?)sql.Number : null,
                privateValuesWithheld = true,
            }));
            throw new XunitException("Native Agent qualification failed; stage=" + stage + "; type=" + error.GetType().Name +
                (error is SqlException sqlFailure ? "; SQL number=" + sqlFailure.Number.ToString(CultureInfo.InvariantCulture) : "") + "; private values withheld.");
        }
    }

    internal static async Task CanonicalExecRefusalAsync(SqlConnection connection, int? maximumRows, int expectedError, CancellationToken token)
    {
        using var command = new SqlCommand("""
            BEGIN TRY
              EXEC master.sqlobserver_agent.capture_v1 @maximum_rows=@limit;
              SELECT CONVERT(int,0);
            END TRY BEGIN CATCH SELECT CONVERT(int,ERROR_NUMBER()); END CATCH;
            """, connection) { CommandTimeout = 5 };
        command.Parameters.Add("@limit", SqlDbType.Int).Value = maximumRows is { } value ? value : DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal(1, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
        Assert.Equal(expectedError, reader.GetInt32(0)); Assert.False(await reader.ReadAsync(token));
        Assert.False(await reader.NextResultAsync(token));
    }

    private static async Task<(Guid Connection, int Session)> IdentityAsync(SqlConnection connection, byte[] sid, CancellationToken token)
    {
        using var command = new SqlCommand(SqlServerCpuTargetWitnessReader.LoadMonitorIdentityQuery()
            .Replace("CONVERT(bit,", "CONVERT(int,", StringComparison.Ordinal), connection) { CommandTimeout = 5 };
        command.Parameters.Add("@expected_sid", SqlDbType.VarBinary, 68).Value = sid;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal(12, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
        for (int index = 1; index <= 8; index++) Assert.Equal(1, reader.GetInt32(index));
        var result = (reader.GetGuid(9), reader.GetInt32(10));
        Assert.NotEqual(Guid.Empty, result.Item1); Assert.True(result.Item2 > 0); Assert.Equal(1, reader.GetInt32(11));
        Assert.Equal(connection.ServerProcessId, result.Item2);
        Assert.False(await reader.ReadAsync(token)); Assert.False(await reader.NextResultAsync(token));
        return result;
    }

    private static async Task DirectPermissionsAsync(SqlConnection connection, CancellationToken token)
    {
        using var command = new SqlCommand("""
            USE msdb;
            SELECT CONVERT(int,COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW SERVER SECURITY STATE'),-1)),
              CONVERT(int,COALESCE(HAS_PERMS_BY_NAME(N'dbo.syssessions',N'OBJECT',N'SELECT'),-1)),
              CONVERT(int,COALESCE(HAS_PERMS_BY_NAME(N'dbo.sysjobactivity',N'OBJECT',N'SELECT'),-1));
            BEGIN TRY EXEC sys.sp_executesql N'DECLARE @ignored int; SELECT TOP(1) @ignored=session_id FROM msdb.dbo.syssessions;';
              SELECT CONVERT(int,0);
            END TRY BEGIN CATCH SELECT CONVERT(int,ERROR_NUMBER()); END CATCH;
            USE master;
            """, connection) { CommandTimeout = 5 };
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal(3, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
        Assert.Equal(0, reader.GetInt32(0)); Assert.Equal(0, reader.GetInt32(1)); Assert.Equal(1, reader.GetInt32(2));
        Assert.False(await reader.ReadAsync(token)); Assert.True(await reader.NextResultAsync(token));
        Assert.Equal(1, reader.FieldCount); Assert.True(await reader.ReadAsync(token)); Assert.Equal(229, reader.GetInt32(0));
        Assert.False(await reader.ReadAsync(token)); Assert.False(await reader.NextResultAsync(token));
    }
}
