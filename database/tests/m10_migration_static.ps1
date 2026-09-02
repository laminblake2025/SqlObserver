<#$
Static M10 migration checks.  This test intentionally does not connect to a
database; it catches accidental history edits, missing safety fences, and
placeholder/digest regressions in CI environments without PostgreSQL.
#>
[CmdletBinding()]
param([string]$RepositoryRoot)
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RepositoryRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
}

$migration = Join-Path $RepositoryRoot 'database\migrations\0014_analytics_host_replication_retention.sql'
$checksums = Join-Path $RepositoryRoot 'database\migrations\checksums.sha256'
$errors = [System.Collections.Generic.List[string]]::new()
foreach ($requiredPath in @($migration,$checksums)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        $errors.Add("missing required file: $requiredPath")
    }
}
if ($errors.Count) { $errors | ForEach-Object { Write-Error $_ }; exit 1 }
$sql = Get-Content -Raw -LiteralPath $migration -ErrorAction Stop
function Require([string]$Pattern,[string]$Name) { if ($script:sql -notmatch $Pattern) { $script:errors.Add("missing: $Name") } }
Require 'sha256\(convert_to' 'core sha256(bytea) M9 repair'
Require "decode\('[0-9a-f]{64}','hex'\)" 'M10 manifest and bundle digests'
Require 'CREATE TABLE IF NOT EXISTS telemetry\.host_metric_snapshot_v2' 'host parent'
Require 'CREATE TABLE IF NOT EXISTS telemetry\.replication_snapshot_v2' 'replication parent'
Require 'ensure_m10_partition_set' 'fixed partition-set function'
Require 'generate_series\(-1,7\)' 'daily D-1..D+7 fence'
Require 'generate_series\(0,2\)' 'monthly current+2 fence'
Require 'p_max_rows NOT BETWEEN 1 AND 100000' 'backfill row fence'
Require 'p_max_bytes NOT BETWEEN 1 AND 8388608' 'backfill byte fence'
Require 'interval ''24 hours''' 'retention grace period'
Require 'ALTER TABLE .* FORCE ROW LEVEL SECURITY' 'FORCE RLS'
Require 'hostId' 'stable host identity payload'
Require 'bindingRevision' 'host binding revision payload'
Require 'profileRevision' 'host profile revision payload'
Require 'fk_m10_host_binding_target_revision' 'target/binding revision foreign key'
Require 'fk_m10_replication_target_revision' 'target/replication revision foreign key'
Require 'host_binding_history' 'immutable host binding history ledger'
Require 'capture_m10_host_binding_history' 'audited binding history trigger'
Require 'divergent host binding replay' 'host binding replay divergence fence'
Require 'divergent backfill replay' 'backfill replay divergence fence'
Require 'divergent recovery attestation replay' 'recovery replay divergence fence'
Require 'source:=CASE' 'explicit legacy source map'
Require 'ON CONFLICT DO NOTHING' 'idempotent backfill/ingestion writes'
$collectorContractSeed = [regex]::Match($sql, '(?is)INSERT INTO control\.collector_contract\s*\(.*?ON CONFLICT \(collector_id,collector_version\).*?(?=DO \$m10_asset_digest_gate\$)').Value
if ([string]::IsNullOrWhiteSpace($collectorContractSeed)) { $errors.Add('M10 collector contract seed block is missing') }
if ($collectorContractSeed -notmatch 'ON CONFLICT \(collector_id,collector_version\) DO NOTHING;') { $errors.Add('M10 collector contract seed must preserve an existing immutable contract') }
if ($collectorContractSeed -match 'ON CONFLICT \(collector_id,collector_version\) DO UPDATE') { $errors.Add('M10 collector contract seed must not invoke the append-only UPDATE trigger') }
Require 'm10_host_metrics.*false,NULL' 'host retention disabled/null seed'
Require 'm10_replication.*false,NULL' 'replication retention disabled/null seed'
Require 'SecurityAdministrator' 'database-side global retention authorization'
Require 'CREATE OR REPLACE FUNCTION control\.update_m10_host_binding' 'fixed host binding mutation contract'
Require 'CREATE OR REPLACE FUNCTION control\.enqueue_m10_backfill' 'fixed audited backfill mutation contract'
Require 'CREATE OR REPLACE FUNCTION reporting\.list_m10_analytics_jobs' 'fixed analytics jobs query contract'
Require 'CREATE OR REPLACE FUNCTION reporting\.search_m10_diagnostics' 'fixed diagnostics query contract'
Require 'CREATE OR REPLACE FUNCTION system\.record_m10_recovery_attestation\s*\(p_attestation_id uuid,p_attested_by text,p_backup_set_reference text,p_expires_at timestamptz,p_digest bytea,p_request_digest bytea,p_correlation_id uuid,p_change_reason text\)' 'fixed recovery attestation contract'
Require 'CREATE OR REPLACE FUNCTION reporting\.list_m10_host_status' 'fixed host status query contract'
Require 'CREATE OR REPLACE FUNCTION reporting\.list_m10_host_metrics_scoped' 'fixed host metrics query contract'
Require 'CREATE OR REPLACE FUNCTION reporting\.list_m10_replication_scoped' 'fixed replication query contract'
Require 'CREATE OR REPLACE FUNCTION reporting\.list_m10_incidents_scoped' 'fixed incidents query contract'
Require 'CREATE OR REPLACE FUNCTION reporting\.list_m10_evidence_packets' 'fixed evidence query contract'
Require 'GRANT EXECUTE ON FUNCTION control\.update_m10_host_binding.*TO sqlobserver_server' 'server-only mutation grant'
Require 'GRANT EXECUTE ON FUNCTION .*reporting\.list_m10_analytics_jobs.*TO sqlobserver_server' 'server-only analytics query grant'
Require 'metric_rollup_v2 ADD PRIMARY KEY \(bucket_start,instance_id,target_revision' 'rollup target-revision identity key'
Require 'metric_rollup_v2 ADD PRIMARY KEY \(bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,dimension_hash,generation\)' 'rollup interval/dimension/generation identity key'
Require 'm10_rollup_identity_collision' 'pre-repair rollup collision fail-safe'
Require 'analytics_job.*target_revision.*status' 'rollup job target revision insert'
Require 'get_m10_surface_generation' 'database-loaded surface generation'
Require 'assert_m10_surface_fence' 'surface cursor fence validator'
Require 'p_cursor_at.*p_cursor_host_id' 'host status composite cursor'
Require 'metric_baseline ADD PRIMARY KEY \(instance_id,target_revision' 'baseline target-revision identity key'
Require 'get_m10_retention_execution_revision' 'fixed retention execution revision resolver'
Require 'get_m10_retention_policy_revision' 'fixed retention policy revision resolver'
Require 'CREATE OR REPLACE FUNCTION control\.claim_m10_analytics_jobs' 'fenced backfill claim function'
Require 'CREATE OR REPLACE FUNCTION control\.claim_m10_derivation_jobs' 'fenced derivation claim function'
Require 'CREATE OR REPLACE FUNCTION control\.schedule_m10_derivation_jobs' 'bounded derivation scheduler'
Require 'CREATE OR REPLACE FUNCTION control\.m10_rollup_due_buckets' 'deterministic completed bucket helper'
Require "k\(job_kind,metric_key\)" 'derivation scheduler kind alias'
Require 'CREATE OR REPLACE FUNCTION control\.complete_m10_derivation_job' 'fenced derivation completion function'
Require 'CREATE OR REPLACE FUNCTION reporting\.read_m10_evidence_derivation_inputs' 'fixed evidence derivation input projection'
Require 'CREATE OR REPLACE FUNCTION reporting\.read_m10_incident_derivation_inputs' 'fixed incident derivation input projection'
Require 'CREATE OR REPLACE FUNCTION reporting\.get_m10_forecast_capacity' 'fixed forecast capacity projection'
Require "host\.volume\.free_bytes','host\.volume\.total_bytes" 'forecast capacity metric allowlist'
Require 'CREATE OR REPLACE FUNCTION reporting\.read_m10_backfill_page' 'bounded backfill page function'
Require 'JOIN telemetry\.collection_run cr ON cr\.run_id=s\.collection_run_id AND cr\.instance_id=s\.instance_id AND cr\.target_revision=p_target_revision' 'raw collection-run target revision fence'
Require 'Raw M2 rows without collection-run provenance' 'legacy raw compatibility/quarantine note'
Require 'm10\.backfill\.v2' 'total-order backfill cursor version'
Require 'source_kind.*source_ordinal' 'source-kind and ordinal cursor tie keys'
Require 'time-first' 'time-first backfill source order'
Require 'ORDER BY o\.observed_at,o\.source_kind' 'time-first union page order'
Require 'safe_cursor' 'durable last-complete-bucket cursor'
Require "status IN \('queued','running','partial'\)" 'retention partial dependency guard'
Require "status='failed' AND j\.attempt>=5" 'retention exhausted dependency guard'
Require '(?is)commit_metric_rollups\s*\(\s*p_operation_id uuid,p_job_id uuid' 'rollup explicit job identity'
Require 'commit_metric_baselines\(p_operation_id uuid,p_job_id uuid' 'baseline explicit job identity'
Require 'commit_metric_forecast\(p_operation_id uuid,p_job_id uuid' 'forecast explicit job identity'
Require 'commit_evidence_packet\(p_operation_id uuid,p_job_id uuid' 'evidence explicit job identity'
Require 'commit_incident_thread\(p_operation_id uuid,p_job_id uuid' 'incident explicit job identity'
Require 'commit_incident_generation\(p_operation_id uuid,p_job_id uuid' 'generation explicit job identity'
Require 'GRANT EXECUTE ON FUNCTION analytics\.commit_metric_rollups\(uuid,uuid,uuid,bigint' 'rollup server grant uses explicit job'
Require 'GRANT EXECUTE ON FUNCTION analytics\.commit_metric_baselines\(uuid,uuid,uuid,bigint' 'baseline server grant uses explicit job'
Require 'GRANT EXECUTE ON FUNCTION analytics\.commit_incident_generation\(uuid,uuid,uuid,bigint' 'generation server grant uses explicit job'
Require 'CREATE OR REPLACE FUNCTION control\.schedule_m10_rollup_jobs' 'live rollup scheduler'
Require 'CREATE OR REPLACE FUNCTION control\.claim_m10_rollup_jobs' 'live rollup claim'
Require 'CREATE OR REPLACE FUNCTION control\.complete_m10_rollup_job' 'live rollup completion'
Require "rollup_interval IN \('5m','hour','day'\)" 'live rollup interval allowlist'
Require 'CREATE OR REPLACE FUNCTION reporting\.read_m10_rollup_derivation_inputs' 'fixed rollup derivation read'
Require 'dimensions_hash' 'dimension-scoped job metadata'
Require 'CREATE OR REPLACE FUNCTION reporting\.get_m10_forecast_scoped' 'dimension-scoped forecast read'
Require 'CREATE OR REPLACE FUNCTION reporting\.get_m10_forecast_capacity_scoped' 'dimension-scoped forecast capacity'
Require '(?is)commit_metric_rollups.*j\.job_kind=''backfill''.*j\.status=''running''' 'rollup job fence validation'
Require '(?is)commit_metric_baselines.*j\.job_kind=''baseline''.*j\.status=''running''' 'baseline job fence validation'
Require '(?is)commit_metric_forecast.*j\.job_kind=''forecast''.*j\.status=''running''' 'forecast job fence validation'
Require '(?is)commit_evidence_packet.*j\.job_kind=''evidence''.*j\.status=''running''' 'evidence job fence validation'
Require '(?is)commit_incident_thread.*j\.job_kind=''correlation''.*j\.status=''running''' 'incident job fence validation'
Require '(?is)commit_incident_generation.*j\.job_kind=''correlation''.*j\.status=''running''' 'generation job fence validation'
Require 'EvidenceV1 reference allowlist: metric, alert, activity, deadlock, replication, host, health' 'EvidenceV1 reference allowlist'
Require "jsonb_build_object\('type','health'" 'allowlisted evidence reference projection'
Require '(?is)CREATE OR REPLACE FUNCTION control\.schedule_m10_derivation_jobs.*?SET TimeZone=''UTC''' 'UTC derivation scheduler'
Require '(?is)CREATE OR REPLACE FUNCTION control\.claim_m10_derivation_jobs.*?SET TimeZone=''UTC''' 'UTC derivation claim'
Require '(?is)CREATE OR REPLACE FUNCTION control\.complete_m10_derivation_job.*?SET TimeZone=''UTC''' 'UTC derivation completion'
Require 'p_job_id uuid,p_source_catalog_version integer' 'cursor job/catalog binding parameters'
Require 'cursor_dimension_hash' 'persisted backfill cursor dimensions'
Require 'CREATE OR REPLACE FUNCTION control\.advance_m10_backfill_cursor' 'fenced backfill cursor function'
Require 'CREATE OR REPLACE FUNCTION control\.complete_m10_analytics_job.*p_owner_execution_id' 'fenced backfill completion function'
Require 'CREATE OR REPLACE FUNCTION control\.replay_m10_analytics_job.*p_target_revision' 'target/replay backfill function'

