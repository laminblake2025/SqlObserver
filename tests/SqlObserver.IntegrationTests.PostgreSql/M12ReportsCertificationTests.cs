using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Reporting;
using SqlObserver.Server;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Release-only product-path checks for the reports lane.</summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
public sealed class M12ReportsCertificationTests(PostgreSql18Fixture fixture)
{
    private static readonly string[] ReportKinds = ["instance-health", "performance-window", "incident-evidence", "capacity-readiness"];
    private static readonly string[] ReportSections = ["health", "performance", "incidents", "capacity"];

    [Fact]
    [Trait("Category", "RequiresM12ReportsRelease")]
    public async Task LiveReleaseReportsExportsRepresentativeVolumeIsBounded()
    {
        (RepositoryTestDatabase database, Guid target) = await PrepareAsync();
        await using (database)
        {
            int observedPostgreSqlVersion = await PostgreSqlMajorVersionAsync(database);
            Assert.Equal(18, observedPostgreSqlVersion);
            await using NpgsqlDataSource server = database.CreateServerDataSource();
            var service = new ReportService(new PostgreSqlReportRepository(server), new PostgreSqlReportAuditPort(server));
            AuthorizationContext authorization = Allowed(target, ApplicationRole.Viewer);
            UpstreamSeed seed = await SeedUpstreamAsync(database, target);

            ReportCatalogResult catalog = await service.CatalogAsync(CancellationToken.None);
            Assert.Equal(ReportContract.Version, catalog.Version);
            Assert.Equal(ReportKinds, catalog.Reports.Select(static report => report.ReportKind));
            Assert.Equal(ReportSections, catalog.Reports.SelectMany(static report => report.Sections).Select(static section => section.Key));
            Assert.Equal(
                ["observedAtUtc,metric,value,state", "observedAtUtc,metric,value,coverage", "occurredAtUtc,severity,eventKind,visibility", "observedAtUtc,metric,value,state"],
                catalog.Reports.Select(static report => string.Join(',', report.Sections.Single().Columns)));

            var runs = new List<ReportRun>();
            foreach ((string kind, int index) in ReportKinds.Select((kind, index) => (kind, index)))
            {
                DateTimeOffset from = seed.BaseUtc.AddMinutes(-1);
                DateTimeOffset to = seed.BaseUtc.AddMinutes(15);
                ReportRequest request = kind == "instance-health" ? new(kind, Guid.NewGuid()) : new(kind, Guid.NewGuid(), from, to);
                runs.Add(await service.CreateAsync(new ReportCreateRequest(target, request, authorization), CancellationToken.None));
            }
            Assert.Collection(runs, _ => { }, _ => { }, _ => { }, _ => { });
            Assert.All(runs, run =>
            {
                Assert.Equal(target, run.TargetId);
                Assert.Equal(ReportContract.Version, run.DefinitionVersion);
                Assert.Equal("finalized", run.State);
                Assert.Equal(TimeSpan.FromHours(24), run.ExpiresAtUtc - run.SnapshotUtc);
            });

            ReportRun health = runs[0];
            ReportSectionPage firstPage = await service.ReadPageAsync(target, health.RunId, "health", 0, ReportContract.PageRows, authorization, CancellationToken.None);
            ReportSectionPage secondPage = await service.ReadPageAsync(target, health.RunId, "health", firstPage.Rows[^1].Ordinal, ReportContract.PageRows, authorization, CancellationToken.None);
            ReportSectionPage finalPage = await service.ReadPageAsync(target, health.RunId, "health", secondPage.Rows[^1].Ordinal, ReportContract.PageRows, authorization, CancellationToken.None);
            Assert.Equal(200, firstPage.Rows.Count);
            Assert.True(firstPage.HasMore);
            Assert.Equal(200, secondPage.Rows.Count);
            Assert.True(secondPage.HasMore);
            Assert.Single(finalPage.Rows);
            Assert.False(finalPage.HasMore);
            IReadOnlyList<ReportRow> readAll = await service.ReadAllAsync(target, health.RunId, "health", 401, ReportContract.MaterializationBytes, authorization, CancellationToken.None);
            Assert.Equal(401, readAll.Count);
            Assert.Equal("engine.metric.001", readAll[0].Values["metric"]);
            Assert.Equal("401", readAll[^1].Values["value"]);

            ReportRun performance = runs[1];
            ReportSectionPage performancePage = await service.ReadPageAsync(target, performance.RunId, "performance", 0, ReportContract.PageRows, authorization, CancellationToken.None);
            Assert.Equal(ReportContract.PageRows, performancePage.Rows.Count);
            Assert.True(performancePage.HasMore);
            IReadOnlyList<ReportRow> productExportRows = await service.ReadAllAsync(target, performance.RunId, "performance", ReportContract.TotalRows, ReportContract.MaterializationBytes, authorization, CancellationToken.None);
            Assert.Equal(ReportContract.TotalRows, productExportRows.Count);
            Assert.Equal("host.cpu.percent", productExportRows[0].Values["metric"]);
            Assert.Equal("1", productExportRows[0].Values["value"]);
            byte[] productCsv = ReportRenderer.RenderCsv(ReportCatalog.Get(ReportKind.PerformanceWindow), ReportCatalog.Get(ReportKind.PerformanceWindow).Sections[0], productExportRows);
            Assert.Equal(ReportContract.TotalRows + 1, ParseCsv(Encoding.UTF8.GetString(productCsv)).Count);
            Assert.InRange(productCsv.Length, 1, ReportContract.MaterializationBytes);
            for (int index = 2; index < runs.Count; index++)
            {
                string section = ReportSections[index];
                ReportSectionPage sectionPage = await service.ReadPageAsync(target, runs[index].RunId, section, 0, ReportContract.PageRows, authorization, CancellationToken.None);
                Assert.Equal(3, sectionPage.Rows.Count);
                Assert.False(sectionPage.HasMore);
                IReadOnlyList<ReportRow> sectionRows = await service.ReadAllAsync(target, runs[index].RunId, section, 201, ReportContract.MaterializationBytes, authorization, CancellationToken.None);
                Assert.Equal(3, sectionRows.Count);
                if (section == "incidents")
                {
                    Assert.Equal("m12.incident.1", sectionRows[0].Values["eventKind"]);
                    Assert.Equal("1", sectionRows[0].Values["severity"]);
                }
                else
                {
                    Assert.Equal(section == "capacity" ? "host.volume.free_bytes" : "host.cpu.percent", sectionRows[0].Values["metric"]);
                    Assert.Equal("1", sectionRows[0].Values["value"]);
                }
                ReportDefinition sectionDefinition = catalog.Reports[index];
                byte[] sectionHtml = ReportRenderer.RenderHtml(runs[index], sectionDefinition, new Dictionary<string, IReadOnlyList<ReportRow>> { [section] = sectionRows });
                byte[] sectionCsv = ReportRenderer.RenderCsv(sectionDefinition, sectionDefinition.Sections.Single(), sectionRows);
                Assert.InRange(sectionHtml.Length, 1, ReportContract.ResponseBytes);
                Assert.Equal(4, ParseCsv(Encoding.UTF8.GetString(sectionCsv)).Count);
                Assert.InRange(sectionCsv.Length, 1, ReportContract.MaterializationBytes);
            }
            bool page200 = firstPage.Rows.Count == ReportContract.PageRows && secondPage.Rows.Count == ReportContract.PageRows && finalPage.Rows.Count == 1 && firstPage.HasMore && secondPage.HasMore && !finalPage.HasMore;
            bool export10000 = productExportRows.Count == ReportContract.TotalRows && ParseCsv(Encoding.UTF8.GetString(productCsv)).Count == ReportContract.TotalRows + 1;

            await AppendUpstreamAfterSnapshotAsync(database, seed);
            Assert.Equal(401, (await service.ReadAllAsync(target, health.RunId, "health", 401, ReportContract.MaterializationBytes, authorization, CancellationToken.None)).Count);
            Assert.Equal(ReportContract.TotalRows, (await service.ReadAllAsync(target, performance.RunId, "performance", ReportContract.TotalRows, ReportContract.MaterializationBytes, authorization, CancellationToken.None)).Count);
            Assert.Equal(3, (await service.ReadAllAsync(target, runs[2].RunId, "incidents", 201, ReportContract.MaterializationBytes, authorization, CancellationToken.None)).Count);
            Assert.Equal(3, (await service.ReadAllAsync(target, runs[3].RunId, "capacity", 201, ReportContract.MaterializationBytes, authorization, CancellationToken.None)).Count);

            ReportDefinition definition = ReportCatalog.Get(ReportKind.InstanceHealth);
            ReportRow[] rendererRows = Enumerable.Range(1, 2_500).Select(index => new ReportRow(index, new Dictionary<string, string?>
            {
                ["observedAtUtc"] = "2026-01-01T00:00:00Z",
                ["metric"] = "bounded",
                ["value"] = index.ToString(CultureInfo.InvariantCulture),
                ["state"] = "ok"
            })).ToArray();
            byte[] html = ReportRenderer.RenderHtml(health, definition, new Dictionary<string, IReadOnlyList<ReportRow>> { ["health"] = rendererRows });
            string htmlText = Encoding.UTF8.GetString(html);
            Assert.Equal(1, CountOccurrences(htmlText, "<tbody>"));
            Assert.Equal(2_001, CountOccurrences(htmlText, "<tr>"));
            Assert.InRange(html.Length, 1, ReportContract.ResponseBytes);
            await Assert.ThrowsAsync<ReportLimitException>(() => service.ReadAllAsync(target, health.RunId, "health", 10_001, ReportContract.MaterializationBytes, authorization, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<ReportLimitException>(() => service.ReadAllAsync(target, health.RunId, "health", 1, 0, authorization, CancellationToken.None).AsTask());
            ReportRow[] oversizedRows = Enumerable.Range(1, 10_000).Select(index => new ReportRow(index, new Dictionary<string, string?> { ["metric"] = new string('x', 1_024) })).ToArray();
            Assert.Throws<ReportLimitException>(() => ReportRenderer.RenderCsv(definition, definition.Sections[0], oversizedRows));
            bool html2000 = CountOccurrences(htmlText, "<tr>") == ReportContract.HtmlRows + 1 && html.Length <= ReportContract.ResponseBytes;
            bool reports = runs.Count == 4 && catalog.Version == ReportContract.Version;
            bool exports = export10000 && productCsv.Length <= ReportContract.MaterializationBytes;

            await AssertReportExpiryFenceAsync(database);

            await WriteResultAsync("m12-reports-exports", nameof(LiveReleaseReportsExportsRepresentativeVolumeIsBounded), new { reports, exports, reportKind = (string?)null, definitions = runs.Count, sections = ReportSections, page200, html2000, export10000, responseBytes = ReportContract.ResponseBytes, materializationBytes = ReportContract.MaterializationBytes, observedPostgreSqlVersion });
        }
    }

    [Fact]
    [Trait("Category", "RequiresM12ReportsRelease")]
    public async Task LiveReleaseReportContractIsSnapshotScopedAndAudited()
    {
        (RepositoryTestDatabase database, Guid target) = await PrepareAsync();
        await using (database)
        {
            int observedPostgreSqlVersion = await PostgreSqlMajorVersionAsync(database);
            Assert.Equal(18, observedPostgreSqlVersion);
            await using NpgsqlDataSource server = database.CreateServerDataSource();
            var service = new ReportService(new PostgreSqlReportRepository(server), new PostgreSqlReportAuditPort(server));
            AuthorizationContext viewer = Allowed(target, ApplicationRole.Viewer);
            await M12ReportServiceBoundTests.AssertRequestBodyBoundaryAsync();
            await M12ReportServiceBoundTests.AssertActorCapacityAsync();
            await M12ReportServiceBoundTests.AssertGlobalCapacityAsync();
            await M12ReportServiceBoundTests.AssertCursorExpiryAsync();
            await M12ReportServiceBoundTests.AssertRepositoryTimeoutAsync();
            await M12ReportServiceBoundTests.AssertOperationDeadlineAsync();
            await AssertReportWindowBoundariesAsync(service, target, viewer);
            Guid operation = Guid.NewGuid();
            DateTimeOffset requestFrom = DateTimeOffset.UtcNow.AddHours(-1);
            DateTimeOffset requestTo = requestFrom.AddMinutes(30);
            ReportRequest request = new("performance-window", operation, requestFrom, requestTo);
            ReportRun first = await service.CreateAsync(new ReportCreateRequest(target, request, viewer), CancellationToken.None);
            ReportRun replay = await service.CreateAsync(new ReportCreateRequest(target, request, viewer), CancellationToken.None);
            Assert.Equal(first, replay);
            Assert.Equal(Convert.ToHexString(ReportRequestValidation.CanonicalDigest(request)).ToLowerInvariant(), first.ParameterDigest);
            Assert.Equal(TimeSpan.FromHours(24), first.ExpiresAtUtc - first.SnapshotUtc);
            Assert.True(first.TargetRevision > 0);
            Assert.Equal(ReportContract.Version, first.DefinitionVersion);
            ReportRequest conflict = request with { ToUtc = requestTo.AddMinutes(1) };
            await Assert.ThrowsAsync<PostgresException>(() => service.CreateAsync(new ReportCreateRequest(target, conflict, viewer), CancellationToken.None).AsTask());

            foreach (ApplicationRole role in new[] { ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator })
                Assert.Equal(first, await service.GetRunAsync(target, first.RunId, Allowed(target, role), CancellationToken.None));
            bool roles = true;
            Guid unrelatedTarget = await TargetAsync(database);
            foreach (ApplicationRole role in new[] { ApplicationRole.SecurityAdministrator, ApplicationRole.Auditor, ApplicationRole.CollectorService })
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetRunAsync(target, first.RunId, Allowed(target, role), CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetRunAsync(unrelatedTarget, first.RunId, Allowed(unrelatedTarget, ApplicationRole.Viewer), CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReadPageAsync(unrelatedTarget, first.RunId, "health", 0, 1, Allowed(unrelatedTarget, ApplicationRole.Viewer), CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetRunAsync(target, first.RunId, Allowed(unrelatedTarget, ApplicationRole.Viewer), CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadPageAsync(target, first.RunId, "health", 0, 1, Allowed(unrelatedTarget, ApplicationRole.Viewer), CancellationToken.None).AsTask());
            bool targetIsolation = true;
            await Assert.ThrowsAsync<ReportLimitException>(() => service.ReadPageAsync(target, first.RunId, "health", 0, 201, viewer, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new ReportCreateRequest(target, new ReportRequest("performance-window", Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), viewer), CancellationToken.None).AsTask());

            IReadOnlyDictionary<(string Kind, string Outcome), long> audit = await ReadAuditCountsAsync(database, target);
            Assert.True(audit.GetValueOrDefault(("create", "succeeded")) >= 2);
            Assert.True(audit.GetValueOrDefault(("read", "succeeded")) >= 3);
            Assert.True(audit.GetValueOrDefault(("deny", "denied")) >= 4);
            Assert.True(audit.GetValueOrDefault(("oversize", "failed")) >= 1);
            Assert.True(audit.GetValueOrDefault(("failure", "failed")) >= 1);
            IReadOnlyDictionary<(string Kind, string Outcome), long> unrelatedAudit = await ReadAuditCountsAsync(database, unrelatedTarget);
            Assert.True(unrelatedAudit.GetValueOrDefault(("read", "failed")) >= 1);
            Assert.True(unrelatedAudit.GetValueOrDefault(("failure", "failed")) >= 1);
            Assert.True(audit.GetValueOrDefault(("deny", "denied")) >= 5);
            bool audited = true;
            await AssertReportExpiryFenceAsync(database);
            await WriteResultAsync("m12-report-contract", nameof(LiveReleaseReportContractIsSnapshotScopedAndAudited), new { reports = true, exports = false, reportKind = "report", snapshotScoped = first.ExpiresAtUtc - first.SnapshotUtc == TimeSpan.FromHours(24) && first.TargetRevision > 0 && first.DefinitionVersion == ReportContract.Version, audited, idempotentReplay = first == replay, targetIsolation, roles, testCount = 1, observedPostgreSqlVersion });
        }
    }

    [Fact]
    [Trait("Category", "RequiresM12ReportsRelease")]
    public async Task LiveReleaseExportContractIsInertAndFormulaSafe()
    {
        (RepositoryTestDatabase database, Guid target) = await PrepareAsync();
        await using (database)
        {
            int observedPostgreSqlVersion = await PostgreSqlMajorVersionAsync(database);
            Assert.Equal(18, observedPostgreSqlVersion);
            await using NpgsqlDataSource server = database.CreateServerDataSource();
            var service = new ReportService(new PostgreSqlReportRepository(server), new PostgreSqlReportAuditPort(server));
            AuthorizationContext authorization = Allowed(target, ApplicationRole.Operator);
            UpstreamSeed seed = await SeedUpstreamAsync(database, target, healthCount: 3, metricCount: 3, incidentCount: 3, capacityCount: 3);
            ReportRun exportRun = await service.CreateAsync(new ReportCreateRequest(target, new ReportRequest("instance-health", Guid.NewGuid()), authorization), CancellationToken.None);
            IReadOnlyList<ReportRow> persistedRows = await service.ReadAllAsync(target, exportRun.RunId, "health", 200, ReportContract.MaterializationBytes, authorization, CancellationToken.None);
            Assert.Equal(3, persistedRows.Count);
            await Assert.ThrowsAsync<ReportLimitException>(() => service.ReadAllAsync(target, exportRun.RunId, "health", ReportContract.TotalRows + 1, ReportContract.MaterializationBytes, authorization, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<ReportLimitException>(() => service.ReadAllAsync(target, exportRun.RunId, "health", 1, 0, authorization, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteExportAsync(target, exportRun.RunId, "health", "csv", ReportContract.TotalRows + 1, authorization, CancellationToken.None).AsTask());
            await service.CompleteExportAsync(target, exportRun.RunId, "health", "csv", persistedRows.Count, authorization, CancellationToken.None);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteExportAsync(target, exportRun.RunId, "health", "xml", persistedRows.Count, authorization, CancellationToken.None).AsTask());

            ReportDefinition definition = ReportCatalog.Get(ReportKind.InstanceHealth);
            string[] values = ["<script>alert(1)</script>", "quote\"value", "comma,value", "line\r\nvalue", "\u0001control", "=SUM(A1:A2)", "+cmd", "-1", "@HYPERLINK", "  =leading", "\t+leading", "\u00a0@leading", "café"];
            ReportRow[] rows = values.Select((value, index) => new ReportRow(index + 1, new Dictionary<string, string?> { ["observedAtUtc"] = "2026-01-01T00:00:00Z", ["metric"] = value, ["value"] = value, ["state"] = value })).ToArray();
            string html = Encoding.UTF8.GetString(ReportRenderer.RenderHtml(exportRun, definition, new Dictionary<string, IReadOnlyList<ReportRow>> { ["health"] = rows }));
            Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
            byte[] csvBytes = ReportRenderer.RenderCsv(definition, definition.Sections[0], rows);
            Assert.False(csvBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            string csv = Encoding.UTF8.GetString(csvBytes);
            List<string[]> parsed = ParseCsv(csv);
            Assert.Equal(values.Length + 1, parsed.Count);
            Assert.Equal("comma,value", parsed[3][1]);
            Assert.Equal("'=SUM(A1:A2)", parsed[6][1]);
            Assert.Equal("'+cmd", parsed[7][1]);
            Assert.Equal("'-1", parsed[8][1]);
            Assert.Equal("'@HYPERLINK", parsed[9][1]);
            Assert.Equal("  '=leading", parsed[10][1]);
            Assert.Equal("\t'+leading", parsed[11][1]);
            Assert.Equal("\u00a0'@leading", parsed[12][1]);
            Assert.Equal("line  value", parsed[4][1]);
            Assert.Equal("control", parsed[5][1]);
            Assert.Equal("café", parsed[13][1]);
            Assert.All(parsed, row => Assert.Equal(4, row.Length));
            Assert.DoesNotContain("\n", csv.Replace("\r\n", string.Empty, StringComparison.Ordinal));
            Assert.Contains("quote\"\"value", csv, StringComparison.Ordinal);
            IReadOnlyDictionary<(string Kind, string Outcome), long> audit = await ReadAuditCountsAsync(database, target);
            Assert.True(audit.GetValueOrDefault(("export", "succeeded")) >= 1);
            Assert.True(audit.GetValueOrDefault(("failure", "failed")) >= 1);
            await WriteResultAsync("m12-export-contract", nameof(LiveReleaseExportContractIsInertAndFormulaSafe), new { reports = false, exports = true, reportKind = "export", inertHtml = html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", StringComparison.Ordinal) && !html.Contains("<script>", StringComparison.OrdinalIgnoreCase), rfc4180 = parsed.All(static row => row.Length == 4) && csv.Contains("\r\n", StringComparison.Ordinal), formulaNeutralization = parsed[6][1] == "'=SUM(A1:A2)" && parsed[7][1] == "'+cmd" && parsed[8][1] == "'-1" && parsed[9][1] == "'@HYPERLINK", formulaSentinels = 4, testCount = 1, observedPostgreSqlVersion });
        }
    }

    private async Task<(RepositoryTestDatabase Database, Guid Target)> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult migration = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
            Assert.False(migration.HasFailures);
            return (database, await TargetAsync(database));
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<Guid> TargetAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand command = new("INSERT INTO control.observation_target(instance_id,instance_key,display_name) VALUES(@id,@key,@name);", connection);
        command.Parameters.AddWithValue("id", target);
        command.Parameters.AddWithValue("key", $"m12-report-{target:N}");
        command.Parameters.AddWithValue("name", "M12 reports certification target");
        await command.ExecuteNonQueryAsync();
        return target;
    }

    private sealed record UpstreamSeed(
        DateTimeOffset BaseUtc,
        DateTimeOffset MutationUtc,
        Guid HostId,
        Guid HealthRunId,
        Guid MetricRunId,
        Guid CapacityRunId,
        Guid TargetId);

    private static async Task<UpstreamSeed> SeedUpstreamAsync(
        RepositoryTestDatabase database,
        Guid target,
        int healthCount = 401,
        int metricCount = ReportContract.TotalRows,
        int incidentCount = 3,
        int capacityCount = 3)
    {
        DateTimeOffset baseUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset mutationUtc = baseUtc.AddMinutes(10);
        Guid hostId = Guid.NewGuid();
        Guid healthRunId = Guid.NewGuid();
        Guid metricRunId = Guid.NewGuid();
        Guid capacityRunId = Guid.NewGuid();

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var leases = new PostgreSqlWorkerLeasePort(collector);
        WorkerLease lease = Assert.IsType<WorkerLease>((await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(new WorkerLeaseKey("m12/reports-seed"), new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(2)), new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Lease);
        try
        {
            var partitions = new PostgreSqlPartitionMaintenancePort(collector);
            foreach ((PartitionSetName set, PartitionGranularity granularity, int ahead) in new[]
            {
                (new PartitionSetName("raw_metric_sample"), PartitionGranularity.Daily, 1),
                (new PartitionSetName("diagnostic_event"), PartitionGranularity.Monthly, 1),
            })
            {
                await partitions.EnsurePartitionsAsync(new PartitionCareRequest(set, granularity, baseUtc, ahead, lease.Identity, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
            }
        }
        finally
        {
            await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease.Identity, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        }

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (NpgsqlCommand targetCommand = new("""
            UPDATE control.observation_target
            SET lifecycle_state='active',host_name='m12-report.test',instance_name='M12REPORT',tcp_port=NULL,
                certificate_host_name='m12-report.test',connect_timeout=interval '5 seconds',
                authentication_mode='windows_integrated_service_identity',transport_security_mode='mandatory_validated'
            WHERE instance_id=@target;
            """, connection))
        {
            targetCommand.Parameters.AddWithValue("target", target);
            await targetCommand.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand command = new("""
            INSERT INTO control.host_binding(instance_id,host_id,target_revision,binding_revision,host_name,identity_fingerprint,binding_state,first_seen_at,last_seen_at)
            SELECT @target,@host,revision,1,'m12-report.test',decode(repeat('11',32),'hex'),'active',@base,@base FROM control.observation_target WHERE instance_id=@target
            ON CONFLICT DO NOTHING;
            INSERT INTO control.host_profile(instance_id,target_revision,host_id,binding_revision,profile_revision,os_family,os_version,cpu_count,memory_bytes,capability_state,profile,observed_at)
            SELECT @target,revision,@host,1,1,'linux','m12',8,17179869184,'available',jsonb_build_object('osFamily','linux','osVersion','m12','cpuCount',8,'memoryBytes',17179869184,'capabilityState','available'),@base FROM control.observation_target WHERE instance_id=@target
            ON CONFLICT DO NOTHING;
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT @health,@target,'engine.core',1,1,target.revision,schedule.schedule_revision,'m12/reports-seed',@owner,1,decode(repeat('22',32),'hex'),@base,@base FROM control.observation_target target JOIN control.collector_schedule schedule ON schedule.instance_id=target.instance_id AND schedule.collector_id='engine.core' WHERE target.instance_id=@target;
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES(@health,'succeeded','completed',1,0,10,@health_count,@health_count,@health_count,0,0,100,@health_count,@health_count,false,false,'none',true,0,0,decode(repeat('33',32),'hex'),@base);
            INSERT INTO telemetry.raw_metric_sample(observed_at,sample_id,instance_id,metric_key,metric_value,dimensions,collected_at,collection_run_id)
            SELECT @base,gen_random_uuid(),@target,'engine.metric.'||lpad(series::text,3,'0'),series::double precision,'{}'::jsonb,@base,@health
            FROM generate_series(1,@health_count) AS value(series);
            INSERT INTO telemetry.host_metric_snapshot_v2(observed_at,run_id,instance_id,target_revision,host_id,binding_revision,profile_revision,metric_key,metric_value,dimensions,collected_at)
            SELECT @base + series * interval '1 millisecond',@metric,@target,target.revision,@host,1,1,'host.cpu.percent',series::double precision,'{}'::jsonb,@base
            FROM generate_series(1,@metric_count) AS value(series) CROSS JOIN control.observation_target target WHERE target.instance_id=@target;
            INSERT INTO telemetry.host_metric_snapshot_v2(observed_at,run_id,instance_id,target_revision,host_id,binding_revision,profile_revision,metric_key,metric_value,dimensions,collected_at)
            SELECT @base + series * interval '1 second',@capacity,@target,target.revision,@host,1,1,'host.volume.free_bytes',series::double precision,jsonb_build_object('volume','m12'),@base
            FROM generate_series(1,@capacity_count) AS value(series) CROSS JOIN control.observation_target target WHERE target.instance_id=@target;
            INSERT INTO events.diagnostic_event(occurred_at,event_id,instance_id,event_kind,severity,safe_metadata,collected_at)
            SELECT @base + series * interval '1 second',gen_random_uuid(),@target,'m12.incident.'||series::text,series,'{}'::jsonb,@base
            FROM generate_series(1,@incident_count) AS value(series);
            """, connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("host", hostId);
        command.Parameters.AddWithValue("health", healthRunId);
        command.Parameters.AddWithValue("metric", metricRunId);
        command.Parameters.AddWithValue("capacity", capacityRunId);
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("base", baseUtc);
        command.Parameters.AddWithValue("health_count", healthCount);
        command.Parameters.AddWithValue("metric_count", metricCount);
        command.Parameters.AddWithValue("capacity_count", capacityCount);
        command.Parameters.AddWithValue("incident_count", incidentCount);
        await command.ExecuteNonQueryAsync();
        return new UpstreamSeed(baseUtc, mutationUtc, hostId, healthRunId, metricRunId, capacityRunId, target);
    }

    private static async Task AppendUpstreamAfterSnapshotAsync(RepositoryTestDatabase database, UpstreamSeed seed)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand command = new("""
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT @health,@target,'engine.core',1,1,target.revision,schedule.schedule_revision,'m12/reports-seed/mutation',@owner,1,decode(repeat('44',32),'hex'),@at,@at
            FROM control.observation_target target JOIN control.collector_schedule schedule ON schedule.instance_id=target.instance_id AND schedule.collector_id='engine.core'
            WHERE target.instance_id=@target;
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES(@health,'succeeded','completed',1,0,10,1,1,1,0,0,10,1,1,false,false,'none',true,0,0,decode(repeat('55',32),'hex'),@at);
            INSERT INTO telemetry.raw_metric_sample(observed_at,sample_id,instance_id,metric_key,metric_value,dimensions,collected_at,collection_run_id)
            VALUES(@at,gen_random_uuid(),@target,'engine.metric.mutated',999,'{}'::jsonb,@at,@health);
            INSERT INTO telemetry.host_metric_snapshot_v2(observed_at,run_id,instance_id,target_revision,host_id,binding_revision,profile_revision,metric_key,metric_value,dimensions,collected_at)
            SELECT @at,@run,@target,revision,@host,1,1,'host.cpu.percent',999999,'{}'::jsonb,@at FROM control.observation_target WHERE instance_id=@target;
            INSERT INTO telemetry.host_metric_snapshot_v2(observed_at,run_id,instance_id,target_revision,host_id,binding_revision,profile_revision,metric_key,metric_value,dimensions,collected_at)
            SELECT @at,@capacity,@target,revision,@host,1,1,'host.volume.free_bytes',999999,jsonb_build_object('volume','m12'),@at FROM control.observation_target WHERE instance_id=@target;
            INSERT INTO events.diagnostic_event(occurred_at,event_id,instance_id,event_kind,severity,safe_metadata,collected_at)
            VALUES(@at,gen_random_uuid(),@target,'m12.incident.mutated',99,'{}'::jsonb,@at);
            """, connection);
        command.Parameters.AddWithValue("at", seed.MutationUtc);
        command.Parameters.AddWithValue("target", seed.TargetId);
        command.Parameters.AddWithValue("host", seed.HostId);
        command.Parameters.AddWithValue("health", Guid.NewGuid());
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("run", Guid.NewGuid());
        command.Parameters.AddWithValue("capacity", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertReportWindowBoundariesAsync(ReportService service, Guid target, AuthorizationContext authorization)
    {
        DateTimeOffset to = DateTimeOffset.UtcNow;
        DateTimeOffset sevenDays = to.AddDays(-7);
        DateTimeOffset thirtyOneDays = to.AddDays(-31);
        foreach (string kind in new[] { "performance-window", "incident-evidence" })
        {
            ReportRun accepted = await service.CreateAsync(new ReportCreateRequest(target, new ReportRequest(kind, Guid.NewGuid(), sevenDays, to), authorization), CancellationToken.None);
            Assert.Equal(kind, accepted.Kind.ToWire());
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new ReportCreateRequest(target, new ReportRequest(kind, Guid.NewGuid(), sevenDays, to.AddMicroseconds(1)), authorization), CancellationToken.None).AsTask());
        }

        ReportRun capacity = await service.CreateAsync(new ReportCreateRequest(target, new ReportRequest("capacity-readiness", Guid.NewGuid(), thirtyOneDays, to), authorization), CancellationToken.None);
        Assert.Equal("capacity-readiness", capacity.Kind.ToWire());
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new ReportCreateRequest(target, new ReportRequest("capacity-readiness", Guid.NewGuid(), thirtyOneDays, to.AddMicroseconds(1)), authorization), CancellationToken.None).AsTask());
    }

    private static async Task AssertReportExpiryFenceAsync(RepositoryTestDatabase database)
    {
        Guid expiredRun = Guid.NewGuid();
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand command = new("""
            INSERT INTO reporting.report_run
            (run_id,target_id,target_revision,report_kind,definition_version,operation_id,actor_sid,parameter_digest,snapshot_utc,expires_at_utc,state)
            VALUES (@run,@target,1,'instance-health',1,@operation,'S-1-5-21-1',@digest,@snapshot,@expiry,'finalized');
            """, connection))
        {
            command.Parameters.AddWithValue("run", expiredRun);
            command.Parameters.AddWithValue("target", await ReadAnyTargetAsync(database));
            command.Parameters.AddWithValue("operation", Guid.NewGuid());
            command.Parameters.AddWithValue("digest", new byte[32]);
            DateTimeOffset snapshot = DateTimeOffset.UtcNow.AddHours(-25);
            command.Parameters.AddWithValue("snapshot", snapshot);
            command.Parameters.AddWithValue("expiry", snapshot.AddHours(24));
            await command.ExecuteNonQueryAsync();
        }

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var leases = new PostgreSqlWorkerLeasePort(collector);
        WorkerLeaseKey key = new("reports/expiry");
        WorkerExecutionId firstOwner = new(Guid.NewGuid());
        WorkerLeaseDuration duration = new(TimeSpan.FromSeconds(30));
        RepositoryCallTimeout timeout = new(TimeSpan.FromSeconds(5));
        WorkerLease? first = Assert.IsType<WorkerLease>((await leases.AcquireAsync(new AcquireWorkerLeaseRequest(key, firstOwner, duration, timeout), CancellationToken.None)).Lease);
        try
        {
            var repository = new PostgreSqlReportRepository(collector);
            Assert.Equal(1, await repository.ExpireAsync(100, first.Identity, CancellationToken.None));
            Assert.Equal(0, await repository.ExpireAsync(100, first.Identity, CancellationToken.None));
            Assert.Equal(LeaseReleaseStatus.Released, await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(first.Identity, timeout), CancellationToken.None));
            first = null;

            WorkerExecutionId secondOwner = new(Guid.NewGuid());
            WorkerLease second = Assert.IsType<WorkerLease>((await leases.AcquireAsync(new AcquireWorkerLeaseRequest(key, secondOwner, duration, timeout), CancellationToken.None)).Lease);
            try
            {
                await Assert.ThrowsAsync<PostgresException>(() => repository.ExpireAsync(100, new WorkerLeaseIdentity(key, firstOwner, second.Identity.FencingToken), CancellationToken.None).AsTask());
            }
            finally
            {
                await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(second.Identity, timeout), CancellationToken.None);
            }
        }
        finally
        {
            if (first is not null)
                await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(first.Identity, timeout), CancellationToken.None);
        }
    }

    private static async Task<Guid> ReadAnyTargetAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand command = new("SELECT instance_id FROM control.observation_target ORDER BY instance_id LIMIT 1;", connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("No report target was found."));
    }

    private static AuthorizationContext Allowed(Guid target, ApplicationRole role) => new(new ActorSecurityIdentifier("S-1-5-21-1"), AuthorizationPrincipalState.Active, [new RoleAuthorizationGrant(role, TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(target)]))]);

    private static async Task<int> PostgreSqlMajorVersionAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand command = new("SHOW server_version_num;", connection);
        string value = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("PostgreSQL version was not returned.");
        Assert.True(int.TryParse(value, out int version));
        return version / 10_000;
    }

    private static async Task<IReadOnlyDictionary<(string Kind, string Outcome), long>> ReadAuditCountsAsync(RepositoryTestDatabase database, Guid target)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand command = new("SELECT activity_kind,outcome,count(*) FROM audit.report_activity WHERE target_id=@target GROUP BY activity_kind,outcome;", connection);
        command.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<(string, string), long>();
        while (await reader.ReadAsync()) result[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
        return result;
    }

    private static int CountOccurrences(string value, string token)
    {
        int count = 0;
        for (int index = 0; (index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0; index += token.Length) count++;
        return count;
    }

    private static List<string[]> ParseCsv(string input)
    {
        if (input.Length == 0 || input[0] == '\ufeff') throw new FormatException("CSV must be non-empty UTF-8 without a BOM.");
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool fieldStart = true;
        bool closedQuote = false;
        for (int index = 0; index < input.Length; index++)
        {
            char current = input[index];
            if (quoted)
            {
                if (current == '"' && index + 1 < input.Length && input[index + 1] == '"') { field.Append('"'); index++; }
                else if (current == '"') { quoted = false; closedQuote = true; }
                else field.Append(current);
            }
            else if (fieldStart && current == '"') { quoted = true; fieldStart = false; }
            else if (closedQuote && current == ',') { row.Add(field.ToString()); field.Clear(); fieldStart = true; closedQuote = false; }
            else if (closedQuote && current == '\r' && index + 1 < input.Length && input[index + 1] == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add([.. row]); row.Clear(); fieldStart = true; closedQuote = false; index++; }
            else throw new FormatException("CSV is not strict RFC4180 quoted CRLF data.");
        }
        if (quoted || !fieldStart || closedQuote || field.Length != 0 || row.Count != 0) throw new FormatException("CSV has an incomplete final record.");
        if (rows.Count == 0) throw new FormatException("CSV has no records.");
        return rows;
    }

    private static async Task WriteResultAsync(string caseId, string testName, object facts)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE"), "Release", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(Environment.GetEnvironmentVariable("SQLOBSERVER_RELEASE_REPORTS_CASE"), caseId, StringComparison.Ordinal)) throw new InvalidOperationException("SQLOBSERVER_RELEASE_REPORTS_CASE is required for Release reports evidence.");
        string? path = Environment.GetEnvironmentVariable("SQLOBSERVER_RELEASE_REPORTS_RESULT_PATH");
        if (string.IsNullOrWhiteSpace(path) || Path.GetFullPath(path) != path) throw new InvalidOperationException("M12 reports result path is invalid.");
        var payload = new { schemaVersion = 1, caseId, status = "passed", testName, testCount = 1, facts };
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, payload);
        await stream.WriteAsync("\n"u8.ToArray());
        await stream.FlushAsync();
    }
}

#pragma warning disable xUnit1013
public sealed class M12ReportServiceBoundTests
{
    [Fact]
    public async Task RequestBodyMiddlewareAcceptsExactBoundaryAndRejectsOneByteOversize()
        => await AssertRequestBodyBoundaryAsync();

    internal static async Task AssertRequestBodyBoundaryAsync()
    {
        Guid target = Guid.NewGuid();
        var audit = new RecordingAudit();
        bool exactNextCalled = false;
        RequestBodyLimitMiddleware exactMiddleware = new(_ => { exactNextCalled = true; return Task.CompletedTask; });
        await exactMiddleware.InvokeAsync(CreateRequestContext(target, PaddedJson(RequestBodyLimitMiddleware.MaximumRequestBytes), audit));
        Assert.True(exactNextCalled);

        bool oversizedNextCalled = false;
        RequestBodyLimitMiddleware oversizedMiddleware = new(_ => { oversizedNextCalled = true; return Task.CompletedTask; });
        DefaultHttpContext oversizedContext = CreateRequestContext(target, PaddedJson(RequestBodyLimitMiddleware.MaximumRequestBytes + 1), audit);
        await oversizedMiddleware.InvokeAsync(oversizedContext);
        Assert.False(oversizedNextCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, oversizedContext.Response.StatusCode);
        Assert.Contains(audit.Events, static item => item.ActivityKind == "oversize" && item.Outcome == "failed");
    }

    [Fact]
    public async Task ReportServiceRejectsThirdConcurrentCreateForOneActor()
        => await AssertActorCapacityAsync();

    internal static async Task AssertActorCapacityAsync()
    {
        var repository = new BlockingReportRepository(2);
        var service = new ReportService(repository, new RecordingAudit());
        Guid target = Guid.NewGuid();
        Task<ReportRun> first = service.CreateAsync(CreateRequest(target, "S-1-5-21-101"), CancellationToken.None).AsTask();
        Task<ReportRun> second = service.CreateAsync(CreateRequest(target, "S-1-5-21-101"), CancellationToken.None).AsTask();
        await repository.WaitForCallsAsync();
        Task<ReportRun> third = service.CreateAsync(CreateRequest(target, "S-1-5-21-101"), CancellationToken.None).AsTask();
        try
        {
            Task completed = await Task.WhenAny(third, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(third, completed);
            await Assert.ThrowsAsync<ReportCapacityException>(() => third);
        }
        finally
        {
            repository.Release();
            await Task.WhenAll(first, second);
            try { await third; } catch (ReportCapacityException) { }
        }
    }

    [Fact]
    public async Task ReportServiceRejectsSeventeenthGlobalCreate()
        => await AssertGlobalCapacityAsync();

    internal static async Task AssertGlobalCapacityAsync()
    {
        var repository = new BlockingReportRepository(16);
        var service = new ReportService(repository, new RecordingAudit());
        Guid target = Guid.NewGuid();
        var active = new List<Task<ReportRun>>();
        for (int index = 0; index < ReportContract.MaximumGlobalConcurrency; index++)
            active.Add(service.CreateAsync(CreateRequest(target, $"S-1-5-21-{200 + index}"), CancellationToken.None).AsTask());
        await repository.WaitForCallsAsync();
        Task<ReportRun> seventeenth = service.CreateAsync(CreateRequest(target, "S-1-5-21-299"), CancellationToken.None).AsTask();
        try
        {
            Task completed = await Task.WhenAny(seventeenth, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(seventeenth, completed);
            await Assert.ThrowsAsync<ReportCapacityException>(() => seventeenth);
        }
        finally
        {
            repository.Release();
            await Task.WhenAll(active);
            try { await seventeenth; } catch (ReportCapacityException) { }
        }
    }

    [Fact]
    public async Task ReportCursorRejectsTokenAfterItsBoundedExpiry()
        => await AssertCursorExpiryAsync();

    internal static async Task AssertCursorExpiryAsync()
    {
        var protector = new ReportCursorProtector(new EphemeralDataProtectionProvider());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string token = protector.Protect(new ReportCursor(Guid.NewGuid(), Guid.NewGuid(), 1, ReportKind.InstanceHealth, ReportContract.Version, "health", 0, now, now.AddMilliseconds(250)));
        await Task.Delay(700);
        Assert.Throws<ReportCursorExpiredException>(() => protector.Unprotect(token));
    }

    [Fact]
    public async Task ReportServiceConvertsRepositoryCancellationToBoundedTimeout()
        => await AssertRepositoryTimeoutAsync();

    internal static async Task AssertRepositoryTimeoutAsync()
    {
        var audit = new RecordingAudit();
        var service = new ReportService(new TimeoutReportRepository(), audit);
        await Assert.ThrowsAsync<TimeoutException>(() => service.CreateAsync(CreateRequest(Guid.NewGuid(), "S-1-5-21-401"), CancellationToken.None).AsTask());
        Assert.Contains(audit.Events, static item => item.ActivityKind == "timeout" && item.Outcome == "failed");
    }

    [Fact]
    public async Task ReportEndpointStartsAndHonorsBoundedOperationDeadline()
        => await AssertOperationDeadlineAsync();

    internal static async Task AssertOperationDeadlineAsync()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ReportContract.TotalOperationTimeout);
        MethodInfo? deadline = typeof(ReportEndpoints).GetMethod("StartOperationDeadline", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(deadline);
        Assert.Equal(typeof(CancellationTokenSource), deadline!.ReturnType);
        using var source = Assert.IsType<CancellationTokenSource>(deadline.Invoke(null, [CancellationToken.None]));
        Stopwatch stopwatch = Stopwatch.StartNew();
        Task cancellation = Task.Delay(Timeout.InfiniteTimeSpan, source.Token);
        Task completed = await Task.WhenAny(cancellation, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(cancellation, completed);
        Assert.True(cancellation.IsCanceled);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(19));
    }

    private static DefaultHttpContext CreateRequestContext(Guid target, byte[] body, RecordingAudit audit)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = $"/api/v1/observation-targets/{target:D}/reports";
        context.Request.RouteValues["instanceId"] = target;
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().AddSingleton<IReportAuditPort>(audit).BuildServiceProvider();
        return context;
    }

    private static byte[] PaddedJson(long length)
    {
        byte[] prefix = Encoding.UTF8.GetBytes("{} ");
        Assert.InRange(length, 1, int.MaxValue);
        return prefix.Concat(Encoding.UTF8.GetBytes(new string(' ', checked((int)length) - prefix.Length))).ToArray();
    }

    private static ReportCreateRequest CreateRequest(Guid target, string actor) => new(target, new ReportRequest("instance-health", Guid.NewGuid()), new AuthorizationContext(new ActorSecurityIdentifier(actor), AuthorizationPrincipalState.Active, [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(target)]))]));

    private sealed class RecordingAudit : IReportAuditPort
    {
        public List<ReportAuditEvent> Events { get; } = [];
        public ValueTask AppendAsync(ReportAuditEvent audit, CancellationToken cancellationToken) { Events.Add(audit); return ValueTask.CompletedTask; }
    }

    private sealed class BlockingReportRepository(int expectedCalls) : IReportRepository
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public ValueTask<ReportRun> CreateAsync(Guid targetId, ReportRequest request, string actorSid, CancellationToken cancellationToken) => CreateCoreAsync(targetId, request);
        private async ValueTask<ReportRun> CreateCoreAsync(Guid targetId, ReportRequest request)
        {
            if (Interlocked.Increment(ref _calls) >= expectedCalls) _entered.TrySetResult(true);
            await _release.Task.ConfigureAwait(false);
            DateTimeOffset snapshot = DateTimeOffset.UtcNow;
            return new ReportRun(Guid.NewGuid(), targetId, 1, request.ParsedKind, ReportContract.Version, snapshot, snapshot.AddHours(24), "", "finalized");
        }
        public ValueTask<ReportRun?> GetRunAsync(Guid targetId, Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReportSectionPage> ReadPageAsync(Guid targetId, Guid runId, string section, long afterOrdinal, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> WaitForCallsAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        public void Release() => _release.TrySetResult(true);
    }

    private sealed class TimeoutReportRepository : IReportRepository
    {
        public ValueTask<ReportRun> CreateAsync(Guid targetId, ReportRequest request, string actorSid, CancellationToken cancellationToken) => ValueTask.FromException<ReportRun>(new OperationCanceledException());
        public ValueTask<ReportRun?> GetRunAsync(Guid targetId, Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ReportSectionPage> ReadPageAsync(Guid targetId, Guid runId, string section, long afterOrdinal, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
#pragma warning restore xUnit1013
