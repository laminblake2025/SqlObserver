using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlObserver.AgentActivityTrial;

public sealed record TrialSignatureBlob(int SourceObjectId, string SourceDefinitionUtf16Sha256,
    string CertificateThumbprintHex, string Hex, int Length, string Sha256)
{
    public override string ToString() => "Private actual SQL signature blob provenance";
}
public sealed record TrialSignatureDonorInstallation(TrialSnapshot Installed, TrialSignatureBlob Original, TrialSignatureBlob Donor)
{
    public override string ToString() => "Private signature donor installation";
}
public sealed record TrialAttachedInvalidSignatureImage(TrialSnapshot Snapshot, TrialSignatureBlob Signature)
{
    public override string ToString() => "Private attached-invalid signature image";
}
public sealed class TrialAttachedInvalidSignatureSqlException : InvalidOperationException
{
    internal TrialAttachedInvalidSignatureSqlException(string operation, SqlException error)
        : base("Signature construction SQL failed; private values withheld.")
    { Operation = operation; Number = error.Number; State = error.State; Severity = error.Class; LineNumber = error.LineNumber; }
    public string Operation { get; }
    public int Number { get; }
    public byte State { get; }
    public byte Severity { get; }
    public int LineNumber { get; }
}
public sealed class TrialSignatureAttachmentRefusedException : InvalidOperationException
{
    internal TrialSignatureAttachmentRefusedException(SqlException error)
        : base("Supplied SQL signature attachment was refused; private values withheld.")
    { SqlNumber = error.Number; SqlState = error.State; }
    public int SqlNumber { get; }
    public byte SqlState { get; }
}