# Upgrade paths must not use a cross-column DEFAULT for dimension identity.
# Existing rows are repaired explicitly before the NOT NULL transition.
Require '(?is)ALTER TABLE analytics\.metric_baseline ADD COLUMN IF NOT EXISTS dimension_hash bytea;\s*UPDATE analytics\.metric_baseline\s+SET dimension_hash=sha256\(convert_to\(dimensions::text' 'baseline explicit dimension hash backfill'
Require '(?is)ALTER TABLE analytics\.metric_forecast ADD COLUMN IF NOT EXISTS dimension_hash bytea;\s*UPDATE analytics\.metric_forecast\s+SET dimension_hash=sha256\(convert_to\(dimensions::text' 'forecast explicit dimension hash backfill'
if ($sql -match '(?is)ALTER TABLE analytics\.metric_(?:baseline|forecast)\s+ADD COLUMN IF NOT EXISTS dimension_hash bytea\s+DEFAULT\s+sha256\([^;]*dimensions') { $errors.Add('dimension hash upgrade must not use a cross-column DEFAULT') }

$hostCommitSql = [regex]::Match($sql, '(?is)CREATE OR REPLACE FUNCTION telemetry\.commit_m10_host_metrics.*?END \$m10_host_commit\$;').Value
foreach ($field in @("jsonb_array_length\(p_payload->'items'\)>256", 'octet_length\(p_payload::text\)>262144', 'computed_payload_digest', "sha256\(convert_to\(p_payload::text,'UTF8'\)\)", '(?is)payload_digest.*computed_payload_digest')) { if ($hostCommitSql -notmatch $field) { $errors.Add("host commit replay/bounds contract missing $field") } }
$replicationCommitSql = [regex]::Match($sql, '(?is)CREATE OR REPLACE FUNCTION telemetry\.commit_m10_replication.*?END \$m10_replication_commit\$;').Value
foreach ($field in @('computed_payload_digest', "sha256\(convert_to\(p_payload::text,'UTF8'\)\)", '(?is)payload_digest.*computed_payload_digest')) { if ($replicationCommitSql -notmatch $field) { $errors.Add("replication commit replay contract missing $field") } }
Require 'computed_result_digest' 'database-authoritative analytics result digest'
Require "jsonb_build_object\('rows',p_rows\)" 'rollup replay binds canonical rows JSON'

