using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SqlObserver.AgentActivityTrial;

public sealed record TrialIdentity(string ServerName, string MachineName, string InstanceName,
    string ProductVersion, int MajorVersion, int EngineEdition, string PathSeparator,
    Guid MasterDatabaseGuid, Guid MsdbDatabaseGuid);
public sealed record TrialMonitor(string Principal, string SidHex, int LoginId, int MasterUserId);
public sealed record TrialObject(string Kind, string Database, string Name, int Id, int? OwnerId,
    string Type, string Detail);
public sealed record TrialGrant(string Scope, int GranteeId, int GrantorId, int Class,
    int MajorId, int MinorId, string Name, string State);
public sealed record TrialFact(string Scope, string Kind, string Key, string Value);
public sealed record TrialSnapshot(TrialIdentity Identity, TrialMonitor Monitor, Guid DeploymentId,
    IReadOnlyList<TrialObject> Objects, IReadOnlyList<TrialGrant> PackagePermissions,
    IReadOnlyList<TrialGrant> MonitorPermissions, IReadOnlyList<TrialFact> Facts);
public sealed record TrialBaseline(TrialSnapshot Snapshot)
{
    public Guid DeploymentId => Snapshot.DeploymentId;
    public string MonitorPrincipal => Snapshot.Monitor.Principal;
}
public sealed class TrialObjectsPresentException : InvalidOperationException
{
    public TrialObjectsPresentException() : base("Named trial objects already exist; fresh installation is forbidden.") { }
}
public sealed class TrialMetadataException : InvalidOperationException
{
    internal TrialMetadataException(string recordTypeName, int jsonLength, long? lineNumber = null, long? bytePositionInLine = null)
        : base("Required SQL metadata is empty or malformed.")
    {
        RecordTypeName = recordTypeName;
        JsonLength = jsonLength;
        LineNumber = lineNumber;
        BytePositionInLine = bytePositionInLine;
    }

    public string RecordTypeName { get; }
    /// <summary>Length in UTF-16 code units; no source text is retained.</summary>
    public int JsonLength { get; }
    public long? LineNumber { get; }
    public long? BytePositionInLine { get; }
}
public sealed class TrialSqlMetadataException : InvalidOperationException
{
    internal TrialSqlMetadataException(string section,SqlException error)
        : base("SQL metadata query failed; only bounded diagnostic fields are retained.")
    {
        Section=section;
        Number=error.Number;
        State=error.State;
        Severity=error.Class;
        LineNumber=error.LineNumber;
    }

    public string Section { get; }
    public int Number { get; }
    public byte State { get; }
    public byte Severity { get; }
    public int LineNumber { get; }
}

/// <summary>
/// Finite SQL17 qualification transaction library. Never opens a connection or commits a transaction.
/// The caller must persist a verified postimage receipt before commit, and roll back on every failure.
/// No method grants direct source permissions to the monitor or performs a permission fallback.
/// </summary>
public static class AgentActivityTrialSql
{
    public const string ModuleUtf8Sha256 = "0ac04136ac0f04b718df47eb7e7f3fe07bd66c00f94b976e175bad5fefdd8991";
    public const string DefinitionUtf16Sha256 = "447846fb7a7eaa0442b2292b17b82ee17aaf998c3b91c161cba403f7c0bf8512";
    private const string Marker = "SqlObserver.AgentActivity.DeploymentId";
    private const int TimeoutSeconds = 15;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<TrialBaseline> CaptureBaselineAsync(SqlConnection connection, SqlTransaction transaction,
        string monitorPrincipal, Guid deploymentId, CancellationToken cancellationToken)
    {
        ValidatePrincipal(monitorPrincipal);
        if (deploymentId == Guid.Empty) throw new ArgumentException("A deployment ID is required.", nameof(deploymentId));
        var state = await CaptureAsync(connection, transaction, monitorPrincipal, deploymentId, cancellationToken).ConfigureAwait(false);
        RequireAbsent(state);
        return new(state);
    }

