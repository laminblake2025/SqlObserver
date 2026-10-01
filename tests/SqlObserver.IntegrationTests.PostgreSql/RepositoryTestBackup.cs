using System.Diagnostics;
using Npgsql;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>A real pg_dump archive of an owned fixture database, restored into fresh owned databases.</summary>
internal sealed class RepositoryTestBackup : IAsyncDisposable
{
    private readonly string containerId;
    private readonly string archivePath;
    private readonly string cleanupConnection;
    private RepositoryTestBackup(string containerId, string archivePath, string cleanupConnection, string digest)
    { this.containerId = containerId; this.archivePath = archivePath; this.cleanupConnection = cleanupConnection; Digest = digest; }
    internal string Digest { get; }
    internal string Reference => "fixture-pg18-archive/" + Path.GetFileName(archivePath);

    internal static async Task<RepositoryTestBackup> CreateAsync(RepositoryTestDatabase source, string? containerId, string cleanupConnection)
    {
        if (containerId is null || containerId.Length != 64 || containerId.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("Backup/restore tests need the owned Testcontainers instance or SQLOBSERVER_POSTGRES_TEST_CONTAINER set to the PostgreSQL 18 service container ID.");
        if (!IsOwnedDatabase(source.DatabaseName)) throw new InvalidOperationException("Backup source must be an owned fixture database.");
        string identity = await RunDocker(containerId, "psql", "-X", "-U", "postgres", "-d", source.DatabaseName, "-At", "-c", "SELECT current_database();");
        if (identity.Trim() != source.DatabaseName) throw new InvalidOperationException("Backup container does not host this fixture database.");
        string archive = "/tmp/sqlobserver_backup_" + Guid.NewGuid().ToString("N") + ".dump";
        try
        {
            await RunDocker(containerId, "pg_dump", "-U", "postgres", "--format=custom", "--file", archive, "--dbname", source.DatabaseName);
            string hashOutput = await RunDocker(containerId, "sha256sum", archive);
            string digest = hashOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidOperationException("Archive digest is invalid.");
            return new RepositoryTestBackup(containerId, archive, cleanupConnection, digest.ToLowerInvariant());
        }
        catch { await RunDocker(containerId, "rm", "-f", "--", archive); throw; }
    }

    internal async Task<RepositoryTestDatabase> RestoreAsync()
    {
        string name = "sqlobserver_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(cleanupConnection))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0 ENCODING 'UTF8';", connection);
            await create.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlConnectionStringBuilder(cleanupConnection) { Database = name, Pooling = false, IncludeErrorDetail = false };
        var restored = new RepositoryTestDatabase(null, name, builder.ConnectionString, cleanupConnection, NpgsqlDataSource.Create(builder.ConnectionString));
        try
        {
            string output = await RunDocker(containerId, "sha256sum", archivePath);
            if (!output.StartsWith(Digest + " ", StringComparison.Ordinal)) throw new InvalidOperationException("Backup archive changed before restoration.");
            await RunDocker(containerId, "pg_restore", "-U", "postgres", "--exit-on-error", "--dbname", name, archivePath);
            return restored;
        }
        catch { await restored.DisposeAsync(); throw; }
    }

    private static bool IsOwnedDatabase(string name) => name.StartsWith("sqlobserver_", StringComparison.Ordinal) &&
        name.Length == 44 && name[12..].All(char.IsAsciiHexDigit);

    private static async Task<string> RunDocker(string containerId, params string[] command)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var process = new Process { StartInfo = new ProcessStartInfo("docker")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("exec"); process.StartInfo.ArgumentList.Add(containerId);
        foreach (string argument in command) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token), errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        string result = await output, error = await errors;
        if (process.ExitCode != 0 || result.Length > 4096 || error.Length > 4096)
            throw new InvalidOperationException($"Fixture PostgreSQL {command[0]} failed (exit {process.ExitCode}); archive or restore verification did not complete.");
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await RunDocker(containerId, "rm", "-f", "--", archivePath);
        GC.SuppressFinalize(this);
    }
}
