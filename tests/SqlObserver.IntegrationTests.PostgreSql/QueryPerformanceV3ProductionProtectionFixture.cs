using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlObserver.Collector;
using SqlObserver.Collectors;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class QueryPerformanceV2CommitPostgreSqlTests
{
    private static ServiceProvider NativeCollectorApplication(ISqlServerConnectionFactory source, string keyPath)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            ["SqlObserver:IdentityFingerprintKey"] = new string('a', 64),
            ["SqlObserver:QueryContent:ProtectedKeyPath"] = keyPath,
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);
        // Replace only the connection opening boundary so native collection
        // runs as the owned non-sysadmin lab login. The collector, assets and
        // production key provider remain real.
        services.AddSingleton(provider => new SqlServerQueryPerformanceV3Collector(source,
            provider.GetRequiredService<SqlObserver.Domain.SensitiveData.IQuerySensitiveContentProtector>()));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Equal(1, provider.GetRequiredService<CollectorRegistry>().GetRequired(CollectorCatalogIds.QueryPerformance).Manifest.ManifestVersion.Value);
        return provider;
    }

    private sealed class NativeQueryProtectionKey : IDisposable
    {
        private readonly string directory;
        public string Path { get; }
        [SupportedOSPlatform("windows")]
        private NativeQueryProtectionKey()
        {
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SqlObserver-v3-key-" + Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(directory, "query-content.dpapi");
            var info = Directory.CreateDirectory(directory);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var sid = identity.User!;
                var directoryAcl = new DirectorySecurity(); directoryAcl.SetAccessRuleProtection(true, false);
                directoryAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(directoryAcl);
                File.WriteAllBytes(Path, ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine));
                var fileAcl = new FileSecurity(); fileAcl.SetAccessRuleProtection(true, false);
                fileAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(Path).SetAccessControl(fileAcl);
            }
            catch { File.Delete(Path); Directory.Delete(directory); throw; }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        public static NativeQueryProtectionKey Create()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native query key qualification requires Windows DPAPI.");
            return new();
        }
        public void Dispose() { File.Delete(Path); Directory.Delete(directory); }
    }
}
