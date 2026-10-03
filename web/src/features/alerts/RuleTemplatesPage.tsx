import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId, validNotificationReason } from "./alertSnoozeModel.ts";
import { prepareRule, type RuleValues } from "./alertRuleAdminActions.ts";
import { ruleSignalLabel, ruleExplanation } from "./alertRuleSignals.ts";
import { getTemplateCatalog, getTemplateVersion, getDeploymentStatus, getTemplatePage, getVersionPage, getDeploymentPage, getBatchHistory, sendTemplateCommand } from "./ruleTemplatesApi.ts";
import { commandResource, parseRuleGroup, positive, safeTemplateText, type ProductCatalog, type TemplateVersion, type DeploymentStatus,
  type DeploymentSummary, type BatchReceipt, type NativePage, type TemplateCommand, type CommandReceipt } from "./ruleTemplatesModel.ts";
import "./ruleTemplates.css";
interface Props {
  readonly allowed: boolean; readonly allTargetAdmin?: boolean; readonly actorSid?: string;
  readonly selectedTemplateId?: string | null; readonly selectedTemplateVersion?: number | null; readonly selectedDeploymentId?: string | null;
  readonly onSelectTemplate: (id: string, version: number) => void; readonly onSelectDeployment: (id: string) => void; readonly onBack: () => void;
}
type Resource = ReturnType<typeof commandResource>;
type Source = "templates" | "versions" | "deployments" | "history";
type PageItem = TemplateVersion | DeploymentSummary | BatchReceipt;
interface Pages { readonly pages: readonly NativePage<PageItem>[]; readonly loading?: boolean; readonly error?: string; readonly attempt?: { readonly before?: string | number; readonly replace: boolean } }
interface Owner { readonly controller: AbortController; readonly command?: TemplateCommand; readonly resource?: Resource; readonly readback?: boolean; readonly newIntent?: boolean }
interface View { readonly template?: TemplateVersion; readonly deployment?: DeploymentStatus; readonly receipt?: CommandReceipt;
  readonly uncertain?: TemplateCommand; readonly busy?: boolean; readonly conflict?: boolean; readonly message?: string;
  readonly retryRead?: { readonly resource: Resource; readonly readback: boolean; readonly newIntent: boolean } }
