using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class BackupEvidenceReadTests
{
    [Fact]
    public async Task BackupSqlSignalsAndCanonicalObservationsMatchProductionForIndependentPolicyAndUnknownCases()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var at = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        BackupFleetSummary?[] cases =
        [
            null,
            BindingSummary(target, at, BindingCounts(within: 2), BindingCounts(within: 2)),
            BindingSummary(target, at, BindingCounts(violated: 1, unknown: 1), BindingCounts(unknown: 2), coverage: BackupFreshnessCoverage.Partial, faults: 1),
            BindingSummary(target, at, BindingCounts(unknown: 2), BindingCounts(notRequired: 2)),
            BindingSummary(target, at, BindingCounts(unknown: 2), BindingCounts(unknown: 2), faults: 1),
            BindingSummary(target, at, BindingCounts(disabled: 2), BindingCounts(disabled: 2)),
            BindingSummary(target, at, BindingCounts(within: 1, disabled: 1), BindingCounts(notRequired: 2)),
            BindingSummary(target, at, BindingCounts(violated: 2), BindingCounts(unknown: 2), enabled: false, faults: 2),
            BindingSummary(target, at, BindingCounts(within: 2), BindingCounts(within: 2), coverage: BackupFreshnessCoverage.Partial),
            BindingSummary(target, at, BindingCounts(within: 2), BindingCounts(notRequired: 2)),
            BindingSummary(target, at, BindingCounts(within: 2), BindingCounts(unknown: 2)),
            BindingSummary(target, at, BindingCounts(within: 2), BindingCounts(violated: 2)),
            BindingSummary(target, at, BindingCounts(violated: 512), BindingCounts(notRequired: 512), databases: 512, faults: 512),
        ];
        foreach (BackupFleetSummary? summary in cases)
        {
            string? payload = summary is null ? null : await BindingPayloadAsync(database, summary);
            string? digest = payload is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var evidence = new BackupAlertEvidence(target, 1, at.AddSeconds(1), summary, digest);
            string signals = JsonSerializer.Serialize(BackupAlertSignalProjection.Build(evidence), BindingSignalOptions);
            foreach (AlertRuleKind kind in BindingKinds)
            {
                AlertRuleDefinition rule = BindingRule(kind);
                AlertObservation observation = BackupAlertObservationFactory.Create(rule, evidence);
                JsonObject wire = BackupDecisionWire(observation, new(new(rule.RuleId, target), null, false, "test"));
                await using var command = database.DataSource.CreateCommand("""
                    SELECT alerting.backup_alert_signals(@target,1,@at,@payload,@digest)=@signals,
                      alerting.backup_alert_observation(@target,1,@rule,@kind,@at,@payload,@digest)=
                        alerting.canonical_decision_snapshot(jsonb_build_object('Observation',@observation))->'Observation';
                    """);
                command.Parameters.AddWithValue("target", target.Value);
                command.Parameters.AddWithValue("at", evidence.ReadAtUtc);
                command.Parameters.AddWithValue("rule", rule.RuleId);
                command.Parameters.AddWithValue("kind", (short)kind);
                command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, (object?)payload ?? DBNull.Value);
                command.Parameters.AddWithValue("digest", NpgsqlDbType.Text, (object?)digest ?? DBNull.Value);
                command.Parameters.AddWithValue("signals", NpgsqlDbType.Jsonb, signals);
                command.Parameters.AddWithValue("observation", NpgsqlDbType.Jsonb, wire["Observation"]!.ToJsonString());
                await using NpgsqlDataReader row = await command.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
                Assert.True(row.GetBoolean(0), $"Signals differ for {kind}");
                Assert.True(row.GetBoolean(1), $"Canonical observation differs for {kind}");
            }
        }
    }

    [Fact]
    public async Task BackupCaptureGuardRejectsForgedObservationsAndChangedLiveEvidence()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        var reader = new PostgreSqlBackupAlertEvidencePort(collector);
        AlertRuleDefinition rule = BindingRule(AlertRuleKind.BackupFullPolicy);
        BackupAlertEvidence missing = await reader.ReadAsync(new(target), 1, lease, Timeout, CancellationToken.None);
        JsonObject absent = BindingObservation(rule, missing);
        await AssertBindingAsync(database, target, rule, missing.ReadAtUtc, null, absent, lease);
        await CollectAsync(database, target, databaseCount: 1, withLog: true);
        await SetPolicyAsync(server, target);
        WorkerLeaseIdentity overviewLease = await OverviewLeaseAsync(collector);
        BackupFleetSummary summary = await new PostgreSqlBackupFleetSummaryPort(collector).ReadAsync(new(new(target), 1), overviewLease, Timeout, CancellationToken.None);
        Assert.True(await new PostgreSqlBackupFleetCachePort(collector).PublishAsync(summary, overviewLease, Timeout, CancellationToken.None));
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBindingAsync(database, target, rule, missing.ReadAtUtc, null, absent, lease))).SqlState);

        BackupAlertEvidence capture = await reader.ReadAsync(new(target), 1, lease, Timeout, CancellationToken.None);
        await using var payloadRead = database.DataSource.CreateCommand("SELECT payload::text FROM reporting.overview_backup_latest WHERE instance_id=@target;");
        payloadRead.Parameters.AddWithValue("target", target);
        string payload = Assert.IsType<string>(await payloadRead.ExecuteScalarAsync());
        foreach (AlertRuleKind kind in BindingKinds)
        {
            rule = BindingRule(kind);
            JsonObject observation = BindingObservation(rule, capture);
            await AssertBindingAsync(database, target, rule, capture.ReadAtUtc, payload, observation, lease);
            JsonObject forged = observation.DeepClone().AsObject();
            forged["Value"] = kind == AlertRuleKind.BackupObservedFaults ? 0 : 2;
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() =>
                AssertBindingAsync(database, target, rule, capture.ReadAtUtc, payload, forged, lease))).SqlState);
        }
        rule = BindingRule(AlertRuleKind.BackupFullPolicy);
        JsonObject valid = BindingObservation(rule, capture);
        foreach (string field in new[] { "Reason", "SampleId", "OperationId", "RunId", "EvidenceDigest", "SourceKind", "SourceVersion" })
        {
            JsonObject forged = valid.DeepClone().AsObject();
            forged[field] = field is "RunId" or "OperationId" or "SampleId" ? Guid.NewGuid().ToString("D") : "forged";
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() =>
                AssertBindingAsync(database, target, rule, capture.ReadAtUtc, payload, forged, lease))).SqlState);
        }
        JsonObject extra = valid.DeepClone().AsObject(); extra["extra"] = true;
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBindingAsync(database, target, rule, capture.ReadAtUtc, payload, extra, lease))).SqlState);
        JsonObject subMicrosecond = valid.DeepClone().AsObject();
        subMicrosecond["ObservedAtUtc"] = capture.ReadAtUtc.AddTicks(1).ToString("O");
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBindingAsync(database,target,rule,capture.ReadAtUtc,payload,subMicrosecond,lease))).SqlState);
        WorkerLeaseIdentity wrongLease = new(lease.Key, new(Guid.NewGuid()), lease.FencingToken);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBindingAsync(database, target, rule, capture.ReadAtUtc, payload, valid, wrongLease))).SqlState);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBindingAsync(database, target, rule, capture.ReadAtUtc.AddMinutes(-3), payload, valid, lease))).SqlState);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBindingAsync(database, target, rule, capture.ReadAtUtc.AddMinutes(3), payload, valid, lease))).SqlState);

        await SetPolicyAsync(server, target, enabled: false, expected: 1);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBindingAsync(database, target, rule, capture.ReadAtUtc, payload, valid, lease))).SqlState);
        BackupAlertEvidence invalidated = await reader.ReadAsync(new(target), 1, lease, Timeout, CancellationToken.None);
        Assert.Null(invalidated.Summary);
        await AssertBindingAsync(database, target, rule, invalidated.ReadAtUtc, null, BindingObservation(rule, invalidated), lease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupCaptureGuardHoldsPublicationUntilCommitAndRejectsTheSupersededDigest(bool hadCache)
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await CollectAsync(database,target,databaseCount:1,withLog:true);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await SetPolicyAsync(server,target);
        WorkerLeaseIdentity alertLease = await BackupAlertLeaseAsync(collector);
        WorkerLeaseIdentity overviewLease = await OverviewLeaseAsync(collector);
        var summaries = new PostgreSqlBackupFleetSummaryPort(collector);
        var cache = new PostgreSqlBackupFleetCachePort(collector);
        BackupFleetSummary initial = await summaries.ReadAsync(new(new(target),1),overviewLease,Timeout,CancellationToken.None);
        if (hadCache) Assert.True(await cache.PublishAsync(initial,overviewLease,Timeout,CancellationToken.None));
        BackupAlertEvidence capture = await new PostgreSqlBackupAlertEvidencePort(collector).ReadAsync(new(target),1,alertLease,Timeout,CancellationToken.None);
        AlertRuleDefinition rule = BindingRule(AlertRuleKind.BackupFullPolicy);
        JsonObject observation = BindingObservation(rule,capture);
        string? payload = hadCache ? await BindingPayloadAsync(database,initial) : null;
        BackupFleetSummary refreshed = await summaries.ReadAsync(new(new(target),1),overviewLease,Timeout,CancellationToken.None);
        Assert.True(refreshed.Context.AtUtc>initial.Context.AtUtc);
        await using NpgsqlConnection guarded = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await guarded.BeginTransactionAsync();
        await ExecuteBindingAsync(guarded,transaction,target,rule,capture.ReadAtUtc,payload,observation,alertLease);
        var publishingConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { ApplicationName="backup-alert-guard-publication", Pooling=false };
        var publishingBuilder = new NpgsqlDataSourceBuilder(publishingConnection.ConnectionString);
        publishingBuilder.UsePhysicalConnectionInitializer(
            connection => { using var role = new NpgsqlCommand("SET ROLE sqlobserver_collector;",connection); role.ExecuteNonQuery(); },
            async connection => { await using var role = new NpgsqlCommand("SET ROLE sqlobserver_collector;",connection); await role.ExecuteNonQueryAsync(); });
        await using NpgsqlDataSource publishing = publishingBuilder.Build();
        Task<bool> publication = new PostgreSqlBackupFleetCachePort(publishing).PublishAsync(refreshed,overviewLease,Timeout,CancellationToken.None).AsTask();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            bool blocked;
            do
            {
                await using var waiting = database.DataSource.CreateCommand("""
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                        WHERE application_name='backup-alert-guard-publication' AND wait_event_type='Lock');
                    """);
                blocked = (bool)(await waiting.ExecuteScalarAsync(deadline.Token))!;
                if (!blocked) await Task.Delay(10,deadline.Token);
            } while (!blocked);
            Assert.False(publication.IsCompleted);
            await transaction.CommitAsync();
        }
        finally
        {
            await transaction.DisposeAsync();
            await publication;
        }
        Assert.True(await publication);
        Assert.Equal("40001",(await Assert.ThrowsAsync<PostgresException>(() =>
            AssertBindingAsync(database,target,rule,capture.ReadAtUtc,payload,observation,alertLease))).SqlState);
    }

    [Fact]
    public async Task BackupSqlSignalBoundaryRejectsMalformedCountsContextsAndDigestsAndKeepsKernelsPrivate()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var at = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        string original = await BindingPayloadAsync(database, BindingSummary(target, at, BindingCounts(within: 2), BindingCounts(within: 2)));
        for (int defect = 0; defect < 8; defect++)
        {
            JsonObject payload = JsonNode.Parse(original)!.AsObject();
            JsonObject context = payload["summary"]!["context"]!.AsObject();
            switch (defect)
            {
                case 0: payload["summary"]!["full"]!["withinThreshold"] = 1.5; break;
                case 1: payload["summary"]!["full"]!["disabled"] = -1; break;
                case 2: context["databaseCount"] = 513; break;
                case 3: context["targetId"] = Guid.NewGuid().ToString("D"); break;
                case 4: context["targetRevision"] = 2; break;
                case 5: payload["summary"]!["status"] = 2; break;
                case 6: payload["summary"]!["extra"] = true; break;
                case 7: payload["summary"]!["validUntilUtc"] = at.ToString("O"); break;
            }
            await using var command = database.DataSource.CreateCommand("""
                SELECT alerting.backup_alert_signals(@target,1,@at,@payload,
                    encode(sha256(convert_to(@payload::jsonb::text,'UTF8')),'hex'));
                """);
            command.Parameters.AddWithValue("target", target.Value); command.Parameters.AddWithValue("at", at);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload.ToJsonString());
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteScalarAsync())).SqlState);
        }
        await using (var badDigest = database.DataSource.CreateCommand("SELECT alerting.backup_alert_signals(@target,1,@at,@payload,@digest);"))
        {
            badDigest.Parameters.AddWithValue("target", target.Value); badDigest.Parameters.AddWithValue("at", at);
            badDigest.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, original); badDigest.Parameters.AddWithValue("digest", new string('a',64));
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await badDigest.ExecuteScalarAsync())).SqlState);
        }
        await using var privilege = database.DataSource.CreateCommand("""
            SELECT p.proname,pg_get_userbyid(p.proowner),p.prosecdef,
              has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='alerting'
              AND p.proname IN ('backup_alert_signals','backup_alert_observation','assert_backup_alert_observation','read_backup_alert_capture');
            """);
        await using NpgsqlDataReader row = await privilege.ExecuteReaderAsync(); int found = 0;
        while (await row.ReadAsync())
        {
            found++; Assert.Equal("sqlobserver_migrator", row.GetString(1)); Assert.True(row.GetBoolean(2));
            Assert.Equal(row.GetString(0)=="read_backup_alert_capture",row.GetBoolean(3));
            Assert.False(row.GetBoolean(4)); Assert.False(row.GetBoolean(5)); Assert.False(row.GetBoolean(6));
        }
        Assert.Equal(4,found);
    }

    private static readonly AlertRuleKind[] BindingKinds = [AlertRuleKind.BackupFullPolicy, AlertRuleKind.BackupLogPolicy, AlertRuleKind.BackupObservedFaults];
    private static readonly JsonSerializerOptions BindingSignalOptions = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
    private static AlertRuleDefinition BindingRule(AlertRuleKind kind) => new(Guid.NewGuid(), "backup.binding",kind,null,
        AlertComparison.GreaterThan,0,0,2,TimeSpan.FromMinutes(5),TimeSpan.FromSeconds(15),clearConfirmationCount:2);
    private static JsonObject BindingObservation(AlertRuleDefinition rule,BackupAlertEvidence capture)
    {
        AlertObservation observation = BackupAlertObservationFactory.Create(rule,capture);
        return BackupDecisionWire(observation,new(new(rule.RuleId,capture.TargetId),null,false,"test"))["Observation"]!.AsObject();
    }
    private static async Task<string> BindingPayloadAsync(RepositoryTestDatabase database,BackupFleetSummary summary)
    {
        BackupFleetSummaryContract.Validate(summary);
        // Exercise the real cache serializer, including its target-id converter
        // and closed envelope, before PostgreSQL canonicalizes the stored JSONB.
        Type codec = typeof(PostgreSqlBackupFleetCachePort).Assembly.GetType("SqlObserver.Infrastructure.PostgreSql.PostgreSqlBackupFleetCacheCodec")!;
        string json = (string)codec.GetMethod("Encode",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,[summary])!;
        await using var command = database.DataSource.CreateCommand("SELECT @payload::jsonb::text;");
        command.Parameters.AddWithValue("payload",NpgsqlDbType.Jsonb,json);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
    private static BackupFleetCheckCounts BindingCounts(int within=0,int violated=0,int unknown=0,int notRequired=0,int disabled=0) =>
        new(within,violated,unknown,notRequired,disabled,unknown==0 ? [] : [new(BackupFreshnessReason.TimestampQualificationUnavailable,unknown)]);
    private static BackupFleetSummary BindingSummary(MonitoredInstanceId target,DateTimeOffset at,BackupFleetCheckCounts full,BackupFleetCheckCounts log,
        BackupFreshnessCoverage coverage=BackupFreshnessCoverage.Complete,bool enabled=true,int faults=0,int databases=2)
    {
        var context = new BackupEvidenceHeader(target,at,1,Guid.NewGuid(),at,at,at,"UTC",at.DateTime.AddDays(-35),
            coverage,BackupEvidenceStatus.Retained,databases*2,databases*2,databases*2,databases,enabled,2,1);
        BackupFleetStatus status = full.PolicyViolated+log.PolicyViolated>0 ? BackupFleetStatus.PolicyViolated
            : !enabled || coverage!=BackupFreshnessCoverage.Complete || full.Indeterminate+log.Indeterminate>0 ? BackupFleetStatus.Indeterminate
            : full.Disabled+log.Disabled==2*databases ? BackupFleetStatus.Disabled
            : full.WithinThreshold+log.WithinThreshold>0 ? BackupFleetStatus.WithinPolicy : BackupFleetStatus.Indeterminate;
        return new(context,at.AddMinutes(2),status,full,log,faults);
    }
    private static async Task AssertBindingAsync(RepositoryTestDatabase database,Guid target,AlertRuleDefinition rule,DateTimeOffset at,
        string? payload,JsonObject observation,WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await ExecuteBindingAsync(connection,transaction,target,rule,at,payload,observation,lease);
        await transaction.CommitAsync();
    }
    private static async Task ExecuteBindingAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,
        Guid target,AlertRuleDefinition rule,DateTimeOffset at,string? payload,JsonObject observation,WorkerLeaseIdentity lease)
    {
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);",connection,transaction))
        { scope.Parameters.AddWithValue("target",target.ToString("D")); await scope.ExecuteNonQueryAsync(); }
        await using var command = new NpgsqlCommand("""
            SELECT alerting.assert_backup_alert_observation(@target,1,@rule,@kind,@at,@payload,@observation,@owner,@fence);
            """,connection,transaction);
        command.Parameters.AddWithValue("target",target); command.Parameters.AddWithValue("rule",rule.RuleId);
        command.Parameters.AddWithValue("kind",(short)rule.Kind); command.Parameters.AddWithValue("at",at);
        command.Parameters.AddWithValue("payload",NpgsqlDbType.Jsonb,(object?)payload ?? DBNull.Value);
        command.Parameters.AddWithValue("observation",NpgsqlDbType.Jsonb,observation.ToJsonString());
        command.Parameters.AddWithValue("owner",lease.Owner.Value); command.Parameters.AddWithValue("fence",lease.FencingToken.Value);
        await command.ExecuteNonQueryAsync();
    }
}
