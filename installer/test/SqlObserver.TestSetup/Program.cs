using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;

namespace SqlObserver.TestSetup;

internal static class Program
{
    [SupportedOSPlatform("windows")]
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        try
        {
            string installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            SetupPolicy.AssertOrdinaryPath(installRoot);
            using Stream input = typeof(Program).Assembly.GetManifestResourceStream("SetupBuild.json") ??
                throw new SetupException("This configuration tool must be built by build-test-installer.ps1.");
            using JsonDocument build = JsonDocument.Parse(input);
            string appRoot = Path.Combine(installRoot, "Application");
            string manifestHash = build.RootElement.GetProperty("manifestSha256").GetString()!;
            if (args is ["--verify"])
            {
                Console.WriteLine("Verified application source: " + SetupPolicy.VerifyPayload(appRoot, manifestHash));
                AssertMigrationCatalog(appRoot);
                return 0;
            }
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new SetupException("Run test setup as Administrator.");
            WindowsHost.AssertProtectedInstallRoot(installRoot);
            if (args is ["--remove-services"])
            {
                WindowsHost.RemoveOwnedServices(appRoot);
                Console.WriteLine("Test services removed. PostgreSQL, repository data, configuration, certificates and keys were preserved.");
                return 0;
            }
            if (args.Length != 0) throw new SetupException("No arguments are required. Use --verify or --remove-services for maintenance.");
            SetupPolicy.VerifyPayload(appRoot, manifestHash);
            AssertMigrationCatalog(appRoot);
            await SetupWizard.RunAsync(installRoot, appRoot, manifestHash).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Provider exceptions may contain connection material, SQL, or account details. Never emit them.
            string message = ex is SetupException ? ex.Message :
                $"Setup stopped ({ex.GetType().Name}). No provider details or credentials were logged.";
            Console.Error.WriteLine(message);
            Console.Error.WriteLine("Setup is incomplete. Any newly created services were stopped and removed. Retained data is never deleted automatically.");
            if (!Console.IsInputRedirected) { Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
            return 1;
        }
    }

    private static void AssertMigrationCatalog(string appRoot)
    {
        string[] shipped = File.ReadAllLines(Path.Combine(appRoot, "Docs", "repository-migrations.sha256"));
        string[] embedded = SqlObserver.Infrastructure.PostgreSql.PostgreSqlMigrationCatalog.LoadEmbedded().Migrations
            .Select(m => m.ChecksumHex + "  " + m.FileName).ToArray();
        if (!shipped.SequenceEqual(embedded, StringComparer.Ordinal))
            throw new SetupException("The setup migration catalog differs from the verified application package.");
    }
}
