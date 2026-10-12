using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SqlObserver.AgentActivityTrial;

/// <summary>Finite failure fixture on one package-owned permission. Never alters monitor grants,
/// signatures, source objects, jobs or services, and never commits the caller's transaction.</summary>
public static class AgentActivityTrialDenySql
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const TrialInstallationPolicy Policy = TrialInstallationPolicy.MetadataVerificationV2;

    public static TrialSnapshot ExpectedDenied(TrialBaseline baseline, TrialSnapshot signedSnapshot)
    {
        AgentActivityTrialSql.ValidateInstalled(baseline, signedSnapshot, Policy);
        TrialGrant grant = SourceGrant(signedSnapshot);
        return signedSnapshot with { PackagePermissions = Order(signedSnapshot.PackagePermissions.Select(row => row == grant ? row with { State = "D" } : row)) };
    }

    /// <summary>Only G→D of the exact same recipient/grantor/source permission is permitted.</summary>
    public static void ValidateDenied(TrialBaseline baseline, TrialSnapshot signedSnapshot, TrialSnapshot denied) =>
        AgentActivityTrialSql.ValidateExactSnapshot(ExpectedDenied(baseline, signedSnapshot), denied);

    public static async Task<TrialSnapshot> CaptureDeniedAsync(SqlConnection connection, SqlTransaction transaction,
        TrialBaseline baseline, TrialSnapshot signedSnapshot, CancellationToken cancellationToken)
    {
        _ = ExpectedDenied(baseline, signedSnapshot);
        TrialSnapshot actual = await AgentActivityTrialSql.CaptureDenyInventoryAsync(connection, transaction, baseline, cancellationToken).ConfigureAwait(false);
        ValidateDenied(baseline, signedSnapshot, actual);
        return actual;
    }

    public static async Task<TrialSnapshot> DenyAsync(SqlConnection connection, SqlTransaction transaction,
        TrialBaseline baseline, TrialSnapshot signedSnapshot, CancellationToken cancellationToken)
    {
        TrialSnapshot actual = await AgentActivityTrialSql.CaptureInstalledAsync(connection, transaction, baseline, cancellationToken, Policy).ConfigureAwait(false);
        AgentActivityTrialSql.ValidateExactSnapshot(signedSnapshot, actual);
        TrialGrant grant = SourceGrant(signedSnapshot);
        await ChangeAsync(connection, transaction, grant, deny: true, cancellationToken).ConfigureAwait(false);
        return await CaptureDeniedAsync(connection, transaction, baseline, signedSnapshot, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<TrialSnapshot> RestoreAsync(SqlConnection connection, SqlTransaction transaction,
        TrialBaseline baseline, TrialSnapshot signedSnapshot, TrialSnapshot denied, CancellationToken cancellationToken)
    {
        ValidateDenied(baseline, signedSnapshot, denied);
        TrialSnapshot actual = await CaptureDeniedAsync(connection, transaction, baseline, signedSnapshot, cancellationToken).ConfigureAwait(false);
        AgentActivityTrialSql.ValidateExactSnapshot(denied, actual);
        await ChangeAsync(connection, transaction, SourceGrant(signedSnapshot), deny: false, cancellationToken).ConfigureAwait(false);
        TrialSnapshot restored = await AgentActivityTrialSql.CaptureInstalledAsync(connection, transaction, baseline, cancellationToken, Policy).ConfigureAwait(false);
        AgentActivityTrialSql.ValidateExactSnapshot(signedSnapshot, restored);
        return restored;
    }

    private static TrialGrant SourceGrant(TrialSnapshot signedSnapshot)
    {
        int recipient = signedSnapshot.Objects.Single(row => row.Kind == "certificate_user").Id;
        string source = signedSnapshot.Facts.Single(row => row.Scope == "msdb" && row.Kind == "base_object" && row.Key == "syssessions").Value;
        if (!int.TryParse(source, NumberStyles.None, CultureInfo.InvariantCulture, out int objectId) || objectId <= 0)
            throw new InvalidDataException("The DENY source object identity is invalid.");
        var grants = signedSnapshot.PackagePermissions.Where(row => row.Scope == "msdb" && row.GranteeId == recipient &&
            row.Class == 1 && row.MajorId == objectId && row.MinorId == 0 && row.Name == "SELECT").ToArray();
        // The fixed installer creates these grants as dbo. Do not impersonate or
        // adopt a different grantor to make a foreign image fit this trial.
        if (grants.Length != 1 || grants[0].State != "G" || grants[0].GrantorId != 1)
            throw new InvalidDataException("The DENY fixture requires its exact dbo-granted package permission.");
        return grants[0];
    }

    private static async Task ChangeAsync(SqlConnection connection, SqlTransaction transaction, TrialGrant grant,
        bool deny, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open || transaction.Connection != connection)
            throw new InvalidOperationException("The DENY fixture requires its active owned transaction.");
        const string guard = """
            USE [msdb]; SET XACT_ABORT ON; SET LOCK_TIMEOUT 5000;
            IF DATABASE_PRINCIPAL_ID(N'SqlObserverAgentActivityCertificateUserV1') IS NULL OR
               DATABASE_PRINCIPAL_ID(N'SqlObserverAgentActivityCertificateUserV1')<>@recipient OR
               OBJECT_ID(N'dbo.syssessions') IS NULL OR OBJECT_ID(N'dbo.syssessions')<>@source OR
               DATABASE_PRINCIPAL_ID(N'dbo')<>@grantor OR
               (SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=@recipient
                AND grantor_principal_id=@grantor AND class=1 AND major_id=@source AND minor_id=0
                AND permission_name=N'SELECT' AND state=@before)<>1
              THROW 51000,'The package-owned DENY permission changed before the fixed mutation.',1;
            """;
        string mutation = deny
            ? "DENY SELECT ON OBJECT::[dbo].[syssessions] TO [SqlObserverAgentActivityCertificateUserV1] AS [dbo];"
            : "GRANT SELECT ON OBJECT::[dbo].[syssessions] TO [SqlObserverAgentActivityCertificateUserV1] AS [dbo];";
        await using var command = new SqlCommand(guard + "\n" + mutation, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.Add("@recipient", SqlDbType.Int).Value = grant.GranteeId;
        command.Parameters.Add("@source", SqlDbType.Int).Value = grant.MajorId;
        command.Parameters.Add("@grantor", SqlDbType.Int).Value = grant.GrantorId;
        command.Parameters.Add("@before", SqlDbType.Char, 1).Value = deny ? "G" : "D";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TrialGrant[] Order(IEnumerable<TrialGrant> values) =>
        values.OrderBy(row => JsonSerializer.Serialize(row, Json), StringComparer.Ordinal).ToArray();
}