$backfillGrant = [regex]::Match($sql, '(?is)GRANT EXECUTE ON FUNCTION control\.claim_m10_analytics_jobs.*?;').Value
if ($backfillGrant -notmatch 'TO sqlobserver_collector') { $errors.Add('collector-owned backfill functions must be granted to sqlobserver_collector') }
if ($backfillGrant -match 'TO sqlobserver_server') { $errors.Add('collector-owned backfill functions must not be granted to sqlobserver_server') }

$capacityScopedSql = [regex]::Match($sql, '(?is)CREATE OR REPLACE FUNCTION reporting\.get_m10_forecast_capacity_scoped.*?\$\$;').Value
foreach ($field in @("h\.metric_key='host\.volume\.total_bytes'", 'h\.dimensions=coalesce\(p_dimensions', 'h\.target_revision=p_target_revision', 'h\.observed_at<=p_snapshot_utc')) { if ($capacityScopedSql -notmatch $field) { $errors.Add("scoped capacity must fence total_bytes by $field") } }
$capacityHashSql = [regex]::Match($sql, '(?is)CREATE OR REPLACE FUNCTION reporting\.get_m10_forecast_capacity\(p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_dimension_hash bytea.*?\$\$;').Value
foreach ($field in @("h\.metric_key='host\.volume\.total_bytes'", 'p_dimension_hash IS NOT NULL', 'sha256\(convert_to\(h\.dimensions::text')) { if ($capacityHashSql -notmatch $field) { $errors.Add("hash capacity must fence total_bytes by $field") } }

