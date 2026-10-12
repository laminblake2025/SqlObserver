import { useEffect, useMemo, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { DetailPane, EvidenceStatus } from "../../components/DiagnosticUi";
import { overviewHref, readOverviewScope } from "../overview/overviewModel";
import { ApiRequestError } from "../../apiClient.ts";
import { getMyAccess, canAcknowledgeAlert } from "../targets/meApi.ts";
import { useAlertInbox } from "./useAlertInbox.ts";
import { useAlertAcknowledgement } from "./useAlertAcknowledgement.ts";
import { AlertTriageControls } from "./AlertTriageControls.tsx";
import "../../components/diagnosticWorkflow.css";
import { alertMatchesFilter } from "./alertFilter";
import type { ActiveAlert } from "./alertTypes";
import { targetAlertHref } from "../../components/commandPaletteModel";
import { alertActivityHref } from "./fleetAlertGrouping";
import { alertReasonSummary } from "./alertRuleSignals.ts";
import { AlertSnoozeControls } from "./AlertSnoozeControls.tsx";
import { AlertAssignmentControls } from "./AlertAssignmentControls.tsx";
import { useAlertMutationLane } from "./useAlertMutationLane.ts";

import type { AlertFilter } from "./alertFilter";

export function TargetAlertsPanel({ instanceId, displayName, onClose, canAcknowledge, canManageRules, canManageDestinations, refresh, selectedAlertId, targetRevision }: { readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly canAcknowledge: boolean; readonly canManageRules: boolean; readonly canManageDestinations: boolean; readonly refresh: number; readonly selectedAlertId?: string; readonly targetRevision?: number }) {
  const formatTime = useTimeFormatter();
  const scope = readOverviewScope(location.hash);
  const context = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.range, scope.from, scope.to, scope.databaseId, scope.compare]);
  const requestedId = selectedAlertId?.toLowerCase();
  const requestedKey = requestedId ? `${instanceId.toLowerCase()}:${requestedId}` : undefined;
  const inbox = useAlertInbox("target", instanceId, context, refresh, requestedKey);
  const items = inbox.rows.map(item => ({ ...item, targetName: displayName }));
  const loading = inbox.loading, error = inbox.error;
  const selectedId = requestedId;
  const [filter, setFilter] = useState<AlertFilter>("active");
  const [ruleFilter, setRuleFilter] = useState("");
  const [recheckedAccess, setRecheckedAccess] = useState<{ key: string; allowed: boolean }>();
  useEffect(() => { if (requestedId) { setFilter("active"); setRuleFilter(""); } }, [instanceId, requestedId]);
  const visible = useMemo(() => items.filter(item => alertMatchesFilter(filter, item.state) &&
    (ruleFilter === "" || item.ruleId.toLowerCase().includes(ruleFilter.toLowerCase()))), [filter, items, ruleFilter]);
  const selected = visible.find(item => item.alertId.toLowerCase() === selectedId) ?? (selectedId ? undefined : visible[0]);
  const loadedSelected = selectedId ? items.find(item => item.alertId.toLowerCase() === selectedId) : undefined;
  const accessKey = JSON.stringify([context, inbox.denial, selected?.targetId]);
  useEffect(() => {
    const controller = new AbortController();
    if (inbox.denial > 0 && selected) void getMyAccess(instanceId, controller.signal)
      .then(access => { if (!controller.signal.aborted) setRecheckedAccess({ key: accessKey, allowed: canAcknowledgeAlert(access, instanceId) }); })
      .catch(failure => { if (!controller.signal.aborted) {
        setRecheckedAccess({ key: accessKey, allowed: false });
        if (failure instanceof ApiRequestError && [401, 403].includes(failure.status)) inbox.deny();
      } });
    return () => controller.abort();
  }, [accessKey]);
  const mayAcknowledge = canAcknowledge && (inbox.denial === 0 || recheckedAccess?.key === accessKey && recheckedAccess.allowed);
  const notificationKey = JSON.stringify([context, inbox.denial, selected?.alertId.toLowerCase(), selected?.firstObservedUtc, selected?.episodeId]);
  const mutationLane = useAlertMutationLane(notificationKey);
  const mutationBusy = mutationLane.busy(notificationKey);
  const notificationBusy = mutationLane.kind(notificationKey) === "snooze" || mutationLane.kind(notificationKey) === "assignment";
  const acknowledgement = useAlertAcknowledgement(context, inbox.denial, selected, mayAcknowledge, inbox.pinMutation, inbox.deny, inbox.acknowledged, mutationLane, notificationKey);
  const busy = acknowledgement.busy, message = acknowledgement.message;
  const selectAlert = (item: ActiveAlert) => { location.hash = targetAlertHref(scope, instanceId, item.alertId); };
  return <section className="alerts-screen target-alerts-screen" aria-labelledby="alerts-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · {displayName}</p><h2 id="alerts-heading">Alert triage</h2><p>Review target-scoped alert state and supporting evidence. Acknowledgement records review, not resolution.</p></div><div className="screen-actions">{canManageRules ? <><a className="secondary-button" href={overviewHref(scope, "rules", instanceId)}>View rules</a><a className="secondary-button" href={overviewHref(scope, "maintenance", instanceId)}>Maintenance windows</a></> : null}{canManageDestinations ? <a className="secondary-button" href={overviewHref(scope, "destinations", instanceId)}>Destinations</a> : null}<button className="secondary-button" onClick={onClose} type="button">Close</button></div></div>
    <div className="alert-filters"><label>State<select value={filter} onChange={(event) => setFilter(event.target.value as AlertFilter)}><option value="active">Active</option><option value="firing">Firing</option><option value="all">All loaded states</option></select></label><label>Rule identifier<input aria-label="Filter by rule identifier" value={ruleFilter} onChange={(event) => setRuleFilter(event.target.value)} placeholder="Search loaded rules" /></label><span>{visible.length} shown · {items.length} loaded</span></div>
    {loading ? <p role="status" className="status-message">Loading target-scoped alerts…</p> : null}
    {message ? <p role="status" className="status-message">{message}</p> : null}
    <p className="table-note">URL lookup reads at most five pages / 500 rows; missing or resolved alerts may be unavailable. First-observed timestamps identify this received cycle only; no server episode precondition is inferred.</p>
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Target alerts"><div className="table-card-heading"><div><h3>Alerts ({visible.length})</h3><p>Alert severity is unavailable in this projection. Rule names are shown as configured.</p></div></div><div className="table-scroll" tabIndex={0} role="region" aria-label="Target alert observations"><table><caption>Target-scoped alert observations for {displayName}</caption><thead><tr><th scope="col">State</th><th scope="col">Alert / rule</th><th scope="col">Observed value</th><th scope="col">First observed</th><th scope="col">State timestamps</th></tr></thead><tbody>{visible.map((item) => <tr className={item.alertId.toLowerCase() === selected?.alertId.toLowerCase() ? "selected-row" : ""} key={item.alertId.toLowerCase()} onClick={() => selectAlert(item)}><td><span className={`state-pill state-${item.state}`}>{item.state}</span></td><td><button className="table-link" type="button" aria-pressed={item.alertId.toLowerCase() === selected?.alertId.toLowerCase()} onClick={() => selectAlert(item)}>{item.ruleName}</button><small>Alert {item.alertId.slice(0, 12)}…</small></td><td>{formatObservedValue(item.value)}</td><td>{formatTime(item.firstObservedUtc)}</td><td>{formatAvailableTimestamps(item, formatTime)}</td></tr>)}</tbody></table></div>{!loading && !error && visible.length === 0 ? <p className="empty-state">No alert rows match the loaded target-scoped evidence.</p> : null}<AlertTriageControls inbox={inbox} label="server alerts" mutationBusy={notificationBusy} mutationBlocked={() => ["snooze", "assignment"].includes(mutationLane.kind(notificationKey) ?? "")} onLatest={() => { if (["snooze", "assignment"].includes(mutationLane.kind(notificationKey) ?? "")) return; acknowledgement.cancel(); inbox.latest(); }} /></section>
      {selected ? <DetailPane title={selected.ruleName} subtitle={`Alert ${selected.alertId} · ${displayName}`} actions={selected.state === "firing" && mayAcknowledge ? <button className="primary" type="button" disabled={busy || mutationBusy} onClick={() => { if (!mutationBusy) acknowledgement.acknowledge(); }}>{busy ? "Acknowledging…" : "Acknowledge"}</button> : undefined}><EvidenceStatus label={selected.state} detail={selected.deliverySuppressed ? "Received row: delivery suppressed" : "Received row: delivery enabled"} tone={selected.state === "firing" ? "critical" : selected.state === "acknowledged" ? "warning" : "neutral"} /><div className="alert-triage-actions">{busy && <button type="button" onClick={acknowledgement.cancel}>Cancel acknowledgement</button>}{acknowledgement.canRetry && <button type="button" disabled={mutationBusy} onClick={() => { if (!mutationBusy) acknowledgement.retry(); }}>Retry acknowledgement</button>}</div><p className="detail-note">Acknowledgement records review. Monitoring continues and state changes remain server-owned.</p>{selected.state === "firing" && !mayAcknowledge ? <p className="detail-note">An Operator or Target Administrator role on this server is required to acknowledge this alert.</p> : null}<p className="detail-note">{alertReasonSummary(selected.reason, selected.value) ?? "No reason was returned for this alert."}</p><section className="alert-observed"><h3>Observed value</h3><strong>{formatObservedValue(selected.value)}</strong></section><section className="alert-chronology"><h3>Available state timestamps</h3><TimelineItem label="First observed" value={selected.firstObservedUtc} /><TimelineItem label="Fired" value={selected.firedUtc} /><TimelineItem label="Acknowledged" value={selected.acknowledgedUtc} /></section><AlertSnoozeControls key={notificationKey} selected={selected} allowed={mayAcknowledge} otherMutationBusy={mutationBusy} hold={inbox.pinMutation} deny={inbox.deny} mutationLane={mutationLane} mutationKey={notificationKey} /><AlertAssignmentControls key={`assignment:${notificationKey}`} selected={selected} allowed={mayAcknowledge} hold={inbox.hold} pinMutation={inbox.pinMutation} deny={inbox.deny} mutationLane={mutationLane} mutationKey={notificationKey} /><section className="related-evidence"><h3>Related evidence</h3><a href={alertActivityHref({ ...selected, targetId: instanceId, targetName: displayName }, scope)}>Open activity <span aria-hidden="true">→</span></a><a href={overviewHref(scope, "deadlocks", instanceId)}>Open deadlocks <span aria-hidden="true">→</span></a><p>Related observations may share a time window without sharing a cause.</p></section><p className="table-note">Rule identifier: {selected.ruleId} · Observed alert state is not a severity classification.</p></DetailPane> : <div className="detail-pane empty-state">{error ? "Selected alert evidence is unavailable. Retry the server alerts request; no other alert has been selected." : selectedId ? loadedSelected ? <>The selected alert is hidden by the current filters. Clear the filters to show this alert. <button type="button" className="secondary-button" onClick={() => { setFilter("all"); setRuleFilter(""); }}>Show selected alert</button></> : "The requested alert is not present in the bounded current server alerts. Browse further or refresh; no other alert has been selected." : "Select an alert to inspect its available evidence."}</div>}
    </div>
  </section>;
}

function TimelineItem({ label, value }: { readonly label: string; readonly value?: string }) {
  const formatTime = useTimeFormatter();
  return <div className={value ? "timeline-item" : "timeline-item is-unavailable"}><span className="timeline-dot" aria-hidden="true" /><div><strong>{label}</strong><span>{value ? formatTime(value) : "Not returned by this alert state"}</span></div></div>;
}

function formatObservedValue(value: number | null | undefined): string {
  return value === undefined || value === null ? "Collector health value unavailable" : value.toLocaleString();
}

function formatAvailableTimestamps(item: ActiveAlert, formatTime: (value: string) => string): string {
  const values = [item.firedUtc ? `fired ${formatTime(item.firedUtc)}` : undefined, item.acknowledgedUtc ? `acknowledged ${formatTime(item.acknowledgedUtc)}` : undefined].filter((value): value is string => value !== undefined);
  return values.length ? values.join(" · ") : "No later state timestamp";
}
