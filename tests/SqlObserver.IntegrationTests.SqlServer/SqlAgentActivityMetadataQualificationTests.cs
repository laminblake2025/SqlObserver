using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Finite metadata-policy experiment using the real monitor and production connection
/// factory/parser. This does not register or admit a production module adapter.</summary>
[Trait("Category", "RequiresSqlServer")]
[Trait("Category", "RequiresSqlAgentActivityMetadataModule")]
public sealed class SqlAgentActivityMetadataQualificationTests(ITestOutputHelper output)
{
    [Fact]
    public Task ActualMonitorVerifiesMetadataAroundSignedCapture() => Safely(async token =>
    {
        var context = await PrepareAsync(false, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        token = deadline.Token;
        long started = Stopwatch.GetTimestamp();
        await using var connection = await OpenAsync(context.Request, token);
        Guid connectionId = connection.ClientConnectionId;
        var identityBefore = await IdentityAsync(connection, context.Sid, token);
        await BaselineAsync(connection, token);
        string sql = await LoadProbeAsync(token);
        var before = await SqlAgentActivityModuleMetadataProbe.ReadAsync(connection, context.Enrollment, sql, Remaining(started), token);
        // This gate executes in the client before the capture command is constructed or sent.
        if (!before.CaptureEligible) throw new InvalidDataException("Module metadata does not admit capture.");
        SqlAgentActivityCapture capture;
        using (var command = new SqlCommand("EXEC master.sqlobserver_agent.capture_v1 @maximum_rows=@maximum_rows;", connection))
        {
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(Remaining(started).TotalSeconds));
            command.Parameters.Add("@maximum_rows", SqlDbType.Int).Value = 513;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            capture = await SqlServerAgentActivityCaptureReader.ParseAsync(context.Request, new CaptureReader(reader), token);
        }
        var after = await SqlAgentActivityModuleMetadataProbe.ReadAsync(connection, context.Enrollment, sql, Remaining(started), token);
        SqlAgentActivityModuleMetadataProbe.RequireStableSigned(before, after);
        await BaselineAsync(connection, token);
        var identityAfter = await IdentityAsync(connection, context.Sid, token);
        Assert.Equal(identityBefore, identityAfter);
        Assert.Equal(connectionId, connection.ClientConnectionId);
        token.ThrowIfCancellationRequested();
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
        var evaluation = SqlAgentActivityEvaluator.Evaluate(capture);
        Assert.False(capture.ProductionAdmitted);
        Assert.False(capture.ServiceSessionAssociationQualified);
        Assert.Equal(SqlAgentActivityNativeQualification.Absent, capture.NativeQualification);
        Assert.Equal(SqlAgentActivityGap.ServiceNotRunning, evaluation.Gap);
        Assert.All(evaluation.Jobs, job =>
        {
            Assert.Equal(SqlAgentActivityCandidateState.Unknown, job.CandidateState);
            Assert.Null(job.StartedAtUtc); Assert.Null(job.ElapsedSeconds); Assert.Null(job.LongRunning);
        });
        Emit("signed_metadata_capture", new
        {
            beforeAndAfterMetadataVerified = true, definitionVerified = true, publicCertificateVerified = true,
            signatureVerified = true, sameOwnedSessionVerified = true, directPermissionsUnchanged = true,
            directSessionsSelectError = 229, capture.SourceRowsRead, retainedRows = capture.Jobs.Count,
            productionAdmitted = false, continuousStabilityQualified = false, elapsedQualified = false,
        });
    });

    [Fact]
    public Task ActualMonitorUnsignedMetadataPreventsCaptureAndExecutionIsDenied() => Safely(async token =>
    {
        var context = await PrepareAsync(true, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        token = deadline.Token;
        long started = Stopwatch.GetTimestamp();
        await using var connection = await OpenAsync(context.Request, token);
        Guid connectionId = connection.ClientConnectionId;
        var identityBefore = await IdentityAsync(connection, context.Sid, token);
        await BaselineAsync(connection, token);
        string sql = await LoadProbeAsync(token);
        var before = await SqlAgentActivityModuleMetadataProbe.ReadAsync(connection, context.Enrollment, sql, Remaining(started), token);
        Assert.Equal(SqlAgentModuleSignatureState.Unsigned, before.State);
        Assert.False(before.CaptureEligible);
        // Separate negative experiment, deliberately bypassing capture admission to prove SQL denial.
        // It cannot produce an accepted capture: exactly one error envelope and no source rows are allowed.
        using (var command = new SqlCommand("""
            BEGIN TRY
              EXEC sys.sp_executesql N'EXEC master.sqlobserver_agent.capture_v1 @maximum_rows=513;';
              SELECT CONVERT(int,0) AS error_number;
            END TRY BEGIN CATCH SELECT CONVERT(int,ERROR_NUMBER()) AS error_number; END CATCH;
            """, connection) { CommandTimeout = Math.Max(1, (int)Math.Ceiling(Remaining(started).TotalSeconds)) })
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.Equal(1, reader.FieldCount); Assert.Equal("error_number", reader.GetName(0));
            Assert.Equal(typeof(int), reader.GetFieldType(0));
            Assert.True(await reader.ReadAsync(token)); Assert.Equal(229, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync(token)); Assert.False(await reader.NextResultAsync(token));
        }
        var after = await SqlAgentActivityModuleMetadataProbe.ReadAsync(connection, context.Enrollment, sql, Remaining(started), token);
        Assert.Equal(SqlAgentModuleSignatureState.Unsigned, after.State); Assert.False(after.CaptureEligible);
        Assert.True(after.ObservedAtUtc >= before.ObservedAtUtc);
        Assert.Equal(before with { ObservedAtUtc = after.ObservedAtUtc }, after);
        await BaselineAsync(connection, token);
        Assert.Equal(identityBefore, await IdentityAsync(connection, context.Sid, token));
        Assert.Equal(connectionId, connection.ClientConnectionId);
        token.ThrowIfCancellationRequested();
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
        Emit("unsigned_metadata_denial", new
        {
            definitionAndPublicCertificateStillVerified = true, signatureAbsent = true,
            captureEligible = false, captureAccepted = false, negativeExecutionError = 229,
            sourceResultSetsObserved = 0, sameOwnedSessionVerified = true, directPermissionsUnchanged = true,
            attachedInvalidSignatureQualified = false, explicitDenyQualified = false, productionAdmitted = false,
        });
    });

