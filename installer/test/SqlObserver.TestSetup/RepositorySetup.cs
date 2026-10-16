using Microsoft.Win32;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SqlObserver.TestSetup;

[SupportedOSPlatform("windows")]
internal sealed class RepositorySetup : IAsyncDisposable
{
    private readonly NpgsqlConnection admin;
    private readonly string administrator;
    private string administratorPassword;
    private readonly string suffix = Guid.NewGuid().ToString("N")[..10];
    private readonly string bootstrapPassword = SetupPolicy.NewSecret();
    private string Bootstrap => "sqlobserver_test_bootstrap_" + suffix;
    public string BootstrapLogin => Bootstrap;
    public string ServerLogin => "sqlobserver_test_server_" + suffix;
    public string CollectorLogin => "sqlobserver_test_collector_" + suffix;
    public string ServerPassword { get; } = SetupPolicy.NewSecret();
    public string CollectorPassword { get; } = SetupPolicy.NewSecret();
    public string RootCertificate { get; private set; } = "";
    public string PostgresSid { get; private set; } = "";
    private bool bootstrapCreated;

    public RepositorySetup(string administrator, string password)
    {
        this.administrator = administrator;
        administratorPassword = password;
        admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
        Host = "127.0.0.1", Port = 5432, Database = "postgres", Username = administrator, Password = password,
        SslMode = SslMode.Disable, Pooling = false, Timeout = 10, CommandTimeout = 30, IncludeErrorDetail = false,
        PersistSecurityInfo = false, ApplicationName = "SqlObserver.TestSetup.Bootstrap"
        }.ConnectionString);
    }

    public async Task PrepareAsync(string database, string dataRoot, string existingRootCertificate, CancellationToken token)
    {
        try { await admin.OpenAsync(token).ConfigureAwait(false); }
        catch (NpgsqlException)
        {
            throw new SetupException("Cannot connect to PostgreSQL at 127.0.0.1:5432. Verify the server is running, allows local connections, and accepts the supplied administrator credentials.");
        }
        int major = (int)(await Scalar("SELECT current_setting('server_version_num')::integer / 10000", token).ConfigureAwait(false))!;
        if (major != 18)
            throw new SetupException($"The server at 127.0.0.1:5432 is PostgreSQL {major}. This application requires PostgreSQL 18.x; other majors are not changed.");
        if (!(bool)(await Scalar("SELECT rolsuper FROM pg_roles WHERE rolname = current_user", token).ConfigureAwait(false))!)
            throw new SetupException("Repository setup requires a local PostgreSQL administrator with superuser rights.");
        if (!(bool)(await Scalar("SELECT pg_try_advisory_lock(6003668672497471828)", token).ConfigureAwait(false))!)
            throw new SetupException("Another test setup is using this PostgreSQL cluster.");
        using (var check = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @database", admin))
        {
            check.Parameters.AddWithValue("database", database);
            if ((long)(await check.ExecuteScalarAsync(token).ConfigureAwait(false))! != 0)
                throw new SetupException("The requested repository already exists. Setup never replaces or migrates an existing database.");
        }
        await ConfigureTlsAsync(dataRoot, existingRootCertificate, token).ConfigureAwait(false);
        // Prove the eventual runtime TLS policy before creating any database or runtime login.
        await using (var tls = new NpgsqlConnection(Connection("postgres", administrator, administratorPassword)))
            await tls.OpenAsync(token).ConfigureAwait(false);
        foreach (string owner in SetupPolicy.ProtectedOwners)
        {
            using var check = new NpgsqlCommand("SELECT rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolinherit OR rolreplication OR NOT rolbypassrls OR EXISTS (SELECT 1 FROM pg_auth_members WHERE roleid = r.oid OR member = r.oid) FROM pg_roles r WHERE rolname = @name", admin);
            check.Parameters.AddWithValue("name", owner);
            object? result = await check.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (result is null)
                await Execute($"CREATE ROLE {SetupPolicy.QuoteIdentifier(owner)} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS", token).ConfigureAwait(false);
            else if ((bool)result) throw new SetupException("A protected repository owner has unexpected attributes or memberships. No existing role was altered.");
        }
        string expires = DateTimeOffset.UtcNow.AddHours(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await Execute($"CREATE ROLE {SetupPolicy.QuoteIdentifier(Bootstrap)} LOGIN CREATEDB CREATEROLE NOSUPERUSER NOREPLICATION NOBYPASSRLS PASSWORD {SetupPolicy.QuoteLiteral(bootstrapPassword)} VALID UNTIL {SetupPolicy.QuoteLiteral(expires)}", token).ConfigureAwait(false);
        bootstrapCreated = true;
        await Execute($"CREATE DATABASE {SetupPolicy.QuoteIdentifier(database)} OWNER {SetupPolicy.QuoteIdentifier(Bootstrap)}", token).ConfigureAwait(false);
    }

    public async Task MigrateAsync(string database, Action<string> progress, CancellationToken token)
    {
        try
        {
            foreach (string owner in SetupPolicy.ProtectedOwners)
                await Execute($"GRANT {SetupPolicy.QuoteIdentifier(owner)} TO {SetupPolicy.QuoteIdentifier(Bootstrap)} WITH ADMIN OPTION", token).ConfigureAwait(false);
            progress("Applying the checksum-verified migration catalog. This can take several minutes...");
            PostgreSqlMigrationExecution result = await PostgreSqlMigrationExecutor.ApplyPendingAsync(Connection(database, Bootstrap, bootstrapPassword),
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(20))), token).ConfigureAwait(false);
            if (!result.Succeeded || result.Batch!.HasFailures)
                throw new SetupException("Repository migration did not complete. Its verified prefix was retained; setup did not start services.");
            await using var verify = new NpgsqlConnection(Connection(database, Bootstrap, bootstrapPassword));
            await verify.OpenAsync(token).ConfigureAwait(false);
            using var ledger = new NpgsqlCommand("SELECT migration_number, migration_name, sha256 FROM system.schema_migration ORDER BY migration_number", verify);
            await using (NpgsqlDataReader reader = await ledger.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                var catalog = PostgreSqlMigrationCatalog.LoadEmbedded().Migrations;
                int index = 0;
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (index >= catalog.Count || reader.GetInt32(0) != catalog[index].Descriptor.Number.Value ||
                        reader.GetString(1) != catalog[index].FileName || reader.GetString(2) != catalog[index].ChecksumHex)
                        throw new SetupException("The final migration ledger did not match the embedded catalog.");
                    index++;
                }
                if (index != catalog.Count) throw new SetupException("The final migration ledger is incomplete.");
                progress($"Verified all {index} migrations.");
            }
            foreach ((string login, string password, string group) in new[]
            {
                (ServerLogin, ServerPassword, "sqlobserver_server"), (CollectorLogin, CollectorPassword, "sqlobserver_collector")
            })
            {
                await Execute($"CREATE ROLE {SetupPolicy.QuoteIdentifier(login)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD {SetupPolicy.QuoteLiteral(password)}", token).ConfigureAwait(false);
                await Execute($"GRANT {group} TO {SetupPolicy.QuoteIdentifier(login)}", token).ConfigureAwait(false);
            }
        }
        finally { await SealBootstrapAsync().ConfigureAwait(false); }
    }

    public string Connection(string database, string user, string password) => new NpgsqlConnectionStringBuilder
    {
        Host = "localhost", Port = 5432, Database = database, Username = user, Password = password,
        SslMode = SslMode.VerifyFull, RootCertificate = string.IsNullOrEmpty(RootCertificate) ? null : RootCertificate,
        Timeout = 10, CommandTimeout = 30, IncludeErrorDetail = false, PersistSecurityInfo = false
    }.ConnectionString;

    private async Task ConfigureTlsAsync(string dataRoot, string rootCertificate, CancellationToken token)
    {
        if (string.Equals((string?)await Scalar("SHOW ssl", token).ConfigureAwait(false), "on", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(rootCertificate))
            {
                SetupPolicy.AssertOrdinaryPath(rootCertificate);
                string destination = Path.Combine(dataRoot, "repository-root.crt");
                File.Copy(rootCertificate, destination, false);
                RootCertificate = destination;
            }
            return; // Existing TLS configuration and service remain unchanged.
        }
        string dataDirectory = ((string)(await Scalar("SHOW data_directory", token).ConfigureAwait(false))!).Replace('/', '\\');
        using RegistryKey services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")!;
        var matches = new List<string>();
        foreach (string name in services.GetSubKeyNames())
        {
            using RegistryKey? service = services.OpenSubKey(name);
            string path = (service?.GetValue("ImagePath") as string ?? "").Replace('/', '\\');
            if (!SetupPolicy.IsPostgresServiceForData(path, dataDirectory)) continue;
            string account = service!.GetValue("ObjectName") as string ?? "";
            if (account.StartsWith(@".\", StringComparison.Ordinal)) account = Environment.MachineName + account[1..];
            matches.Add(account);
        }
        if (matches.Count != 1) throw new SetupException("Cannot identify the exact PostgreSQL Windows service. Configure PostgreSQL TLS before retrying.");
        PostgresSid = WindowsHost.AccountSid(matches[0]);
        WindowsHost.ProtectDirectory(dataRoot, PostgresSid);
        string tlsRoot = Path.Combine(dataRoot, "PostgresTls");
        WindowsHost.ProtectDirectory(tlsRoot, PostgresSid);
        using X509Certificate2 cert = WindowsHost.CreateTlsCertificate("localhost");
        string certificatePath = Path.Combine(tlsRoot, "repository.crt");
        string keyPath = Path.Combine(tlsRoot, "repository.key");
        await File.WriteAllTextAsync(certificatePath, cert.ExportCertificatePem(), token).ConfigureAwait(false);
        using RSA key = cert.GetRSAPrivateKey()!;
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), token).ConfigureAwait(false);
        // Explicit test-host opt-in in the review screen. No pg_hba.conf or listen-address edits.
        await Execute("ALTER SYSTEM SET ssl_cert_file = " + SetupPolicy.QuoteLiteral(certificatePath.Replace('\\', '/')), token).ConfigureAwait(false);
        await Execute("ALTER SYSTEM SET ssl_key_file = " + SetupPolicy.QuoteLiteral(keyPath.Replace('\\', '/')), token).ConfigureAwait(false);
        await Execute("ALTER SYSTEM SET ssl_min_protocol_version = 'TLSv1.2'", token).ConfigureAwait(false);
        await Execute("ALTER SYSTEM SET ssl = 'on'", token).ConfigureAwait(false);
        if (!(bool)(await Scalar("SELECT pg_reload_conf()", token).ConfigureAwait(false))!)
            throw new SetupException("PostgreSQL did not accept its test TLS reload.");
        RootCertificate = Path.Combine(dataRoot, "repository-root.crt");
        File.Copy(certificatePath, RootCertificate, false);
        // Reload is asynchronous. Bound TLS validation, and keep normal hostname/chain validation enabled.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await using var check = new NpgsqlConnection(Connection("postgres", administrator, administratorPassword));
                await check.OpenAsync(token).ConfigureAwait(false);
                return;
            }
            catch (NpgsqlException) when (attempt < 19) { await Task.Delay(500, token).ConfigureAwait(false); }
        }
    }

    private async Task<object?> Scalar(string sql, CancellationToken token)
    {
        using var command = new NpgsqlCommand(sql, admin);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }
    private async Task Execute(string sql, CancellationToken token)
    {
        using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private async Task SealBootstrapAsync()
    {
        if (!bootstrapCreated || admin.State != System.Data.ConnectionState.Open) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        bool revoked = false;
        try
        {
            foreach (string owner in SetupPolicy.ProtectedOwners)
            {
                await Execute($"REVOKE {SetupPolicy.QuoteIdentifier(owner)} FROM {SetupPolicy.QuoteIdentifier(Bootstrap)}", cleanup.Token).ConfigureAwait(false);
                // Preflight required these roles to have no members. These memberships belong only to this bootstrap.
                if ((bool)(await Scalar("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sqlobserver_migrator')", cleanup.Token).ConfigureAwait(false))!)
                    await Execute($"REVOKE {SetupPolicy.QuoteIdentifier(owner)} FROM sqlobserver_migrator", cleanup.Token).ConfigureAwait(false);
            }
            revoked = true;
        }
        finally
        {
            // Still disable login/administration if membership cleanup failed. Never reuse the operation's cancelled token.
            using var seal = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Execute($"ALTER ROLE {SetupPolicy.QuoteIdentifier(Bootstrap)} NOLOGIN NOCREATEDB NOCREATEROLE PASSWORD NULL", seal.Token).ConfigureAwait(false);
            if (revoked) bootstrapCreated = false;
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { await SealBootstrapAsync().ConfigureAwait(false); }
        finally { administratorPassword = ""; await admin.DisposeAsync().ConfigureAwait(false); }
    }
}
