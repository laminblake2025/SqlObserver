import { useEffect, useMemo, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { DetailPane, EvidenceStatus } from "../../components/DiagnosticUi";
import { overviewHref, readOverviewScope } from "../overview/overviewModel";
import { acknowledgeAlert, getActiveAlerts } from "./alertApi";
import { AcknowledgementAttempts } from "./acknowledgementAttempts";
import { alertMatchesFilter } from "./alertFilter";
import type { ActiveAlert, AlertState } from "./alertTypes";
import { targetAlertHref } from "../../components/commandPaletteModel";
import { loadTargetAlertJump } from "./targetAlertJump";
import { alertSnapshotKey } from "./fleetAlertJump";

import type { AlertFilter } from "./alertFilter";

const acknowledgementAttempts = new AcknowledgementAttempts();

export function TargetAlertsPanel({ instanceId, displayName, onClose, canAcknowledge, canManageRules, canManageDestinations, refresh, selectedAlertId }: { readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly canAcknowledge: boolean; readonly canManageRules: boolean; readonly canManageDestinations: boolean; readonly refresh: number; readonly selectedAlertId?: string }) {
  const formatTime = useTimeFormatter();
  const [receivedItems, setItems] = useState<readonly ActiveAlert[]>([]);
  const [evidenceKey, setEvidenceKey] = useState("");
  const [successfulGeneration, setSuccessfulGeneration] = useState("");
  const [isLoading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [reload, setReload] = useState(0);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [nextCursor, setNextCursor] = useState<string>();
  const [localSelectedId, setSelectedId] = useState<string>();
  const [busy, setBusy] = useState<string>();
  const [filter, setFilter] = useState<AlertFilter>("active");
  const [ruleFilter, setRuleFilter] = useState("");
  const [snapshotUtc, setSnapshotUtc] = useState<string>();
  const pagingRequest = useRef<AbortController | undefined>(undefined);
  const acknowledgementRequest = useRef<AbortController | undefined>(undefined);
  const requestedId = selectedAlertId?.toLowerCase();
  const selectedId = requestedId ?? localSelectedId;
  const generationKey = JSON.stringify([instanceId.toLowerCase(), reload, refresh]);
  const requestKey = JSON.stringify([generationKey, requestedId]);
  const requestIdentity = useRef(requestKey);
  requestIdentity.current = requestKey;
  const targetMatches = evidenceKey === generationKey;
  const items = targetMatches ? receivedItems : [];
  const loading = isLoading || !targetMatches;

  useEffect(() => {
    setSelectedId(undefined);
    if (requestedId) { setFilter("active"); setRuleFilter(""); }
  }, [instanceId, requestedId]);

  useEffect(() => {
    const controller = new AbortController();
    pagingRequest.current?.abort();
    pagingRequest.current = undefined;
    setLoadingMore(false);
    setError(undefined);
    setMessage(undefined);
    const sameSnapshot = evidenceKey === generationKey && successfulGeneration === generationKey;
    if (sameSnapshot && (!requestedId || receivedItems.some(item => item.alertId.toLowerCase() === requestedId))) {
      setLoading(false);
      return () => controller.abort();
    }
    setLoading(true);
    if (!sameSnapshot) { setItems([]); setNextCursor(undefined); setSnapshotUtc(undefined); }
    setEvidenceKey(generationKey);
    void loadTargetAlertJump(async cursor => {
      if (controller.signal.aborted) throw new Error("Alert loading was cancelled.");
      const page = await getActiveAlerts(instanceId, controller.signal, 100, cursor);
      if (controller.signal.aborted) throw new Error("Alert loading was cancelled.");
      return page;
    }, instanceId, requestedId)
      .then((page) => {
        if (controller.signal.aborted || requestIdentity.current !== requestKey) return;
        setItems(page.items); setSuccessfulGeneration(generationKey); setNextCursor(page.nextCursor); setSnapshotUtc(page.snapshotUtc); setError(undefined); setLoading(false);
        if (requestedId && !page.found) setMessage("That alert is no longer in the loaded current server alerts. Browse further or refresh; no other alert has been selected.");
      })
      .catch((failure: unknown) => { if (!controller.signal.aborted && requestIdentity.current === requestKey) { setError(failure instanceof Error ? failure.message : "Alerts are unavailable."); setLoading(false); } });
    return () => { controller.abort(); pagingRequest.current?.abort(); };
  }, [requestKey]);

  const visible = useMemo(() => items.filter((item) => {
    return alertMatchesFilter(filter, item.state) && (ruleFilter === "" || item.ruleId.toLowerCase().includes(ruleFilter.toLowerCase()));
  }), [filter, items, ruleFilter]);
  const selected = visible.find((item) => item.alertId.toLowerCase() === selectedId) ?? (selectedId ? undefined : visible[0]);
  const loadedSelected = selectedId ? items.find(item => item.alertId.toLowerCase() === selectedId) : undefined;
  const scope = readOverviewScope(location.hash);
  const selectAlert = (item: ActiveAlert) => { location.hash = targetAlertHref(scope, instanceId, item.alertId); };
  const actionKey = JSON.stringify([requestKey, selected?.alertId.toLowerCase(), selected?.firstObservedUtc]);
  const actionIdentity = useRef(actionKey);
  actionIdentity.current = actionKey;

  useEffect(() => {
    acknowledgementRequest.current?.abort();
    acknowledgementRequest.current = undefined;
    setBusy(undefined);
    return () => { acknowledgementRequest.current?.abort(); acknowledgementRequest.current = undefined; };
  }, [actionKey]);

  async function nextPage() {
    if (!nextCursor || loading || pagingRequest.current) return;
    const controller = new AbortController();
    pagingRequest.current = controller;
    setLoadingMore(true);
    try {
      const page = await getActiveAlerts(instanceId, controller.signal, 100, nextCursor);
      if (controller.signal.aborted || requestIdentity.current !== requestKey) return;
      if (page.targetId.toLowerCase() !== instanceId.toLowerCase()) throw new Error("Alert evidence did not match the selected server.");
      if (alertSnapshotKey(page.snapshotUtc) !== alertSnapshotKey(snapshotUtc)) throw new Error("Alert snapshot changed while paging. Refresh the server alerts.");
      setItems((current) => [...current, ...page.items.filter((item) => !current.some((candidate) => candidate.alertId.toLowerCase() === item.alertId.toLowerCase()))]);
      setNextCursor(page.nextCursor);
      setError(undefined);
      if (requestedId && page.items.some(item => item.alertId.toLowerCase() === requestedId)) setMessage(undefined);
    } catch (failure: unknown) {
      if (!controller.signal.aborted && requestIdentity.current === requestKey) setError(failure instanceof Error ? failure.message : "Alerts are unavailable.");
    } finally {
      if (pagingRequest.current === controller) pagingRequest.current = undefined;
      if (!controller.signal.aborted && requestIdentity.current === requestKey) setLoadingMore(false);
    }
  }

  async function acknowledge(item: ActiveAlert) {
    const isFiring = item.state === "firing";
    if (!isFiring || !canAcknowledge || acknowledgementRequest.current || item.alertId.toLowerCase() !== selected?.alertId.toLowerCase()) return;
    const controller = new AbortController();
    acknowledgementRequest.current = controller;
    setBusy(item.alertId);
    setMessage(undefined);
    try {
      await acknowledgeAlert(instanceId, item.alertId, acknowledgementAttempts.tokenFor(instanceId, item.alertId, item.firstObservedUtc), controller.signal);
      if (controller.signal.aborted || actionIdentity.current !== actionKey) return;
      acknowledgementAttempts.complete(instanceId, item.alertId, item.firstObservedUtc);
      setItems((current) => current.map((candidate) => candidate.alertId.toLowerCase() === item.alertId.toLowerCase() && candidate.firstObservedUtc === item.firstObservedUtc ? { ...candidate, state: "acknowledged" as AlertState } : candidate));
      setMessage("Acknowledgement recorded. Monitoring continues; the server-provided acknowledgement timestamp will appear on refresh.");
    } catch (error: unknown) {
      if (!controller.signal.aborted && actionIdentity.current === actionKey) setMessage(error instanceof Error ? error.message : "The alert could not be acknowledged.");
    } finally {
      if (acknowledgementRequest.current === controller) acknowledgementRequest.current = undefined;
      if (!controller.signal.aborted && actionIdentity.current === actionKey) setBusy(undefined);
    }
  }

  return <section className="alerts-screen" aria-labelledby="alerts-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · {displayName}</p><h2 id="alerts-heading">Alert triage</h2><p>Review target-scoped alert state and supporting evidence. Acknowledgement records review, not resolution.</p></div><div className="screen-actions">{canManageRules ? <><a className="secondary-button" href={overviewHref(scope, "rules", instanceId)}>View rules</a><a className="secondary-button" href={overviewHref(scope, "maintenance", instanceId)}>Maintenance windows</a></> : null}{canManageDestinations ? <a className="secondary-button" href={overviewHref(scope, "destinations", instanceId)}>Destinations</a> : null}<button className="secondary-button" onClick={onClose} type="button">Close</button></div></div>
    <div className="alert-filters"><label>State<select value={filter} onChange={(event) => setFilter(event.target.value as AlertFilter)}><option value="active">Active</option><option value="firing">Firing</option><option value="all">All loaded states</option></select></label><label>Rule identifier<input aria-label="Filter by rule identifier" value={ruleFilter} onChange={(event) => setRuleFilter(event.target.value)} placeholder="Search loaded rules" /></label><span>{visible.length} shown · {items.length} loaded</span></div>
    {loading ? <p role="status" className="status-message">Loading target-scoped alerts…</p> : null}
    {error ? <div role="alert" className="status-message">{error} <button className="secondary-button" type="button" onClick={() => setReload((value) => value + 1)}>Retry</button></div> : null}
    {message ? <p role="status" className="status-message">{message}</p> : null}
    {!loading && !error ? <p className="table-note">Alerts show the latest target-scoped state, independently of the history window. Repository snapshot: {snapshotUtc ? formatTime(snapshotUtc) : "Not returned"}. First observed and state-transition timestamps appear with each alert. URL lookup reads at most five pages / 500 rows; missing or resolved alerts may be unavailable.</p> : null}
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Target alerts"><div className="table-card-heading"><div><h3>Alerts ({visible.length})</h3><p>Alert severity is unavailable in this projection. Rule names are shown as configured.</p></div></div><div className="table-scroll" tabIndex={0} role="region" aria-label="Target alert observations"><table><caption>Target-scoped alert observations for {displayName}</caption><thead><tr><th scope="col">State</th><th scope="col">Alert / rule</th><th scope="col">Observed value</th><th scope="col">First observed</th><th scope="col">State timestamps</th></tr></thead><tbody>{visible.map((item) => <tr className={item.alertId.toLowerCase() === selected?.alertId.toLowerCase() ? "selected-row" : ""} key={item.alertId.toLowerCase()} onClick={() => selectAlert(item)}><td><span className={`state-pill state-${item.state}`}>{item.state}</span></td><td><button className="table-link" type="button" aria-pressed={item.alertId.toLowerCase() === selected?.alertId.toLowerCase()} onClick={() => selectAlert(item)}>{item.ruleName}</button><small>Alert {item.alertId.slice(0, 12)}…</small></td><td>{formatObservedValue(item.value)}</td><td>{formatTime(item.firstObservedUtc)}</td><td>{formatAvailableTimestamps(item, formatTime)}</td></tr>)}</tbody></table></div>{!loading && !error && visible.length === 0 ? <p className="empty-state">No alert rows match the loaded target-scoped evidence.</p> : null}<div className="pager"><span>{nextCursor ? "More bounded alerts are available." : "End of loaded alert page."}</span><button className="secondary-button" type="button" disabled={!nextCursor || loading || loadingMore} onClick={() => void nextPage()}>{loadingMore ? "Loading…" : "Load more"}</button></div></section>
      {loading ? <div className="detail-pane status-message">Loading alert evidence…</div> : selected ? <DetailPane title={selected.ruleName} subtitle={`Alert ${selected.alertId} · ${displayName}`} actions={selected.state === "firing" && canAcknowledge ? <button className="primary" type="button" disabled={busy !== undefined} onClick={() => void acknowledge(selected)}>{busy === selected.alertId ? "Acknowledging…" : "Acknowledge"}</button> : undefined}><EvidenceStatus label={selected.state} detail={selected.deliverySuppressed ? "Delivery suppressed" : "Delivery enabled"} tone={selected.state === "firing" ? "critical" : selected.state === "acknowledged" ? "warning" : "neutral"} /><p className="detail-note">Acknowledgement records review. Monitoring continues and state changes remain server-owned.</p>{selected.state === "firing" && !canAcknowledge ? <p className="detail-note">An Operator or Target Administrator role on this server is required to acknowledge this alert.</p> : null}<section className="alert-observed"><h3>Observed value</h3><strong>{formatObservedValue(selected.value)}</strong></section><section className="alert-chronology"><h3>Available state timestamps</h3><TimelineItem label="First observed" value={selected.firstObservedUtc} /><TimelineItem label="Fired" value={selected.firedUtc} /><TimelineItem label="Acknowledged" value={selected.acknowledgedUtc} /></section><section className="related-evidence"><h3>Related evidence</h3><a href={overviewHref(scope, "activity", instanceId)}>Open activity <span aria-hidden="true">→</span></a><a href={overviewHref(scope, "deadlocks", instanceId)}>Open deadlocks <span aria-hidden="true">→</span></a><p>Related observations may share a time window without sharing a cause.</p></section><p className="table-note">Rule identifier: {selected.ruleId} · Observed alert state is not a severity classification.</p></DetailPane> : <div className="detail-pane empty-state">{error ? "Selected alert evidence is unavailable. Retry the server alerts request; no other alert has been selected." : selectedId ? loadedSelected ? <>The selected alert is hidden by the current filters. Clear the filters to show this alert. <button type="button" className="secondary-button" onClick={() => { setFilter("all"); setRuleFilter(""); }}>Show selected alert</button></> : "The requested alert is not present in the bounded current server alerts. Browse further or refresh; no other alert has been selected." : "Select an alert to inspect its available evidence."}</div>}
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
