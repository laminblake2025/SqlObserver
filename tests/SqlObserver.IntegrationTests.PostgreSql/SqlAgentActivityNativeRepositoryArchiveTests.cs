using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace SqlObserver.IntegrationTests.PostgreSql;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeRepositoryArchiveCollectionDefinition
{
    public const string Name = "Owned native repository archive export";
}

/// <summary>Exports an empty repository from a dedicated disposable cluster. Never uses external repository settings.</summary>
[Collection(NativeRepositoryArchiveCollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
[Trait("Category", "RequiresAgentNativeRepositoryArchive")]
public sealed class SqlAgentActivityNativeRepositoryArchiveTests(ITestOutputHelper output)
{
    private const string Prefix = "SQLOBSERVER_AGENT_NATIVE_REPOSITORY_ARCHIVE";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Schemas = ["alerting", "analytics", "audit", "control", "events", "reporting", "security", "system", "telemetry"];
    private static readonly string[] Roles = ["sqlobserver_auditor", "sqlobserver_collector", "sqlobserver_migrator", "sqlobserver_payload_expirer", "sqlobserver_report_expirer", "sqlobserver_run_dependency_reader", "sqlobserver_server"];

    [Fact]
    public async Task ExportEmptySealedRepositoryWithPreservedOwnersAndPrivileges()
    {
        Assert.Equal("1", Required(Prefix));
        string directory = CreateOwnedOutputDirectory(Required(Prefix + "_OUTPUT_DIRECTORY"));
        string hostArchive = Path.Combine(directory, "repository.dump");
        string manifestPath = Path.Combine(directory, "manifest.json");
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var container = new PostgreSqlBuilder(PostgreSql18Fixture.Image).WithDatabase("sqlobserver_test_admin")
            .WithUsername("postgres").WithPassword(password).WithCleanUp(true).Build();
        password = string.Empty;
        RepositoryTestDatabase? database = null;
        string? containerId = null;
        string archive = "/tmp/sqlobserver_native_repository_" + Guid.NewGuid().ToString("N") + ".dump";
        string? databaseName = null;
        string? archiveDigest = null;
        string? archiveTableOfContentsSha256 = null;
        long archiveBytes = 0;
        JsonElement state = default;
        bool databaseRemoved = false;
        bool containerRemoved = false;
        try
        {
            await container.StartAsync();
            containerId = container.Id;
            Assert.Equal(64, containerId.Length);
            Assert.All(containerId, character => Assert.True(char.IsAsciiHexDigit(character)));
            string adminConnection = container.GetConnectionString();
            databaseName = "sqlobserver_" + Guid.NewGuid().ToString("N");
            Assert.True(IsOwnedDatabase(databaseName));
            await using (var connection = new NpgsqlConnection(adminConnection))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\" TEMPLATE template0 ENCODING 'UTF8';", connection);
                await command.ExecuteNonQueryAsync();
            }
            string fixtureConnection = new NpgsqlConnectionStringBuilder(adminConnection)
            {
                Database = databaseName, Pooling = false, IncludeErrorDetail = false,
                ApplicationName = "SqlObserver.NativeRepositoryArchive",
            }.ConnectionString;
            database = new RepositoryTestDatabase(container, databaseName, fixtureConnection, adminConnection, NpgsqlDataSource.Create(fixtureConnection));
            var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
            Assert.False(result.HasFailures, string.Join(",", result.Results.Where(item => item.Outcome == MigrationOutcome.Failed).Select(item => $"{item.Migration.Number.Value}:{item.FailureCode}")));
            Assert.Equal(372, result.Results.Count);
            await SqlVolumeWindowHistoryFixture.CutoverCanonicalForeignKeysAsync(database);
            await SqlVolumeWindowHistoryFixture.SealAsync(database);
            state = await ReadAndValidateStateAsync(database);
            string identity = await DockerAsync("exec", containerId, "psql", "-X", "-U", "postgres", "-d", databaseName, "-At", "-c", "SELECT current_database();");
            Assert.Equal(databaseName, identity.Trim());
            await DockerAsync("exec", containerId, "pg_dump", "-U", "postgres", "--format=custom", "--file", archive, "--dbname", databaseName);
            string tableOfContents = await DockerAsync(4 * 1024 * 1024, "exec", containerId, "pg_restore", "--list", archive);
            Assert.Single(tableOfContents.Split('\n'), line => line.TrimStart(';', ' ', '\t').TrimEnd('\r') == "Format: CUSTOM");
            // A database archive must not contain cluster role creation or membership entries.
            Assert.DoesNotContain(" ROLE ", tableOfContents, StringComparison.Ordinal);
            Assert.DoesNotContain(" ROLE MEMBERSHIP ", tableOfContents, StringComparison.Ordinal);
            archiveTableOfContentsSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tableOfContents))).ToLowerInvariant();
            string hashLine = await DockerAsync("exec", containerId, "sha256sum", archive);
            archiveDigest = hashLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            Assert.Equal(64, archiveDigest.Length);
            Assert.All(archiveDigest, character => Assert.True(char.IsAsciiHexDigit(character)));
            // docker cp is a literal file copy, never shell interpolation or a role/cluster export.
            Assert.False(File.Exists(hostArchive));
            await DockerAsync("cp", containerId + ":" + archive, hostArchive);
            using (var stream = File.OpenRead(hostArchive))
            {
                archiveBytes = stream.Length;
                Assert.InRange(archiveBytes, 1, 64 * 1024 * 1024);
                var magic = new byte[5];
                stream.ReadExactly(magic);
                Assert.Equal("PGDMP", System.Text.Encoding.ASCII.GetString(magic));
                stream.Position = 0;
                Assert.Equal(archiveDigest, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            }
            Assert.Equal(state.GetRawText(), (await ReadAndValidateStateAsync(database)).GetRawText());
            await DockerAsync("exec", containerId, "rm", "-f", "--", archive);
            await database.DisposeAsync();
            database = null;
            await using (var connection = new NpgsqlConnection(adminConnection))
            {
                await connection.OpenAsync();
                await using var absent = new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_database WHERE datname=@database", connection);
                absent.Parameters.AddWithValue("database", databaseName);
                Assert.Equal(0L, await absent.ExecuteScalarAsync());
                databaseRemoved = true;
            }
            await container.DisposeAsync();
            Assert.True(string.IsNullOrWhiteSpace(await DockerAsync("ps", "-a", "--no-trunc", "--filter", "id=" + containerId, "--format", "{{.ID}}")));
            containerRemoved = true;
        }
        finally
        {
            try
            {
                if (database is not null) await database.DisposeAsync();
            }
            finally
            {
                if (!containerRemoved) await container.DisposeAsync();
            }
        }
        Assert.True(databaseRemoved);
        Assert.True(containerRemoved);
        var manifest = new
        {
            schemaVersion = 1, diagnosticOnly = true, completed = true,
            globalClusterIsDisposable = true, existingGuestRepositoryTouched = false,
            sourceFixtureDatabase = databaseName, sourceContainerId = containerId,
            pinnedImage = PostgreSql18Fixture.Image, databaseOnlyArchive = true,
            preservedOwners = true, preservedObjectPrivileges = true,
            roleCreationOrMembershipDdlInArchive = false,
            archive = new { file = "repository.dump", sha256 = archiveDigest, bytes = archiveBytes, tableOfContentsSha256 = archiveTableOfContentsSha256 },
            state,
            cleanup = new { databaseRemoved, containerRemoved, containerArchiveRemoved = true },
            guestRestoreRequiresExistingRoles = Roles,
            guestDatabaseAclMustBeAppliedSeparately = true,
            repositoryResolverNativeQualified = false, continuousDeploymentQualified = false,
            productionAdmitted = false, serviceSessionAssociationQualified = false, nativeQualification = "Absent",
        };
        using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, manifest, Json);
        File.SetAttributes(hostArchive, File.GetAttributes(hostArchive) | FileAttributes.ReadOnly);
        File.SetAttributes(manifestPath, File.GetAttributes(manifestPath) | FileAttributes.ReadOnly);
        output.WriteLine("AGENT_NATIVE_REPOSITORY_ARCHIVE_V1:" + JsonSerializer.Serialize(new
        {
            directory, manifestPath, archiveSha256 = archiveDigest, archiveBytes, databaseRemoved, containerRemoved,
            globalClusterIsDisposable = true, nativeRepositoryResolutionQualified = false,
        }));
    }

    private static async Task<JsonElement> ReadAndValidateStateAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object(
              'serverVersionNum',current_setting('server_version_num')::integer,
              'migrations',(SELECT jsonb_agg(jsonb_build_object('number',migration_number,'name',migration_name,'sha256',sha256) ORDER BY migration_number) FROM system.schema_migration),
              'schemas',(SELECT jsonb_agg(jsonb_build_object('name',n.nspname,'owner',pg_get_userbyid(n.nspowner)) ORDER BY n.nspname) FROM pg_namespace n WHERE n.nspname=ANY(@schemas)),
              'roles',(SELECT jsonb_agg(jsonb_build_object('name',r.rolname,'canLogin',r.rolcanlogin,'superuser',r.rolsuper,'createDatabase',r.rolcreatedb,'createRole',r.rolcreaterole,'inherit',r.rolinherit,'replication',r.rolreplication,'bypassRls',r.rolbypassrls,'comment',shobj_description(r.oid,'pg_authid'),'memberships',(SELECT coalesce(jsonb_agg(jsonb_build_object('role',granted.rolname,'admin',m.admin_option,'inherit',m.inherit_option,'set',m.set_option) ORDER BY granted.rolname),'[]'::jsonb) FROM pg_auth_members m JOIN pg_roles granted ON granted.oid=m.roleid WHERE m.member=r.oid)) ORDER BY r.rolname) FROM pg_roles r WHERE r.rolname=ANY(@roles)),
              'emptyProof',jsonb_build_object('targets',(SELECT count(*) FROM control.observation_target),'enrollments',(SELECT count(*) FROM control.sql_agent_module_enrollment_command),'runIdentity',(SELECT count(*) FROM telemetry.collection_run_identity),'runsV2',(SELECT count(*) FROM telemetry.collection_run_v2),'originalRuns',(SELECT count(*) FROM telemetry.collection_run),'outcomes',(SELECT count(*) FROM telemetry.collection_run_outcome),'agentReceipts',(SELECT count(*) FROM telemetry.sql_agent_activity_receipt)),
              'seal',(SELECT jsonb_build_object('sealed',sealed_at IS NOT NULL,'sealedBy',sealed_by) FROM system.collection_run_original_rollback_seal WHERE singleton),
              'routes',(SELECT jsonb_object_agg(route,use_v2 ORDER BY route) FROM (
                SELECT 'activity' route,use_v2 FROM system.collection_run_v2_activity_read_route UNION ALL
                SELECT 'health',use_v2 FROM system.collection_run_v2_health_read_route UNION ALL
                SELECT 'm9',use_v2 FROM system.collection_run_v2_m9_read_route UNION ALL
                SELECT 'm10',use_v2 FROM system.collection_run_v2_m10_read_route UNION ALL
                SELECT 'overview',use_v2 FROM system.collection_run_v2_overview_read_route UNION ALL
                SELECT 'ple',use_v2 FROM system.collection_run_v2_ple_read_route UNION ALL
                SELECT 'query_catalog',use_v2 FROM system.collection_run_v2_query_catalog_read_route UNION ALL
                SELECT 'scheduler',use_v2 FROM system.collection_run_v2_scheduler_read_route UNION ALL
                SELECT 'sql_volume',use_v2 FROM system.collection_run_v2_sql_volume_read_route UNION ALL
                SELECT 'wait',use_v2 FROM system.collection_run_v2_wait_read_route UNION ALL
                SELECT 'writer',use_v2 FROM system.collection_run_v2_writer_route) routes),
              'relations',(SELECT jsonb_agg(jsonb_build_object('schema',n.nspname,'name',c.relname,'kind',c.relkind::text,'owner',pg_get_userbyid(c.relowner),'rls',c.relrowsecurity,'forceRls',c.relforcerowsecurity,'acl',c.relacl::text) ORDER BY n.nspname,c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=ANY(@schemas)),
              'functions',(SELECT jsonb_agg(jsonb_build_object('schema',n.nspname,'name',p.proname,'arguments',pg_get_function_identity_arguments(p.oid),'owner',pg_get_userbyid(p.proowner),'securityDefiner',p.prosecdef,'definitionSha256',encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex'),'acl',p.proacl::text) ORDER BY n.nspname,p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=ANY(@schemas)))::text;
            """);
        command.CommandTimeout = 30;
        command.Parameters.AddWithValue("schemas", Schemas);
        command.Parameters.AddWithValue("roles", Roles);
        string text = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(text), 1, 4 * 1024 * 1024);
        using var document = JsonDocument.Parse(text);
        var state = document.RootElement;
        Assert.Equal(180004, state.GetProperty("serverVersionNum").GetInt32());
        var migrations = state.GetProperty("migrations").EnumerateArray().ToArray();
        Assert.Equal(372, migrations.Length);
        var catalog = PostgreSqlMigrationCatalog.LoadEmbedded();
        for (int index = 0; index < migrations.Length; index++)
        {
            var migration = catalog.Migrations[index];
            Assert.Equal(index + 1, migrations[index].GetProperty("number").GetInt32());
            Assert.Equal(migration.FileName, migrations[index].GetProperty("name").GetString());
            Assert.Equal(migration.ChecksumHex, migrations[index].GetProperty("sha256").GetString());
        }
        var schemas = state.GetProperty("schemas").EnumerateArray().ToArray();
        Assert.Equal(Schemas, schemas.Select(item => item.GetProperty("name").GetString()).ToArray());
        Assert.All(schemas, item => Assert.Equal("sqlobserver_migrator", item.GetProperty("owner").GetString()));
        var roles = state.GetProperty("roles").EnumerateArray().ToArray();
        Assert.Equal(Roles, roles.Select(item => item.GetProperty("name").GetString()).ToArray());
        foreach (var role in roles)
        {
            foreach (string flag in new[] { "canLogin", "superuser", "createDatabase", "createRole", "inherit", "replication" }) Assert.False(role.GetProperty(flag).GetBoolean());
            string name = role.GetProperty("name").GetString()!;
            Assert.Equal(name is "sqlobserver_payload_expirer" or "sqlobserver_report_expirer" or "sqlobserver_run_dependency_reader", role.GetProperty("bypassRls").GetBoolean());
            Assert.Empty(role.GetProperty("memberships").EnumerateArray());
        }
        foreach (var count in state.GetProperty("emptyProof").EnumerateObject()) Assert.Equal(0L, count.Value.GetInt64());
        Assert.True(state.GetProperty("seal").GetProperty("sealed").GetBoolean());
        Assert.Equal("postgres", state.GetProperty("seal").GetProperty("sealedBy").GetString());
        var routes = state.GetProperty("routes").EnumerateObject().ToArray();
        Assert.Equal(11, routes.Length);
        Assert.All(routes, route => Assert.True(route.Value.GetBoolean()));
        Assert.NotEmpty(state.GetProperty("relations").EnumerateArray());
        Assert.NotEmpty(state.GetProperty("functions").EnumerateArray());
        return state.Clone();
    }

    private static string CreateOwnedOutputDirectory(string supplied)
    {
        if (!Path.IsPathFullyQualified(supplied)) throw new InvalidOperationException("Archive output directory must be absolute.");
        // dotnet test may start in its project directory; locate the repository by its artifacts directory and solution.
        DirectoryInfo? cursor = new(Directory.GetCurrentDirectory());
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "SqlObserver.slnx"))) cursor = cursor.Parent;
        if (cursor is null) throw new InvalidOperationException("Archive export requires the repository checkout.");
        string root = Path.GetFullPath(Path.Combine(cursor.FullName, "artifacts"));
        string selected = Path.GetFullPath(supplied);
        if (!selected.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Archive output must be inside this checkout's artifacts directory.");
        for (DirectoryInfo? ancestor = new(selected); ancestor is not null && ancestor.FullName.Length >= root.Length; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Archive output cannot traverse a reparse point.");
        Directory.CreateDirectory(selected);
        string directory = Path.Combine(selected, "native-repository-archive-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new InvalidOperationException("Archive output identity already exists.");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Explicit archive export setting is required: " + name);

    private static bool IsOwnedDatabase(string name) => name.StartsWith("sqlobserver_", StringComparison.Ordinal) &&
        name.Length == 44 && name[12..].All(char.IsAsciiHexDigit);

    private static Task<string> DockerAsync(params string[] arguments) => DockerAsync(4096, arguments);

    private static async Task<string> DockerAsync(int maximumOutputCharacters, params string[] arguments)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var process = new Process { StartInfo = new ProcessStartInfo("docker")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token), stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        string result = await stdout, error = await stderr;
        if (process.ExitCode != 0 || result.Length > maximumOutputCharacters || error.Length > 4096)
            throw new InvalidOperationException($"Owned archive Docker operation {arguments[0]} failed (exit {process.ExitCode}).");
        return result;
    }
}
