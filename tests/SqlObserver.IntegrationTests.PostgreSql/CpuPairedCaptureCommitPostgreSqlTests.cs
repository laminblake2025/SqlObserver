using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CpuPairedCaptureCommitPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string Fingerprint = new('a', 64), Machine = new('b', 64), Logical = new('c', 64);
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private const long ExactLargeInteger = 9007199254740993;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCommitAndReplayPreserveExactPrivateEvidenceBeforeAndAfterCanonicalWriterSeal(bool sealedWriter)
    {
        await using var database = await PrepareAsync(sealedWriter);
        if (sealedWriter) await SqlVolumeWindowHistoryFixture.SealAsync(database);
        Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var request = Commit(run, Capture(run));
        var committed = await runtime.CommitRunAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
        Assert.Equal(1, committed.InsertedCount);
        string receipt = await ReceiptAsync(database, run.Id);
        var payload = JsonNode.Parse(receipt)!.AsObject();
        Assert.Equal("absent", payload["native_qualification"]!.GetValue<string>());
        Assert.False(payload["target_comparison_admitted"]!.GetValue<bool>());
        Assert.Null(payload["sql_process_percent"]); Assert.Null(payload["operating_system_percent"]);
        JsonObject capture = payload["payload"]!["capture"]!.AsObject();
        Assert.True(capture["candidateLocalAssociation"]!.GetValue<bool>());
        Assert.Equal(ExactLargeInteger, capture["cpuBefore"]!["processCounter"]!["firstValue"]!.GetValue<long>());
        Assert.Equal(-ExactLargeInteger, capture["tcpBefore"]!["sqlProcess"]!["exitAfter100ns"]!.GetValue<long>());
        Assert.EndsWith(".1234567", capture["sqlBefore"]!["engine"]!["sourceStartLocal"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(capture["sqlProcessPercent"]); Assert.Null(capture["numericalEligibility"]!["sqlProcessPercent"]);
        Assert.DoesNotContain("serverEndpoint", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("clientEndpoint", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic.invalid", receipt, StringComparison.Ordinal);
        var replay = await runtime.CommitRunAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Replayed, replay.Status);
        Assert.Equal(committed.CommittedAtUtc, replay.CommittedAtUtc);
        Assert.Equal(receipt, await ReceiptAsync(database, run.Id));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run;", ("run", run.Id.Value)));
        if (sealedWriter) Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal((long)request.Summary.Accounting.OutputBytes, await CountAsync(database, "SELECT persisted_bytes::bigint FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
    }

    [Theory]
    [InlineData(CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded)]
    [InlineData(CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure)]
    [InlineData(CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing)]
    [InlineData(CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported)]
    [InlineData(CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed)]
    public async Task FailedAttemptIsCanonicalWithoutInventedCaptureOrCpuMetric(CollectorRunOutcome outcome, CollectorRunReason reason)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var request = Commit(run, null, outcome, reason);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        var row = JsonNode.Parse(await ReceiptAsync(database, run.Id))!;
        Assert.False(row["has_capture"]!.GetValue<bool>()); Assert.Null(row["payload"]!["capture"]);
        Assert.False(row["target_comparison_admitted"]!.GetValue<bool>());
        Assert.Equal(0L, await CountAsync(database, "SELECT persisted_bytes::bigint FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(outcome == CollectorRunOutcome.OutputInvalid ? 1L : 0L,
            await CountAsync(database, "SELECT rejected_item_count::bigint FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.visibility_gap WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ReturnedIncompleteSourceIsAnUnavailableDiagnosticReceiptWithItsActualRefusalTokens()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var request = Commit(run, Capture(run, complete: false));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        var capture = JsonNode.Parse(await ReceiptAsync(database, run.Id))!["payload"]!["capture"]!;
        Assert.Equal(0, capture["state"]!.GetValue<int>());
        Assert.Equal("wrong_host", capture["tcpBeforeReadReason"]!.GetValue<string>());
        Assert.Equal("permission_denied", capture["cpuPairReadReason"]!.GetValue<string>());
        Assert.Equal("process_mapping_ambiguous", capture["processReadReason"]!.GetValue<string>());
        Assert.Null(capture["sqlBefore"]); Assert.Null(capture["cpuBefore"]);
        Assert.Equal(0L, await CountAsync(database, "SELECT source_row_count::bigint FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(1L, await CountAsync(database, "SELECT inserted_item_count::bigint FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
    }

    [Fact]
    public async Task MissingHostBindingPersistsGeneratedDiagnosticOutputWithoutInventedSourceResponseBytes()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var request = Commit(run, Capture(run, complete: false, noSourceResponse: true));
        Assert.Equal(0, request.Summary.Accounting.ResponseBytes); Assert.True(request.Summary.Accounting.OutputBytes > 0);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal(0L, await CountAsync(database, "SELECT response_bytes FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
        var captured = JsonNode.Parse(await ReceiptAsync(database, run.Id))!["payload"]!["capture"]!;
        Assert.Equal((int)CpuPairedCaptureGap.HostBindingUnavailable, captured["reason"]!.GetValue<int>());
        Assert.Null(captured["cpuBefore"]); Assert.False(captured["targetComparisonAdmitted"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("foreign_context")]
    [InlineData("admission")]
    [InlineData("public_percent")]
    [InlineData("private_percent")]
    [InlineData("raw_address")]
    [InlineData("promoted_witness")]
    [InlineData("source_frames")]
    [InlineData("response_bytes")]
    [InlineData("ordinal")]
    [InlineData("helper_bytes")]
    [InlineData("enum")]
    [InlineData("eligibility")]
    [InlineData("read_reason")]
    [InlineData("partial_outcome")]
    [InlineData("success_loss")]
    public async Task NativeClosedWriterRejectsTamperingWithoutAdvancingTheRun(string changed)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target); var request = Commit(run, Capture(run));
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(collector, request, body =>
        {
            JsonObject capture = body["capture"]!.AsObject();
            switch (changed)
            {
                case "foreign_context": capture["cpuBefore"]!["binding"]!["targetId"] = Guid.NewGuid(); break;
                case "admission": capture["targetComparisonAdmitted"] = true; break;
                case "public_percent": capture["sqlProcessPercent"] = 42; break;
                case "private_percent": capture["numericalEligibility"]!["sqlProcessPercent"] = 42; break;
                case "raw_address": capture["tcpBefore"]!["serverAddress"] = "127.0.0.1"; break;
                case "promoted_witness": capture["cpuBefore"]!["binding"]!["sqlProcessWitness"] = 1; break;
                case "source_frames": body["sourceFrames"] = 5; break;
                case "ordinal": capture["sqlAfter"]!["ordinal"] = 1; break;
                case "helper_bytes": capture["bytes"]!["tcpBeforeHelperBytes"] = 65537; break;
                case "enum": capture["candidateReason"] = 999; break;
                case "eligibility": capture["numericalEligibility"]!["sameNativeInterval"] = false; break;
                case "read_reason": capture["processReadReason"] = "unbounded exception text"; break;
                case "partial_outcome": body["outcome"] = "partial"; break;
            }
        }, command =>
        {
            if (changed == "response_bytes") command.Parameters["response_bytes"].Value = 0L;
            if (changed == "partial_outcome") command.Parameters["outcome"].Value = "partial";
            if (changed == "success_loss")
            { command.Parameters["loss_kind"].Value = "visibility_incomplete"; command.Parameters["minimum_lost_items"].Value = 1; }
        }));
        Assert.Equal("22023", rejected.SqlState);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.cpu_paired_capture_receipt WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM control.collector_schedule WHERE active_run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ChangedReplayAndLostLeaseCannotRewriteAnImmutableCompletion()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target); var request = Commit(run, Capture(run));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        string receipt = await ReceiptAsync(database, run.Id);
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(collector, request,
            body => body["capture"]!["processStatusAfter"] = 5));
        Assert.Equal("40001", rejected.SqlState); Assert.Equal(receipt, await ReceiptAsync(database, run.Id));
        Assert.Equal(LeaseReleaseStatus.Released, await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(run.Lease, Timeout), CancellationToken.None));
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal(receipt, await ReceiptAsync(database, run.Id));
    }

    [Theory]
    [InlineData("lost_lease")]
    [InlineData("request_digest")]
    [InlineData("target_revision")]
    public async Task FreshCommitRequiresTheActualLeaseRunAndCurrentTargetFence(string changed)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target); var request = Commit(run, Capture(run));
        if (changed == "lost_lease")
            Assert.Equal(LeaseReleaseStatus.Released, await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(run.Lease, Timeout), CancellationToken.None));
        if (changed == "target_revision")
            await ExecuteAsync(database, "UPDATE control.observation_target SET revision=revision+1,updated_at=clock_timestamp() WHERE instance_id=@target;", ("target", target));
        // Target mutation completes the active run before any staged CPU write.
        string? canonicalCompletion = changed == "target_revision"
            ? await OutcomeAsync(database, run.Id) : null;
        if (canonicalCompletion is not null)
            Assert.Equal(1L, await CountAsync(database,
                "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run AND outcome='permanent_failure' AND reason_code='target_revision_changed';",
                ("run", run.Id.Value)));
        var error = await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(collector, request, _ => { },
            changed == "request_digest" ? command => command.Parameters["request_digest"].Value = new byte[32] : null));
        Assert.Equal(changed == "target_revision" ? "40001" : "55000", error.SqlState);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.cpu_paired_capture_receipt WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(changed == "target_revision" ? 1L : 0L,
            await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
        if (canonicalCompletion is not null)
            Assert.Equal(canonicalCompletion, await OutcomeAsync(database, run.Id));
    }

    [Fact]
    public async Task RetentionPolicyUsesExistingGlobalAuthorizationAndBoundedLifetimeWithoutChangingOtherClasses()
    {
        await using var database = await PrepareAsync(); await using var server = database.CreateServerDataSource();
        async Task<long> ChangeAsync(string role, TimeSpan age, int floor, long revision)
        {
            await using var connection = await server.OpenConnectionAsync(); await using var transaction = await connection.BeginTransactionAsync();
            await using (var context = new NpgsqlCommand("SELECT set_config('sqlobserver.role',@role,true),set_config('sqlobserver.authorization_scope','global',true);", connection, transaction))
            { context.Parameters.AddWithValue("role", role); await context.ExecuteNonQueryAsync(); }
            await using var command = new NpgsqlCommand("SELECT system.update_m10_retention_policy('cpu_paired_captures',true,@age,@floor,@revision,'S-1-5-21-1234','Native bounded policy qualification');", connection, transaction);
            command.Parameters.AddWithValue("age", age); command.Parameters.AddWithValue("floor", floor); command.Parameters.AddWithValue("revision", revision);
            long result = Assert.IsType<long>(await command.ExecuteScalarAsync()); await transaction.CommitAsync(); return result;
        }
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync("TargetAdministrator", TimeSpan.FromDays(90), 3, 1))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync("SecurityAdministrator", TimeSpan.FromDays(89), 3, 1))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync("SecurityAdministrator", TimeSpan.FromDays(90), 2, 1))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync("SecurityAdministrator", TimeSpan.FromDays(3651), 3, 1))).SqlState);
        Assert.Equal(2L, await ChangeAsync("SecurityAdministrator", TimeSpan.FromDays(90), 3, 1));
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync("SecurityAdministrator", TimeSpan.FromDays(90), 3, 1))).SqlState);
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM system.retention_policy WHERE data_class='cpu_paired_captures' AND enabled AND retain_for=interval '90 days' AND minimum_partitions_to_keep=3 AND policy_revision=2;"));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM system.retention_policy WHERE data_class IN('server_lifecycle_receipts','server_lifecycle_events') AND enabled;"));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM control.collector_contract WHERE collector_id='cpu.paired-capture';"));
    }

    [Fact]
    public async Task PrivatePartitionsReferencesAndRetentionAdmissionDoNotActivateOrExposeTheSource()
    {
        await using var database = await PrepareAsync();
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM control.collector_contract WHERE collector_id='cpu.paired-capture';"));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM control.collector_schedule WHERE collector_id='cpu.paired-capture';"));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM system.retention_policy WHERE data_class='cpu_paired_captures' AND NOT enabled AND retain_for IS NULL AND minimum_partitions_to_keep=3;"));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM system.collection_run_reviewed_references() WHERE child_relation='telemetry.cpu_paired_capture_receipt'::regclass AND column_name='run_id';"));
        Assert.Equal(3L, await CountAsync(database, "SELECT count(*) FROM system.partition_registry WHERE parent_schema='telemetry' AND parent_table='cpu_paired_capture_receipt' AND lifecycle_state='attached';"));
        Assert.Equal(0L, await CountAsync(database, """
            SELECT count(*) FROM pg_class c WHERE (c.oid='telemetry.cpu_paired_capture_receipt'::regclass OR
             c.oid IN(SELECT inhrelid FROM pg_inherits WHERE inhparent='telemetry.cpu_paired_capture_receipt'::regclass))
            AND NOT(c.relrowsecurity AND c.relforcerowsecurity AND c.relowner='sqlobserver_migrator'::regrole);
            """));
        foreach (string role in new[] { "sqlobserver_collector", "sqlobserver_server", "sqlobserver_auditor" })
            Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM pg_class c WHERE (c.oid='telemetry.cpu_paired_capture_receipt'::regclass OR c.oid IN(SELECT inhrelid FROM pg_inherits WHERE inhparent='telemetry.cpu_paired_capture_receipt'::regclass)) AND has_table_privilege(@role,c.oid,'SELECT,INSERT,UPDATE,DELETE');", ("role", role)));
        Assert.Equal(0L, await CountAsync(database, """
            SELECT count(*) FROM pg_proc p CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
            WHERE p.pronamespace='control'::regnamespace AND p.proname IN('validate_cpu_capture_json','commit_cpu_paired_capture_collection_run','ensure_cpu_paired_capture_partitions','ensure_cpu_paired_capture_partition_set') AND a.grantee=0;
            """));
        await using var collector = database.CreateCollectorDataSource();
        await using (var command = collector.CreateCommand("SELECT control.ensure_cpu_paired_capture_partition_set(@owner,1);"))
        {
            command.Parameters.AddWithValue("owner", Guid.NewGuid());
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
        }
        var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("collector/catalog/reconcile"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
        var lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        await using var ensure = collector.CreateCommand("SELECT control.ensure_cpu_paired_capture_partition_set(@owner,@fencing);");
        ensure.Parameters.AddWithValue("owner", lease.Owner.Value); ensure.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        Assert.Equal(0, await ensure.ExecuteScalarAsync());
        Guid target = await SeedAsync(database); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        await runtime.CommitRunAsync(Commit(run, Capture(run)), CancellationToken.None);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database,
            "UPDATE telemetry.cpu_paired_capture_receipt SET has_capture=false WHERE run_id=@run;", ("run", run.Id.Value)))).SqlState);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database,
            "DELETE FROM telemetry.cpu_paired_capture_receipt WHERE run_id=@run;", ("run", run.Id.Value)))).SqlState);
        // The retention dependency reader intentionally has BYPASSRLS. Probe scope
        // with a disposable ordinary role; its creation/grants roll back with this transaction.
        Assert.Equal(1L, await CountAsync(database,
            "SELECT count(*) FROM pg_roles WHERE rolname='sqlobserver_run_dependency_reader' AND rolbypassrls;"));
        await using var scoped = await database.DataSource.OpenConnectionAsync(); await using var tx = await scoped.BeginTransactionAsync();
        string probeRole = $"sqlobserver_cpu_scope_probe_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlCommand($"""
            CREATE ROLE "{probeRole}" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
            GRANT USAGE ON SCHEMA telemetry TO "{probeRole}";
            GRANT SELECT ON telemetry.cpu_paired_capture_receipt TO "{probeRole}";
            """, scoped, tx))
            await create.ExecuteNonQueryAsync();
        await using var readerRole = new NpgsqlCommand($"SET LOCAL ROLE \"{probeRole}\"; SELECT set_config('sqlobserver.target_scope',@target,true);", scoped, tx);
        readerRole.Parameters.AddWithValue("target", target.ToString("D")); await readerRole.ExecuteNonQueryAsync();
        await using var visible = new NpgsqlCommand("SELECT count(*) FROM telemetry.cpu_paired_capture_receipt;", scoped, tx);
        Assert.Equal(1L, await visible.ExecuteScalarAsync());
        readerRole.Parameters["target"].Value = Guid.NewGuid().ToString("D"); await readerRole.ExecuteNonQueryAsync();
        Assert.Equal(0L, await visible.ExecuteScalarAsync()); await tx.RollbackAsync();
    }

    private async Task<RepositoryTestDatabase> PrepareAsync(bool canonicalForeignKeys = false)
    {
        RepositoryTestDatabase database;
        if (canonicalForeignKeys) database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        else
        {
            database = await fixture.CreateDatabaseAsync();
            var applied = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(
                MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
            Assert.False(applied.HasFailures, string.Join(",", applied.Results.Where(row => row.Outcome == MigrationOutcome.Failed)
                .Select(row => $"{row.Migration.Number.Value}:{row.FailureCode}")));
        }
        await using var version = database.DataSource.CreateCommand("SHOW server_version_num;");
        Assert.Equal("180004", await version.ExecuteScalarAsync());
        return database;
    }

    internal static async Task<Guid> SeedAsync(RepositoryTestDatabase database)
    {
        // Explicit disposable-fixture admission only. No production registry or default schedule changes.
        var manifest = Assert.IsType<CollectorManifest>(typeof(PairedCpuCaptureCollector).GetMethod("CreateManifest", Hidden)!.Invoke(null, null));
        using Stream asset = typeof(SqlServerCpuTargetWitnessReader).Assembly.GetManifestResourceStream(
            "SqlObserver.Infrastructure.SqlServer.CpuWitnessSourceAssets.cpu.target-witness.sqlserver15-17-windows.v2.sql")!;
        using var bytes = new MemoryStream(); await asset.CopyToAsync(bytes);
        await ExecuteAsync(database, """
            INSERT INTO control.collector_contract(collector_id,collector_version,execution_order,manifest_schema_version,
             output_schema_version,manifest_sha256,asset_bundle_sha256,default_interval,minimum_interval,execution_timeout,
             maximum_rows,maximum_response_bytes,estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval)
            SELECT 'cpu.paired-capture',1,max(execution_order)+1,1,1,@manifest,@asset,@interval,@minimum,@timeout,
             @rows,@bytes,'moderate',@attempts,@threshold,@open FROM control.collector_contract;
            """, ("manifest", SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest))), ("asset", SHA256.HashData(bytes.ToArray())),
            ("interval", manifest.Intervals.DefaultInterval), ("minimum", manifest.Intervals.HardMinimumInterval),
            ("timeout", manifest.Limits.CommandTimeout), ("rows", manifest.Limits.MaxRows), ("bytes", manifest.Limits.MaxResponseBytes),
            ("attempts", manifest.Resilience.MaximumAttempts), ("threshold", manifest.Resilience.CircuitFailureThreshold), ("open", manifest.Resilience.CircuitOpenDuration));
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, """
            WITH sampled_clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
             authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT @target,@key,'CPU native fixture','synthetic.invalid',1433,interval '5 seconds',
             'windows_integrated_service_identity','mandatory_validated','active',1,now_utc,now_utc,now_utc FROM sampled_clock;
            INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,
             enabled,collection_interval,next_due_at,created_at,updated_at)
            VALUES(@target,'cpu.paired-capture',1,1,1,true,interval '30 seconds',statement_timestamp(),statement_timestamp(),statement_timestamp())
            ON CONFLICT(instance_id,collector_id) DO NOTHING;
            UPDATE control.collector_schedule SET enabled=(collector_id IN('capability.connection','engine.core','cpu.paired-capture')),
             next_due_at=CASE WHEN collector_id='cpu.paired-capture' THEN statement_timestamp()-interval '1 second' ELSE statement_timestamp()+interval '1 day' END,
             last_outcome=CASE WHEN collector_id IN('capability.connection','engine.core') THEN 'succeeded' ELSE last_outcome END,
             last_succeeded_at=CASE WHEN collector_id IN('capability.connection','engine.core') THEN statement_timestamp() ELSE last_succeeded_at END
            WHERE instance_id=@target;
            """, ("target", target), ("key", "cpu.native." + target.ToString("N")));
        return target;
    }

    internal sealed record Run(CollectorDueWorkItem Work, CollectorRunId Id, WorkerLeaseIdentity Lease);
    internal static async Task<Run> BeginAsync(RepositoryTestDatabase database, NpgsqlDataSource collector,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target)
    {
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='cpu.paired-capture';", ("target", target));
        var work = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items,
            row => row.TargetId.Value == target && row.CollectorId.Value == "cpu.paired-capture");
        var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new($"collector/run/cpu.paired-capture/{target:N}"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
        var lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity; var id = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, id, lease, Timeout), CancellationToken.None)).Status);
        return new(work, id, lease);
    }

    internal static CpuPairedCaptureObservation Capture(Run run, bool complete = true, bool noSourceResponse = false)
    {
        var binding = new PairedCpuBinding(run.Work.TargetId, run.Work.TargetRevision, run.Id, Fingerprint, 10);
        var host = new PairedCpuHostEvidence(Fingerprint, Machine, ExactLargeInteger, PairedCpuHostWitness.NativeMachineAndBoot);
        var engine = new ServerLifecycleEngineEvidence(Fingerprint, Logical, 10,
            new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(1234567), 100, 1000);
        Guid session = Guid.NewGuid(), connection = Guid.NewGuid(), serverConnection = Guid.NewGuid(), query = Guid.NewGuid();
        var utc = run.Work.RepositoryTimeUtc;
        var sqlBefore = new CpuPairedSqlEvidence(run.Work.TargetId, run.Work.TargetRevision, run.Id, session, connection, 1,
            utc, engine, SqlCpuPhysicalTransport.Tcp, ExactLargeInteger, 20, 4, 2, serverConnection, 20, 42, CpuTcpSocketCompleteness.Complete);
        var sqlAfter = sqlBefore with { Ordinal = 2, SourceObservedAtUtc = utc.AddSeconds(2), ProcessUserTimeMs = ExactLargeInteger + 2 };
        var process = new CpuTcpProcessEpoch(ExactLargeInteger, ExactLargeInteger, -ExactLargeInteger, 0, 258);
        CpuPairedSocketRow[] sockets = [new(CpuPairedSocketDirection.ServerToClient, 5, 10), new(CpuPairedSocketDirection.ClientToServer, 5, 42)];
        var tcpBefore = new CpuPairedTcpEvidence(binding, utc, 1000, host, process, process, 0, 0, 2, 0, sockets);
        var tcpAfter = new CpuPairedTcpEvidence(binding, utc.AddSeconds(2), 2000, host, process, process, 0, 0, 2, 0, sockets);
        var counter = new PdhCpuRawCounter(PdhCpuRawCounter.ProcessTimerType, 0, ExactLargeInteger, ExactLargeInteger + 1, ExactLargeInteger + 2);
        var cpuBefore = new PairedCpuEndpoint(binding, query, utc.AddMilliseconds(500), 1200, 10_000_000, host,
            ExactLargeInteger, 4, counter, counter with { CounterType = PdhCpuRawCounter.OsIdleInverseTimerType });
        var cpuAfter = new PairedCpuEndpoint(binding, query, utc.AddMilliseconds(1500), 1800, 10_000_000, host,
            ExactLargeInteger, 4, counter with { FirstValue = ExactLargeInteger + 10, SecondValue = ExactLargeInteger + 20 }, counter);
        var frame = new CpuPairedFrameWitness(ExactLargeInteger, ExactLargeInteger, -ExactLargeInteger, 4, 4, 1, false, 258);
        return new(run.Work.TargetId, run.Work.TargetRevision, run.Id, complete ? 1 : null, complete ? 1 : null,
            complete ? CpuPairedCaptureGap.SourceQualificationAbsent : noSourceResponse ? CpuPairedCaptureGap.HostBindingUnavailable : CpuPairedCaptureGap.NativeCaptureIncomplete,
            complete ? CpuTcpAssociationGap.None : CpuTcpAssociationGap.NativeCaptureIncomplete,
            complete ? sqlBefore : null, complete ? sqlAfter : null, complete ? tcpBefore : null, complete ? tcpAfter : null,
            complete ? cpuBefore : null, complete ? cpuAfter : null, complete ? counter : null, complete ? counter : null,
            complete ? frame : null, complete ? frame : null, complete ? Fingerprint : null, complete ? Fingerprint : null, 0, 0, 0, 0,
            complete ? new(true, true, true, PairedCpuGapReason.None, PairedCpuGapReason.None, 1000, 2000, 1000, 2000) : null,
            new(complete ? 6000 : null, complete ? 6000 : null, complete ? 6000 : null, noSourceResponse ? 0 : 16384),
            complete ? null : noSourceResponse ? "host_binding_unavailable" : "wrong_host", complete ? null : "permission_denied", complete ? null : "tcp_source_unavailable", complete ? null : "process_mapping_ambiguous");
    }

    internal static CommitCollectorRunRequest Commit(Run run, CpuPairedCaptureObservation? capture,
        CollectorRunOutcome outcome = CollectorRunOutcome.Succeeded, CollectorRunReason reason = CollectorRunReason.Completed)
    {
        var payload = capture is null ? CollectorPayload.Empty : new CollectorPayload(cpuPairedCapture: capture);
        var summary = new CollectorRunSummary(run.Id, run.Work.TargetId, run.Work.TargetRevision, run.Work.CollectorId,
            1, 1, outcome, reason, TimeSpan.FromMilliseconds(5), 1,
            new(capture?.SourceFrames ?? 0, payload.ItemCount, capture is null ? 0 : checked((int)CpuPairedCaptureOutputValidation.AccountedResponseBytes(capture)), payload.EstimatedSizeBytes),
            outcome == CollectorRunOutcome.OutputInvalid ? new(CollectorLossKind.OutputValidationFailure, 1, false) : CollectorLossEvidence.None);
        var circuit = outcome is CollectorRunOutcome.TransientFailure or CollectorRunOutcome.TimedOut
            ? new CollectorCircuitSnapshot(CollectorCircuitState.Closed, 1, run.Work.RepositoryTimeUtc)
            : CollectorCircuitSnapshot.Closed(run.Work.RepositoryTimeUtc);
        return new(run.Work, summary, payload, circuit, run.Lease, Timeout);
    }

    private static async Task<string> RawCommitAsync(NpgsqlDataSource collector, CommitCollectorRunRequest request,
        Action<JsonObject> mutate, Action<NpgsqlCommand>? mutateCommand = null)
    {
        Type port = typeof(PostgreSqlCollectorRuntimeRepositoryPort);
        string sql = Assert.IsType<string>(port.GetField("CommitCpuPairedCaptureSql", Hidden)!.GetValue(null));
        byte[] digest = (byte[])port.GetMethod("CreateRequestDigest", Hidden)!.Invoke(null, [request.Work, request.Summary.RunId, request.Lease])!;
        await using var connection = await collector.OpenConnectionAsync(); await using var command = new NpgsqlCommand(sql, connection);
        port.GetMethod("AddRunIdentity", Hidden)!.Invoke(null, [command, request.Work, request.Summary.RunId, request.Lease, digest]);
        port.GetMethod("AddSummaryCommitParameters", Hidden)!.Invoke(null, [command, request]);
        Type projection = port.Assembly.GetType("SqlObserver.Infrastructure.PostgreSql.CpuPairedCapturePersistencePayload")!;
        string outcome = (string)port.GetMethod("MapOutcome", Hidden)!.Invoke(null, [request.Summary.Outcome])!;
        string reason = (string)port.GetMethod("MapReason", Hidden)!.Invoke(null, [request.Summary.Reason])!;
        object body = projection.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [request, outcome, reason])!;
        var json = JsonNode.Parse(JsonSerializer.Serialize(body, Web))!.AsObject(); mutate(json);
        command.Parameters.AddWithValue("cpu_capture_payload", NpgsqlDbType.Jsonb, json.ToJsonString()); mutateCommand?.Invoke(command);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
    private static async Task<string> OutcomeAsync(RepositoryTestDatabase database, CollectorRunId run)
    {
        await using var command = database.DataSource.CreateCommand("SELECT to_jsonb(r)::text FROM telemetry.collection_run_outcome r WHERE run_id=@run;");
        command.Parameters.AddWithValue("run", run.Value); return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
    private static async Task<string> ReceiptAsync(RepositoryTestDatabase database, CollectorRunId run)
    {
        await using var command = database.DataSource.CreateCommand("SELECT to_jsonb(r)::text FROM telemetry.cpu_paired_capture_receipt r WHERE run_id=@run;");
        command.Parameters.AddWithValue("run", run.Value); return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
    internal static async Task<long> CountAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql); foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        object? value = await command.ExecuteScalarAsync(); return value is null ? 0 : Assert.IsType<long>(value);
    }
    internal static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql); foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        await command.ExecuteNonQueryAsync();
    }
}
