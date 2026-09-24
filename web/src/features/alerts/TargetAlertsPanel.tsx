import { useEffect, useMemo, useState } from "react";
import { DetailPane, EvidenceStatus } from "../../components/DiagnosticUi";
import { overviewHref, readOverviewScope } from "../overview/overviewModel";
import { acknowledgeAlert, getActiveAlerts } from "./alertApi";
import { AcknowledgementAttempts } from "./acknowledgementAttempts";
import { alertMatchesFilter } from "./alertFilter";
import type { ActiveAlert, AlertState } from "./alertTypes";

import type { AlertFilter } from "./alertFilter";

const acknowledgementAttempts = new AcknowledgementAttempts();

export function TargetAlertsPanel({ instanceId, displayName, onClose, canAcknowledge }: { readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly canAcknowledge: boolean }) {
  const [items, setItems] = useState<readonly ActiveAlert[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [reload, setReload] = useState(0);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [nextCursor, setNextCursor] = useState<string>();
  const [selectedId, setSelectedId] = useState<string>();
  const [busy, setBusy] = useState<string>();
  const [filter, setFilter] = useState<AlertFilter>("active");
  const [ruleFilter, setRuleFilter] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setItems([]);
    setNextCursor(undefined);
    setSelectedId(undefined);
    setError(undefined);
    setMessage(undefined);
    void getActiveAlerts(instanceId, controller.signal)
      .then((page) => { if (!controller.signal.aborted) { setItems(page.items); setNextCursor(page.nextCursor); setError(undefined); setLoading(false); } })
      .catch((failure: unknown) => { if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : "Alerts are unavailable."); setLoading(false); } });
    return () => controller.abort();
  }, [instanceId, reload]);

  const visible = useMemo(() => items.filter((item) => {
    return alertMatchesFilter(filter, item.state) && (ruleFilter === "" || item.ruleId.toLowerCase().includes(ruleFilter.toLowerCase()));
  }), [filter, items, ruleFilter]);
  const selected = visible.find((item) => item.alertId === selectedId) ?? visible[0];
  const scope = readOverviewScope(location.hash);

  useEffect(() => {
    if (selected && selected.alertId !== selectedId) setSelectedId(selected.alertId);
  }, [selected, selectedId]);

  async function nextPage() {
    if (!nextCursor || loadingMore) return;
    const controller = new AbortController();
    setLoadingMore(true);
    try {
      const page = await getActiveAlerts(instanceId, controller.signal, 100, nextCursor);
      setItems((current) => [...current, ...page.items.filter((item) => !current.some((candidate) => candidate.alertId === item.alertId))]);
      setNextCursor(page.nextCursor);
      setError(undefined);
    } catch (failure: unknown) {
      setError(failure instanceof Error ? failure.message : "Alerts are unavailable.");
    } finally {
      setLoadingMore(false);
    }
  }

  async function acknowledge(item: ActiveAlert) {
    const isFiring = item.state === "firing";
    if (!isFiring || !canAcknowledge) return;
    const controller = new AbortController();
    setBusy(item.alertId);
    setMessage(undefined);
    try {
      await acknowledgeAlert(instanceId, item.alertId, acknowledgementAttempts.tokenFor(instanceId, item.alertId, item.firstObservedUtc), controller.signal);
      acknowledgementAttempts.complete(instanceId, item.alertId, item.firstObservedUtc);
      setItems((current) => current.map((candidate) => candidate.alertId === item.alertId ? { ...candidate, state: "acknowledged" as AlertState } : candidate));
      setMessage("Acknowledgement recorded. Monitoring continues; the server-provided acknowledgement timestamp will appear on refresh.");
    } catch (error: unknown) {
      setMessage(error instanceof Error ? error.message : "The alert could not be acknowledged.");
    } finally {
      setBusy(undefined);
    }
  }

  return <section className="alerts-screen" aria-labelledby="alerts-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · {displayName}</p><h2 id="alerts-heading">Alert triage</h2><p>Review target-scoped alert state and supporting evidence. Acknowledgement records review, not resolution.</p></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    <div className="alert-filters"><label>State<select value={filter} onChange={(event) => setFilter(event.target.value as AlertFilter)}><option value="active">Active</option><option value="firing">Firing</option><option value="all">All loaded states</option></select></label><label>Rule identifier<input aria-label="Filter by rule identifier" value={ruleFilter} onChange={(event) => setRuleFilter(event.target.value)} placeholder="Search loaded rules" /></label><span>{visible.length} shown · {items.length} loaded</span></div>
    {loading ? <p role="status" className="status-message">Loading target-scoped alerts…</p> : null}
    {error ? <div role="alert" className="status-message">{error} <button className="secondary-button" type="button" onClick={() => setReload((value) => value + 1)}>Retry</button></div> : null}
    {message ? <p role="status" className="status-message">{message}</p> : null}
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Target alerts"><div className="table-card-heading"><div><h3>Alerts ({visible.length})</h3><p>Alert severity is unavailable in this projection. Rule names are shown as configured.</p></div></div><div className="table-scroll"><table><caption>Target-scoped alert observations for {displayName}</caption><thead><tr><th scope="col">State</th><th scope="col">Alert / rule</th><th scope="col">Observed value</th><th scope="col">First observed</th><th scope="col">State timestamps</th></tr></thead><tbody>{visible.map((item) => <tr className={item.alertId === selected?.alertId ? "selected-row" : ""} key={item.alertId} onClick={() => setSelectedId(item.alertId)}><td><span className={`state-pill state-${item.state}`}>{item.state}</span></td><td><button className="table-link" type="button" onClick={() => setSelectedId(item.alertId)}>{item.ruleName}</button><small>Alert {item.alertId.slice(0, 12)}…</small></td><td>{formatObservedValue(item.value)}</td><td>{formatUtc(item.firstObservedUtc)}</td><td>{formatAvailableTimestamps(item)}</td></tr>)}</tbody></table></div>{!loading && !error && visible.length === 0 ? <p className="empty-state">No alert rows match the loaded target-scoped evidence.</p> : null}<div className="pager"><span>{nextCursor ? "More bounded alerts are available." : "End of loaded alert page."}</span><button className="secondary-button" type="button" disabled={!nextCursor || loadingMore} onClick={() => void nextPage()}>{loadingMore ? "Loading…" : "Load more"}</button></div></section>
      {loading ? <div className="detail-pane status-message">Loading alert evidence…</div> : selected ? <DetailPane title={selected.ruleName} subtitle={`Alert ${selected.alertId} · ${displayName}`} actions={selected.state === "firing" && canAcknowledge ? <button className="primary" type="button" disabled={busy === selected.alertId} onClick={() => void acknowledge(selected)}>{busy === selected.alertId ? "Acknowledging…" : "Acknowledge"}</button> : undefined}><EvidenceStatus label={selected.state} detail={selected.deliverySuppressed ? "Delivery suppressed" : "Delivery enabled"} tone={selected.state === "firing" ? "critical" : selected.state === "acknowledged" ? "warning" : "neutral"} /><p className="detail-note">Acknowledgement records review. Monitoring continues and state changes remain server-owned.</p>{selected.state === "firing" && !canAcknowledge ? <p className="detail-note">An Operator or Target Administrator role on this server is required to acknowledge this alert.</p> : null}<section className="alert-observed"><h3>Observed value</h3><strong>{formatObservedValue(selected.value)}</strong></section><section className="alert-chronology"><h3>Available state timestamps</h3><TimelineItem label="First observed" value={selected.firstObservedUtc} /><TimelineItem label="Fired" value={selected.firedUtc} /><TimelineItem label="Acknowledged" value={selected.acknowledgedUtc} /></section><section className="related-evidence"><h3>Related evidence</h3><a href={overviewHref(scope, "activity", instanceId)}>Open activity <span aria-hidden="true">→</span></a><a href={overviewHref(scope, "deadlocks", instanceId)}>Open deadlocks <span aria-hidden="true">→</span></a><p>Related observations may share a time window without sharing a cause.</p></section><p className="table-note">Rule identifier: {selected.ruleId} · Observed alert state is not a severity classification.</p></DetailPane> : <div className="detail-pane empty-state">Select an alert to inspect its available evidence.</div>}
    </div>
  </section>;
}

function TimelineItem({ label, value }: { readonly label: string; readonly value?: string }) {
  return <div className={value ? "timeline-item" : "timeline-item is-unavailable"}><span className="timeline-dot" aria-hidden="true" /><div><strong>{label}</strong><span>{value ? formatUtc(value) : "Not returned by this alert state"}</span></div></div>;
}

function formatObservedValue(value: number | null | undefined): string {
  return value === undefined || value === null ? "Collector health value unavailable" : value.toLocaleString();
}

function formatAvailableTimestamps(item: ActiveAlert): string {
  const values = [item.firedUtc ? `fired ${formatUtc(item.firedUtc)}` : undefined, item.acknowledgedUtc ? `acknowledged ${formatUtc(item.acknowledgedUtc)}` : undefined].filter((value): value is string => value !== undefined);
  return values.length ? values.join(" · ") : "No later state timestamp";
}

function formatUtc(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? "Invalid timestamp" : parsed.toISOString().replace("T", " ").replace(".000Z", " UTC");
}
