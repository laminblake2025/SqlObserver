using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlObserver.AgentActivityTrial;

public sealed record TrialRunResult(Guid DeploymentId, bool QualificationPassed, bool CleanupVerified, string? FailureCode);

/// <summary>Coordinates one finite trial. Caller supplies an already open, reviewed integrated TLS DBA connection.
/// Qualification is a trusted bounded callback; it must finish its owned monitor process before returning.</summary>
public static class TrialCoordinator
{


    public static async Task<TrialBaseline> PreviewAsync(SqlConnection connection, string principal, Guid deploymentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await AcquireAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            TrialBaseline baseline = await AgentActivityTrialSql.CaptureBaselineAsync(connection, transaction, principal, deploymentId, cancellationToken).ConfigureAwait(false);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return baseline;
        }
        finally { await ReleaseAsync(connection).ConfigureAwait(false); }
    }

    public static Task<TrialRunResult> RunAsync(SqlConnection connection, string principal, Guid deploymentId,
        byte[] moduleUtf8, Func<CancellationToken, Task<bool>> qualify, CancellationToken cancellationToken)
        => RunCoreAsync(connection, principal, deploymentId, moduleUtf8, qualify, null, cancellationToken);

    /// <summary>Qualifies the signed module, records a committed unsigned postimage, then qualifies
    /// permission denial under the same actual monitor. Both callbacks must join their owned processes.</summary>
    public static Task<TrialRunResult> RunSignatureRemovalAsync(SqlConnection connection, string principal, Guid deploymentId,
        byte[] moduleUtf8, Func<CancellationToken, Task<bool>> qualifySigned,
        Func<CancellationToken, Task<bool>> qualifyUnsigned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(qualifyUnsigned);
        return RunCoreAsync(connection, principal, deploymentId, moduleUtf8, qualifySigned, qualifyUnsigned, cancellationToken);
    }

    private static async Task<TrialRunResult> RunCoreAsync(SqlConnection connection, string principal, Guid deploymentId,
        byte[] moduleUtf8, Func<CancellationToken, Task<bool>> qualify,
        Func<CancellationToken, Task<bool>>? qualifyUnsigned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(qualify);
        AgentActivityTrialSql.ValidateModule(moduleUtf8);
        await AcquireAsync(connection, cancellationToken).ConfigureAwait(false);
        Guid connectionId = connection.ClientConnectionId;
        TrialJournal? journal = null;
        bool passed = false;
        bool removed = false;
        string? failure = null;
        string stage = "baseline";
        try
        {
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                TrialBaseline baseline = await AgentActivityTrialSql.CaptureBaselineAsync(connection, transaction, principal, deploymentId, cancellationToken).ConfigureAwait(false);
                stage = "journal_prepare";
                journal = TrialJournal.Create(deploymentId);
                var receipt = new TrialReceipt(qualifyUnsigned is null ? 1 : 2, deploymentId, principal, "prepared", baseline, null, null);
                journal.Save(receipt);
                stage = "install_verify";
                TrialSnapshot installed = await AgentActivityTrialSql.InstallAsync(connection, transaction, baseline, moduleUtf8, cancellationToken).ConfigureAwait(false);
                receipt = receipt with { Phase = "commit_pending", Installed = installed };
                stage = "install_commit";
                // Write-ahead postimage: if the process dies during COMMIT, recovery checks live state.
                journal.Save(receipt);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                journal.Save(receipt with { Phase = "installed" });
            }
            // Recheck committed state before exposing the EXECUTE-only module to the monitor callback.
            stage = "committed_verify";
            await using (var verify = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                TrialReceipt receipt = journal.Read();
                TrialSnapshot actual = await AgentActivityTrialSql.CaptureInstalledAsync(connection, verify, receipt.Baseline, cancellationToken).ConfigureAwait(false);
                AgentActivityTrialSql.ValidateExactSnapshot(receipt.Installed!, actual);
                await verify.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            stage = "qualification";
            passed = await qualify(cancellationToken).ConfigureAwait(false);
            if (!passed) failure = "qualification_failed";
            if (passed && qualifyUnsigned is not null)
            {
                passed = false;
                stage = "signature_remove_verify";
                await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                {
                    TrialReceipt receipt = journal.Read();
                    TrialSnapshot unsigned = await AgentActivityTrialSql.RemoveSignatureAsync(connection, transaction,
                        receipt.Baseline, receipt.Installed!, cancellationToken).ConfigureAwait(false);
                    receipt = receipt with { Phase = "signature_remove_pending", UnsignedSnapshot = unsigned };
                    stage = "signature_remove_commit";
                    // Either exact postimage is recoverable if COMMIT acknowledgement is lost.
                    journal.Save(receipt);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    journal.Save(receipt with { Phase = "unsigned" });
                }
                stage = "unsigned_committed_verify";
                await using (var verify = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                {
                    TrialReceipt receipt = journal.Read();
                    TrialSnapshot actual = await AgentActivityTrialSql.CaptureUnsignedAsync(connection, verify,
                        receipt.Baseline, receipt.Installed!, cancellationToken).ConfigureAwait(false);
                    AgentActivityTrialSql.ValidateExactSnapshot(receipt.UnsignedSnapshot!, actual);
                    await verify.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                stage = "unsigned_qualification";
                passed = await qualifyUnsigned(cancellationToken).ConfigureAwait(false);
                if (!passed) failure = "unsigned_qualification_failed";
            }
        }
        catch (Exception exception)
        {
            // SQL messages may contain deployment data or DDL. Persist only type and numeric code.
            failure = stage + ":" + FailureCode(exception);
        }
        finally
        {
            // Cancellation never authorizes leaving a known installed helper behind. Cleanup gets
            // a separate finite budget; connection loss/drift leaves a recovery receipt and stops.
            if (journal is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    RequireSameSession(connection, connectionId);
                    await RecoverUnderLockAsync(connection, journal, failure, cleanup.Token).ConfigureAwait(false);
                    removed = true;
                }
                catch (Exception exception)
                {
                    failure = "recovery_required:" + FailureCode(exception);
                }
            }
            try
            {
                RequireSameSession(connection, connectionId);
                await ReleaseAsync(connection).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= "lock_release_unconfirmed:" + FailureCode(exception);
            }
        }
        return new(deploymentId, passed, removed, failure);
    }

    public static async Task<TrialRunResult> RecoverAsync(SqlConnection connection, Guid deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        TrialJournal journal = TrialJournal.Open(deploymentId);
        await AcquireAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverUnderLockAsync(connection, journal, "explicit_recovery", cancellationToken).ConfigureAwait(false);
            return new(deploymentId, false, true, "qualification_not_replayed");
        }
        finally { await ReleaseAsync(connection).ConfigureAwait(false); }
    }

    private static async Task RecoverUnderLockAsync(SqlConnection connection, TrialJournal journal, string? failure, CancellationToken token)
    {
        TrialReceipt receipt = journal.Read();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        try
        {
            TrialBaseline absent = await AgentActivityTrialSql.CaptureBaselineAsync(connection, transaction, receipt.MonitorPrincipal, receipt.DeploymentId, token).ConfigureAwait(false);
            AgentActivityTrialSql.ValidateBaselineRestored(receipt.Baseline, absent);
            await transaction.RollbackAsync(token).ConfigureAwait(false);
            journal.Save(receipt with { Phase = "removed", FailureCode = failure });
            return;
        }
        catch (TrialObjectsPresentException)
        {
            // Presence alone never grants ownership. Exact postimage and deployment marker are next.
        }
        if (receipt.Installed is null || receipt.Phase == "removed")
            throw new InvalidDataException("Present SQL objects have no matching owned installed receipt.");
        bool unsigned = receipt.Phase is "unsigned" or "unsigned_remove_pending";
        if (receipt.Phase == "signature_remove_pending")
        {
            try
            {
                TrialSnapshot signed = await AgentActivityTrialSql.CaptureInstalledAsync(connection, transaction, receipt.Baseline, token).ConfigureAwait(false);
                AgentActivityTrialSql.ValidateExactSnapshot(receipt.Installed, signed);
            }
            catch (InvalidDataException)
            {
                // An uncertain signature-removal COMMIT permits only the other recorded exact
                // image. Drift still fails its independent unsigned validation below.
                unsigned = true;
            }
        }
        TrialSnapshot installed = unsigned
            ? await AgentActivityTrialSql.CaptureUnsignedAsync(connection, transaction, receipt.Baseline, receipt.Installed, token).ConfigureAwait(false)
            : await AgentActivityTrialSql.CaptureInstalledAsync(connection, transaction, receipt.Baseline, token).ConfigureAwait(false);
        AgentActivityTrialSql.ValidateExactSnapshot(unsigned ? receipt.UnsignedSnapshot! : receipt.Installed, installed);
        receipt = receipt with { Phase = unsigned ? "unsigned_remove_pending" : "remove_pending", FailureCode = failure };
        journal.Save(receipt);
        if (unsigned)
            await AgentActivityTrialSql.RemoveUnsignedAsync(connection, transaction, receipt.Baseline, receipt.Installed, receipt.UnsignedSnapshot!, token).ConfigureAwait(false);
        else
            await AgentActivityTrialSql.RemoveAsync(connection, transaction, receipt.Baseline, receipt.Installed, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        // A second read after COMMIT proves the baseline, rather than assuming an acknowledgement.
        await using var verify = (SqlTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        TrialBaseline restored = await AgentActivityTrialSql.CaptureBaselineAsync(connection, verify, receipt.MonitorPrincipal, receipt.DeploymentId, token).ConfigureAwait(false);
        AgentActivityTrialSql.ValidateBaselineRestored(receipt.Baseline, restored);
        await verify.RollbackAsync(token).ConfigureAwait(false);
        journal.Save(receipt with { Phase = "removed" });
    }

    private static async Task AcquireAsync(SqlConnection connection, CancellationToken token)
    {
        if (connection.State != ConnectionState.Open || connection.ClientConnectionId == Guid.Empty)
            throw new InvalidOperationException("An open reviewed DBA connection is required.");
        var settings = new SqlConnectionStringBuilder(connection.ConnectionString);
        if (!settings.IntegratedSecurity || settings.TrustServerCertificate || settings.ConnectRetryCount != 0 || settings.Pooling ||
            settings.Encrypt == SqlConnectionEncryptOption.Optional ||
            !settings.DataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(settings.HostNameInCertificate) || settings.InitialCatalog != "master")
            throw new InvalidOperationException("The DBA connection must use the enrolled TCP endpoint, integrated identity, validated TLS and no transparent reconnect.");
        await using var command = new SqlCommand("""
            USE [master]; DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=N'SqlObserver.AgentActivityTrialV1',
              @LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=5000;
            SELECT @result;
            """, connection) { CommandTimeout = 10 };
        try
        {
            object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (result is not int code || code < 0) throw new InvalidOperationException("The SQL trial lock is unavailable.");
        }
        catch
        {
            // The server may have acquired the lock before a timeout reached the caller.
            // This dedicated nonpooled session must not survive an uncertain acquisition.
            await connection.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ReleaseAsync(SqlConnection connection)
    {
        if (connection.State != ConnectionState.Open) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var command = new SqlCommand("""
            USE [master]; DECLARE @result int;
            EXEC @result=sys.sp_releaseapplock @Resource=N'SqlObserver.AgentActivityTrialV1',@LockOwner=N'Session';
            SELECT @result;
            """, connection) { CommandTimeout = 5 };
        try
        {
            object? result = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            if (result is not int code || code < 0) throw new InvalidOperationException("The SQL trial lock release was not confirmed.");
        }
        catch
        {
            await connection.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string FailureCode(Exception exception) => exception switch
    {
        SqlException sql => "sql_" + sql.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        TrialSqlMetadataException query => query.Section + "_sql_" + query.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_line_" + query.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        TrialMetadataException metadata => "metadata_" + metadata.RecordTypeName + "_length_" + metadata.JsonLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        InvalidDataException invalid when SafeInvariantMessages.Contains(invalid.Message) => "invariant:" + invalid.Message,
        _ => exception.GetType().Name,
    };

    // Exact constants from our validator only. Never expose arbitrary exception or SQL messages.
    private static readonly HashSet<string> SafeInvariantMessages = new(StringComparer.Ordinal)
    {
        "Trial identity or monitor mapping drifted.",
        "The installed bounded object inventory is not exact.",
        "The temporary monitor user differs from its attested Windows login.",
        "Trial object names differ from the fixed package.",
        "Package principal type or ownership differs from the plan.",
        "Required trial evidence is absent or duplicated.",
        "Temporary monitor mapping or provenance marker is invalid.",
        "An existing monitor user cannot be adopted by the trial.",
        "Definition, deployment marker, certificate or signature verification failed.",
        "Unexpected package consumers, permissions or ownership block the trial.",
        "Package signature inventory differs from the single expected module signature.",
        "Base source object identity is invalid.",
        "Temporary monitor CONNECT inventory is not exact.",
        "Package grants differ from the exact five-grant plan.",
        "Temporary monitor permission inventories disagree.",
        "Pre-existing monitor permissions changed.",
        "Pre-existing monitor mappings, roles or base source objects changed.",
        "Trial snapshot drift requires manual review; no cleanup was authorized.",
    };

    private static void RequireSameSession(SqlConnection connection, Guid expected)
    {
        if (connection.State != ConnectionState.Open || connection.ClientConnectionId != expected)
            throw new InvalidOperationException("The locked DBA session was lost; explicit recovery is required.");
    }
}
