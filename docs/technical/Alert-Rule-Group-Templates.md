# Alert rule templates and group deployments

This dashboard feature uses a durable shared template library and complete native group previews. A loaded Overview page is never a deployment cohort. This contract does not claim completion of the broader repository plan.

## Library and authorization

A template has a UUID, owner SID and immutable numbered versions. Each version contains a safe title, source product-catalog template name, catalog digest, full rule settings, reason, actor, operation/audit IDs and timestamp. Its version digest is independent of the product catalog digest. Editing creates the next version and does not change existing target rules or deployments. The intended rule name is part of the settings; changing it can create a different target rule and never renames another rule implicitly.

Any active Target Administrator may read the shared library or create a template. Only its owner while holding that role, or an all-target Target Administrator, may publish a new version. Library operations have a dedicated immutable audit ledger because they are global configuration, not fake target mutations. Deployment reads and writes require the current Target Administrator grant on every frozen member. Role scopes are resolved by the server; request bodies cannot supply scopes, approvals, digests or audit metadata.

Rule settings are exactly `name`, `kind`, `metricId`, `comparison`, `threshold`, `hysteresis`, `confirmationCount`, `confirmationSeconds`, `evaluationSeconds`, `clearConfirmationCount`, `enabled`. Existing Domain bounds and `AlertCatalog.IsApproved` apply. Source collector/schema and unit come from the selected product catalog entry. Native admission also checks its catalog registry and settings bounds.

## Preview and application

Selectors are `{kind: environment|tag, value: normalized-label}`. Native preview resolves all matching configured, non-retired targets in one transaction, deduplicates IDs and authorizes every member before exposing or persisting the result. Missing authorization returns generic 403, without member identities or counts. Empty groups and groups above 100 members are refused; there is no truncated deployment.

Preview persists an actor-bound deployment UUID, immutable template/version and complete member manifest: target identity/name/configuration revision, grouping revision, intended rule UUID, exact prior configuration/revision, proposed settings, collector source compatibility and intended decision. It expires after 15 minutes for fresh application; exact operation replay precedes expiry and current-state checks. New matching members never join an existing preview. A member that changes labels, target revision or rule configuration after preview conflicts instead of being overwritten.

`create_missing` skips any existing rule with the intended name. `replace_named` updates only that exact target/name rule, and only if its kind and metric match the selected signal; a different signal is an explicit incompatibility. Equal configurations are skipped. Other rules for the same signal remain unchanged. An enabled proposal requires an enabled collector schedule for the target's current revision and the approved source schema; unavailable source coverage is disclosed per target and does not become a healthy verdict. Disabling an existing rule remains possible with approved settings even when collection is unavailable.

Application processes at most ten pending members per request in canonical target-ID order. A durable job lock serializes batches. Per-target savepoints retain successful targets and record conflict/failure results without claiming all-or-nothing fleet success. Each rule write reuses the native dispatch fence, revision guard and queue/evaluation effects. Child operation IDs are deterministic and durable; a batch ledger separately binds actor, deployment, expected job revision, action and reason. Exact retry returns the immutable original batch receipt and never processes additional members. Continuing pending members is a new explicit operation after authoritative status readback. Cancellation stops waiting; it is not evidence that a dispatched batch was unapplied.

Stopping remaining work is an explicit revision-checked operation that marks pending members stopped and seals the job. Zero pending members seal a job automatically. Fresh application to a sealed job is refused. Per-target results and current server state remain separate from historical batch receipts. Public continuations use native private ordering and bound anchors.

## Rollback and provenance

Rollback is a new actor-bound preview referencing a sealed non-rollback deployment. It includes only successfully applied source members and captures current target/grouping revisions. Each candidate must still have the exact applied rule revision and configuration. Later manual or template edits conflict, even if values happen to match again. Original updated rules restore their complete prior settings; newly created rules are disabled, never deleted. No applied members means no rollback proposal. Rollback does not retract sent notifications, recreate resolved episodes or reverse audit history.

Immutable source/member records retain template/version/catalog digests and before/after settings. Current reads expose current rule revision/settings and whether they still match the applied result, so later one-off edits cannot be labelled inherited current configuration. Restoring a previous template binding is conditional on the restored configuration; historical provenance is never rewritten. Updating a template or changing group membership never propagates automatically. A new preview is required.

## HTTP contract

All reads are `no-store`. Unsafe methods require JSON, same-origin protections, bounded bodies, authentication and administrative rate limits. UUIDs are nonzero canonical D strings; UTC timestamps use the existing native UTC format. Unknown producer fields are rejected. Reason text is safe and at most 512 UTF-8 bytes; titles are at most 256 bytes. Template versions and job revisions are positive safe integers.

Library base: `/api/v1/alerts/rule-templates`.

