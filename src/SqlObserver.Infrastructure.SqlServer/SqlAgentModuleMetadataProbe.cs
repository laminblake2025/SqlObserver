using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlObserver.Infrastructure.SqlServer;

// Construct only from independently trusted DBA enrollment, never from the monitor's probe output.
internal sealed record SqlAgentModuleMetadataPins(int ObjectId, int CertificateId,
    string DefinitionSha256, string PublicCertificateSha256, string ThumbprintHex);

internal enum SqlAgentModuleMetadataSignatureState { SignedVerified, Unsigned }

internal sealed record SqlAgentModuleMetadataReceipt(int ObjectId, int CertificateId,
    string DefinitionSha256, string PublicCertificateSha256, string ThumbprintHex,
    SqlAgentModuleMetadataSignatureState State, int SessionId, DateTime ObservedAtUtc, Guid ClientConnectionId)
{
    // Metadata eligibility for the next guarded command, never production admission.
    internal bool CaptureEligible => State == SqlAgentModuleMetadataSignatureState.SignedVerified;
}

/// <summary>Fixed external metadata query. Caller owns the open connection, actual-monitor
/// identity attestation and the total before/capture/after deadline. No connection is opened here.
/// A successful precheck must complete before submitting capture EXEC as a separate command.</summary>
internal static class SqlAgentModuleMetadataProbe
{
    internal const string ResourceName = "SqlObserver.Infrastructure.SqlServer.AgentModuleAssets.sql-agent.activity.module-metadata.sqlserver17-windows.v1.sql";
    internal const string SqlSha256 = "3cf2c35cc8c371f88e3e9334f2d4ba45ceb9375749bc110baa09e61c5fe876e9";
    private const string Failure = "Agent module metadata evidence is invalid; private values withheld.";
    private static readonly (string Name, Type Type)[] Header =
    [
        ("schema_version", typeof(int)), ("database_id", typeof(int)), ("database_name", typeof(string)),
        ("session_id", typeof(int)), ("observed_utc", typeof(DateTime)), ("object_id", typeof(int)),
        ("schema_name", typeof(string)), ("module_name", typeof(string)), ("module_type", typeof(string)),
        ("execute_as_principal_id", typeof(int)), ("uses_ansi_nulls", typeof(int)), ("uses_quoted_identifier", typeof(int)),
        ("definition_sha256", typeof(string)), ("certificate_id", typeof(int)), ("certificate_name", typeof(string)),
        ("public_certificate_sha256", typeof(string)), ("thumbprint_hex", typeof(string)), ("thumbprint_bytes", typeof(int)),
        ("private_key_state", typeof(string)), ("object_view_definition", typeof(int)),
        ("certificate_view_definition", typeof(int)), ("is_object_signed", typeof(int))
    ];
    private static readonly (string Name, Type Type)[] Inventory =
        [("signature_class", typeof(int)), ("object_id", typeof(int)), ("crypt_type", typeof(string)), ("thumbprint_hex", typeof(string))];
    private static readonly (string Name, Type Type)[] Verification =
        [("object_id", typeof(int)), ("is_signed", typeof(int)), ("is_signature_valid", typeof(int))];

    internal static string LoadSql()
    {
        using Stream stream = typeof(SqlAgentModuleMetadataProbe).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(Failure);
        if (stream.Length is <= 0 or > 16384) throw new InvalidDataException(Failure);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return ValidateSql(reader.ReadToEnd());
    }