    private static async ValueTask<SqlConnection> OpenAsync(CollectorExecutionRequest request, CancellationToken token)
        => await new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName)
            .OpenConnectionAsync(request.ConnectionPolicy, token);

    private static TimeSpan Remaining(long started)
    {
        TimeSpan remaining = TimeSpan.FromSeconds(10) - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Metadata capture budget exhausted.");
        return remaining;
    }

    private static async Task<string> LoadProbeAsync(CancellationToken token)
    {
        using Stream stream = typeof(SqlAgentActivityMetadataQualificationTests).Assembly.GetManifestResourceStream(
            "SqlObserver.IntegrationTests.SqlServer.verify-metadata-v2.sql") ?? throw new InvalidDataException("Probe asset missing.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token);
    }

    private static async Task<Context> PrepareAsync(bool unsigned, CancellationToken token)
    {
        SqlAgentModuleMetadataEnrollment enrollment = SqlAgentMetadataQualificationEnrollment.Load(Environment.GetEnvironmentVariable, unsigned);
        var lab = PairedCpuQualificationLabContract.Load(Environment.GetEnvironmentVariable);
        Assert.Equal(17, lab.ExpectedSqlMajor);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(identity.User?.Value == lab.ExpectedWindowsSid, "Actual monitor identity mismatch; values withheld.");
        byte[] sid = new byte[identity.User!.BinaryLength]; identity.User.GetBinaryForm(sid, 0);
        var profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(lab.DiscoveryRequest(), token);
        Assert.Equal(lab.TargetId, profile.TargetId); Assert.Equal(lab.TargetRevision, profile.TargetRevision);
        Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded);
        Assert.Equal(17, profile.ServerIdentity!.Version.Major); Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.True(profile.TransportEncrypted); Assert.False(profile.IsSysAdmin);
        Assert.True(profile.AuthenticationScheme is SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm);
        return new(lab.ExecutionRequest(profile), sid, enrollment);
    }

    private sealed record Context(CollectorExecutionRequest Request, byte[] Sid, SqlAgentModuleMetadataEnrollment Enrollment);

    private static async Task<(Guid Connection, int Session)> IdentityAsync(SqlConnection connection, byte[] sid, CancellationToken token)
    {
        string query = SqlServerCpuTargetWitnessReader.LoadMonitorIdentityQuery().Replace("CONVERT(bit,", "CONVERT(int,", StringComparison.Ordinal);
        using var command = new SqlCommand(query, connection) { CommandTimeout = 5 };
        command.Parameters.Add("@expected_sid", SqlDbType.VarBinary, 68).Value = sid;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal(12, reader.FieldCount); Assert.True(await reader.ReadAsync(token));
        for (int index = 1; index <= 8; index++) Assert.Equal(1, reader.GetInt32(index));
        var result = (reader.GetGuid(9), reader.GetInt32(10));
        Assert.NotEqual(Guid.Empty, result.Item1); Assert.True(result.Item2 > 0); Assert.Equal(1, reader.GetInt32(11));
        Assert.False(await reader.ReadAsync(token)); Assert.False(await reader.NextResultAsync(token));
        return result;
    }

    private static async Task BaselineAsync(SqlConnection connection, CancellationToken token)
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

    private void Emit(string stage, object evidence) => output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, stage, evidence }));
    private static async Task Safely(Func<CancellationToken, Task> action)
    {
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await action(deadline.Token); deadline.Token.ThrowIfCancellationRequested(); }
        catch (Exception error)
        {
            throw new XunitException("Metadata module trial failed; type=" + error.GetType().Name +
                (error is SqlException sql ? "; SQL number=" + sql.Number.ToString(CultureInfo.InvariantCulture) : "") + "; private details withheld.");
        }
    }

    private sealed class CaptureReader(SqlDataReader reader) : ISqlAgentActivityResultReader
    {
        public int FieldCount => reader.FieldCount;
        public string GetName(int ordinal) => reader.GetName(ordinal);
        public Type GetFieldType(int ordinal) => reader.GetFieldType(ordinal);
        public bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);
        public int GetInt32(int ordinal) => reader.GetInt32(ordinal);
        public Guid GetGuid(int ordinal) => reader.GetGuid(ordinal);
        public DateTime GetDateTime(int ordinal) => reader.GetDateTime(ordinal);
        public DateTimeOffset GetDateTimeOffset(int ordinal) => reader.GetDateTimeOffset(ordinal);
        public async ValueTask<bool> ReadAsync(CancellationToken token) => await reader.ReadAsync(token);
        public async ValueTask<bool> NextResultAsync(CancellationToken token) => await reader.NextResultAsync(token);
    }
}