- `GET /catalog`: `{actorSid,allTargetAdministrator,catalogDigest,items:[product entries with name,kind,metricId,unit,comparison,threshold,hysteresis,confirmationCount,confirmationSeconds,evaluationSeconds,clearConfirmationCount]}`. Identity and all-target capability are current server-derived metadata for owner controls; they do not grant authority or change `/me`.
- `GET ?limit=50&beforeTemplateId=UUID`: `{serverNowUtc,items:[TemplateVersion],nextBeforeTemplateId:null|UUID}`.
- `GET /{templateId}?version=N`: exact version, or current if version is absent.
- `GET /{templateId}/versions?limit=50&beforeVersion=N`: `{templateId,serverNowUtc,items:[TemplateVersion],nextBeforeVersion:null|N}`.
- `POST`: `{operationId,templateId,title,sourceTemplateName,catalogDigest,values,reason}`.
- `PUT /{templateId}`: same fields except templateId, with `expectedVersion`.
- Write receipt: `{operationId,auditId,recordedAtUtc,templateId,version,versionDigest}`.
- `TemplateVersion`: `{templateId,version,currentVersion,title,ownerSid,actorSid,sourceTemplateName,catalogDigest,versionDigest,values,reason,operationId,auditId,createdAtUtc,serverNowUtc}`. The currentVersion/serverNowUtc fields describe current read state; the version payload and audit fields are immutable.

Deployment base: `/api/v1/alerts/rule-deployments`.

- `POST /preview`: `{operationId,deploymentId,templateId,templateVersion,group:{kind,value},mode:create_missing|replace_named,reason}`.
- `POST /{sourceDeploymentId}/rollback-preview`: `{operationId,deploymentId,reason}`.
- Preview receipt: `{operationId,auditId,recordedAtUtc,deployment:DeploymentStatus}`. Replay returns historical preview state; current status requires GET.
- `GET /{deploymentId}`: `DeploymentStatus`.
- `GET ?limit=50&beforeDeploymentId=UUID`: `{serverNowUtc,items:[DeploymentSummary],nextBeforeDeploymentId:null|UUID}`; only jobs fully authorized for the reader are listed, with no partial member exposure.
- `POST /{deploymentId}/apply` and `/stop`: `{operationId,expectedRevision,reason}`.
- Batch receipt: `{operationId,auditId,recordedAtUtc,deploymentId,revision,action:apply|stop,items:[BatchItem],pendingCount,sealed}`.
- `GET /{deploymentId}/history?limit=50&beforeOperationId=UUID`: `{deploymentId,serverNowUtc,items:[BatchReceipt],nextBeforeOperationId:null|UUID}`. Equal timestamps never lose operations.

`DeploymentStatus`: `{deploymentId,revision,actorSid,group:null|{kind,value},mode:create_missing|replace_named|rollback,sourceDeploymentId:null|UUID,template:TemplateVersion,previewAtUtc,expiresAtUtc,sealed,pendingCount,serverNowUtc,reason,items:[DeploymentMember]}`.

`DeploymentSummary`: `{deploymentId,revision,actorSid,group:null|{kind,value},mode,sourceDeploymentId:null|UUID,templateId,templateVersion,templateTitle,previewAtUtc,sealed,pendingCount,memberCount,serverNowUtc}`. Listing metadata is current; opening an exact deployment reads its complete bounded member state.

`DeploymentMember`: `{targetId,targetName,targetRevision,groupingRevision,ruleId,expectedRuleRevision:null|N,beforeValues:null|RuleValues,desiredValues:RuleValues,decision:create|update|restore|disable_created|skip_existing|skip_unchanged|incompatible,compatibility:available|unavailable,compatibilityReason:null|string,operationId,status:pending|applied|skipped|conflict|failed|stopped,resultRevision:null|N,resultAuditId:null|UUID,resultAtUtc:null|UTC,resultCode:null|string,currentRuleRevision:null|N,currentValues:null|RuleValues,matchesApplied:null|boolean}`.

`BatchItem`: `{targetId,ruleId,status,code:null|string,ruleRevision:null|N,auditId:null|UUID,recordedAtUtc:null|UTC}`. Receipt arrays contain only that operation's processed members, at most ten for apply and at most 100 for stop. Counts are historical outcomes, not proof of current rule state.

## Dashboard and qualification

A fleet Rule templates screen offers library versions/settings, server-resolved group preview, explicit deployment batches, per-server outcomes/current readback, stop confirmation, rollback preview and immutable history. It uses shared dark tokens, responsive bounded tables and exact template/deployment URL selections. Reads are explicit; workspace polling and diagnostic time changes do not discard uncertain commands. Confirmed writes retry only failed GET readback. Uncertain writes retain exact command/operation identity; context/role/denial/unmount cancel owned work and fence late results. The mutation lane synchronously excludes competing writes.

Required evidence includes native full-membership authorization/overflow, immutable versions, exact replay and actor divergence, membership/catalog/target/rule drift, source compatibility, partial batches, dispatch races, stop/apply/rollback races, rollback after manual changes, history paging and grants/RLS. Production HTTP tests use only authentication replacement. Mounted and built-App tests cover payloads, ownership, recovery, URLs, dark theme, keyboard, axe and 320/390/768/1440 widths. Final screenshots and input hashes are bound to the local commit. Final qualification passes: 46 native web, 43 mounted, 8 built-App, and affected administration/workflow/keyboard groups 16/14/6; 228 .NET checks (19 unit, 53 API, 50 security/worker, 106 PostgreSQL); strict product/harness TypeScript, production build and asset verification. All 1939 final frozen inputs are unchanged. See [screenshots](../../artifacts/dashboard-rule-templates/index.html) and [exact cases/hashes/provenance](../../artifacts/dashboard-rule-templates/completion.json). Native checks use disposable pinned PostgreSQL 18.4 and production HTTP with authentication replaced; browser fixtures are synthetic. Full failed attempts are preserved separately. No SQL Server collector, manual assistive-technology or cross-browser certification is claimed. No GitHub upload occurred.