    internal static string ValidateSql(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (sql.Length is <= 0 or > 16384) throw new InvalidDataException(Failure);
        string normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))) != SqlSha256)
            throw new InvalidDataException(Failure);
        return normalized;
    }

    internal static async Task<SqlAgentModuleMetadataReceipt> ReadAsync(SqlConnection connection,
        SqlAgentModuleMetadataPins enrollment, string fixedSqlText, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEnrollment(enrollment);
        string sql = ValidateSql(fixedSqlText);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(budget));
        Guid connectionId = connection.ClientConnectionId;
        if (connection.State != ConnectionState.Open || connectionId == Guid.Empty || connection.Database != "master")
            throw new InvalidDataException(Failure);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        long started = Stopwatch.GetTimestamp();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds)) };
        try
        {
            SqlAgentModuleMetadataReceipt observation;
            await using (SqlDataReader reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
                observation = await ParseAsync(reader, enrollment, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (connection.State != ConnectionState.Open || connection.ClientConnectionId != connectionId ||
                connection.Database != "master") throw new InvalidDataException(Failure);
            if (Stopwatch.GetElapsedTime(started) >= budget) throw new TimeoutException("Agent module metadata command deadline expired.");
            return observation with { ClientConnectionId = connectionId };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Agent module metadata command deadline expired."); }
        catch (SqlException error)
        {
            // Do not retain SQL text, metadata values or server exception details in a test failure.
            throw ClassifySqlFailure(error.Number);
        }
    }

    internal static Exception ClassifySqlFailure(int number)
    {
        if (SqlServerCollectorErrorClassifier.IsCommandTimeout(number))
            return new TimeoutException("Agent module metadata command deadline expired.");
        if (SqlServerCollectorErrorClassifier.IsPermissionDenied(number))
            return new UnauthorizedAccessException("Agent module metadata permissions are unavailable.");
        return new InvalidDataException("Agent module metadata command failed; SQL number=" + number.ToString(CultureInfo.InvariantCulture) + ".");
    }

    internal static async Task<SqlAgentModuleMetadataReceipt> ParseAsync(DbDataReader reader,
        SqlAgentModuleMetadataPins enrollment, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reader);
        token.ThrowIfCancellationRequested();
        ValidateEnrollment(enrollment);
        Schema(reader, Header);
        if (!await ReadAsync(reader, token).ConfigureAwait(false)) throw new InvalidDataException(Failure);
        int schema = Number(reader, 0), database = Number(reader, 1);
        string databaseName = Text(reader, 2, 128);
        int session = Number(reader, 3);
        if (reader.IsDBNull(4)) throw new InvalidDataException(Failure);
        DateTime observed = reader.GetDateTime(4);
        int objectId = Number(reader, 5);
        string schemaName = Text(reader, 6, 128), moduleName = Text(reader, 7, 128), type = Text(reader, 8, 2);
        bool caller = reader.IsDBNull(9);
        int ansi = Number(reader, 10), quoted = Number(reader, 11);
        string definition = Text(reader, 12, 64);
        int certificateId = Number(reader, 13);
        string certificateName = Text(reader, 14, 128), publicHash = Text(reader, 15, 64), thumbprint = Text(reader, 16, 64);
        int thumbprintBytes = Number(reader, 17);
        string privateKey = Text(reader, 18, 60);
        int objectView = Number(reader, 19), certificateView = Number(reader, 20);
        int? objectSigned = reader.IsDBNull(21) ? null : Number(reader, 21);
        if (schema != 2 || database != 1 || databaseName != "master" || session <= 0 || session > 32767 ||
            observed == DateTime.MinValue || observed.Kind == DateTimeKind.Local || objectId != enrollment.ObjectId ||
            schemaName != "sqlobserver_agent" || moduleName != "capture_v1" || type != "P" || !caller || ansi != 1 || quoted != 1 ||
            definition != enrollment.DefinitionSha256 || certificateId != enrollment.CertificateId ||
            certificateName != "SqlObserverAgentActivityCertificateV1" || publicHash != enrollment.PublicCertificateSha256 ||
            thumbprint != enrollment.ThumbprintHex || thumbprintBytes != 20 || privateKey != "NO_PRIVATE_KEY" ||
            objectView != 1 || certificateView != 1)
            throw new InvalidDataException(Failure);
        if (await ReadAsync(reader, token).ConfigureAwait(false) || !await NextAsync(reader, token).ConfigureAwait(false))
            throw new InvalidDataException(Failure);

        Schema(reader, Inventory);
        bool hasSignature = await ReadAsync(reader, token).ConfigureAwait(false);
        if (hasSignature)
        {
            if (Number(reader, 0) != 1 || Number(reader, 1) != objectId || Text(reader, 2, 4) != "SPVC" || Text(reader, 3, 64) != thumbprint)
                throw new InvalidDataException(Failure);
            if (await ReadAsync(reader, token).ConfigureAwait(false)) throw new InvalidDataException(Failure);
        }
        if (!await NextAsync(reader, token).ConfigureAwait(false)) throw new InvalidDataException(Failure);
        Schema(reader, Verification);
        if (!await ReadAsync(reader, token).ConfigureAwait(false) || Number(reader, 0) != objectId) throw new InvalidDataException(Failure);
        int signed = Number(reader, 1), valid = Number(reader, 2);
        SqlAgentModuleMetadataSignatureState state;
        if (hasSignature && objectSigned == 1 && signed == 1 && valid == 1) state = SqlAgentModuleMetadataSignatureState.SignedVerified;
        else if (!hasSignature && objectSigned is null && signed == 0 && valid == 0) state = SqlAgentModuleMetadataSignatureState.Unsigned;
        else throw new InvalidDataException(Failure);
        if (await ReadAsync(reader, token).ConfigureAwait(false) || await NextAsync(reader, token).ConfigureAwait(false)) throw new InvalidDataException(Failure);
        return new(objectId, certificateId, definition, publicHash, thumbprint, state, session,
            DateTime.SpecifyKind(observed, DateTimeKind.Utc), Guid.Empty);
    }

    internal static void RequireStableSigned(SqlAgentModuleMetadataReceipt before, SqlAgentModuleMetadataReceipt after)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        if (!before.CaptureEligible || !after.CaptureEligible || before.ClientConnectionId == Guid.Empty ||
            after.ObservedAtUtc < before.ObservedAtUtc || (before with { ObservedAtUtc = after.ObservedAtUtc }) != after)
            throw new InvalidDataException(Failure);
        // Matching point-in-time observations do not exclude a privileged change-and-restore.
    }

    private static void ValidateEnrollment(SqlAgentModuleMetadataPins enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        if (enrollment.ObjectId <= 0 || enrollment.CertificateId <= 0 || !Hex(enrollment.DefinitionSha256, 64) ||
            !Hex(enrollment.PublicCertificateSha256, 64) || !Hex(enrollment.ThumbprintHex, 40)) throw new InvalidDataException(Failure);
    }
    private static bool Hex(string value, int length) => value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Schema(DbDataReader reader, (string Name, Type Type)[] expected)
    {
        if (reader.FieldCount != expected.Length) throw new InvalidDataException(Failure);
        for (int index = 0; index < expected.Length; index++)
            if (reader.GetName(index) != expected[index].Name || reader.GetFieldType(index) != expected[index].Type) throw new InvalidDataException(Failure);
    }
    private static int Number(DbDataReader reader, int index) => !reader.IsDBNull(index) ? reader.GetInt32(index) : throw new InvalidDataException(Failure);
    private static string Text(DbDataReader reader, int index, int maximum)
    {
        if (reader.IsDBNull(index)) throw new InvalidDataException(Failure);
        long length = reader.GetChars(index, 0, null, 0, 0);
        if (length is <= 0 || length > maximum) throw new InvalidDataException(Failure);
        string value = reader.GetString(index);
        if (value.Length != length) throw new InvalidDataException(Failure);
        return value;
    }
    private static async Task<bool> ReadAsync(DbDataReader reader, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return await reader.ReadAsync(token).ConfigureAwait(false); }
    private static async Task<bool> NextAsync(DbDataReader reader, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return await reader.NextResultAsync(token).ConfigureAwait(false); }
}
