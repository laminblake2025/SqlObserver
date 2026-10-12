using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
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

/// <summary>Opt-in native proof of the public reader using independently enrolled inputs.
/// The resolver is a finite harness fixture, not a production repository or enrollment workflow.</summary>
[Trait("Category", "RequiresSqlServer")]
[Trait("Category", "RequiresSqlAgentActivityProductionModule")]
public sealed class SqlAgentActivityProductionModuleQualificationTests(ITestOutputHelper output)
{
    private QualificationStage stage;
    private FixtureResolver? activeResolver;
    private bool? diagnosticConnectionIdsDiffer;

    [Fact]
    public Task ActualMonitorCapturesThroughPublicEnrolledModuleReader() => Safely(async token =>
    {
        var context = await PrepareAsync(false, token);
        stage = QualificationStage.DiagnosticOpen;
        await using var diagnostics = await OpenDiagnosticsAsync(context.Request, token);
        Guid diagnosticClientId = diagnostics.ClientConnectionId;
        Assert.NotEqual(Guid.Empty, diagnosticClientId);
        stage = QualificationStage.DiagnosticIdentityBefore;
        var identity = await IdentityAsync(diagnostics, context.Sid, token);
        diagnosticConnectionIdsDiffer = identity.Connection != diagnosticClientId;
        stage = QualificationStage.DirectPermissionsBefore;
        await DirectPermissionsAsync(diagnostics, token);
        var resolver = activeResolver = new FixtureResolver(context.Input.Enrollment);
        var reader = new SqlServerAgentActivityModuleCaptureReader(resolver);
        long started = Stopwatch.GetTimestamp();
        stage = QualificationStage.PublicReader;
        SqlAgentModuleCaptureResult result = await reader.ReadAsync(context.Request, token);
        stage = QualificationStage.ResultValidation;
        Assert.True(Stopwatch.GetElapsedTime(started) < SqlServerAgentActivityModuleCaptureReader.MaximumBudget);
        Assert.Equal(3, resolver.Calls);
        Assert.Equal(context.Input.Enrollment.Revision, result.EnrollmentRevision);
        Assert.Equal(context.Input.Enrollment.EnrollmentId, result.EnrollmentId);
        Assert.Equal(context.Input.Enrollment.EvidenceSha256, result.EvidenceSha256);
        Assert.Equal(context.Request.TargetId, result.Capture.TargetId);
        Assert.Equal(context.Request.TargetRevision, result.Capture.TargetRevision);
        Assert.Equal(context.Request.RunId, result.Capture.RunId);
        Assert.False(result.ProductionAdmitted); Assert.False(result.ServiceSessionAssociationQualified);
        Assert.False(result.Capture.ProductionAdmitted); Assert.False(result.Capture.ServiceSessionAssociationQualified);
        Assert.Equal(SqlAgentActivityNativeQualification.Absent, result.NativeQualification);
        Assert.Equal(SqlAgentActivityNativeQualification.Absent, result.Capture.NativeQualification);
        var evaluation = SqlAgentActivityEvaluator.Evaluate(result.Capture);
        Assert.Equal(SqlAgentActivityGap.ServiceNotRunning, evaluation.Gap);
        Assert.All(evaluation.Jobs, job =>
        {
            Assert.Equal(SqlAgentActivityCandidateState.Unknown, job.CandidateState);
            Assert.Null(job.StartedAtUtc); Assert.Null(job.ElapsedSeconds); Assert.Null(job.LongRunning);
        });
        Assert.InRange(result.Capture.SourceRowsRead, 0, SqlAgentActivityCapture.MaximumSourceRows);
        stage = QualificationStage.DirectPermissionsAfter;
        await DirectPermissionsAsync(diagnostics, token);
        stage = QualificationStage.DiagnosticIdentityAfter;
        Assert.Equal(identity, await IdentityAsync(diagnostics, context.Sid, token));
        Assert.Equal(diagnosticClientId, diagnostics.ClientConnectionId);
        stage = QualificationStage.FinalDiscovery;
        RequireUnmodifiedDiscovery(context.Request.CapabilityProfile);
        Emit("public_module_signed_capture", new
        {
            publicReaderInvoked = true, actualWindowsIdentityVerified = true, independentEnrollmentUsed = true,
            authorizationResolutions = resolver.Calls, directPermissionsUnchanged = true, directSessionsSelectError = 229,
            result.Capture.SourceRowsRead, retainedRows = result.Capture.Jobs.Count, result.Capture.Truncated,
            productionAdmitted = false, runningAssociationQualified = false, elapsedQualified = false,
            repositoryResolverQualified = false, continuousStabilityQualified = false,
            clientAndServerConnectionIdsDiffer = diagnosticConnectionIdsDiffer,
            diagnosticClientConnectionStable = true, diagnosticServerConnectionAndSessionStable = true,
        });
    });