    public static async Task<TrialSnapshot> InstallAsync(SqlConnection connection, SqlTransaction transaction,
        TrialBaseline baseline, byte[] pinnedModuleUtf8, CancellationToken cancellationToken, TrialInstallationPolicy policy = TrialInstallationPolicy.ExecuteOnlyV1)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(pinnedModuleUtf8);
        ValidatePolicy(policy);
        byte[] moduleBytes = (byte[])pinnedModuleUtf8.Clone();
        ValidateModule(moduleBytes);
        RequireAbsent(baseline.Snapshot);
        var current = await CaptureAsync(connection, transaction, baseline.Snapshot.Monitor.Principal,
            baseline.Snapshot.DeploymentId, cancellationToken).ConfigureAwait(false);
        ValidateExactSnapshot(baseline.Snapshot, current);
        RequireAbsent(current);
        if (current.Monitor.MasterUserId == 0)
        {
            await ExecuteAsync(connection, transaction, """
                USE [master];
                DECLARE @ddl nvarchar(max)=N'CREATE USER '+QUOTENAME(@monitor)+N' FOR LOGIN '+QUOTENAME(@monitor)+N';';
                EXEC sys.sp_executesql @ddl;
                EXEC sys.sp_addextendedproperty @name=N'SqlObserver.AgentActivity.DeploymentId',@value=@deployment,
                  @level0type=N'USER',@level0name=@monitor;
                """, cancellationToken,P("@monitor",SqlDbType.NVarChar,current.Monitor.Principal,128),
                P("@deployment",SqlDbType.NVarChar,current.DeploymentId.ToString("D"),36)).ConfigureAwait(false);
        }
        // No secret is returned in records, command text, or application output. The random secret
        // is supplied as a parameter; the fixed DDL quotes it inside SQL Server.
        string password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            await ExecuteAsync(connection, transaction, """
                USE [master]; SET XACT_ABORT ON; SET LOCK_TIMEOUT 5000;
                CREATE USER [SqlObserverAgentActivitySchemaOwnerV1] WITHOUT LOGIN;
                REVOKE CONNECT FROM [SqlObserverAgentActivitySchemaOwnerV1];
                EXEC(N'CREATE SCHEMA [sqlobserver_agent] AUTHORIZATION [SqlObserverAgentActivitySchemaOwnerV1]');
                EXEC sys.sp_addextendedproperty @name=N'SqlObserver.AgentActivity.DeploymentId',@value=@deployment,
                  @level0type=N'SCHEMA',@level0name=N'sqlobserver_agent';
                DECLARE @ddl nvarchar(max)=N'CREATE CERTIFICATE [SqlObserverAgentActivityCertificateV1] AUTHORIZATION [dbo] ENCRYPTION BY PASSWORD = '
                    +QUOTENAME(@password,'''')+N' WITH SUBJECT = ''SqlObserver Agent activity qualification v1'', EXPIRY_DATE = ''20991231'';';
                EXEC sys.sp_executesql @ddl;
                CREATE LOGIN [SqlObserverAgentActivityCertificateLoginV1] FROM CERTIFICATE [SqlObserverAgentActivityCertificateV1];
                REVOKE CONNECT SQL FROM [SqlObserverAgentActivityCertificateLoginV1];
                GRANT VIEW SERVER SECURITY STATE TO [SqlObserverAgentActivityCertificateLoginV1];
                """, cancellationToken, P("@password", SqlDbType.NVarChar, password, 128),
                P("@deployment", SqlDbType.NVarChar, current.DeploymentId.ToString("D"), 36)).ConfigureAwait(false);
            // CREATE PROCEDURE is the only statement in this exact, pinned batch.
            await ExecuteAsync(connection, transaction, "SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, new UTF8Encoding(false, true).GetString(moduleBytes), cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                USE [master];
                DECLARE @public varbinary(max)=CERTENCODED(CERT_ID(N'SqlObserverAgentActivityCertificateV1'));
                IF @public IS NULL OR DATALENGTH(@public) NOT BETWEEN 1 AND 16384 THROW 51000,'Invalid public certificate.',1;
                DECLARE @ddl nvarchar(max)=N'USE [msdb]; CREATE CERTIFICATE [SqlObserverAgentActivityCertificateV1] AUTHORIZATION [dbo] FROM BINARY = '
                    +CONVERT(nvarchar(max),@public,1)+N'; CREATE USER [SqlObserverAgentActivityCertificateUserV1] FROM CERTIFICATE [SqlObserverAgentActivityCertificateV1];
                    GRANT CONNECT TO [SqlObserverAgentActivityCertificateUserV1];
                    GRANT SELECT ON OBJECT::[dbo].[syssessions] TO [SqlObserverAgentActivityCertificateUserV1];
                    GRANT SELECT ON OBJECT::[dbo].[sysjobactivity] TO [SqlObserverAgentActivityCertificateUserV1];';
                EXEC sys.sp_executesql @ddl;
                SET @ddl=N'ADD SIGNATURE TO OBJECT::[sqlobserver_agent].[capture_v1] BY CERTIFICATE [SqlObserverAgentActivityCertificateV1] WITH PASSWORD = '+QUOTENAME(@password,'''')+N';';
                EXEC sys.sp_executesql @ddl;
                SET @ddl=N'GRANT EXECUTE ON OBJECT::[sqlobserver_agent].[capture_v1] TO '+QUOTENAME(@monitor)
                    +CASE WHEN @metadataPolicy=1 THEN N' AS [SqlObserverAgentActivitySchemaOwnerV1]' ELSE N'' END+N';';
                EXEC sys.sp_executesql @ddl;
                ALTER CERTIFICATE [SqlObserverAgentActivityCertificateV1] REMOVE PRIVATE KEY;
                """, cancellationToken, P("@password", SqlDbType.NVarChar, password, 128),
                P("@monitor", SqlDbType.NVarChar, current.Monitor.Principal, 128),
                P("@metadataPolicy", SqlDbType.Bit, policy == TrialInstallationPolicy.MetadataVerificationV2)).ConfigureAwait(false);
        }
        finally { password = string.Empty; }
        if (policy == TrialInstallationPolicy.MetadataVerificationV2)
        {
            await ExecuteAsync(connection, transaction, """
                USE [master];
                DECLARE @ddl nvarchar(max)=N'GRANT VIEW DEFINITION ON OBJECT::[sqlobserver_agent].[capture_v1] TO '
                  +QUOTENAME(@monitor)+N' AS [SqlObserverAgentActivitySchemaOwnerV1]; GRANT VIEW DEFINITION ON CERTIFICATE::[SqlObserverAgentActivityCertificateV1] TO '
                  +QUOTENAME(@monitor)+N' AS [dbo];';
                EXEC sys.sp_executesql @ddl;
                """, cancellationToken, P("@monitor", SqlDbType.NVarChar, current.Monitor.Principal, 128)).ConfigureAwait(false);
        }
        return await CaptureInstalledAsync(connection, transaction, baseline, cancellationToken, policy).ConfigureAwait(false);
    }

    public static async Task<TrialSnapshot> CaptureInstalledAsync(SqlConnection connection, SqlTransaction transaction,
        TrialBaseline baseline, CancellationToken cancellationToken, TrialInstallationPolicy policy = TrialInstallationPolicy.ExecuteOnlyV1)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ValidatePolicy(policy);
        var state = await CaptureAsync(connection, transaction, baseline.Snapshot.Monitor.Principal,
            baseline.Snapshot.DeploymentId, cancellationToken,baseline.Snapshot.Monitor.MasterUserId==0).ConfigureAwait(false);
        ValidateInstalled(baseline, state, policy);
        return state;
    }

    public static async Task RemoveAsync(SqlConnection connection, SqlTransaction transaction,
        TrialBaseline baseline, TrialSnapshot expectedInstalled, CancellationToken cancellationToken, TrialInstallationPolicy policy = TrialInstallationPolicy.ExecuteOnlyV1)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(expectedInstalled);
        ValidateInstalled(baseline, expectedInstalled, policy);
        TrialSnapshot actual = await CaptureInstalledAsync(connection, transaction, baseline, cancellationToken, policy).ConfigureAwait(false);
        ValidateExactSnapshot(expectedInstalled, actual);
        await RemoveVerifiedObjectsAsync(connection,transaction,baseline,cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes only the attested module signature inside the caller's transaction. The caller must
    /// persist the unsigned postimage before commit; no monitor test can observe an uncommitted removal.</summary>
    public static async Task<TrialSnapshot> RemoveSignatureAsync(SqlConnection connection,SqlTransaction transaction,
        TrialBaseline baseline,TrialSnapshot expectedSigned,CancellationToken cancellationToken, TrialInstallationPolicy policy = TrialInstallationPolicy.ExecuteOnlyV1)
    {
        ValidateInstalled(baseline,expectedSigned,policy);
        TrialSnapshot actual=await CaptureInstalledAsync(connection,transaction,baseline,cancellationToken,policy).ConfigureAwait(false);
        ValidateExactSnapshot(expectedSigned,actual);
        await ExecuteAsync(connection,transaction,"""
            USE [master]; SET XACT_ABORT ON; SET LOCK_TIMEOUT 5000;
            DROP SIGNATURE FROM [sqlobserver_agent].[capture_v1]
              BY CERTIFICATE [SqlObserverAgentActivityCertificateV1];
            """,cancellationToken).ConfigureAwait(false);
        return await CaptureUnsignedAsync(connection,transaction,baseline,expectedSigned,cancellationToken,policy).ConfigureAwait(false);
    }

    public static async Task<TrialSnapshot> CaptureUnsignedAsync(SqlConnection connection,SqlTransaction transaction,
        TrialBaseline baseline,TrialSnapshot expectedSigned,CancellationToken cancellationToken, TrialInstallationPolicy policy = TrialInstallationPolicy.ExecuteOnlyV1)
    {
        ValidateInstalled(baseline,expectedSigned,policy);
        var actual=await CaptureAsync(connection,transaction,baseline.MonitorPrincipal,baseline.DeploymentId,
            cancellationToken,baseline.Snapshot.Monitor.MasterUserId==0).ConfigureAwait(false);
        ValidateUnsigned(baseline,expectedSigned,actual,policy);
        return actual;
    }

    public static async Task RemoveUnsignedAsync(SqlConnection connection,SqlTransaction transaction,
        TrialBaseline baseline,TrialSnapshot expectedSigned,TrialSnapshot expectedUnsigned,CancellationToken cancellationToken, TrialInstallationPolicy policy = TrialInstallationPolicy.ExecuteOnlyV1)
    {
        ValidateUnsigned(baseline,expectedSigned,expectedUnsigned,policy);
        var actual=await CaptureUnsignedAsync(connection,transaction,baseline,expectedSigned,cancellationToken,policy).ConfigureAwait(false);
        ValidateExactSnapshot(expectedUnsigned,actual);
        await RemoveVerifiedObjectsAsync(connection,transaction,baseline,cancellationToken).ConfigureAwait(false);
    }

    private static async Task RemoveVerifiedObjectsAsync(SqlConnection connection,SqlTransaction transaction,
        TrialBaseline baseline,CancellationToken cancellationToken)
    {
        // Only fixed, receipt-verified package objects are dropped. Object deletion removes its
        // package-created EXECUTE and policy-specific metadata grants. The optional user name is receipt-verified and QUOTENAME-escaped.
        await ExecuteAsync(connection, transaction, """
            USE [master]; SET XACT_ABORT ON; SET LOCK_TIMEOUT 5000;
            DROP PROCEDURE [sqlobserver_agent].[capture_v1];
            IF @ownedMonitor=1
            BEGIN
              DECLARE @ddl nvarchar(max)=N'DROP USER '+QUOTENAME(@monitor)+N';';
              EXEC sys.sp_executesql @ddl;
            END;
            EXEC sys.sp_dropextendedproperty @name=N'SqlObserver.AgentActivity.DeploymentId',
              @level0type=N'SCHEMA',@level0name=N'sqlobserver_agent';
            DROP SCHEMA [sqlobserver_agent];
            DROP USER [SqlObserverAgentActivitySchemaOwnerV1];
            DROP LOGIN [SqlObserverAgentActivityCertificateLoginV1];
            USE [msdb];
            DROP USER [SqlObserverAgentActivityCertificateUserV1];
            DROP CERTIFICATE [SqlObserverAgentActivityCertificateV1];
            USE [master];
            DROP CERTIFICATE [SqlObserverAgentActivityCertificateV1];
            """, cancellationToken,P("@ownedMonitor",SqlDbType.Bit,baseline.Snapshot.Monitor.MasterUserId==0),
            P("@monitor",SqlDbType.NVarChar,baseline.MonitorPrincipal,128)).ConfigureAwait(false);
        var restored = await CaptureAsync(connection, transaction, baseline.Snapshot.Monitor.Principal,
            baseline.Snapshot.DeploymentId, cancellationToken).ConfigureAwait(false);
        RequireAbsent(restored);
        ValidateExactSnapshot(baseline.Snapshot, restored);
    }

    public static void ValidateModule(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is < 1 or > 65536 || bytes.Contains((byte)13) ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != ModuleUtf8Sha256)
            throw new InvalidDataException("The trial requires the exact approved module bytes.");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(text))) != DefinitionUtf16Sha256)
            throw new InvalidDataException("The approved SQL definition encoding is invalid.");
    }

    /// <summary>Pure, ordinal comparison. Every inventory must be in canonical order; duplicates are refused.</summary>
    public static void ValidateExactSnapshot(TrialSnapshot expected, TrialSnapshot actual)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(actual);
        ValidateInventory(expected); ValidateInventory(actual);
        if (JsonSerializer.Serialize(expected, Json) != JsonSerializer.Serialize(actual, Json))
            throw new InvalidDataException("Trial snapshot drift requires manual review; no cleanup was authorized.");
    }

    public static void ValidateBaselineRestored(TrialBaseline expected, TrialBaseline fresh)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(fresh);
        RequireAbsent(expected.Snapshot); RequireAbsent(fresh.Snapshot);
        ValidateExactSnapshot(expected.Snapshot, fresh.Snapshot);
    }

    /// <summary>An unsigned image is authorized only as the exact single-signature removal from an independently
    /// valid signed postimage. No definition, identity, ownership, permission or other fact may change.</summary>
    public static void ValidateUnsigned(TrialBaseline baseline,TrialSnapshot expectedSigned,TrialSnapshot unsignedSnapshot, TrialInstallationPolicy policy = TrialInstallationPolicy.ExecuteOnlyV1)
    {
        ValidateInstalled(baseline,expectedSigned,policy);
        ArgumentNullException.ThrowIfNull(unsignedSnapshot);
        var expectedUnsigned=expectedSigned with {
            Facts=Order(expectedSigned.Facts.Where(x=>x.Kind!="signature").Select(x=>
                x.Scope=="master" && x.Kind=="signature_validity" && x.Key=="module" ? x with {Value="0:0"} : x))
        };
        ValidateExactSnapshot(expectedUnsigned,unsignedSnapshot);
    }

    public static void ValidateInstalled(TrialBaseline baseline, TrialSnapshot installed, TrialInstallationPolicy policy = TrialInstallationPolicy.ExecuteOnlyV1)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(installed);
        ValidatePolicy(policy);
        RequireAbsent(baseline.Snapshot); ValidateInventory(installed);
        bool ownsMonitor = baseline.Snapshot.Monitor.MasterUserId==0;
        if (baseline.Snapshot.Identity != installed.Identity ||
            baseline.Snapshot.Monitor != (ownsMonitor ? installed.Monitor with {MasterUserId=0} : installed.Monitor) ||
            installed.Monitor.MasterUserId<=0 ||
            baseline.Snapshot.DeploymentId != installed.DeploymentId)
            throw new InvalidDataException("Trial identity or monitor mapping drifted.");
        string[] kinds = ownsMonitor
            ? ["certificate_login", "certificate_user", "master_certificate", "module", "monitor_user", "msdb_certificate", "owner_user", "schema"]
            : ["certificate_login", "certificate_user", "master_certificate", "module", "msdb_certificate", "owner_user", "schema"];
        if (!installed.Objects.Select(x => x.Kind).Order(StringComparer.Ordinal).SequenceEqual(kinds))
            throw new InvalidDataException("The installed bounded object inventory is not exact.");
        TrialObject Obj(string kind) => installed.Objects.Single(x => x.Kind == kind);
        if (ownsMonitor && (Obj("monitor_user").Database!="master" || Obj("monitor_user").Name!=installed.Monitor.Principal ||
            Obj("monitor_user").Id!=installed.Monitor.MasterUserId || Obj("monitor_user").Type!="U" ||
            Obj("monitor_user").Detail!=installed.Monitor.SidHex+":WINDOWS" || Obj("monitor_user").OwnerId is not (null or 1)))
            throw new InvalidDataException("The temporary monitor user differs from its attested Windows login.");
        var fixedNames = new[] {
            ("owner_user","master","SqlObserverAgentActivitySchemaOwnerV1"), ("schema","master","sqlobserver_agent"),
            ("module","master","capture_v1"), ("master_certificate","master","SqlObserverAgentActivityCertificateV1"),
            ("certificate_login","server","SqlObserverAgentActivityCertificateLoginV1"),
            ("msdb_certificate","msdb","SqlObserverAgentActivityCertificateV1"), ("certificate_user","msdb","SqlObserverAgentActivityCertificateUserV1")
        };
        if (fixedNames.Any(x => Obj(x.Item1).Database != x.Item2 || Obj(x.Item1).Name != x.Item3))
            throw new InvalidDataException("Trial object names differ from the fixed package.");
        if (Obj("owner_user").Type != "S" || Obj("owner_user").Detail != "NONE" ||
            Obj("schema").OwnerId != Obj("owner_user").Id || Obj("module").Type != "P" ||
            Obj("module").OwnerId != Obj("owner_user").Id || Obj("certificate_login").Type != "C" ||
            Obj("certificate_user").Type != "C" || Obj("master_certificate").OwnerId != 1 || Obj("msdb_certificate").OwnerId != 1)
            throw new InvalidDataException("Package principal type or ownership differs from the plan.");
        string Fact(string scope, string kind, string key)
        {
            var matches = installed.Facts.Where(x => x.Scope == scope && x.Kind == kind && x.Key == key).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Required trial evidence is absent or duplicated.");
            return matches[0].Value;
        }
        if (ownsMonitor && (Fact("master","monitor_marker",Marker)!=installed.DeploymentId.ToString("D") ||
            installed.Facts.Count(x=>x.Scope=="master" && x.Kind=="monitor_mapping")!=1 ||
            Fact("master","monitor_mapping",installed.Monitor.MasterUserId.ToString(System.Globalization.CultureInfo.InvariantCulture))!=
                installed.Monitor.Principal+":U:WINDOWS:"+installed.Monitor.SidHex))
            throw new InvalidDataException("Temporary monitor mapping or provenance marker is invalid.");
        if (!ownsMonitor && installed.Facts.Any(x=>x.Kind=="monitor_marker"))
            throw new InvalidDataException("An existing monitor user cannot be adopted by the trial.");
        if (Fact("master", "definition", "sha256") != DefinitionUtf16Sha256 ||
            Fact("master", "definition", "execute_as") != "CALLER" || Fact("master", "definition", "ansi") != "1:1" ||
            Fact("master", "marker", Marker) != installed.DeploymentId.ToString("D") ||
            Fact("master", "certificate", "private_key") != "NO_PRIVATE_KEY" || Fact("msdb", "certificate", "private_key") != "NO_PRIVATE_KEY" ||
            Fact("master", "certificate", "public_sha256") != Fact("msdb", "certificate", "public_sha256") ||
            Fact("master", "certificate", "thumbprint") != Fact("msdb", "certificate", "thumbprint") ||
            Obj("certificate_login").Detail.Split(':')[0] != Fact("master", "certificate", "sid") ||
            Obj("certificate_user").Detail != Fact("master", "certificate", "sid") ||
            Fact("master", "signature_validity", "module") != "1:1")
            throw new InvalidDataException("Definition, deployment marker, certificate or signature verification failed.");
        if (installed.Facts.Any(x => x.Kind.StartsWith("unexpected", StringComparison.Ordinal)))
            throw new InvalidDataException("Unexpected package consumers, permissions or ownership block the trial.");
        var signatures = installed.Facts.Where(x => x.Kind == "signature").ToArray();
        if (signatures.Length != 1 || signatures[0].Scope != "master" ||
            signatures[0].Key != Obj("module").Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            signatures[0].Value != "SPVC:" + Fact("master", "certificate", "thumbprint"))
            throw new InvalidDataException("Package signature inventory differs from the single expected module signature.");
        if (!int.TryParse(Fact("msdb", "base_object", "syssessions"), out int sessions) || sessions <= 0 ||
            !int.TryParse(Fact("msdb", "base_object", "sysjobactivity"), out int activity) || activity <= 0)
            throw new InvalidDataException("Base source object identity is invalid.");
        var expected = new[] {
            ("server",Obj("certificate_login").Id,100,0,0,"VIEW SERVER SECURITY STATE","G"),
            ("msdb",Obj("certificate_user").Id,0,0,0,"CONNECT","G"),
            ("msdb",Obj("certificate_user").Id,1,sessions,0,"SELECT","G"),
            ("msdb",Obj("certificate_user").Id,1,activity,0,"SELECT","G"),
            ("master",installed.Monitor.MasterUserId,1,Obj("module").Id,0,"EXECUTE","G")
        };
        TrialGrant[] metadataGrants = policy == TrialInstallationPolicy.MetadataVerificationV2
            ? [new("master", installed.Monitor.MasterUserId, Obj("owner_user").Id, 1, Obj("module").Id, 0, "VIEW DEFINITION", "G"),
               new("master", installed.Monitor.MasterUserId, 1, 25, Obj("master_certificate").Id, 0, "VIEW DEFINITION", "G")]
            : [];
        if (metadataGrants.Length != 0)
        {
            expected = [..expected, ..metadataGrants.Select(x => (x.Scope,x.GranteeId,x.Class,x.MajorId,x.MinorId,x.Name,x.State))];
            if (metadataGrants.Any(x => installed.PackagePermissions.Count(y => y == x) != 1 ||
                installed.MonitorPermissions.Count(y => y == x) != 1))
                throw new InvalidDataException("Metadata grants differ from the exact policy recipient, grantor or object.");
            var executeGrant = new TrialGrant("master", installed.Monitor.MasterUserId, Obj("owner_user").Id, 1, Obj("module").Id, 0, "EXECUTE", "G");
            if (installed.PackagePermissions.Count(x => x == executeGrant) != 1 || installed.MonitorPermissions.Count(x => x == executeGrant) != 1)
                throw new InvalidDataException("Monitor EXECUTE inventories disagree under the metadata policy.");
        }
        bool ImplicitConnect(TrialGrant x) => ownsMonitor && x.Scope=="master" && x.GranteeId==installed.Monitor.MasterUserId &&
            x.GrantorId==1 && x.Class==0 && x.MajorId==0 && x.MinorId==0 && x.Name=="CONNECT" && x.State=="G";
        if (installed.PackagePermissions.Count(ImplicitConnect)>1)
            throw new InvalidDataException("Temporary monitor CONNECT inventory is not exact.");
        var actual = installed.PackagePermissions.Where(x=>!ImplicitConnect(x)).Select(x => (x.Scope,x.GranteeId,x.Class,x.MajorId,x.MinorId,x.Name,x.State)).ToArray();
        if (actual.Length != expected.Length || expected.Any(x => actual.Count(y => y == x) != 1))
            throw new InvalidDataException("Package grants differ from the exact installation policy.");
        if (ownsMonitor && !installed.MonitorPermissions.Where(x=>x.Scope=="master").SequenceEqual(
            installed.PackagePermissions.Where(x=>x.Scope=="master" && x.GranteeId==installed.Monitor.MasterUserId)))
            throw new InvalidDataException("Temporary monitor permission inventories disagree.");
        var originalMonitor = installed.MonitorPermissions.Where(x => !ImplicitConnect(x) &&
            !metadataGrants.Contains(x) && !(x.Scope == "master" && x.Class == 1 && x.MajorId == Obj("module").Id &&
                (policy == TrialInstallationPolicy.ExecuteOnlyV1 ||
                    (x.GranteeId == installed.Monitor.MasterUserId && x.GrantorId == Obj("owner_user").Id && x.MinorId == 0 && x.Name == "EXECUTE" && x.State == "G")))).ToArray();
        if (!originalMonitor.SequenceEqual(baseline.Snapshot.MonitorPermissions))
            throw new InvalidDataException("Pre-existing monitor permissions changed.");
        var baselineFacts = installed.Facts.Where(x => (x.Kind is "monitor_role" or "monitor_mapping" or "base_object") &&
            !(ownsMonitor && x.Scope=="master" && x.Kind=="monitor_mapping")).ToArray();
        if (!baselineFacts.SequenceEqual(baseline.Snapshot.Facts))
            throw new InvalidDataException("Pre-existing monitor mappings, roles or base source objects changed.");
    }

    private static void ValidatePolicy(TrialInstallationPolicy policy)
    {
        if (policy is not (TrialInstallationPolicy.ExecuteOnlyV1 or TrialInstallationPolicy.MetadataVerificationV2))
            throw new InvalidDataException("Unknown trial installation policy.");
    }

    private static void RequireAbsent(TrialSnapshot snapshot)
    {
        ValidateInventory(snapshot);
        if (snapshot.Objects.Count != 0) throw new TrialObjectsPresentException();
        if (snapshot.DeploymentId == Guid.Empty || snapshot.Objects.Count != 0 || snapshot.PackagePermissions.Count != 0 ||
            snapshot.Facts.Any(x => x.Kind is not ("monitor_role" or "monitor_mapping" or "base_object")))
            throw new InvalidDataException("Package objects or consumers already exist; adoption and overwrite are forbidden.");
    }

    private static void ValidateInventory(TrialSnapshot state)
    {
        if (state is null || state.Identity is null || state.Monitor is null || state.Objects is null || state.PackagePermissions is null || state.MonitorPermissions is null || state.Facts is null)
            throw new InvalidDataException("Incomplete trial snapshot.");
        if (state.DeploymentId == Guid.Empty || state.Identity.MajorVersion != 17 || state.Identity.EngineEdition is not (2 or 3) ||
            state.Identity.PathSeparator != "\\" || state.Identity.MasterDatabaseGuid == Guid.Empty || state.Identity.MsdbDatabaseGuid == Guid.Empty ||
            !Text(state.Identity.ServerName,128) || !Text(state.Identity.MachineName,128) || !Text(state.Identity.InstanceName,128,true) ||
            !Text(state.Identity.ProductVersion,128) || !Text(state.Monitor.Principal,128) || !Text(state.Monitor.SidHex,172) ||
            state.Monitor.SidHex.Length % 2 != 0 || !state.Monitor.SidHex.All(Uri.IsHexDigit) || state.Monitor.LoginId <= 0 || state.Monitor.MasterUserId < 0)
            throw new InvalidDataException("Invalid trial identity evidence.");
        try { ValidatePrincipal(state.Monitor.Principal); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid monitor principal evidence.",error); }
        Ordered(state.Objects); Ordered(state.PackagePermissions); Ordered(state.MonitorPermissions); Ordered(state.Facts);
        if (state.Objects.Any(x => !Text(x.Kind,64) || !Text(x.Database,128) || !Text(x.Name,128) || !Text(x.Type,64) || !Text(x.Detail,1024,true) || x.Id <= 0) ||
            state.PackagePermissions.Concat(state.MonitorPermissions).Any(x => !Text(x.Scope,128) || !Text(x.Name,128) || !Text(x.State,1) || x.GranteeId < 0 || x.GrantorId < 0) ||
            state.Facts.Any(x => !Text(x.Scope,128) || !Text(x.Kind,64) || !Text(x.Key,1024) || !Text(x.Value,8192,true)))
            throw new InvalidDataException("Invalid trial inventory row.");
        if (state.Monitor.MasterUserId==0 && (state.Facts.Any(x=>x.Scope=="master" && x.Kind is "monitor_mapping" or "monitor_role") ||
            state.MonitorPermissions.Any(x=>x.Scope=="master")))
            throw new InvalidDataException("Absent monitor user has contradictory master evidence.");
        static bool Text(string? value,int maximum,bool empty=false) => value is not null && (empty || value.Length>0) && value.Length<=maximum;
        static void Ordered<T>(IReadOnlyList<T> values)
        {
            if (values.Count > 10000) throw new InvalidDataException("Trial inventory exceeds its bound.");
            string? previous = null;
            foreach (var value in values)
            {
                if (value is null) throw new InvalidDataException("Null trial inventory row.");
                string current = JsonSerializer.Serialize(value, Json);
                if (current.Length > 32768) throw new InvalidDataException("Trial inventory row exceeds its bound.");
                if (previous is not null && string.CompareOrdinal(previous, current) >= 0)
                    throw new InvalidDataException("Trial inventory is not unique and canonically ordered.");
                previous = current;
            }
        }
    }

    private static T[] Order<T>(IEnumerable<T> items) => items.OrderBy(x => JsonSerializer.Serialize(x, Json), StringComparer.Ordinal).ToArray();

    private static async Task<TrialSnapshot> CaptureAsync(SqlConnection connection, SqlTransaction transaction,
        string monitor, Guid deployment, CancellationToken token,bool ownsMonitorUser=false)
    {
        RequireTransaction(connection, transaction); ValidatePrincipal(monitor);
        await ExecuteAsync(connection, transaction, """
            USE [master]; SET XACT_ABORT ON; SET LOCK_TIMEOUT 5000;
            IF IS_SRVROLEMEMBER(N'sysadmin')<>1 OR IS_SRVROLEMEMBER(N'sysadmin') IS NULL
              THROW 51000,'A sysadmin provisioning connection is required for complete drift inspection.',1;
            DECLARE @status int;
            EXEC @status=sys.sp_getapplock @Resource=N'SqlObserver.AgentActivity.Qualification.V1',
              @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=5000;
            IF @status<0 THROW 51000,'Another qualification transaction owns this instance.',1;
            """, token).ConfigureAwait(false);
        var identity = await ReadOneAsync<TrialIdentity>(connection, transaction, """
            SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) ServerName,
              CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) MachineName,
              COALESCE(CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName')),N'') InstanceName,
              CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')) ProductVersion,
              CONVERT(int,SERVERPROPERTY('ProductMajorVersion')) MajorVersion,
              CONVERT(int,SERVERPROPERTY('EngineEdition')) EngineEdition,
              CONVERT(nvarchar(1),SERVERPROPERTY('PathSeparator')) PathSeparator,
              (SELECT database_guid FROM master.sys.database_recovery_status WHERE database_id=1) MasterDatabaseGuid,
              (SELECT database_guid FROM master.sys.database_recovery_status WHERE database_id=4) MsdbDatabaseGuid
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES
            """, "identity",token).ConfigureAwait(false);
        if (identity.MajorVersion != 17 || identity.EngineEdition is not (2 or 3) || identity.PathSeparator != "\\" ||
            identity.MasterDatabaseGuid == Guid.Empty || identity.MsdbDatabaseGuid == Guid.Empty)
            throw new InvalidDataException("The qualification target must be the exact supported Windows SQL17 instance.");
        var principal = await ReadOneAsync<TrialMonitor>(connection, transaction, """
            IF EXISTS(SELECT 1 FROM master.sys.database_principals d
              WHERE (d.name=@monitor OR d.sid=SUSER_SID(@monitor)) AND
                (d.name COLLATE Latin1_General_100_BIN2<>@monitor COLLATE Latin1_General_100_BIN2 OR
                 d.sid IS NULL OR d.sid<>SUSER_SID(@monitor) OR d.type<>'U' OR d.authentication_type_desc<>'WINDOWS'))
              THROW 51000,'A conflicting master principal prevents the bounded monitor mapping.',1;
            SELECT s.name Principal,CONVERT(varchar(172),s.sid,2) SidHex,s.principal_id LoginId,COALESCE(d.principal_id,0) MasterUserId
            FROM master.sys.server_principals s LEFT JOIN master.sys.database_principals d ON d.sid=s.sid
              AND d.name COLLATE Latin1_General_100_BIN2=@monitor COLLATE Latin1_General_100_BIN2
              AND d.type='U' AND d.authentication_type_desc='WINDOWS'
            WHERE s.name COLLATE Latin1_General_100_BIN2=@monitor COLLATE Latin1_General_100_BIN2
              AND s.type='U'
              AND s.is_disabled=0 AND IS_SRVROLEMEMBER(N'sysadmin',s.name)=0
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES
            """, "monitor_mapping",token, P("@monitor",SqlDbType.NVarChar,monitor,128)).ConfigureAwait(false);
        if (principal.LoginId <= 0 || principal.MasterUserId < 0 || string.IsNullOrEmpty(principal.SidHex))
            throw new InvalidDataException("Existing exact Windows monitor mapping was not verified.");
        var parameters = new[] { P("@monitor",SqlDbType.NVarChar,monitor,128), P("@sid",SqlDbType.VarBinary,Convert.FromHexString(principal.SidHex),85) };
        TrialObject[] objects = await ReadListAsync<TrialObject>(connection, transaction, ObjectsSql,"objects", token,
            P("@ownedMonitor",SqlDbType.Bit,ownsMonitorUser),P("@monitor",SqlDbType.NVarChar,monitor,128)).ConfigureAwait(false);
        TrialGrant[] packagePermissions = await ReadListAsync<TrialGrant>(connection, transaction, PackagePermissionsSql,"package_grants", token,
            P("@ownedMonitor",SqlDbType.Bit,ownsMonitorUser),P("@monitor",SqlDbType.NVarChar,monitor,128)).ConfigureAwait(false);
        TrialGrant[] monitorPermissions = await ReadListAsync<TrialGrant>(connection, transaction, MonitorPermissionsSql,"monitor_grants", token, parameters).ConfigureAwait(false);
        var facts = (await ReadListAsync<TrialFact>(connection, transaction, FactsSql,"facts", token,
            P("@monitor",SqlDbType.NVarChar,monitor,128),P("@sid",SqlDbType.VarBinary,Convert.FromHexString(principal.SidHex),85),
            P("@ownedMonitor",SqlDbType.Bit,ownsMonitorUser)).ConfigureAwait(false)).ToList();
        if (objects.Any(x => x.Kind == "module"))
            facts.AddRange(await IncomingDependenciesAsync(connection, transaction, token).ConfigureAwait(false));
        return new(identity,principal,deployment,Order(objects),Order(packagePermissions),Order(monitorPermissions),Order(facts));
    }

    private static async Task<IReadOnlyList<TrialFact>> IncomingDependenciesAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        var databases = await ReadListAsync<DatabaseRow>(connection,transaction,"SELECT name COLLATE Latin1_General_100_BIN2 Name,state State FROM master.sys.databases WHERE database_id<>2 FOR JSON PATH","databases",token).ConfigureAwait(false);
        if (databases.Length > 128 || databases.Any(x => x.State != 0)) throw new InvalidDataException("All databases must be online and bounded for package dependency inspection.");
        var facts = new List<TrialFact>();
        foreach (var db in databases.OrderBy(x=>x.Name,StringComparer.Ordinal))
        {
            string identifier = QuoteIdentifier(db.Name);
            string sql = $"""
                SELECT TOP(10001) @db COLLATE Latin1_General_100_BIN2 Scope,'unexpected_dependency' COLLATE Latin1_General_100_BIN2 Kind,
                  CONCAT(d.referencing_id,':',d.referencing_minor_id,':',COALESCE(d.referenced_id,0),':',d.referenced_entity_name) COLLATE Latin1_General_100_BIN2 [Key],
                  CONCAT(OBJECT_SCHEMA_NAME(d.referencing_id,DB_ID(@db)) COLLATE Latin1_General_100_BIN2,N'.',o.name COLLATE Latin1_General_100_BIN2,N':',d.referenced_entity_name COLLATE Latin1_General_100_BIN2) COLLATE Latin1_General_100_BIN2 Value
                FROM {identifier}.sys.sql_expression_dependencies d JOIN {identifier}.sys.objects o ON o.object_id=d.referencing_id
                WHERE (d.referenced_database_name=N'master' OR (@db=N'master' AND d.referenced_database_name IS NULL))
                  AND d.referenced_schema_name=N'sqlobserver_agent'
                FOR JSON PATH
                """;
            facts.AddRange(await ReadListAsync<TrialFact>(connection,transaction,sql,"dependencies",token,P("@db",SqlDbType.NVarChar,db.Name,128)).ConfigureAwait(false));
            string certificates = $"""
                SELECT TOP(10001) Scope COLLATE Latin1_General_100_BIN2 Scope,Kind COLLATE Latin1_General_100_BIN2 Kind,
                  [Key] COLLATE Latin1_General_100_BIN2 [Key],Value COLLATE Latin1_General_100_BIN2 Value FROM (
                  SELECT @db Scope,'unexpected_certificate_copy' Kind,CONVERT(varchar(12),certificate_id) [Key],name Value
                  FROM {identifier}.sys.certificates WHERE thumbprint=(SELECT thumbprint FROM master.sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1')
                    AND NOT (@db IN(N'master',N'msdb') AND name=N'SqlObserverAgentActivityCertificateV1')
                  UNION ALL SELECT @db,'unexpected_certificate_mapping',CONVERT(varchar(12),principal_id),name
                  FROM {identifier}.sys.database_principals WHERE sid=(SELECT sid FROM master.sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1')
                    AND NOT (@db=N'msdb' AND name=N'SqlObserverAgentActivityCertificateUserV1')
                ) q FOR JSON PATH
                """;
            facts.AddRange(await ReadListAsync<TrialFact>(connection,transaction,certificates,"certificate_copies",token,P("@db",SqlDbType.NVarChar,db.Name,128)).ConfigureAwait(false));
            if (facts.Count > 10000) throw new InvalidDataException("Dependency inventory exceeds the trial bound.");
        }
        return facts;
    }

    private sealed record DatabaseRow(string Name,int State);

    private static SqlParameter P(string name,SqlDbType type,object value,int size=0) => new(name,type,size) { Value=value };
    private static void RequireTransaction(SqlConnection connection, SqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection,connection))
            throw new InvalidOperationException("An open caller-owned connection and active transaction are required.");
    }
    private static void ValidatePrincipal(string principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        if (principal.Length>128 || principal!=principal.Trim() || principal.Any(char.IsControl) ||
            !(System.Text.RegularExpressions.Regex.IsMatch(principal,@"^[^\\@]+\\[^\\]+$") ||
              System.Text.RegularExpressions.Regex.IsMatch(principal,@"^[^@\\\s]+@[^@\\\s]+$")))
            throw new ArgumentException("The exact existing Windows monitor principal is required.",nameof(principal));
    }
    private static string QuoteIdentifier(string name)
    {
        if (name.Length is <1 or >128 || name.Any(char.IsControl)) throw new InvalidDataException("Database identifier cannot be safely inspected.");
        return "["+name.Replace("]","]]",StringComparison.Ordinal)+"]";
    }
    private static async Task ExecuteAsync(SqlConnection connection,SqlTransaction transaction,string sql,CancellationToken token,params SqlParameter[] parameters)
    {
        RequireTransaction(connection,transaction);
        await using var command=new SqlCommand(sql,connection,transaction) { CommandTimeout=TimeoutSeconds };
        command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private readonly record struct MetadataDocument(string Json,int FragmentCount);

    private static async Task<MetadataDocument> ReadJsonAsync(SqlConnection connection,SqlTransaction transaction,string sql,string section,CancellationToken token,params SqlParameter[] parameters)
    {
        RequireTransaction(connection,transaction);
        await using var command=new SqlCommand(sql,connection,transaction) { CommandTimeout=TimeoutSeconds };
        command.Parameters.AddRange(parameters);
        try
        {
            // FOR JSON may split output over multiple rows; read the entire bounded document.
            await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if(reader.FieldCount!=1 || reader.GetFieldType(0)!=typeof(string))
                throw new InvalidDataException("SQL metadata must have exactly one string column, including an empty result.");
            var json=new StringBuilder();
            int fragments=0;
            while(await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if(reader.IsDBNull(0)) throw new InvalidDataException("Incomplete SQL metadata evidence.");
                fragments++;
                json.Append(reader.GetString(0)); if(json.Length>4*1024*1024) throw new InvalidDataException("SQL metadata exceeds the trial bound.");
            }
            if(await reader.NextResultAsync(token).ConfigureAwait(false)) throw new InvalidDataException("Unexpected SQL metadata result set.");
            return new(json.ToString(),fragments);
        }
        catch(SqlException error) { throw new TrialSqlMetadataException(section,error); }
    }
    /// <summary>Pure bounded metadata parser. Failures expose only a CLR record type, length and numeric position.
    /// JSON exception messages, paths, source text and inner exceptions are deliberately not retained.</summary>
    public static T ParseMetadata<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 4*1024*1024 || string.IsNullOrWhiteSpace(json))
            throw new TrialMetadataException(typeof(T).Name,json.Length);
        try
        {
            return JsonSerializer.Deserialize<T>(json,Json) ?? throw new TrialMetadataException(typeof(T).Name,json.Length);
        }
        catch (JsonException error)
        {
            throw new TrialMetadataException(typeof(T).Name,json.Length,error.LineNumber,error.BytePositionInLine);
        }
    }

    /// <summary>Normalizes only a zero-fragment document from an already shape-validated SQL reader.
    /// A returned empty or whitespace fragment remains malformed JSON.</summary>
    public static T[] ParseMetadataList<T>(string json,int fragmentCount)
    {
        ArgumentNullException.ThrowIfNull(json);
        if(fragmentCount<0 || (fragmentCount==0 && json.Length!=0))
            throw new InvalidDataException("SQL metadata fragment accounting is inconsistent.");
        return fragmentCount==0 ? [] : ParseMetadata<T[]>(json);
    }

    private static async Task<T> ReadOneAsync<T>(SqlConnection c,SqlTransaction t,string sql,string section,CancellationToken token,params SqlParameter[] p)
    {
        var document=await ReadJsonAsync(c,t,sql,section,token,p).ConfigureAwait(false);
        return ParseMetadata<T>(document.Json);
    }
    private static async Task<T[]> ReadListAsync<T>(SqlConnection c,SqlTransaction t,string sql,string section,CancellationToken token,params SqlParameter[] p)
    {
        var document=await ReadJsonAsync(c,t,sql,section,token,p).ConfigureAwait(false);
        return ParseMetadataList<T>(document.Json,document.FragmentCount);
    }

    private const string ObjectsSql = """
        SELECT TOP(10001) Kind COLLATE Latin1_General_100_BIN2 Kind,[Database] COLLATE Latin1_General_100_BIN2 [Database],
          Name COLLATE Latin1_General_100_BIN2 Name,Id,OwnerId,RTRIM(Type) COLLATE Latin1_General_100_BIN2 Type,Detail COLLATE Latin1_General_100_BIN2 Detail FROM (
          SELECT 'owner_user' Kind,'master' [Database],name Name,principal_id Id,owning_principal_id OwnerId,type Type,authentication_type_desc Detail FROM master.sys.database_principals WHERE name=N'SqlObserverAgentActivitySchemaOwnerV1'
          UNION ALL SELECT 'schema','master',name,schema_id,principal_id,'SCHEMA','' FROM master.sys.schemas WHERE name=N'sqlobserver_agent'
          UNION ALL SELECT 'module','master',o.name,o.object_id,COALESCE(o.principal_id,s.principal_id),o.type,'' FROM master.sys.objects o JOIN master.sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=N'sqlobserver_agent' AND o.name=N'capture_v1'
          UNION ALL SELECT 'master_certificate','master',name,certificate_id,principal_id,'CERTIFICATE','' FROM master.sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1'
          UNION ALL SELECT 'certificate_login','server',name,principal_id,owning_principal_id,type,CONCAT(CONVERT(varchar(172),sid,2),':',is_disabled,':',COALESCE(CONVERT(varchar(12),credential_id),'')) FROM master.sys.server_principals WHERE name=N'SqlObserverAgentActivityCertificateLoginV1'
          UNION ALL SELECT 'msdb_certificate','msdb',name,certificate_id,principal_id,'CERTIFICATE','' FROM msdb.sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1'
          UNION ALL SELECT 'certificate_user','msdb',name,principal_id,owning_principal_id,type,CONVERT(varchar(172),sid,2) FROM msdb.sys.database_principals WHERE name=N'SqlObserverAgentActivityCertificateUserV1'
          UNION ALL SELECT 'monitor_user','master',p.name,p.principal_id,p.owning_principal_id,p.type,CONCAT(CONVERT(varchar(172),p.sid,2),':',p.authentication_type_desc)
            FROM master.sys.database_principals p WHERE p.name=@monitor AND (@ownedMonitor=1 OR EXISTS(
              SELECT 1 FROM master.sys.extended_properties e WHERE e.class=4 AND e.major_id=p.principal_id AND e.name=N'SqlObserver.AgentActivity.DeploymentId'))
        ) q FOR JSON PATH,INCLUDE_NULL_VALUES
        """;

    private const string PackagePermissionsSql = """
        SELECT TOP(10001) Scope COLLATE Latin1_General_100_BIN2 Scope,GranteeId,GrantorId,Class,MajorId,MinorId,
          Name COLLATE Latin1_General_100_BIN2 Name,State COLLATE Latin1_General_100_BIN2 State FROM (
          SELECT 'server' Scope,p.grantee_principal_id GranteeId,p.grantor_principal_id GrantorId,p.class Class,p.major_id MajorId,0 MinorId,p.permission_name Name,p.state State
          FROM master.sys.server_permissions p WHERE p.grantee_principal_id IN(SELECT principal_id FROM master.sys.server_principals WHERE name=N'SqlObserverAgentActivityCertificateLoginV1') OR p.grantor_principal_id IN(SELECT principal_id FROM master.sys.server_principals WHERE name=N'SqlObserverAgentActivityCertificateLoginV1')
            OR (p.class=101 AND p.major_id IN(SELECT principal_id FROM master.sys.server_principals WHERE name=N'SqlObserverAgentActivityCertificateLoginV1'))
          UNION ALL SELECT 'master',p.grantee_principal_id,p.grantor_principal_id,p.class,p.major_id,p.minor_id,p.permission_name,p.state FROM master.sys.database_permissions p
          WHERE p.grantee_principal_id IN(SELECT principal_id FROM master.sys.database_principals WHERE name=N'SqlObserverAgentActivitySchemaOwnerV1')
            OR (@ownedMonitor=1 AND (p.grantee_principal_id=DATABASE_PRINCIPAL_ID(@monitor) OR p.grantor_principal_id=DATABASE_PRINCIPAL_ID(@monitor) OR (p.class=4 AND p.major_id=DATABASE_PRINCIPAL_ID(@monitor))))
            OR p.grantor_principal_id IN(SELECT principal_id FROM master.sys.database_principals WHERE name=N'SqlObserverAgentActivitySchemaOwnerV1')
            OR (p.class=3 AND p.major_id IN(SELECT schema_id FROM master.sys.schemas WHERE name=N'sqlobserver_agent'))
            OR (p.class=1 AND p.major_id IN(SELECT o.object_id FROM master.sys.objects o JOIN master.sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=N'sqlobserver_agent'))
            OR (p.class=25 AND p.major_id IN(SELECT certificate_id FROM master.sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1'))
            OR (p.class=4 AND p.major_id IN(SELECT principal_id FROM master.sys.database_principals WHERE name=N'SqlObserverAgentActivitySchemaOwnerV1'))
          UNION ALL SELECT 'msdb',p.grantee_principal_id,p.grantor_principal_id,p.class,p.major_id,p.minor_id,p.permission_name,p.state FROM msdb.sys.database_permissions p
          WHERE p.grantee_principal_id IN(SELECT principal_id FROM msdb.sys.database_principals WHERE name=N'SqlObserverAgentActivityCertificateUserV1')
            OR p.grantor_principal_id IN(SELECT principal_id FROM msdb.sys.database_principals WHERE name=N'SqlObserverAgentActivityCertificateUserV1')
            OR (p.class=25 AND p.major_id IN(SELECT certificate_id FROM msdb.sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1'))
            OR (p.class=4 AND p.major_id IN(SELECT principal_id FROM msdb.sys.database_principals WHERE name=N'SqlObserverAgentActivityCertificateUserV1'))
        ) q FOR JSON PATH
        """;

    private const string MonitorPermissionsSql = """
        SELECT TOP(10001) Scope COLLATE Latin1_General_100_BIN2 Scope,GranteeId,GrantorId,Class,MajorId,MinorId,
          Name COLLATE Latin1_General_100_BIN2 Name,State COLLATE Latin1_General_100_BIN2 State FROM (
          SELECT 'server' Scope,p.grantee_principal_id GranteeId,p.grantor_principal_id GrantorId,p.class Class,p.major_id MajorId,0 MinorId,p.permission_name Name,p.state State FROM master.sys.server_permissions p WHERE p.grantee_principal_id IN(SELECT principal_id FROM master.sys.server_principals WHERE sid=@sid)
          UNION ALL SELECT 'master',p.grantee_principal_id,p.grantor_principal_id,p.class,p.major_id,p.minor_id,p.permission_name,p.state FROM master.sys.database_permissions p WHERE p.grantee_principal_id IN(SELECT principal_id FROM master.sys.database_principals WHERE sid=@sid)
          UNION ALL SELECT 'msdb',p.grantee_principal_id,p.grantor_principal_id,p.class,p.major_id,p.minor_id,p.permission_name,p.state FROM msdb.sys.database_permissions p WHERE p.grantee_principal_id IN(SELECT principal_id FROM msdb.sys.database_principals WHERE sid=@sid)
        ) q FOR JSON PATH
        """;

    private const string FactsSql = """
        USE [master];
        DECLARE @owner int=DATABASE_PRINCIPAL_ID(N'SqlObserverAgentActivitySchemaOwnerV1'),@schema int=SCHEMA_ID(N'sqlobserver_agent'),
          @module int=OBJECT_ID(N'sqlobserver_agent.capture_v1'),@cert int=CERT_ID(N'SqlObserverAgentActivityCertificateV1'),
          @login int=SUSER_ID(N'SqlObserverAgentActivityCertificateLoginV1'),@thumb varbinary(64);
        DECLARE @ownedMonitorId int=CASE WHEN @ownedMonitor=1 THEN DATABASE_PRINCIPAL_ID(@monitor) END;
        SELECT @thumb=thumbprint FROM sys.certificates WHERE certificate_id=@cert;
        DECLARE @msuser int=(SELECT principal_id FROM msdb.sys.database_principals WHERE name=N'SqlObserverAgentActivityCertificateUserV1');
        DECLARE @msPublic varbinary(max);
        EXEC msdb.sys.sp_executesql N'SELECT @public=CERTENCODED(CERT_ID(N''SqlObserverAgentActivityCertificateV1''));',
          N'@public varbinary(max) OUTPUT',@public=@msPublic OUTPUT;
        DECLARE @valid TABLE(entity_id int,is_signed int,is_signature_valid int);
        IF @module IS NOT NULL AND @thumb IS NOT NULL
          INSERT @valid SELECT entity_id,is_signed,is_signature_valid FROM sys.fn_check_object_signatures(N'certificate',@thumb) WHERE entity_id=@module;
        SELECT TOP(10001) Scope COLLATE Latin1_General_100_BIN2 Scope,Kind COLLATE Latin1_General_100_BIN2 Kind,
          [Key] COLLATE Latin1_General_100_BIN2 [Key],Value COLLATE Latin1_General_100_BIN2 Value FROM (
          SELECT 'master' Scope,'definition' Kind,'sha256' [Key],LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),OBJECT_DEFINITION(@module))),2)) Value WHERE @module IS NOT NULL
          UNION ALL SELECT 'master','definition','execute_as',CASE WHEN execute_as_principal_id IS NULL THEN 'CALLER' ELSE CONVERT(varchar(12),execute_as_principal_id) END FROM sys.sql_modules WHERE object_id=@module
          UNION ALL SELECT 'master','definition','ansi',CONCAT(CONVERT(int,uses_ansi_nulls),':',CONVERT(int,uses_quoted_identifier)) FROM sys.sql_modules WHERE object_id=@module
          UNION ALL SELECT 'master',CASE WHEN name=N'SqlObserver.AgentActivity.DeploymentId' THEN 'marker' ELSE 'unexpected_property' END,name,CONVERT(nvarchar(4000),value) FROM sys.extended_properties WHERE class=3 AND major_id=@schema
          UNION ALL SELECT 'master','unexpected_property',CONCAT(class,':',major_id,':',minor_id,':',name),CONVERT(nvarchar(4000),value) FROM sys.extended_properties WHERE class=1 AND major_id=@module
          UNION ALL SELECT 'master',CASE WHEN name=N'SqlObserver.AgentActivity.DeploymentId' THEN 'monitor_marker' ELSE 'unexpected_property' END,name,CONVERT(nvarchar(4000),value)
            FROM sys.extended_properties WHERE class=4 AND major_id=DATABASE_PRINCIPAL_ID(@monitor) AND (@ownedMonitor=1 OR name=N'SqlObserver.AgentActivity.DeploymentId')
          UNION ALL SELECT 'master','unexpected_execute_as',CONVERT(varchar(12),object_id),CONVERT(varchar(12),execute_as_principal_id) FROM sys.sql_modules WHERE execute_as_principal_id=@ownedMonitorId
          UNION ALL SELECT 'master','unexpected_queue_execute_as',CONVERT(varchar(12),object_id),CONVERT(varchar(12),execute_as_principal_id) FROM sys.service_queues WHERE execute_as_principal_id=@ownedMonitorId
          UNION ALL SELECT 'master','monitor_user_properties','defaults',CONCAT(default_schema_name,':',default_language_name,':',allow_encrypted_value_modifications) FROM sys.database_principals WHERE principal_id=@ownedMonitorId
          UNION ALL SELECT 'master','certificate','public_sha256',LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CERTENCODED(certificate_id)),2)) FROM sys.certificates WHERE certificate_id=@cert
          UNION ALL SELECT 'master','certificate','thumbprint',CONVERT(varchar(128),thumbprint,2) FROM sys.certificates WHERE certificate_id=@cert
          UNION ALL SELECT 'master','certificate','sid',CONVERT(varchar(172),sid,2) FROM sys.certificates WHERE certificate_id=@cert
          UNION ALL SELECT 'master','certificate','private_key',pvt_key_encryption_type_desc FROM sys.certificates WHERE certificate_id=@cert
          UNION ALL SELECT 'msdb','certificate','public_sha256',LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',@msPublic),2)) FROM msdb.sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1'
          UNION ALL SELECT 'msdb','certificate','thumbprint',CONVERT(varchar(128),thumbprint,2) FROM msdb.sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1'
          UNION ALL SELECT 'msdb','certificate','private_key',pvt_key_encryption_type_desc FROM msdb.sys.certificates WHERE name=N'SqlObserverAgentActivityCertificateV1'
          UNION ALL SELECT 'master','signature',CONVERT(varchar(12),major_id),CONCAT(crypt_type,':',CONVERT(varchar(128),thumbprint,2)) FROM sys.crypt_properties WHERE thumbprint=@thumb OR (class=1 AND major_id=@module)
          UNION ALL SELECT 'msdb','unexpected_signature',CONVERT(varchar(12),major_id),CONCAT(crypt_type,':',CONVERT(varchar(128),thumbprint,2)) FROM msdb.sys.crypt_properties WHERE thumbprint=@thumb
          UNION ALL SELECT 'master','signature_validity','module',CONCAT(is_signed,':',is_signature_valid) FROM @valid WHERE entity_id=@module
          UNION ALL SELECT 'msdb','base_object',name,CONVERT(varchar(12),object_id) FROM msdb.sys.objects WHERE schema_id=1 AND name IN(N'syssessions',N'sysjobactivity')
          UNION ALL SELECT 'master','monitor_mapping',CONVERT(varchar(12),principal_id),CONCAT(name,':',type,':',authentication_type_desc,':',CONVERT(varchar(172),sid,2)) FROM sys.database_principals WHERE sid=@sid
          UNION ALL SELECT 'msdb','monitor_mapping',CONVERT(varchar(12),principal_id),CONCAT(name,':',type,':',authentication_type_desc,':',CONVERT(varchar(172),sid,2)) FROM msdb.sys.database_principals WHERE sid=@sid
          UNION ALL SELECT 'server','monitor_role',CONVERT(varchar(12),r.role_principal_id),CONVERT(varchar(12),r.member_principal_id) FROM sys.server_role_members r JOIN sys.server_principals p ON p.principal_id=r.member_principal_id WHERE p.sid=@sid
          UNION ALL SELECT 'master','monitor_role',CONVERT(varchar(12),r.role_principal_id),CONVERT(varchar(12),r.member_principal_id) FROM sys.database_role_members r JOIN sys.database_principals p ON p.principal_id=r.member_principal_id WHERE p.sid=@sid
          UNION ALL SELECT 'msdb','monitor_role',CONVERT(varchar(12),r.role_principal_id),CONVERT(varchar(12),r.member_principal_id) FROM msdb.sys.database_role_members r JOIN msdb.sys.database_principals p ON p.principal_id=r.member_principal_id WHERE p.sid=@sid
          UNION ALL SELECT 'master','unexpected_role',CONVERT(varchar(12),role_principal_id),CONVERT(varchar(12),member_principal_id) FROM sys.database_role_members WHERE member_principal_id IN(@owner,@ownedMonitorId) OR role_principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'msdb','unexpected_role',CONVERT(varchar(12),role_principal_id),CONVERT(varchar(12),member_principal_id) FROM msdb.sys.database_role_members WHERE member_principal_id=@msuser OR role_principal_id=@msuser
          UNION ALL SELECT 'server','unexpected_role',CONVERT(varchar(12),role_principal_id),CONVERT(varchar(12),member_principal_id) FROM sys.server_role_members WHERE member_principal_id=@login OR role_principal_id=@login
          UNION ALL SELECT 'master','unexpected_owned_schema',CONVERT(varchar(12),schema_id),name FROM sys.schemas WHERE principal_id IN(@owner,@ownedMonitorId) AND schema_id<>@schema
          UNION ALL SELECT 'master','unexpected_owned_object',CONVERT(varchar(12),object_id),name FROM sys.objects WHERE (principal_id IN(@owner,@ownedMonitorId) OR schema_id=@schema) AND object_id<>@module
          UNION ALL SELECT 'msdb','unexpected_owned_schema',CONVERT(varchar(12),schema_id),name FROM msdb.sys.schemas WHERE principal_id=@msuser
          UNION ALL SELECT 'msdb','unexpected_owned_object',CONVERT(varchar(12),object_id),name FROM msdb.sys.objects WHERE principal_id=@msuser
          UNION ALL SELECT 'master','unexpected_owned_certificate',CONVERT(varchar(12),certificate_id),name FROM sys.certificates WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'msdb','unexpected_owned_certificate',CONVERT(varchar(12),certificate_id),name FROM msdb.sys.certificates WHERE principal_id=@msuser
          UNION ALL SELECT 'master','unexpected_owned_asymmetric_key',CONVERT(varchar(12),asymmetric_key_id),name FROM sys.asymmetric_keys WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'msdb','unexpected_owned_asymmetric_key',CONVERT(varchar(12),asymmetric_key_id),name FROM msdb.sys.asymmetric_keys WHERE principal_id=@msuser
          UNION ALL SELECT 'master','unexpected_owned_symmetric_key',CONVERT(varchar(12),symmetric_key_id),name FROM sys.symmetric_keys WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'msdb','unexpected_owned_symmetric_key',CONVERT(varchar(12),symmetric_key_id),name FROM msdb.sys.symmetric_keys WHERE principal_id=@msuser
          UNION ALL SELECT 'master','unexpected_owned_type',CONVERT(varchar(12),user_type_id),name FROM sys.types WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'msdb','unexpected_owned_type',CONVERT(varchar(12),user_type_id),name FROM msdb.sys.types WHERE principal_id=@msuser
          UNION ALL SELECT 'master','unexpected_owned_assembly',CONVERT(varchar(12),assembly_id),name FROM sys.assemblies WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'msdb','unexpected_owned_assembly',CONVERT(varchar(12),assembly_id),name FROM msdb.sys.assemblies WHERE principal_id=@msuser
          UNION ALL SELECT 'master','unexpected_owned_xml_schema',CONVERT(varchar(12),xml_collection_id),name FROM sys.xml_schema_collections WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'msdb','unexpected_owned_xml_schema',CONVERT(varchar(12),xml_collection_id),name FROM msdb.sys.xml_schema_collections WHERE principal_id=@msuser
          UNION ALL SELECT 'master','unexpected_owned_fulltext',CONVERT(varchar(12),fulltext_catalog_id),name FROM sys.fulltext_catalogs WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'master','unexpected_owned_stoplist',CONVERT(varchar(12),stoplist_id),name FROM sys.fulltext_stoplists WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'master','unexpected_owned_service',CONVERT(varchar(12),service_id),name FROM sys.services WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'master','unexpected_owned_route',CONVERT(varchar(12),route_id),name FROM sys.routes WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'master','unexpected_owned_remote_binding',CONVERT(varchar(12),remote_service_binding_id),name FROM sys.remote_service_bindings WHERE principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'msdb','unexpected_owned_fulltext',CONVERT(varchar(12),fulltext_catalog_id),name FROM msdb.sys.fulltext_catalogs WHERE principal_id=@msuser
          UNION ALL SELECT 'master','unexpected_owned_principal',CONVERT(varchar(12),principal_id),name FROM sys.database_principals WHERE owning_principal_id IN(@owner,@ownedMonitorId)
          UNION ALL SELECT 'msdb','unexpected_owned_principal',CONVERT(varchar(12),principal_id),name FROM msdb.sys.database_principals WHERE owning_principal_id=@msuser
          UNION ALL SELECT 'server','unexpected_owned_principal',CONVERT(varchar(12),principal_id),name FROM sys.server_principals WHERE owning_principal_id=@login
          UNION ALL SELECT 'server','unexpected_owned_database',CONVERT(varchar(12),database_id),name FROM sys.databases WHERE owner_sid=(SELECT sid FROM sys.server_principals WHERE principal_id=@login)
          UNION ALL SELECT 'server','unexpected_owned_endpoint',CONVERT(varchar(12),endpoint_id),name FROM sys.endpoints WHERE principal_id=@login
          UNION ALL SELECT 'msdb','unexpected_owned_job',CONVERT(varchar(36),job_id),CONVERT(varchar(36),job_id) FROM msdb.dbo.sysjobs WHERE owner_sid=(SELECT sid FROM sys.server_principals WHERE principal_id=@login)
          UNION ALL SELECT 'master','unexpected_certificate_user',CONVERT(varchar(12),p.principal_id),p.name FROM sys.database_principals p JOIN sys.certificates c ON c.sid=p.sid WHERE c.certificate_id=@cert
          UNION ALL SELECT 'msdb','unexpected_certificate_user',CONVERT(varchar(12),p.principal_id),p.name FROM msdb.sys.database_principals p JOIN msdb.sys.certificates c ON c.sid=p.sid WHERE c.name=N'SqlObserverAgentActivityCertificateV1' AND p.principal_id<>@msuser
          UNION ALL SELECT 'server','unexpected_certificate_login',CONVERT(varchar(12),p.principal_id),p.name FROM sys.server_principals p JOIN sys.certificates c ON c.sid=p.sid WHERE c.certificate_id=@cert AND p.principal_id<>@login
        ) q FOR JSON PATH,INCLUDE_NULL_VALUES
        """;
}
