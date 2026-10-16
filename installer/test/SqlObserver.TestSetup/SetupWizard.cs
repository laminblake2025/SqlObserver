using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace SqlObserver.TestSetup;

[SupportedOSPlatform("windows")]
internal static class SetupWizard
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static async Task RunAsync(string installRoot, string appRoot, string manifestHash)
    {
        if (Console.IsInputRedirected) throw new SetupException("Interactive setup is required; passwords are never accepted as arguments or environment variables.");
        Console.Title = "SQL Observer — unsigned test configuration";
        Console.WriteLine("SQL Observer test setup\n\nThis configures a fresh, isolated test host. Production use and upgrades are not qualified.\nPasswords are hidden and are not written to setup logs. Ctrl+C cancels.\n");
        if (WindowsHost.ServiceExists(SetupPolicy.ServerService) || WindowsHost.ServiceExists(SetupPolicy.CollectorService))
            throw new SetupException("Test services already exist. Setup never replaces running services. Use the existing configuration or uninstall first.");
        string dataRoot = Prompt("New configuration/data directory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlObserverTest"));
        dataRoot = Path.GetFullPath(dataRoot);
        SetupPolicy.AssertOrdinaryPath(dataRoot);
        string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.Equals(Path.GetDirectoryName(dataRoot), commonData, StringComparison.OrdinalIgnoreCase) || Directory.Exists(dataRoot) || File.Exists(dataRoot))
            throw new SetupException("Choose a fresh direct child of ProgramData. Retained configuration and keys are never overwritten.");
        string database = SetupPolicy.Database(Prompt("New PostgreSQL database", "sqlobserver_test"));
        string dns = SetupPolicy.Host(Prompt("Dashboard DNS hostname (must resolve to this machine)", System.Net.Dns.GetHostName()));
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback).ToHashSet();
        if (!(await Dns.GetHostAddressesAsync(dns).ConfigureAwait(false)).Any(localAddresses.Contains))
            throw new SetupException("The dashboard hostname must resolve to this machine before setup.");
        if (!int.TryParse(Prompt("HTTPS port", "5444"), out int port)) throw new SetupException("The HTTPS port is invalid.");
        SetupPolicy.Port(port);
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == port))
            throw new SetupException("That TCP port is already in use. Existing listeners were not changed.");
        string group = Prompt("Existing dedicated Windows operator group, or NEW to create SqlObserverTestOperators", "NEW");
        string? groupSid = group.Equals("NEW", StringComparison.OrdinalIgnoreCase) ? null : WindowsHost.OperatorGroupSid(group);
        if (groupSid is not null && (groupSid.StartsWith("S-1-5-32-", StringComparison.Ordinal) || groupSid.StartsWith("S-1-1-", StringComparison.Ordinal) || groupSid is "S-1-5-11" or "S-1-5-4"))
            throw new SetupException("Choose a dedicated operator group rather than a built-in or broad access group.");
        string collector = Prompt("Collector Windows identity: VIRTUAL for local SQL/domain machine access, or DOMAIN\\service-account / DOMAIN\\gmsa$", "VIRTUAL");
        bool virtualCollector = collector.Equals("VIRTUAL", StringComparison.OrdinalIgnoreCase);
        string? collectorPassword = null;
        if (!virtualCollector)
        {
            if (!collector.Contains('\\') || collector.StartsWith(@".\", StringComparison.Ordinal) || collector.StartsWith(Environment.MachineName + "\\", StringComparison.OrdinalIgnoreCase))
                throw new SetupException("For remote SQL, use an existing dedicated domain account or gMSA. A virtual account is available for local SQL.");
            WindowsHost.AccountSid(collector);
            if (!collector.EndsWith('$')) collectorPassword = ReadPassword("Collector service password");
        }
        bool enableSql = Prompt("Enable protected captured SQL and QueryTextReader for this test operator group? YES/NO", "NO").Equals("YES", StringComparison.OrdinalIgnoreCase);
        string pgUser = Prompt("Local PostgreSQL administrator", "postgres");
        string pgRoot = Prompt("Existing PostgreSQL TLS root certificate path (blank uses Windows trust; ignored when TLS is off)", "");
        Console.WriteLine($"\nReview\n  Application: {installRoot}\n  Configuration: {dataRoot}\n  Fresh repository: {database} on local PostgreSQL 18, port 5432\n  Services: {SetupPolicy.ServerService}, {SetupPolicy.CollectorService}\n  Dashboard: https://{dns}:{port}/\n  PostgreSQL runtime accounts: separate, generated, non-admin\n  Windows Server identity: dedicated virtual service account\n  Windows Collector identity: {(virtualCollector ? "dedicated virtual service account" : collector)}\n  Operators: {group}\n  Initial roles: Viewer, Operator, TargetAdministrator for all test targets{(enableSql ? ", QueryTextReader" : "")}\n");
        Console.WriteLine("Setup registers Server and Collector Application event sources, creates a 365-day test HTTPS certificate and trusts its public certificate on this host.\nIf local PostgreSQL TLS is off, setup enables TLS using a new test certificate and reloads its configuration.\nNo firewall rule or SQL Server permission is added. Database, certificates, keys, and configuration survive uninstall.\nNarrow the initial all-target authorization after registering targets.\n");
        if (Prompt("Type INSTALL to apply this configuration", "").Equals("INSTALL", StringComparison.Ordinal) is false)
            throw new SetupException("Configuration cancelled before changes were applied.");
        string pgPassword = ReadPassword("PostgreSQL administrator password");
        WindowsHost.CreateFreshDataRoot(dataRoot);
        string receiptPath = Path.Combine(dataRoot, "install-state.json");
        WriteJson(receiptPath, new { schemaVersion = 1, status = "configuring", manifestSha256 = manifestHash, installRoot, database, startedAtUtc = DateTimeOffset.UtcNow });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        bool servicesAttempted = false;
        string phase = "event-source-provisioning";
        try
        {
            Console.WriteLine("Registering Application event sources for Server and Collector...");
            WindowsHost.ProvisionApplicationEventSources();
            phase = "repository-prepare";
            await using var repository = new RepositorySetup(pgUser, pgPassword);
            pgPassword = "";
            WriteJson(Path.Combine(dataRoot, "repository-setup.json"), new { database, bootstrapLogin = repository.BootstrapLogin, serverLogin = repository.ServerLogin, collectorLogin = repository.CollectorLogin, protectedOwners = SetupPolicy.ProtectedOwners });
            Console.WriteLine("Checking local PostgreSQL and verifying runtime TLS...");
            await repository.PrepareAsync(database, dataRoot, pgRoot, cancel.Token).ConfigureAwait(false);
            phase = "repository-migrate";
            await repository.MigrateAsync(database, Console.WriteLine, cancel.Token).ConfigureAwait(false);
            phase = "service-provisioning";
            groupSid ??= WindowsHost.CreateOperatorGroup();
            string serverAccount = @"NT SERVICE\" + SetupPolicy.ServerService;
            if (virtualCollector) collector = @"NT SERVICE\" + SetupPolicy.CollectorService;
            else WindowsHost.GrantServiceLogon(collector);
            servicesAttempted = true;
            WindowsHost.CreateService(SetupPolicy.ServerService, SetupPolicy.ServiceCommand(appRoot, dataRoot, "Server"), serverAccount, null);
            WindowsHost.CreateService(SetupPolicy.CollectorService, SetupPolicy.ServiceCommand(appRoot, dataRoot, "Collector"), collector, collectorPassword);
            collectorPassword = null;
            string serverSid = WindowsHost.AccountSid(serverAccount), collectorSid = WindowsHost.AccountSid(collector);
            phase = "configuration-and-certificates";
            var readers = new List<string> { serverSid, collectorSid };
            if (!string.IsNullOrEmpty(repository.PostgresSid)) readers.Add(repository.PostgresSid);
            WindowsHost.ProtectDirectory(dataRoot, readers.ToArray());
            WindowsHost.ProtectDirectory(Path.Combine(dataRoot, "Server"), serverSid);
            WindowsHost.ProtectDirectory(Path.Combine(dataRoot, "Collector"), collectorSid);
            string sharedRoot = Path.Combine(dataRoot, "Shared");
            WindowsHost.ProtectDirectory(sharedRoot, serverSid, collectorSid);
            string liveKey = "", queryKey = "";
            if (enableSql)
            {
                liveKey = Path.Combine(sharedRoot, "live-activity.dpapi");
                queryKey = Path.Combine(sharedRoot, "query-content.dpapi");
                WindowsHost.WriteProtectedKey(liveKey);
                WindowsHost.WriteProtectedKey(queryKey);
            }
            using X509Certificate2 webCert = WindowsHost.CreateTlsCertificate(dns);
            string certPassword = SetupPolicy.NewSecret();
            string certPath = Path.Combine(dataRoot, "Server", "https.pfx");
            await File.WriteAllBytesAsync(certPath, webCert.Export(X509ContentType.Pfx, certPassword), cancel.Token).ConfigureAwait(false);
            string publicCert = Path.Combine(dataRoot, "dashboard-test-root.cer");
            await File.WriteAllBytesAsync(publicCert, webCert.Export(X509ContentType.Cert), cancel.Token).ConfigureAwait(false);
            using (var trust = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
            {
                trust.Open(OpenFlags.ReadWrite);
                using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(webCert.RawData);
                trust.Add(publicOnly);
            }
            string identityKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string[] roles = enableSql ? ["Viewer", "Operator", "TargetAdministrator", "QueryTextReader"] : ["Viewer", "Operator", "TargetAdministrator"];
            WriteJson(Path.Combine(dataRoot, "Server", "appsettings.json"), new
            {
                ConnectionStrings = new { SqlObserverRepository = repository.Connection(database, repository.ServerLogin, repository.ServerPassword) },
                SqlObserver = new
                {
                    IdentityFingerprintKey = identityKey, WebRootPath = Path.Combine(appRoot, "Web"),
                    Authorization = new { Bindings = new[] { new { GroupSid = groupSid, Roles = roles, AllTargets = true, TargetIds = Array.Empty<string>() } }, DisabledActorSids = Array.Empty<string>() },
                    LiveActivity = new { ProtectedKeyPath = liveKey }, QueryContent = new { ProtectedKeyPath = queryKey }
                },
                Kestrel = new { Endpoints = new { Https = new { Url = $"https://0.0.0.0:{port}", Certificate = new { Path = certPath, Password = certPassword } } } }
            });
            WriteJson(Path.Combine(dataRoot, "Collector", "appsettings.json"), new
            {
                ConnectionStrings = new { SqlObserverRepository = repository.Connection(database, repository.CollectorLogin, repository.CollectorPassword) },
                SqlObserver = new { IdentityFingerprintKey = identityKey, LiveActivity = new { ProtectedKeyPath = liveKey }, QueryContent = new { ProtectedKeyPath = queryKey } }
            });
            Console.WriteLine("Starting the Server and verifying authenticated HTTPS repository readiness...");
            phase = "server-start";
            WindowsHost.Start(SetupPolicy.ServerService);
            phase = "server-readiness";
            await WaitForReady(port, cancel.Token).ConfigureAwait(false);
            phase = "server-identity-check";
            WindowsHost.AssertRunning(SetupPolicy.ServerService);
            phase = "collector-start";
            WindowsHost.Start(SetupPolicy.CollectorService);
            await Task.Delay(TimeSpan.FromSeconds(5), cancel.Token).ConfigureAwait(false);
            phase = "collector-identity-check";
            WindowsHost.AssertRunning(SetupPolicy.CollectorService);
            phase = "completion-record";
            string guide = $"""
                SQL Observer unsigned test deployment
                Dashboard: https://{dns}:{port}/
                Operator group SID: {groupSid}
                Collector Windows identity: {collector}
                Repository: {database} (PostgreSQL 18, localhost:5432; VerifyFull TLS)
                Configuration: {dataRoot}

                1. If setup created the operator group, SIGN OUT AND BACK IN to refresh Windows group membership.
                2. On other browser machines, verify and trust dashboard-test-root.cer, or replace the test certificate with a trusted certificate. Its SHA-256 is {Convert.ToHexString(SHA256.HashData(webCert.RawData))}.
                3. Open the dashboard as a member of the operator group and register the SQL Server target.
                4. Generate and review least-privilege SQL grants using Application\Tools\generate-permissions.ps1. Use the Collector Windows identity above. An authorized DBA applies these; setup never grants SQL sysadmin or executes target SQL.
                5. A virtual Collector uses its own virtual identity for local SQL and the computer account for remote domain SQL. For a workgroup remote SQL target, configure an appropriate dedicated identity separately; machine authentication will not cross an untrusted workgroup.
                6. After registration, replace AllTargets:true with false and set TargetIds to the intended target GUIDs in Server\appsettings.json. Restart SqlObserverTestServer.
                7. Verify discovery and fresh evidence. /ready confirms the repository, not SQL target health.
                8. For remote clients, open only the chosen HTTPS port in your existing firewall policy. Setup adds no inbound firewall rules.

                Uninstall stops/removes only these test services. It retains PostgreSQL, data, roles, configuration, keys, operator group, service-logon grants and certificates. Preserve this entire directory; do not rotate protected keys used by retained evidence.
                Automatic upgrade and database purge are not provided. Never point this test installer at a production repository.
                """;
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "NEXT-STEPS.txt"), guide, cancel.Token).ConfigureAwait(false);
            WriteJson(receiptPath, new { schemaVersion = 1, status = "configured", manifestSha256 = manifestHash, installRoot, database, dashboard = $"https://{dns}:{port}/", operatorGroupSid = groupSid, collectorIdentity = collector, certificateSha256 = Convert.ToHexString(SHA256.HashData(webCert.RawData)), completedAtUtc = DateTimeOffset.UtcNow, productionSupported = false });
            Console.WriteLine("\nTest services are configured and repository readiness passed. SQL target onboarding remains.\n" + guide);
            Console.WriteLine("Press Enter to finish.");
            Console.ReadLine();
        }
        catch (Exception error)
        {
            // Only bounded phase IDs and exception codes, never messages, SQL,
            // credentials, connection strings or provider exception bodies.
            var failure = SetupFailureDiagnostic.Create(phase, error);
            Console.Error.WriteLine($"Configuration failed in phase {failure.Phase} ({failure.ExceptionType}, {failure.HResult}).");
            try { WriteJson(Path.Combine(dataRoot, "setup-failure.json"), failure); }
            catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Console.Error.WriteLine("Could not write setup-failure.json. The phase and error code above remain the failure evidence."); }
            bool cleanupFailed = false;
            try { if (servicesAttempted) WindowsHost.RemoveOwnedServices(appRoot); }
            catch (Exception)
            {
                cleanupFailed = true;
                Console.Error.WriteLine("Service cleanup failed. Inspect the retained test services before retrying setup.");
            }
            try { WriteJson(receiptPath, new { schemaVersion = 1, status = "incomplete", phase, cleanupFailed, manifestSha256 = manifestHash, installRoot, database, stoppedAtUtc = DateTimeOffset.UtcNow, dataPreserved = true }); }
            catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Console.Error.WriteLine("Could not update install-state.json. Retained data was not deleted."); }
            throw;
        }
        finally { Console.CancelKeyPress -= handler; pgPassword = ""; collectorPassword = null; }
    }

    private static async Task WaitForReady(int port, CancellationToken token)
    {
        using var handler = new HttpClientHandler { UseDefaultCredentials = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        for (int attempt = 0; attempt < 40; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response = await client.GetAsync($"https://localhost:{port}/ready", token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
        throw new SetupException("The Server did not pass authenticated HTTPS readiness. Test services were removed; configuration and repository were preserved for inspection.");
    }

    private static string Prompt(string label, string defaultValue)
    {
        Console.Write($"{label}{(string.IsNullOrEmpty(defaultValue) ? "" : " [" + defaultValue + "]")}: ");
        string value = Console.ReadLine()?.Trim() ?? throw new SetupException("Input cancelled.");
        if (value.Length > 1024 || value.Any(char.IsControl)) throw new SetupException("Input exceeds the allowed bound.");
        return value.Length == 0 ? defaultValue : value;
    }

    private static string ReadPassword(string label)
    {
        Console.Write(label + " (hidden): ");
        var buffer = new char[256];
        int length = 0;
        try
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return length > 0 ? new string(buffer, 0, length) : throw new SetupException("A password is required.");
                }
                if (key.Key == ConsoleKey.Backspace) { if (length > 0) buffer[--length] = '\0'; continue; }
                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) throw new OperationCanceledException();
                if (char.IsControl(key.KeyChar)) continue;
                if (length == buffer.Length) throw new SetupException("Password exceeds 256 characters.");
                buffer[length++] = key.KeyChar;
            }
        }
        finally { Array.Clear(buffer); }
    }
    private static void WriteJson(string path, object value)
    {
        string temp = path + ".new";
        using (FileStream file = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(file, value, JsonOptions);
        File.Move(temp, path, true);
    }
}