    [Fact]
    public Task ActualMonitorUnsignedModuleIsRefusedBeforeCapture() => Safely(async token =>
    {
        var context = await PrepareAsync(true, token);
        stage = QualificationStage.DiagnosticOpen;
        await using var diagnostics = await OpenDiagnosticsAsync(context.Request, token);
        Guid diagnosticClientId = diagnostics.ClientConnectionId;
        Assert.NotEqual(Guid.Empty, diagnosticClientId);
        stage = QualificationStage.DiagnosticIdentityBefore;
        var identity = await IdentityAsync(diagnostics, context.Sid, token);
        diagnosticConnectionIdsDiffer = identity.Connection != diagnosticClientId;
        stage = QualificationStage.DirectPermissionsBefore;
        await DirectPermissionsAsync(diagnostics, token);
        stage = QualificationStage.UnsignedMetadataBefore;
        string sql = SqlAgentActivityModuleMetadataProbe.LoadSql();
        var before = await SqlAgentActivityModuleMetadataProbe.ReadAsync(diagnostics, context.Input.Metadata, sql,
            TimeSpan.FromSeconds(5), token);
        Assert.Equal(SqlAgentModuleSignatureState.Unsigned, before.State); Assert.False(before.CaptureEligible);
        var resolver = activeResolver = new FixtureResolver(context.Input.Enrollment);
        var reader = new SqlServerAgentActivityModuleCaptureReader(resolver);
        long started = Stopwatch.GetTimestamp();
        stage = QualificationStage.PublicReader;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(context.Request, token).AsTask());
        stage = QualificationStage.ResultValidation;
        Assert.True(Stopwatch.GetElapsedTime(started) < SqlServerAgentActivityModuleCaptureReader.MaximumBudget);
        // The reviewed public reader resolves a second time immediately before submitting EXEC.
        // One call proves that fence was not reached; this is not native SQL statement tracing.
        Assert.Equal(1, resolver.Calls);
        stage = QualificationStage.UnsignedMetadataAfter;
        var after = await SqlAgentActivityModuleMetadataProbe.ReadAsync(diagnostics, context.Input.Metadata, sql,
            TimeSpan.FromSeconds(5), token);
        Assert.Equal(SqlAgentModuleSignatureState.Unsigned, after.State); Assert.False(after.CaptureEligible);
        Assert.True(after.ObservedAtUtc >= before.ObservedAtUtc);
        Assert.Equal(before with { ObservedAtUtc = after.ObservedAtUtc }, after);
        stage = QualificationStage.DirectPermissionsAfter;
        await DirectPermissionsAsync(diagnostics, token);
        stage = QualificationStage.DiagnosticIdentityAfter;
        Assert.Equal(identity, await IdentityAsync(diagnostics, context.Sid, token));
        Assert.Equal(diagnosticClientId, diagnostics.ClientConnectionId);
        stage = QualificationStage.FinalDiscovery;
        RequireUnmodifiedDiscovery(context.Request.CapabilityProfile);
        Emit("public_module_unsigned_refusal", new
        {
            publicReaderInvoked = true, actualWindowsIdentityVerified = true, independentEnrollmentUsed = true,
            unsignedMetadataIndependentlyObserved = true, publicReaderRefused = true,
            authorizationResolutions = resolver.Calls, preExecutionAuthorizationFenceReached = false,
            captureAccepted = false, directPermissionsUnchanged = true, directSessionsSelectError = 229,
            nativeStatementTraceQualified = false, repositoryResolverQualified = false,
            attachedInvalidSignatureQualified = false, explicitDenyQualified = false, productionAdmitted = false,
            clientAndServerConnectionIdsDiffer = diagnosticConnectionIdsDiffer,
            diagnosticClientConnectionStable = true, diagnosticServerConnectionAndSessionStable = true,
        });
    });

    private async Task<Context> PrepareAsync(bool unsigned, CancellationToken token)
    {
        stage = QualificationStage.Fixture;
        var input = SqlAgentProductionModuleQualificationEnrollment.Load(Environment.GetEnvironmentVariable, unsigned);
        stage = QualificationStage.ActualWindowsToken;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string? actualSid = identity.User?.Value;
        Assert.True(actualSid == input.Lab.ExpectedWindowsSid && actualSid == input.Enrollment.ExpectedWindowsSid,
            "Actual monitor identity mismatch; values withheld.");
        byte[] sid = new byte[identity.User!.BinaryLength]; identity.User.GetBinaryForm(sid, 0);
        stage = QualificationStage.Discovery;
        CapabilityProfile profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(input.Lab.DiscoveryRequest(), token);
        stage = QualificationStage.ProfileValidation;
        Assert.Equal(input.Lab.TargetId, profile.TargetId); Assert.Equal(input.Lab.TargetRevision, profile.TargetRevision);
        Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded);
        Assert.Equal(17, profile.ServerIdentity!.Version.Major); Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.True(profile.ServerIdentity.EngineEdition is SqlServerEngineEdition.Standard or SqlServerEngineEdition.Enterprise);
        Assert.True(profile.TransportEncrypted); Assert.False(profile.IsSysAdmin);
        Assert.True(profile.AuthenticationScheme is SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm);
        Assert.True(profile.IsUsable && input.Enrollment.IsActiveAt(DateTimeOffset.UtcNow));
        RequireUnmodifiedDiscovery(profile);
        return new(input, input.Lab.ExecutionRequest(profile), sid);
    }

    private static void RequireUnmodifiedDiscovery(CapabilityProfile profile)
    {
        // The existing discovery does not establish these direct permissions. Never fabricate them
        // to make the separate module route satisfy the direct reader's RequireProfile gate.
        Assert.DoesNotContain(profile.Permissions, permission =>
            (permission.PermissionId.Value is "server.view-security-state" or "msdb.syssessions.select") &&
            permission.Outcome == PermissionEvidenceOutcome.Granted);
    }

    private static async ValueTask<SqlConnection> OpenDiagnosticsAsync(CollectorExecutionRequest request, CancellationToken token) =>
        await new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName)
            .OpenConnectionAsync(request.ConnectionPolicy, token);

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

    private sealed record Context(SqlAgentProductionModuleQualificationEnrollment Input, CollectorExecutionRequest Request, byte[] Sid);

    private sealed class FixtureResolver(DomainEnrollment enrollment) : ISqlAgentModuleEnrollmentResolver
    {
        public int Calls { get; private set; }
        public ValueTask<SqlAgentModuleEnrollmentSnapshot?> ResolveAsync(MonitoredInstanceId targetId,
            ObservationTargetRevision targetRevision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(enrollment.TargetId, targetId); Assert.Equal(enrollment.TargetRevision, targetRevision);
            Calls++;
            return ValueTask.FromResult<SqlAgentModuleEnrollmentSnapshot?>(new(enrollment.Revision, enrollment));
        }
    }

    private void Emit(string stage, object evidence) => output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, stage, evidence }));
    private async Task Safely(Func<CancellationToken, Task> action)
    {
        stage = QualificationStage.Fixture; activeResolver = null; diagnosticConnectionIdsDiffer = null;
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); await action(timeout.Token); timeout.Token.ThrowIfCancellationRequested(); }
        catch (Exception error)
        {
            string classification = error switch
            {
                UnauthorizedAccessException when error.Message == "Agent module authorization or identity is unavailable; private values withheld." => "module_authorization_or_binding",
                UnauthorizedAccessException when error.Message == "Agent module connection identity is invalid; private values withheld." => "module_connection_identity",
                UnauthorizedAccessException when error.Message == "Agent module SQL permissions are unavailable." => "module_sql_permission",
                UnauthorizedAccessException => "authorization_other",
                InvalidDataException => "invalid_data",
                TimeoutException => "timeout",
                OperationCanceledException => "cancelled",
                SqlException => "sql_exception",
                XunitException => "assertion",
                _ => "other",
            };
            Emit("public_module_failure_diagnostic", new
            {
                testStage = stage.ToString(), classification, resolverCalls = activeResolver?.Calls ?? 0,
                clientAndServerConnectionIdsDiffer = diagnosticConnectionIdsDiffer,
                sqlNumber = error is SqlException sqlFailure ? (int?)sqlFailure.Number : null,
                privateValuesWithheld = true,
            });
            throw new XunitException("Public Agent module qualification failed; type=" + error.GetType().Name +
                "; stage=" + stage + "; classification=" + classification + "; resolverCalls=" +
                (activeResolver?.Calls ?? 0).ToString(CultureInfo.InvariantCulture) +
                (error is SqlException sql ? "; SQL number=" + sql.Number.ToString(CultureInfo.InvariantCulture) : "") + "; private values withheld.");
        }
    }

    private enum QualificationStage
    {
        Fixture, ActualWindowsToken, Discovery, ProfileValidation, DiagnosticOpen, DiagnosticIdentityBefore,
        DirectPermissionsBefore, UnsignedMetadataBefore, PublicReader, ResultValidation, UnsignedMetadataAfter,
        DirectPermissionsAfter, DiagnosticIdentityAfter, FinalDiscovery,
    }
}