interface Draft { readonly id: string; readonly title: string; readonly source: string; readonly values?: RuleValues; readonly reason: string; readonly expectedVersion?: number }
const emptyPages = (): Record<Source, Pages> => ({ templates: { pages: [] }, versions: { pages: [] }, deployments: { pages: [] }, history: { pages: [] } });
const newDraft = (): Draft => ({ id: crypto.randomUUID(), title: "", source: "", reason: "" });
function draftFromCatalog(catalog: ProductCatalog | undefined): Draft {
  const draft = newDraft(), first = catalog?.items[0]; if (!first) return draft;
  const { unit: _unit, ...values } = first;
  return { ...draft, source: first.name, values: { ...values, name: first.kind === "metric_threshold" ? "" : first.name, enabled: true } };
}
function Settings({ values }: { readonly values: RuleValues }) {
  return <dl className="rule-details"><div><dt>Exact rule name</dt><dd>{values.name}</dd></div><div><dt>Signal / metric</dt><dd>{ruleSignalLabel(values.kind, values.metricId)} · {values.metricId ?? "signal derived"}</dd></div><div><dt>Condition</dt><dd>{values.comparison} {values.threshold}</dd></div><div><dt>Hysteresis</dt><dd>{values.hysteresis}</dd></div><div><dt>Fire confirmation</dt><dd>{values.confirmationCount} observations within {values.confirmationSeconds} seconds</dd></div><div><dt>Evaluation</dt><dd>Every {values.evaluationSeconds} seconds</dd></div><div><dt>Clear confirmation</dt><dd>{values.clearConfirmationCount} observations</dd></div><div><dt>Enabled</dt><dd>{values.enabled ? "Yes" : "No"}</dd></div></dl>;
}
export function RuleTemplatesPage({ allowed, selectedTemplateId, selectedTemplateVersion, selectedDeploymentId, onSelectTemplate, onSelectDeployment, onBack }: Props) {
  const mounted = useRef(false), authority = useRef(allowed), epoch = useRef(0), owner = useRef<Owner | undefined>(undefined);
  authority.current = allowed;
  const [denied, setDenied] = useState(false), denial = useRef(false), [accessGeneration, setAccessGeneration] = useState(0);
  const [catalog, setCatalog] = useState<ProductCatalog>(), [catalogError, setCatalogError] = useState<string>();
  const catalogIdentity = useRef<string | undefined>(undefined);
  const [authorityNotice, setAuthorityNotice] = useState<string>();
  const catalogVerified = useRef(false);
  const catalogOwner = useRef<AbortController | undefined>(undefined), pageOwners = useRef(new Map<Source, AbortController>());
  const [pages, setPagesState] = useState(emptyPages), pagesRef = useRef(pages);
  const updatePages = (source: Source, change: (previous: Pages) => Pages) => { const next = { ...pagesRef.current, [source]: change(pagesRef.current[source]) }; pagesRef.current = next; setPagesState(next); };
  const [view, setViewState] = useState<View>({}), viewRef = useRef(view);
  const suspendedReadback = useRef<{ resource: Resource; receipt?: CommandReceipt } | undefined>(undefined);
  const update = (change: (previous: View) => View) => { const next = change(viewRef.current); viewRef.current = next; setViewState(next); };
  const [draft, setDraft] = useState(newDraft), [groupKind, setGroupKind] = useState<"environment" | "tag">("environment"), [groupLabel, setGroupLabel] = useState("");
  const [mode, setMode] = useState<"create_missing" | "replace_named">("create_missing"), [deploymentReason, setDeploymentReason] = useState("");
  const [operationReason, setOperationReason] = useState(""), [stopProposal, setStopProposal] = useState<TemplateCommand>();
  const [localTemplate, setLocalTemplate] = useState<{ id: string; version: number }>(), [localJob, setLocalJob] = useState<string>();
  const templateId = selectedTemplateId === undefined ? localTemplate?.id : selectedTemplateId;
  const templateVersion = selectedTemplateVersion === undefined ? localTemplate?.version : selectedTemplateVersion;
  const deploymentId = selectedDeploymentId === undefined ? localJob : selectedDeploymentId;
  const selectionKey = JSON.stringify([templateId, templateVersion, deploymentId]), selection = useRef(selectionKey); selection.current = selectionKey;
  const previousSelection = useRef(selectionKey), acceptedSelection = useRef<string | undefined>(undefined);
  const automaticReads = useRef(new Set<string>());
  const [, clock] = useState(Date.now);
  const usable = () => mounted.current && authority.current && !denial.current;
  function abortReads() {
    epoch.current++; if (catalogOwner.current && mounted.current) setCatalogError("Catalog read cancelled. Retry before editing settings."); catalogOwner.current?.abort(); catalogOwner.current = undefined;
    for (const [source, controller] of pageOwners.current) { controller.abort(); if (mounted.current) updatePages(source, old => ({ ...old, loading: false, error: "Read cancelled. Received pages remain; retry keeps the exact anchor." })); }
    pageOwners.current.clear(); owner.current?.controller.abort(); owner.current = undefined;
  }
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false; abortReads(); }; }, []);
  useLayoutEffect(() => {
    if (allowed) return; const request = owner.current, retry = request?.resource && request.readback
      ? { resource: request.resource, readback: true, newIntent: Boolean(request.newIntent) } : viewRef.current.retryRead?.readback ? viewRef.current.retryRead : undefined;
    if (retry?.readback) suspendedReadback.current = { resource: retry.resource, receipt: viewRef.current.receipt };
    catalogVerified.current = false; abortReads();
    update(old => ({ uncertain: request?.command ?? old.uncertain, conflict: old.conflict, retryRead: retry })); setCatalog(undefined); setCatalogError(undefined);
    const empty = emptyPages(); pagesRef.current = empty; setPagesState(empty); setStopProposal(undefined);
  }, [allowed]);
  useLayoutEffect(() => {
    if (previousSelection.current === selectionKey) return; previousSelection.current = selectionKey;
    if (acceptedSelection.current === selectionKey) { acceptedSelection.current = undefined; return; }
    const request = owner.current, retry = request?.resource && request.readback
      ? { resource: request.resource, readback: true, newIntent: Boolean(request.newIntent) } : viewRef.current.retryRead?.readback ? viewRef.current.retryRead : undefined;
    if (retry?.readback) suspendedReadback.current = { resource: retry.resource, receipt: viewRef.current.receipt };
    abortReads(); update(old => ({ uncertain: request?.command ?? old.uncertain, conflict: old.conflict, retryRead: retry }));
    updatePages("versions", () => ({ pages: [] })); updatePages("history", () => ({ pages: [] })); setStopProposal(undefined); setDraft(draftFromCatalog(catalog));
  }, [selectionKey]);
  useEffect(() => { if (allowed && !denied) { void loadCatalog(); void loadPage("templates", undefined, true); void loadPage("deployments", undefined, true); } }, [allowed, denied, accessGeneration]);
  useEffect(() => {
    if (!allowed || denied) return;
    if (templateId && notificationId(templateId) && templateVersion !== null && (templateVersion === undefined || positive(templateVersion))
      && !(viewRef.current.template?.templateId === templateId && (templateVersion === undefined || viewRef.current.template.version === templateVersion)))
      automaticRead({ kind: "template", id: templateId, ...(templateVersion === undefined ? {} : { version: templateVersion }) });
  }, [templateId, templateVersion, allowed, denied, accessGeneration, view.busy]);
  useEffect(() => {
    if (allowed && !denied && deploymentId && notificationId(deploymentId) && viewRef.current.deployment?.deploymentId !== deploymentId)
      automaticRead({ kind: "deployment", id: deploymentId });
  }, [deploymentId, allowed, denied, accessGeneration, view.busy]);
  useEffect(() => {
    const end = view.deployment?.expiresAtUtc; if (!end) return;
    let timer: ReturnType<typeof setTimeout>; const tick = () => { clearTimeout(timer); clock(Date.now()); const remaining = utcMicroseconds(end) - BigInt(Date.now()) * 1000n;
      if (remaining > 0n) timer = setTimeout(tick, Number((remaining + 999n) / 1000n)); };
    tick(); addEventListener("focus", tick); document.addEventListener("visibilitychange", tick);
    return () => { clearTimeout(timer); removeEventListener("focus", tick); document.removeEventListener("visibilitychange", tick); };
  }, [view.deployment?.expiresAtUtc]);
  function rejectAccess(failure: unknown) {
    if (!(failure instanceof ApiRequestError) || ![401, 403].includes(failure.status)) return false;
    const pending = owner.current?.command; denial.current = true; catalogVerified.current = false; suspendedReadback.current = undefined; abortReads(); setDenied(true);
    update(old => ({ uncertain: pending ?? old.uncertain })); setCatalog(undefined); const empty = emptyPages(); pagesRef.current = empty; setPagesState(empty); setStopProposal(undefined); return true;
  }
  function automaticRead(resource: Resource) {
    if (owner.current || !usable() || viewRef.current.retryRead || suspendedReadback.current) return;
    const key = JSON.stringify([epoch.current, resource]); if (automaticReads.current.has(key)) return;
    automaticReads.current.add(key); void readResource(resource);
  }
  async function loadCatalog() {
    if (!usable() || catalogOwner.current) return; const controller = new AbortController(), captured = epoch.current; catalogOwner.current = controller; setCatalogError(undefined);
    try { const result = await getTemplateCatalog(controller.signal); if (!usable() || captured !== epoch.current || controller.signal.aborted) return;
      const identity = JSON.stringify([result.actorSid, result.allTargetAdministrator]);
      if (catalogIdentity.current !== undefined && catalogIdentity.current !== identity) {
        abortReads(); update(() => ({})); setAuthorityNotice("Catalog authority changed. Previous principal receipts and commands were removed; review a new intention.");
        suspendedReadback.current = undefined;
        const empty = emptyPages(); pagesRef.current = empty; setPagesState(empty); setDraft(newDraft()); setStopProposal(undefined); setAccessGeneration(n => n + 1);
      }
      catalogIdentity.current = identity; catalogVerified.current = true; setCatalogError(undefined); setCatalog(result); setDraft(old => { if (old.values) return old; const first = result.items[0]!, { unit: _unit, ...values } = first;
        return { ...old, source: first.name, values: { ...values, name: first.kind === "metric_threshold" ? "" : first.name, enabled: true } }; }); }
    catch (failure) { if (!controller.signal.aborted && captured === epoch.current && !rejectAccess(failure)) setCatalogError("The approved product catalog is unavailable. Retry before editing settings."); }
    finally { if (catalogOwner.current === controller) catalogOwner.current = undefined; }
  }
  async function readResource(resource: Resource, readback = false, newIntent = false) {
    if (!usable() || readback && !catalogVerified.current || owner.current) return;
    const request: Owner = { controller: new AbortController(), resource, readback, newIntent }, captured = epoch.current; owner.current = request;
    update(old => ({ ...old, busy: true, retryRead: undefined, message: undefined }));
    try {
      const result = resource.kind === "template" ? await getTemplateVersion(resource.id, resource.version, request.controller.signal) : await getDeploymentStatus(resource.id, request.controller.signal);
      if (!usable() || epoch.current !== captured || owner.current !== request || request.controller.signal.aborted) return;
      update(old => { const uncertain = old.uncertain && commandResource(old.uncertain);
        const suspended = suspendedReadback.current;
        return { ...old, ...(resource.kind === "template" ? { template: result as TemplateVersion } : { deployment: result as DeploymentStatus }), busy: false,
          ...(readback && suspended?.resource.kind === resource.kind && suspended.resource.id === resource.id ? { receipt: suspended.receipt } : {}),
          ...(newIntent && old.conflict && uncertain?.kind === resource.kind && uncertain.id === resource.id ? { uncertain: undefined, conflict: false } : {}),
          message: readback ? "Current configuration read back. Preview, batch and history receipts remain historical evidence." : undefined }; });
      if (readback) {
        suspendedReadback.current = undefined;
        owner.current = undefined;
        if (resource.kind === "template") { const version = (result as TemplateVersion).version;
          acceptedSelection.current = JSON.stringify([resource.id, version, deploymentId]); setLocalTemplate({ id: resource.id, version }); onSelectTemplate(resource.id, version); }
        else { acceptedSelection.current = JSON.stringify([templateId, templateVersion, resource.id]); setLocalJob(resource.id); onSelectDeployment(resource.id); }
      }
    } catch (failure) {
      if (!usable() || epoch.current !== captured || request.controller.signal.aborted || rejectAccess(failure)) return;
      update(old => ({ ...old, busy: false, retryRead: { resource, readback, newIntent }, message: readback ? "Operation recorded, but current readback is unavailable. Retry status only; the confirmed command will not be replayed." : "Current status is unavailable. The exact uncertain operation remains available for retry." }));
    } finally { if (owner.current === request) owner.current = undefined; }
  }
  async function loadPage(source: Source, before?: string | number, replace = false) {
    const old = pagesRef.current[source], id = source === "versions" ? templateId : source === "history" ? deploymentId : undefined;
    if (!usable() || pageOwners.current.has(source) || !replace && old.pages.length >= 20 || (source === "versions" || source === "history") && !id) return;
    const controller = new AbortController(), captured = epoch.current; pageOwners.current.set(source, controller);
    updatePages(source, p => ({ ...p, loading: true, error: undefined, attempt: { before, replace } }));
    try {
      const prior = replace ? [] : old.pages;
      const result = source === "templates" ? await getTemplatePage(controller.signal, prior as NativePage<TemplateVersion>[], before as string | undefined)
        : source === "versions" ? await getVersionPage(id!, controller.signal, prior as NativePage<TemplateVersion>[], before as number | undefined)
          : source === "deployments" ? await getDeploymentPage(controller.signal, prior as NativePage<DeploymentSummary>[], before as string | undefined)
            : await getBatchHistory(id!, controller.signal, prior as NativePage<BatchReceipt>[], before as string | undefined);
      if (!usable() || captured !== epoch.current || controller.signal.aborted || pageOwners.current.get(source) !== controller) return;
      updatePages(source, p => ({ pages: replace ? [result] : [...p.pages, result] }));
    } catch (failure) { if (!controller.signal.aborted && captured === epoch.current && !rejectAccess(failure)) updatePages(source, p => ({ ...p, loading: false, error: "This listing is unavailable. Received pages remain; retry retains the exact anchor." })); }
    finally { if (pageOwners.current.get(source) === controller) pageOwners.current.delete(source); }
  }
  async function send(command: TemplateCommand) {
    if (!usable() || !catalogVerified.current || owner.current) return;
    const request: Owner = { controller: new AbortController(), command }, captured = epoch.current; owner.current = request;
    update(old => ({ ...old, uncertain: command, busy: true, conflict: false, retryRead: undefined, message: undefined }));
    try {
      const result = await sendTemplateCommand(command, request.controller.signal, viewRef.current.deployment?.deploymentId === commandResource(command).id ? viewRef.current.deployment.items : undefined);
      if (!usable() || captured !== epoch.current || request.controller.signal.aborted || owner.current !== request) return;
      owner.current = undefined; update(old => ({ ...old, receipt: result, uncertain: undefined, busy: false, message: "Operation recorded. Reading exact current state before another mutation." }));
      setStopProposal(undefined); await readResource(commandResource(command), true);
    } catch (failure) {
      if (!usable() || captured !== epoch.current || request.controller.signal.aborted || rejectAccess(failure)) return;
      update(old => ({ ...old, busy: false, uncertain: command, conflict: failure instanceof ApiRequestError && [400, 404, 409, 422].includes(failure.status),
        message: failure instanceof ApiRequestError && [400, 404, 409, 422].includes(failure.status) ? "The server refused this operation. Read its exact current resource and review a new intention; no fresh batch was inferred."
          : "Completion is uncertain. Retry retains the exact operation, resource, members, revision, settings and reason." }));
    } finally { if (owner.current === request) owner.current = undefined; }
  }
  function cancelRequest() {
    const request = owner.current; if (!request) return; request.controller.abort(); owner.current = undefined;
    update(old => ({ ...old, busy: false, uncertain: request.command ?? old.uncertain,
      retryRead: request.resource ? { resource: request.resource, readback: Boolean(request.readback), newIntent: Boolean(request.newIntent) } : old.retryRead,
      message: request.command ? "Waiting cancelled; server completion remains uncertain. Retry keeps the original command." : "Read cancelled. Received evidence remains available." }));
  }
  function selectTemplate(item: TemplateVersion) { setLocalTemplate({ id: item.templateId, version: item.version }); onSelectTemplate(item.templateId, item.version); }
  function selectJob(id: string) { setLocalJob(id); onSelectDeployment(id); }
  const template = view.template, job = view.deployment, busy = Boolean(view.busy), blocked = busy || !catalogVerified.current || Boolean(view.uncertain) || Boolean(view.retryRead);
  const product = catalog?.items.find(item => item.name === draft.source);
  const ownerCanEdit = Boolean(template && catalog && (template.ownerSid === catalog.actorSid || catalog.allTargetAdministrator));
  const draftCanEdit = ownerCanEdit && template?.templateId === draft.id && template.currentVersion === draft.expectedVersion;
  const draftAlreadyPublished = template?.templateId === draft.id && (draft.expectedVersion === undefined || draft.expectedVersion < template.currentVersion);
  const expired = Boolean(job && BigInt(Date.now()) * 1000n >= utcMicroseconds(job.expiresAtUtc));
  const refresh = view.uncertain ? commandResource(view.uncertain) : job ? { kind: "deployment" as const, id: job.deploymentId } : template ? { kind: "template" as const, id: template.templateId, version: template.version } : undefined;
  const patch = (value: Partial<RuleValues>) => setDraft(old => ({ ...old, values: old.values ? { ...old.values, ...value } : undefined }));
  function prepareLibrary() {
    if (blocked || !catalog || !product || !draft.values || draftAlreadyPublished || draft.expectedVersion !== undefined && !draftCanEdit) return;
    try { if (!safeTemplateText(draft.title) || !validNotificationReason(draft.reason)) throw new Error("Enter a safe title and template reason.");
      const values = prepareRule(draft.values, product), common = { operationId: crypto.randomUUID(), title: draft.title, sourceTemplateName: product.name, catalogDigest: catalog.catalogDigest, values: { ...values }, reason: draft.reason };
      void send(draft.expectedVersion ? { kind: "publish_template", templateId: draft.id, body: { ...common, expectedVersion: draft.expectedVersion } }
        : { kind: "create_template", body: { ...common, templateId: draft.id } }); }
    catch (failure) { update(old => ({ ...old, message: failure instanceof Error ? failure.message : "Review the settings." })); }
  }
  const pageControls = (source: Source, initial: string, older: string, refreshLabel: string) => {
    const state = pages[source], next = state.pages.at(-1)?.next;
    return <><p className="table-note">{state.pages.flatMap(page => page.items).length} received rows in {state.pages.length} pages; at most 20 pages / 1,000 rows. Server as of {state.pages.at(-1)?.serverNowUtc ?? "not received"}. Reads are explicit.</p>
      {state.error && <p role="alert">{state.error}<button type="button" disabled={state.loading} onClick={() => void loadPage(source, state.attempt?.before, state.attempt?.replace)}>Retry {source === "versions" ? "template versions" : source === "history" ? "deployment history" : source === "templates" ? "template library" : "deployment listing"}</button></p>}
      <div className="form-actions"><button type="button" disabled={state.loading || state.pages.length >= 20 || state.pages.length > 0 && next === null} onClick={() => void loadPage(source, next ?? undefined)}>{state.pages.length ? older : initial}</button><button type="button" disabled={state.loading} onClick={() => void loadPage(source, undefined, true)}>{refreshLabel}</button></div></>;
  };
  if (!allowed || denied) return <section className="rule-templates-page" aria-label="Rule templates"><h2>Rule templates</h2><p role={denied ? "alert" : "status"}>{denied ? "Rule template access was denied. Protected library, deployment and history evidence was removed." : "An active Target Administrator role is required for the template library. Deployment access requires that role on every frozen member."}</p>{denied && <button type="button" onClick={() => { denial.current = false; setDenied(false); setAccessGeneration(n => n + 1); }}>Retry rule template access</button>}<button type="button" onClick={onBack}>Back to alerts</button></section>;
  return <section className="rule-templates-page" aria-label="Rule templates"><div className="screen-intro"><div><p className="eyebrow">Fleet administration</p><h2>Rule templates</h2><p>Publish immutable library versions, preview complete server groups and apply explicit batches. Diagnostic time and loaded Overview rows do not define membership.</p></div><button type="button" className="secondary-button" onClick={onBack}>Back to alerts</button></div>
    {selectedTemplateId === null || selectedTemplateVersion === null || selectedDeploymentId === null ? <p role="alert">The template or deployment URL selection is invalid. No other record was substituted.</p> : null}
    {authorityNotice && <p role="status">{authorityNotice}</p>}{view.message && <p role="status">{view.message}</p>}{busy && <p role="status">Reading or recording an owned template request…</p>}
    <div className="form-actions">{busy && <button type="button" onClick={cancelRequest}>Cancel template request</button>}{view.uncertain && !view.conflict && <button type="button" disabled={busy || !catalogVerified.current} onClick={() => void send(view.uncertain!)}>Retry template operation</button>}
      {view.retryRead && <button type="button" disabled={busy || view.retryRead.readback && !catalogVerified.current} onClick={() => void readResource(view.retryRead!.resource, view.retryRead!.readback, view.retryRead!.newIntent)}>Retry {view.retryRead.resource.kind === "template" ? "template" : "deployment"} status</button>}
      {refresh && <button type="button" disabled={busy} onClick={() => void readResource(refresh, false, true)}>Refresh {refresh.kind === "template" ? "template" : "deployment"} status</button>}
      {view.uncertain && view.conflict && <button type="button" disabled={busy} onClick={() => update(old => ({ ...old, uncertain: undefined, conflict: false, retryRead: undefined, message: "Refused operation explicitly abandoned. Review current settings before a new operation." }))}>{view.uncertain.kind === "create_template" || view.uncertain.kind === "preview" || view.uncertain.kind === "rollback" ? "Review refused draft" : "Review refused operation"}</button>}</div>
    {view.receipt && <section className="panel" aria-label="Immutable operation receipt"><h3>Immutable operation receipt</h3><p>Operation {view.receipt.operationId} · recorded {view.receipt.recordedAtUtc}. This is historical evidence, separate from current readback.</p>{"deployment" in view.receipt ? <p>Preview received for {view.receipt.deployment.deploymentId}; {view.receipt.deployment.items.length} complete members at {view.receipt.deployment.previewAtUtc}. New group members will not join this job.</p> : "items" in view.receipt ? <p>{view.receipt.action} processed {view.receipt.items.length} members · {view.receipt.pendingCount} pending at receipt · {view.receipt.sealed ? "sealed" : "open"}. Exact retry does not process the next batch.</p> : <p>Template {view.receipt.templateId}, immutable version {view.receipt.version}.</p>}</section>}
    <section className="panel" aria-label="Template library"><h3>Template library</h3><div className="table-scroll" role="region" tabIndex={0} aria-label="Received template library"><table><caption>Current library listing, independent of target rules</caption><thead><tr><th scope="col">Template</th><th scope="col">Rule / version</th><th scope="col">Owner</th></tr></thead><tbody>{pages.templates.pages.flatMap(page => page.items as TemplateVersion[]).map(item => <tr key={item.templateId}><td><button className="table-link" type="button" aria-label={`Select template ${item.templateId} version ${item.version} · ${item.title}`} aria-pressed={templateId === item.templateId && templateVersion === item.version} onClick={() => selectTemplate(item)}>{item.title}</button></td><td>{item.values.name} · {item.version}</td><td>{item.ownerSid}</td></tr>)}</tbody></table></div>{pageControls("templates", "Load template library", "Load older templates", "Refresh template library")}</section>
    {template && <section className="panel" aria-label="Current template version"><h3>{template.title}</h3><p>Template {template.templateId} · selected immutable version {template.version} · current version {template.currentVersion}. Server as of {template.serverNowUtc}.</p><p>Intended rule {template.values.name} · {ruleSignalLabel(template.values.kind, template.values.metricId)} · owner {template.ownerSid}. Publishing a version changes no deployed rule automatically.</p><p>{template.reason}</p><button type="button" disabled={blocked || !ownerCanEdit} onClick={() => { if (!ownerCanEdit || blocked) return; setDraft({ id: template.templateId, title: template.title, source: template.sourceTemplateName, values: template.values, reason: "", expectedVersion: template.currentVersion }); }}>Edit template version</button>{!ownerCanEdit && <p>Only the owner with Target Administrator access, or an all-target Target Administrator, may publish a version.</p>}
      <details><summary>Immutable template settings and provenance</summary><Settings values={template.values} /><p>Source product {template.sourceTemplateName} · catalog digest {template.catalogDigest} · version digest {template.versionDigest}. Recorded {template.createdAtUtc} by {template.actorSid}.</p></details>
      <div className="table-scroll" role="region" tabIndex={0} aria-label="Received immutable versions"><table><caption>Immutable template versions</caption><thead><tr><th scope="col">Version</th><th scope="col">Title / reason</th><th scope="col">Recorded</th></tr></thead><tbody>{pages.versions.pages.flatMap(page => page.items as TemplateVersion[]).map(item => <tr key={item.version}><td><button type="button" aria-label={`Select template ${item.templateId} version ${item.version}`} onClick={() => selectTemplate(item)}>{item.version}</button></td><td>{item.title} · {item.reason}</td><td>{item.createdAtUtc}</td></tr>)}</tbody></table></div>{pageControls("versions", "Load template versions", "Load older template versions", "Refresh template versions")}</section>}
    <form className="panel" aria-label="Rule template editor" onSubmit={event => { event.preventDefault(); prepareLibrary(); }}><h3>{draft.expectedVersion ? "Publish the next immutable version" : "Create a library template"}</h3>
      {catalogError && <p role="alert">{catalogError}<button type="button" onClick={() => void loadCatalog()}>Retry product catalog</button></p>}
      {catalog && <button type="button" disabled={busy} onClick={() => void loadCatalog()}>Refresh product catalog</button>}
      <div className="template-fields"><label>Product signal<select aria-label="Product signal" value={draft.source} disabled={busy || !catalog} onChange={event => { const item = catalog?.items.find(p => p.name === event.target.value); if (!item) return; const { unit: _unit, ...values } = item; setDraft({ ...draft, source: item.name, values: { ...values, name: item.kind === "metric_threshold" ? "" : item.name, enabled: true } }); }}>{catalog?.items.map(item => <option key={item.name} value={item.name}>{ruleSignalLabel(item.kind, item.metricId)} ({item.unit})</option>)}</select></label>
        <label>Template title<input aria-label="Template title" value={draft.title} maxLength={256} required disabled={busy} onChange={event => setDraft({ ...draft, title: event.target.value })} /></label>
        {draft.values && <><label>Intended rule name<input aria-label="Intended rule name" required maxLength={128} pattern="[A-Za-z0-9][A-Za-z0-9._-]*" value={draft.values.name} disabled={busy || draft.values.kind === "collector_health"} onChange={event => patch({ name: event.target.value })} /></label>
          {(["threshold", "hysteresis", "confirmationCount", "confirmationSeconds", "evaluationSeconds", "clearConfirmationCount"] as const).map(key => { const values = draft.values; if (!values) return null; return <label key={key}>{({ threshold: "Threshold", hysteresis: "Hysteresis", confirmationCount: "Fire observations", confirmationSeconds: "Confirmation seconds", evaluationSeconds: "Evaluation seconds", clearConfirmationCount: "Clear observations" })[key]}<input aria-label={({ threshold: "Threshold", hysteresis: "Hysteresis", confirmationCount: "Fire observations", confirmationSeconds: "Confirmation seconds", evaluationSeconds: "Evaluation seconds", clearConfirmationCount: "Clear observations" })[key]} type="number" step={key === "threshold" || key === "hysteresis" ? "any" : 1} min={0} required value={values[key]} disabled={busy || values.kind === "collector_health" && key !== "clearConfirmationCount" || (values.kind !== "metric_threshold" && values.kind !== "blocking_duration_v1" && values.kind !== "deadlock_count_v1") && (key === "threshold" || key === "hysteresis")} onChange={event => patch({ [key]: Number(event.target.value) })} /></label>; })}
          <label>Comparison<select aria-label="Comparison" value={draft.values.comparison} disabled={busy || !["metric_threshold", "blocking_duration_v1", "deadlock_count_v1"].includes(draft.values.kind)} onChange={event => patch({ comparison: event.target.value as RuleValues["comparison"] })}><option value="greater_than">Greater than</option><option value="greater_than_or_equal">At least</option>{!["greater_than", "greater_than_or_equal"].includes(draft.values.comparison) && <option value={draft.values.comparison}>{draft.values.comparison}</option>}</select></label>
          <label className="template-checkbox"><input type="checkbox" checked={draft.values.enabled} disabled={busy} onChange={event => patch({ enabled: event.target.checked })} />Proposal enabled</label></>}
        <label className="template-reason">Template reason<textarea aria-label="Template reason" required maxLength={512} value={draft.reason} disabled={busy} onChange={event => setDraft({ ...draft, reason: event.target.value })} /></label></div>
      {draftAlreadyPublished && <p>This draft was published. Choose Edit template version or New template draft before another library write.</p>}{draft.values && ruleExplanation(draft.values.kind) && <p>{ruleExplanation(draft.values.kind)}</p>}<p className="table-note">A template title is independent of its exact target rule name. Different names never rename another rule. Safe reasons use at most 512 UTF-8 bytes. The server validates the approved catalog.</p>
      <div className="form-actions"><button type="submit" disabled={blocked || !product || !draft.values || draftAlreadyPublished || draft.expectedVersion !== undefined && !draftCanEdit}>{draft.expectedVersion ? "Publish template version" : "Save template"}</button><button type="button" disabled={blocked} onClick={() => { const fresh = newDraft(); if (catalog?.items[0]) { const first = catalog.items[0], { unit: _unit, ...values } = first; setDraft({ ...fresh, source: first.name, values: { ...values, name: first.kind === "metric_threshold" ? "" : first.name, enabled: true } }); } else setDraft(fresh); }}>New template draft</button></div></form>
    {template && <form className="panel" aria-label="Group deployment preview" onSubmit={event => { event.preventDefault(); if (blocked || !validNotificationReason(deploymentReason)) return; try { const group = parseRuleGroup({ kind: groupKind, value: groupLabel.trim().toLowerCase() }); void send({ kind: "preview", body: { operationId: crypto.randomUUID(), deploymentId: crypto.randomUUID(), templateId: template.templateId, templateVersion: template.version, group, mode, reason: deploymentReason } }); } catch { update(old => ({ ...old, message: "Choose a normalized environment or tag label and a safe deployment reason." })); } }}><h3>Preview a complete group</h3><div className="template-fields"><label>Group kind<select aria-label="Group kind" disabled={busy} value={groupKind} onChange={event => setGroupKind(event.target.value as "environment" | "tag")}><option value="environment">Environment</option><option value="tag">Tag</option></select></label><label>Group label<input aria-label="Group label" required maxLength={64} disabled={busy} value={groupLabel} onChange={event => setGroupLabel(event.target.value)} /></label><label>Deployment mode<select aria-label="Deployment mode" disabled={busy} value={mode} onChange={event => setMode(event.target.value as "create_missing" | "replace_named")}><option value="create_missing">Create missing exact names</option><option value="replace_named">Replace compatible exact names</option></select></label><label className="template-reason">Deployment reason<textarea aria-label="Deployment reason" required disabled={busy} maxLength={512} value={deploymentReason} onChange={event => setDeploymentReason(event.target.value)} /></label></div><p>The server resolves every matching configured non-retired member and verifies all grants. Empty or over-100 groups are refused; no truncated cohort is applied. Other names and incompatible signals remain unchanged.</p><button type="submit" disabled={blocked}>Preview group deployment</button></form>}
    <section className="panel" aria-label="Deployment listing"><h3>Received deployments</h3><div className="table-scroll" role="region" tabIndex={0} aria-label="Received deployments"><table><caption>Fully authorized jobs in the received listing</caption><thead><tr><th scope="col">Deployment</th><th scope="col">Members / state</th></tr></thead><tbody>{pages.deployments.pages.flatMap(page => page.items as DeploymentSummary[]).map(item => <tr key={item.deploymentId}><td><button type="button" className="table-link" aria-label={`Select deployment ${item.deploymentId} · ${item.templateTitle} · ${item.mode}`} aria-pressed={deploymentId === item.deploymentId} onClick={() => selectJob(item.deploymentId)}>{item.templateTitle} · {item.mode}</button></td><td>{item.memberCount} members · {item.pendingCount} pending · {item.sealed ? "sealed" : "open"}</td></tr>)}</tbody></table></div>{pageControls("deployments", "Load deployments", "Load older deployments", "Refresh deployment listing")}</section>
    {job && <section className="panel" aria-label="Current deployment status"><h3>Current deployment status</h3><p>Deployment {job.deploymentId} · revision {job.revision} · {job.mode} · {job.items.length} complete frozen members · {job.pendingCount} pending · {job.sealed ? "sealed" : "open"}.</p><p>Server as of {job.serverNowUtc}. Preview {job.previewAtUtc}; fresh application expires {job.expiresAtUtc}. {expired ? "The preview is expired; a new preview is required for fresh application. Exact uncertain retries keep their original identity." : "Preview expiry is not a guarantee of successful application."}</p>
      <section aria-label="Frozen deployment settings"><h4>{job.template.title} · immutable version {job.template.version}</h4><p>Frozen template {job.template.templateId}; this deployment keeps these settings even when the selected library version changes.</p><Settings values={job.template.values} /><details><summary>Deployment source digests</summary><p>Product {job.template.sourceTemplateName} · catalog digest {job.template.catalogDigest} · immutable version digest {job.template.versionDigest}.</p></details></section>
      <label>Operation reason<textarea aria-label="Operation reason" maxLength={512} value={operationReason} disabled={busy} onChange={event => setOperationReason(event.target.value)} /></label>
      <div className="form-actions"><button type="button" disabled={blocked || job.sealed || !job.pendingCount || expired || !validNotificationReason(operationReason)} onClick={() => { if (blocked || job.sealed || !job.pendingCount || BigInt(Date.now()) * 1000n >= utcMicroseconds(job.expiresAtUtc) || !validNotificationReason(operationReason)) return; void send({ kind: "apply", deploymentId: job.deploymentId, body: { operationId: crypto.randomUUID(), expectedRevision: job.revision, reason: operationReason } }); }}>Apply next batch</button><button type="button" disabled={blocked || job.sealed || !validNotificationReason(operationReason)} onClick={() => { if (!blocked && !job.sealed && validNotificationReason(operationReason)) setStopProposal({ kind: "stop", deploymentId: job.deploymentId, body: { operationId: crypto.randomUUID(), expectedRevision: job.revision, reason: operationReason } }); }}>Stop remaining deployment</button><button type="button" disabled={blocked || !job.sealed || job.mode === "rollback" || !job.items.some(item => item.status === "applied") || !validNotificationReason(operationReason)} onClick={() => { if (!blocked && job.sealed && job.mode !== "rollback" && validNotificationReason(operationReason)) void send({ kind: "rollback", sourceDeploymentId: job.deploymentId, body: { operationId: crypto.randomUUID(), deploymentId: crypto.randomUUID(), reason: operationReason } }); }}>Preview rollback</button></div>
      {stopProposal && <section aria-label="Stop deployment confirmation"><p>Stop remaining pending members in deployment {"deploymentId" in stopProposal ? stopProposal.deploymentId : ""}. Already applied changes remain; stopping does not roll them back.</p><button type="button" disabled={blocked} onClick={() => { if (!blocked) void send(stopProposal); }}>Confirm stop</button><button type="button" disabled={busy} onClick={() => setStopProposal(undefined)}>Close stop confirmation</button></section>}
      {job.mode === "rollback" && <p>Rollback restores guarded prior settings or disables newly created rules; it never deletes rules or retracts sent notifications. Later manual changes conflict even when settings appear equal.</p>}
      <div className="table-scroll" role="region" tabIndex={0} aria-label="Frozen deployment members"><table><caption>Per-server outcomes and separate current rule evidence</caption><thead><tr><th scope="col">Server / decision</th><th scope="col">Source / result</th><th scope="col">Current rule readback</th></tr></thead><tbody>{job.items.map(item => <tr key={item.targetId}><td>{item.targetName}<small>{item.targetId} · {item.decision}</small><details><summary>Before and proposed settings for {item.targetName}</summary>{item.beforeValues ? <><strong>Captured before</strong><Settings values={item.beforeValues} /></> : <p>No rule existed under the exact intended name at preview.</p>}<strong>Proposed</strong><Settings values={item.desiredValues} /></details></td><td>{item.compatibility === "unavailable" ? item.desiredValues.enabled ? <p>Enable a compatible {ruleSignalLabel(item.desiredValues.kind, item.desiredValues.metricId)} collection schedule for the current server configuration, then create a new preview.</p> : <p>Collection coverage is unavailable. This disabled proposal can proceed without enabling a schedule; diagnostic coverage remains unavailable.</p> : <p>Source schedule compatible at preview; this does not establish diagnostic health.</p>}{item.compatibilityReason && <details><summary>Recorded source reason</summary><p>{item.compatibilityReason}</p></details>}{item.status}<small>{item.resultCode} · applied revision {item.resultRevision ?? "none"}</small></td><td>{item.currentRuleRevision === null ? "No current rule received" : `Current revision ${item.currentRuleRevision}`}<small>{item.matchesApplied === true ? "Matches applied configuration at this read" : item.matchesApplied === false ? "Current configuration differs from the applied result" : "No applied configuration match established"}</small>{item.currentValues && <small>{item.currentValues.name}: {item.currentValues.enabled ? "enabled" : "disabled"} · threshold {item.currentValues.threshold}</small>}</td></tr>)}</tbody></table></div>
      <section aria-label="Immutable deployment history"><h4>Immutable deployment history</h4><p>Recorded operation sequence; equal timestamps do not establish order. Receipts do not establish current rule inheritance.</p><div className="table-scroll" role="region" tabIndex={0} aria-label="Received deployment history"><table><caption>Historical batches and stop receipts</caption><thead><tr><th scope="col">Operation</th><th scope="col">Historical result</th><th scope="col">Recorded</th></tr></thead><tbody>{pages.history.pages.flatMap(page => page.items as BatchReceipt[]).map(item => <tr key={item.operationId}><td>{item.operationId}</td><td>{item.action}: {item.items.length} processed · {item.pendingCount} then pending · {item.sealed ? "sealed" : "open"}</td><td>{item.recordedAtUtc}</td></tr>)}</tbody></table></div>{pageControls("history", "Load deployment history", "Load older deployment history", "Refresh deployment history")}</section>
    </section>}
  </section>;
}