if ($sql -match '(?is)GRANT EXECUTE ON FUNCTION control\.(?:schedule_m10_rollup_jobs|claim_m10_rollup_jobs|complete_m10_rollup_job)\([^;]*TO\s+sqlobserver_(?:server|collector)') { $errors.Add('retired rollup scheduler surface must not be executable by runtime roles') }
Require '(?is)GRANT EXECUTE ON FUNCTION control\.schedule_m10_derivation_jobs\(uuid,bigint\).*TO sqlobserver_collector' 'canonical derivation scheduler collector grant'
$repositoryPort = Join-Path $RepositoryRoot 'src\SqlObserver.Infrastructure.PostgreSql\PostgreSqlAnalyticsRepositoryPort.cs'
$backfillPort = Join-Path $RepositoryRoot 'src\SqlObserver.Infrastructure.PostgreSql\PostgreSqlAnalyticsBackfillStore.cs'
$repositorySql = Get-Content -Raw -LiteralPath $repositoryPort -ErrorAction Stop
$backfillSql = Get-Content -Raw -LiteralPath $backfillPort -ErrorAction Stop
$surfaceSql = [regex]::Match($repositorySql, '(?is)ReadSurfaceAsync.*?return new AnalyticsSurfacePage').Value
if ($surfaceSql -match '(?is)FROM\s+(?:control|telemetry|events|analytics)\.') { $errors.Add('surface repository must use fixed scoped functions, not protected base tables') }
foreach ($field in @('SurfaceCursorKind','TargetRevision','Generation','SnapshotUtc','SourceCutoffUtc','TieAt','TieId','TieMetric','TieDimensionsJson','TieFingerprint')) { if ($repositorySql -notmatch [regex]::Escape($field)) { $errors.Add("surface cursor missing $field") } }
foreach ($field in @('BeginTransactionAsync','sqlobserver.target_scope','read_m10_backfill_page','NpgsqlTransaction')) { if ($backfillSql -notmatch [regex]::Escape($field)) { $errors.Add("backfill transaction contract missing $field") } }
$partitionMethod = [regex]::Match($backfillSql, '(?is)EnsureHistoricalPartitionAsync.*?(?=\r?\n\s*public async|\r?\n\s*private |\r?\n})').Value
foreach ($field in @('BeginTransactionAsync','set_config\(\x27sqlobserver.target_scope\x27','ensure_m10_backfill_partition.*connection, transaction','CommitAsync')) { if ($partitionMethod -notmatch $field) { $errors.Add("partition mutation scope contract missing $field") } }
$derivationPort = Join-Path $RepositoryRoot 'src\SqlObserver.Infrastructure.PostgreSql\PostgreSqlAnalyticsRepositoryPort.cs'
$derivationSql = Get-Content -Raw -LiteralPath $derivationPort
foreach ($field in @('schedule_m10_derivation_jobs','claim_m10_derivation_jobs','read_m10_evidence_derivation_inputs','read_m10_incident_derivation_inputs','complete_m10_derivation_job')) { if ($derivationSql -notmatch [regex]::Escape($field)) { $errors.Add("derivation repository contract missing $field") } }
$workerPath = Join-Path $RepositoryRoot 'src\SqlObserver.Collector\AnalyticsBackfillWorker.cs'
$workerSql = Get-Content -Raw -LiteralPath $workerPath -ErrorAction Stop
foreach ($field in @('skipLeadingPartialBucket','leadingBucket','carry','page.HasMore')) { if ($workerSql -notmatch [regex]::Escape($field)) { $errors.Add("backfill worker missing $field") } }
$schedulerSql = [regex]::Match($sql, '(?is)CREATE OR REPLACE FUNCTION control\.schedule_m10_derivation_jobs.*?END \$m10_schedule_derivation\$;').Value
$bucketSql = [regex]::Match($sql, '(?is)CREATE OR REPLACE FUNCTION control\.m10_rollup_due_buckets.*?\$m10_due_buckets\$;').Value
foreach ($field in @("date_trunc\('hour',p_now\).*floor\(extract\(minute FROM p_now\)/5\).*interval '5 minutes'-interval '5 minutes'", "date_trunc\('hour',p_now\)-interval '1 hour'", "date_trunc\('day',p_now\)-interval '1 day'", "SET TimeZone='UTC'")) { if ($bucketSql -notmatch $field) { $errors.Add("completed bucket helper missing $field") } }
if ($sql -match '(?is)GRANT EXECUTE ON FUNCTION control\.m10_rollup_due_buckets') { $errors.Add('completed bucket helper must remain internal') }
if ($schedulerSql -notmatch 'schedule_now timestamptz:=clock_timestamp\(\); day_end timestamptz:=date_trunc\(''day'',schedule_now\)') { $errors.Add('scheduler must capture schedule_now once and derive day_end from it') }
if ($schedulerSql -notmatch 'm10_rollup_due_buckets\(schedule_now\)') { $errors.Add('scheduler must use deterministic completed bucket helper') }
if ($schedulerSql -match "date_trunc\('hour',day_end\)") { $errors.Add('intraday bucket scheduler must not derive from midnight day_end') }
foreach ($field in @("b\.bucket_start,b\.bucket_start\+b\.width", "'m10-rollup|'", 'b\.bucket_start::text', 'ON CONFLICT\(job_id\) DO NOTHING')) { if ($schedulerSql -notmatch $field) { $errors.Add("scheduler bucket identity/range contract missing $field") } }
if ($schedulerSql -match 'x\.job_kind') { $errors.Add('derivation scheduler references target alias for job kind') }
foreach ($kind in @('baseline','forecast','evidence','correlation')) { if ($schedulerSql -notmatch "'$kind'") { $errors.Add("derivation scheduler missing $kind job kind") } }
foreach ($field in @('dimensions AS','r.metric_key=''host.volume.free_bytes''','r.rollup_interval=''day''','r.visibility_state=''complete''','r.dimension_hash','r.bucket_start >= day_end-interval ''29 days''','r.bucket_start < day_end-interval ''1 day''','row_number()','PARTITION BY c.instance_id,c.revision ORDER BY c.dimension_hash','dimension_ordinal<=256','dimensions_hash')) { if ($schedulerSql -notmatch [regex]::Escape($field)) { $errors.Add("forecast dimension scheduler missing $field") } }
if ($schedulerSql -match "'incident'") { $errors.Add('derivation scheduler must use one correlation job kind') }
if ($sql -notmatch "(?is)j\.work_key='analytics/derivation'.*j\.job_kind IN \('baseline','forecast','evidence','correlation'\)") { $errors.Add('derivation claim must fence the analytics/derivation work key') }
if ($schedulerSql -notmatch "CASE WHEN k\.job_kind='evidence' THEN day_end-interval '1 day'") { $errors.Add('evidence scheduler must use the newly complete day') }
if ($schedulerSql -notmatch "CASE WHEN k\.job_kind='evidence' THEN day_end ELSE day_end-interval '1 day'") { $errors.Add('baseline/forecast scheduler must stop at the prior day boundary') }
if ($sql -match '(?is)CREATE OR REPLACE FUNCTION analytics\.commit_[^$]+\$.*?record_m10_analytics_replay\(p_operation_id,\x27[^\x27]+\x27,p_operation_id') { $errors.Add('analytics commit must not synthesize a job from operation_id') }
if ($sql -match "jsonb_build_object\('type','diagnostic'") { $errors.Add('EvidenceV1 projection uses a non-allowlisted diagnostic type') }
if ($sql -match '(?is)CREATE OR REPLACE FUNCTION telemetry\.commit_m10_host_metrics.*?gen_random_uuid') { $errors.Add('host commit may not allocate random host identities') }
if ($sql -match '(?i)GRANT\s+SELECT') { $errors.Add('server/runtime must not receive direct SELECT grants') }
if ($sql -match '(?i)GRANT\s+EXECUTE\s+ON\s+FUNCTION\s+(?:reporting\.(?:list_m10_host_metrics|list_m10_replication|list_host_metrics|list_replication_health|list_metric_series|list_metric_baselines|list_incidents)|analytics\.(?:compare_metric_windows)|reporting\.(?:get_storage_forecast|get_incident_evidence))\s*\([^)]*(?:uuid\s*,\s*timestamptz|uuid\s*,\s*text)') { $errors.Add('legacy no-target-revision overload is executable by runtime') }
if ($sql -match 'GRANT EXECUTE ON FUNCTION system\.(?:detach_m10_partition|drop_m10_partition).*sqlobserver_collector') { $errors.Add('collector role must never execute retention detach/drop') }
if ($sql -match '(?i)USING\s*\(\s*instance_id\s+IS\s+NULL\s+OR') { $errors.Add('M10 RLS must not expose unbound NULL instance rows') }
if ($sql -match '(?i)\bdigest\s*\(') { $errors.Add('pgcrypto digest() call remains in M10 migration') }
if ($sql -match "decode\(repeat\('00',32\),'hex'\)") { $errors.Add('M10 collector contract contains an all-zero digest placeholder') }
if ($sql -notmatch "false,NULL") { $errors.Add('disabled/null retention defaults are not visible') }
$hash = (Get-FileHash -LiteralPath $migration -Algorithm SHA256).Hash.ToLowerInvariant()
$line = (Get-Content -LiteralPath $checksums | Where-Object { $_ -match '0014_analytics_host_replication_retention\.sql$' })
if (-not $line -or $line -notmatch "^$hash\s+0014_analytics_host_replication_retention\.sql$") { $errors.Add('checksum entry does not match migration bytes') }
if ($errors.Count) { $errors | ForEach-Object { Write-Error $_ }; exit 1 }
Write-Output 'M10 static migration checks: PASS'