/// <summary>Construction feasibility only. Owns no connection, transaction commit, monitor,
/// journal or restoration. Caller must unconditionally roll back its probe transaction.</summary>
public static class TrialAttachedInvalidSignatureSql
{
    public const int MaximumSignatureBytes = 4096;
    public const string DonorDefinitionUtf16Sha256 = "9be6e70edd1ea0e2b24241f141fbb28dc229fef9074ce496837df4deafaf5018";
    public const string DonorDefinition = "CREATE PROCEDURE [sqlobserver_agent].[signature_donor_v1]\nAS\nBEGIN\n    SET NOCOUNT ON;\n    SELECT CONVERT(int, 1) AS [donor_constant];\nEND;\n";
    private static readonly Type[] BlobTypes = [typeof(int), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string)];

    public static async Task<TrialSignatureDonorInstallation> InstallWithDonorAsync(SqlConnection connection,
        SqlTransaction transaction, TrialBaseline baseline, byte[] moduleBytes, Guid fixtureId, CancellationToken token)
    {
        if (fixtureId == Guid.Empty) throw Invalid();
        var created = await AgentActivityTrialSql.InstallWithSignatureDonorCoreAsync(connection, transaction, baseline, moduleBytes, fixtureId, token).ConfigureAwait(false);
        TrialObject module = created.Installed.Objects.Single(x => x.Kind == "module");
        TrialSignatureBlob original = await ReadBlobAsync(connection, transaction, module.Id, token).ConfigureAwait(false);
        var installation = new TrialSignatureDonorInstallation(created.Installed, original, created.Donor ?? throw Invalid());
        ValidateInstallation(baseline, installation);
        return installation;
    }

    public static async Task<TrialAttachedInvalidSignatureImage> ReplaceWithDonorSignatureAsync(SqlConnection connection,
        SqlTransaction transaction, TrialBaseline baseline, TrialSignatureDonorInstallation installation, CancellationToken token)
    {
        ValidateInstallation(baseline, installation);
        TrialSnapshot signed = await AgentActivityTrialSql.CaptureInstalledAsync(connection, transaction, baseline, token, TrialInstallationPolicy.MetadataVerificationV2).ConfigureAwait(false);
        AgentActivityTrialSql.ValidateExactSnapshot(installation.Installed, signed);
        TrialSignatureBlob original = await ReadBlobAsync(connection, transaction, installation.Original.SourceObjectId, token).ConfigureAwait(false);
        if (original != installation.Original) throw Invalid();
        byte[] bytes = Convert.FromHexString(installation.Donor.Hex);
        try
        {
            await ExecuteAsync(connection, transaction, """
                USE [master]; SET XACT_ABORT ON; SET LOCK_TIMEOUT 5000;
                DROP SIGNATURE FROM [sqlobserver_agent].[capture_v1]
                  BY CERTIFICATE [SqlObserverAgentActivityCertificateV1];
                """, "signature_drop", token).ConfigureAwait(false);
            await using var import = Command(connection, transaction, """
                USE [master];
                DECLARE @ddl nvarchar(max)=N'ADD SIGNATURE TO OBJECT::[sqlobserver_agent].[capture_v1] BY CERTIFICATE [SqlObserverAgentActivityCertificateV1] WITH SIGNATURE = '
                  +CONVERT(nvarchar(max),@blob,1)+N';';
                EXEC sys.sp_executesql @ddl;
                """);
            import.Parameters.Add("@blob", SqlDbType.VarBinary, MaximumSignatureBytes).Value = bytes;
            try { await import.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
            catch (SqlException error) { throw new TrialSignatureAttachmentRefusedException(error); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        // Import acceptance is not assumed. Read the engine's actual complete inventory
        // and cryptographic status; only a real attached 1:0 tuple can return successfully.
        return await CaptureAttachedInvalidAsync(connection, transaction, baseline, installation, token).ConfigureAwait(false);
    }

    public static async Task<TrialAttachedInvalidSignatureImage> CaptureAttachedInvalidAsync(SqlConnection connection,
        SqlTransaction transaction, TrialBaseline baseline, TrialSignatureDonorInstallation installation, CancellationToken token)
    {
        ValidateInstallation(baseline, installation);
        TrialSnapshot actual = await AgentActivityTrialSql.CaptureSignatureInventoryAsync(connection, transaction, baseline, token).ConfigureAwait(false);
        TrialSignatureBlob signature = await ReadBlobAsync(connection, transaction, installation.Original.SourceObjectId, token).ConfigureAwait(false);
        var image = new TrialAttachedInvalidSignatureImage(actual, signature);
        ValidateAttachedInvalid(baseline, installation, image);
        return image;
    }

    public static void ValidateInstallation(TrialBaseline baseline, TrialSignatureDonorInstallation installation)
    {
        if (baseline is null || installation is null || installation.Installed is null) throw Invalid();
        AgentActivityTrialSql.ValidateInstalled(baseline, installation.Installed, TrialInstallationPolicy.MetadataVerificationV2);
        ValidateBlob(installation.Original); ValidateBlob(installation.Donor);
        int objectId = installation.Installed.Objects.Single(x => x.Kind == "module").Id;
        string thumbprint = installation.Installed.Facts.Single(x => x.Scope == "master" && x.Kind == "certificate" && x.Key == "thumbprint").Value.ToLowerInvariant();
        if (installation.Original.SourceObjectId != objectId || installation.Original.SourceDefinitionUtf16Sha256 != AgentActivityTrialSql.DefinitionUtf16Sha256 ||
            installation.Donor.SourceObjectId == objectId || installation.Donor.SourceDefinitionUtf16Sha256 != DonorDefinitionUtf16Sha256 ||
            installation.Original.CertificateThumbprintHex != thumbprint || installation.Donor.CertificateThumbprintHex != thumbprint ||
            installation.Original.Hex == installation.Donor.Hex || installation.Original.Sha256 == installation.Donor.Sha256) throw Invalid();
    }

    public static void ValidateAttachedInvalid(TrialBaseline baseline, TrialSignatureDonorInstallation installation, TrialAttachedInvalidSignatureImage image)
    {
        ValidateInstallation(baseline, installation);
        if (image is null || image.Snapshot is null) throw Invalid();
        ValidateBlob(image.Signature);
        TrialSnapshot expected = installation.Installed with
        {
            Facts = Order(installation.Installed.Facts.Select(x => x.Scope == "master" && x.Kind == "signature_validity" && x.Key == "module" ? x with { Value = "1:0" } : x)),
        };
        AgentActivityTrialSql.ValidateExactSnapshot(expected, image.Snapshot);
        // Actual target metadata must independently describe the approved target, while
        // the actual signature bytes must match the separately generated donor capsule.
        TrialSignatureBlob expectedBlob = installation.Donor with
        { SourceObjectId = installation.Original.SourceObjectId, SourceDefinitionUtf16Sha256 = installation.Original.SourceDefinitionUtf16Sha256 };
        if (image.Signature != expectedBlob) throw Invalid();
    }

    internal static async Task<TrialSignatureBlob> CreateDonorAsync(SqlConnection connection, SqlTransaction transaction,
        TrialBaseline baseline, Guid fixtureId, string password, CancellationToken token)
    {
        if (fixtureId == Guid.Empty || baseline is null || string.IsNullOrWhiteSpace(password) || password.Length > 128 ||
            Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(DonorDefinition))) != DonorDefinitionUtf16Sha256) throw Invalid();
        await ExecuteAsync(connection, transaction, """
            USE [master]; SET XACT_ABORT ON; SET LOCK_TIMEOUT 5000;
            IF OBJECT_ID(N'sqlobserver_agent.signature_donor_v1') IS NOT NULL OR
              NOT EXISTS(SELECT 1 FROM sys.schemas WHERE name=N'sqlobserver_agent' AND principal_id=USER_ID(N'SqlObserverAgentActivitySchemaOwnerV1')) OR
              NOT EXISTS(SELECT 1 FROM sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1' AND pvt_key_encryption_type_desc=N'ENCRYPTED_BY_PASSWORD')
              THROW 51000,'Signature donor precondition failed.',1;
            """, "signature_donor_preflight", token).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, DonorDefinition, "signature_donor_create", token).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            USE [master];
            EXEC sys.sp_addextendedproperty @name=N'SqlObserver.AgentActivity.SignatureDonorFixtureId',@value=@fixture,
              @level0type=N'SCHEMA',@level0name=N'sqlobserver_agent',@level1type=N'PROCEDURE',@level1name=N'signature_donor_v1';
            DECLARE @ddl nvarchar(max)=N'ADD SIGNATURE TO OBJECT::[sqlobserver_agent].[signature_donor_v1] BY CERTIFICATE [SqlObserverAgentActivityCertificateV1] WITH PASSWORD = '+QUOTENAME(@password,'''')+N';';
            EXEC sys.sp_executesql @ddl;
            DECLARE @donor int=OBJECT_ID(N'sqlobserver_agent.signature_donor_v1',N'P'),
              @thumb varbinary(20)=(SELECT thumbprint FROM sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1');
            IF @donor IS NULL OR NOT EXISTS(SELECT 1 FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id JOIN sys.sql_modules m ON m.object_id=o.object_id
              WHERE o.object_id=@donor AND COALESCE(o.principal_id,s.principal_id)=USER_ID(N'SqlObserverAgentActivitySchemaOwnerV1') AND m.execute_as_principal_id IS NULL AND m.uses_ansi_nulls=1 AND m.uses_quoted_identifier=1
              AND m.definition=@definition COLLATE Latin1_General_100_BIN2) OR
              (SELECT COUNT(*) FROM sys.extended_properties WHERE class=1 AND major_id=@donor)<>1 OR
              NOT EXISTS(SELECT 1 FROM sys.extended_properties WHERE class=1 AND major_id=@donor AND minor_id=0 AND name=N'SqlObserver.AgentActivity.SignatureDonorFixtureId' AND CONVERT(nvarchar(36),value)=@fixture) OR
              EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND major_id=@donor OR class=3 AND major_id=SCHEMA_ID(N'sqlobserver_agent')) OR
              (SELECT COUNT(*) FROM sys.crypt_properties WHERE class=1 AND major_id=@donor)<>1 OR
              NOT EXISTS(SELECT 1 FROM sys.crypt_properties WHERE class=1 AND major_id=@donor AND crypt_type='SPVC' AND thumbprint=@thumb) OR
              (SELECT COUNT(*) FROM sys.fn_check_object_signatures(N'certificate',@thumb) WHERE entity_id=@donor)<>1 OR
              NOT EXISTS(SELECT 1 FROM sys.fn_check_object_signatures(N'certificate',@thumb) WHERE entity_id=@donor AND is_signed=1 AND is_signature_valid=1)
              THROW 51000,'Signature donor provenance failed.',1;
            """, "signature_donor_sign_verify", token, new SqlParameter("@fixture", SqlDbType.NVarChar, 36) { Value = fixtureId.ToString("D") },
            new SqlParameter("@password", SqlDbType.NVarChar, 128) { Value = password }, new SqlParameter("@definition", SqlDbType.NVarChar, -1) { Value = DonorDefinition }).ConfigureAwait(false);
        int donorId;
        await using (var command = Command(connection, transaction, "SELECT CONVERT(int,OBJECT_ID(N'sqlobserver_agent.signature_donor_v1',N'P'));"))
        {
            try { donorId = await command.ExecuteScalarAsync(token).ConfigureAwait(false) is int value && value > 0 ? value : throw Invalid(); }
            catch (SqlException error) { throw new TrialAttachedInvalidSignatureSqlException("signature_donor_identity", error); }
        }
        TrialSignatureBlob blob = await ReadBlobAsync(connection, transaction, donorId, token).ConfigureAwait(false);
        if (blob.SourceDefinitionUtf16Sha256 != DonorDefinitionUtf16Sha256) throw Invalid();
        await ExecuteAsync(connection, transaction, """
            USE [master]; DROP PROCEDURE [sqlobserver_agent].[signature_donor_v1];
            IF OBJECT_ID(N'sqlobserver_agent.signature_donor_v1') IS NOT NULL OR
              EXISTS(SELECT 1 FROM sys.crypt_properties WHERE class=1 AND major_id=@donor) OR
              EXISTS(SELECT 1 FROM sys.extended_properties WHERE class=1 AND major_id=@donor) OR
              EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND major_id=@donor)
              THROW 51000,'Signature donor residue remained.',1;
            """, "signature_donor_drop_verify", token, new SqlParameter("@donor", SqlDbType.Int) { Value = donorId }).ConfigureAwait(false);
        return blob;
    }

    private static async Task<TrialSignatureBlob> ReadBlobAsync(SqlConnection connection, SqlTransaction transaction, int objectId, CancellationToken token)
    {
        await using var command = Command(connection, transaction, """
            USE [master];
            SELECT CONVERT(int,cp.major_id),LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),m.definition)),2)),
              LOWER(CONVERT(varchar(40),cp.thumbprint,2)),LOWER(CONVERT(varchar(max),cp.crypt_property,2)),CONVERT(int,DATALENGTH(cp.crypt_property)),
              LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',cp.crypt_property),2))
            FROM sys.crypt_properties cp JOIN sys.sql_modules m ON m.object_id=cp.major_id
            WHERE cp.class=1 AND cp.major_id=@object AND cp.crypt_type='SPVC' AND DATALENGTH(cp.thumbprint)=20 AND
              DATALENGTH(cp.crypt_property) BETWEEN 1 AND 4096 AND DATALENGTH(m.definition) BETWEEN 1 AND 65536;
            """);
        command.Parameters.Add("@object", SqlDbType.Int).Value = objectId;
        try
        {
            await using SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (reader.FieldCount != BlobTypes.Length) throw Invalid();
            for (int index = 0; index < BlobTypes.Length; index++) if (reader.GetFieldType(index) != BlobTypes[index]) throw Invalid();
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw Invalid();
            for (int index = 0; index < BlobTypes.Length; index++) if (reader.IsDBNull(index)) throw Invalid();
            var blob = new TrialSignatureBlob(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5));
            ValidateBlob(blob);
            if (blob.SourceObjectId != objectId || await reader.ReadAsync(token).ConfigureAwait(false) || await reader.NextResultAsync(token).ConfigureAwait(false)) throw Invalid();
            return blob;
        }
        catch (SqlException error) { throw new TrialAttachedInvalidSignatureSqlException("signature_blob_read", error); }
    }
    private static void ValidateBlob(TrialSignatureBlob blob)
    {
        if (blob is null || blob.SourceObjectId <= 0 || blob.Length is < 1 or > MaximumSignatureBytes || !Hex(blob.SourceDefinitionUtf16Sha256, 64) ||
            !Hex(blob.CertificateThumbprintHex, 40) || !Hex(blob.Sha256, 64) || !Hex(blob.Hex, blob.Length * 2)) throw Invalid();
        byte[] bytes = Convert.FromHexString(blob.Hex);
        try { if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != blob.Sha256) throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static TrialFact[] Order(IEnumerable<TrialFact> values) => values.OrderBy(x => System.Text.Json.JsonSerializer.Serialize(x), StringComparer.Ordinal).ToArray();
    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        if (connection is null || transaction is null || connection.State != ConnectionState.Open || transaction.Connection != connection || connection.Database != "master") throw Invalid();
        return new SqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
    }
    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, string operation, CancellationToken token, params SqlParameter[] parameters)
    {
        await using var command = Command(connection, transaction, sql); command.Parameters.AddRange(parameters);
        try { await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        catch (SqlException error) { throw new TrialAttachedInvalidSignatureSqlException(operation, error); }
    }
    private static InvalidDataException Invalid() => new("Attached-invalid signature construction evidence is invalid; private values withheld.");
}
