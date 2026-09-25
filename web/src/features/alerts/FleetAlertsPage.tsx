import { useEffect, useMemo, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { DetailPane, EvidenceStatus } from "../../components/DiagnosticUi";
import { overviewHref, readOverviewScope } from "../overview/overviewModel";
import { canAcknowledgeAlert, getMyAccess } from "../targets/meApi";
import { acknowledgeAlert, getFleetActiveAlerts } from "./alertApi";
import { AcknowledgementAttempts } from "./acknowledgementAttempts";
import { alertMatchesFilter, type AlertFilter } from "./alertFilter";
import type { FleetAlert } from "./alertTypes";

const acknowledgementAttempts = new AcknowledgementAttempts();

export function FleetAlertsPage({ refresh }: { readonly refresh: number }) {
  const { mode } = useTimeDisplay();
  const formatTime = (value?: string) => value ? formatDisplayTime(value, mode) : "Not returned";
  const [items, setItems] = useState<readonly FleetAlert[]>([]);
  const [nextCursor, setNextCursor] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string>();
  const [reload, setReload] = useState(0);
  const [filter, setFilter] = useState<AlertFilter>("active");
  const [search, setSearch] = useState("");
  const [selectedKey, setSelectedKey] = useState<string>();
  const [canAcknowledge, setCanAcknowledge] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string>();
  const moreRequest = useRef<AbortController | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    moreRequest.current?.abort();
    moreRequest.current = null;
    setLoading(true);
    setLoadingMore(false);
    setItems([]);
    setNextCursor(undefined);
    setError(undefined);
    setMessage(undefined);
    void getFleetActiveAlerts(controller.signal)
      .then(page => { if (!controller.signal.aborted) { setItems(page.items); setNextCursor(page.nextCursor); } })
      .catch((failure: unknown) => { if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Fleet alerts are unavailable."); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => { controller.abort(); moreRequest.current?.abort(); };
  }, [refresh, reload]);

  const visible = useMemo(() => items.filter(item =>
    alertMatchesFilter(filter, item.state) &&
    (search === "" || `${item.targetName} ${item.ruleName} ${item.targetId}`.toLowerCase().includes(search.toLowerCase()))
  ), [filter, items, search]);
  const selected = visible.find(item => key(item) === selectedKey) ?? visible[0];

  useEffect(() => {
    if (selected && key(selected) !== selectedKey) setSelectedKey(key(selected));
  }, [selected, selectedKey]);

  useEffect(() => {
    const controller = new AbortController();
    setCanAcknowledge(false);
    if (selected) {
      void getMyAccess(selected.targetId, controller.signal)
        .then(access => { if (!controller.signal.aborted) setCanAcknowledge(canAcknowledgeAlert(access, selected.targetId)); })
        .catch(() => { if (!controller.signal.aborted) setCanAcknowledge(false); });
    }
    return () => controller.abort();
  }, [selected?.targetId]);

  async function loadMore() {
    if (!nextCursor || loadingMore) return;
    const controller = new AbortController();
    moreRequest.current = controller;
    setLoadingMore(true);
    try {
      const page = await getFleetActiveAlerts(controller.signal, 100, nextCursor);
      if (!controller.signal.aborted) {
        setItems(current => [...current, ...page.items.filter(item => !current.some(existing => key(existing) === key(item)))]);
        setNextCursor(page.nextCursor);
        setError(undefined);
      }
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Fleet alerts are unavailable.");
    } finally { if (!controller.signal.aborted) setLoadingMore(false); if (moreRequest.current === controller) moreRequest.current = null; }
  }

  async function acknowledge(item: FleetAlert) {
    if (!canAcknowledge || item.state !== "firing" || busy) return;
    const controller = new AbortController();
    setBusy(true);
    setMessage(undefined);
    try {
      await acknowledgeAlert(item.targetId, item.alertId,
        acknowledgementAttempts.tokenFor(item.targetId, item.alertId, item.firstObservedUtc), controller.signal);
      acknowledgementAttempts.complete(item.targetId, item.alertId, item.firstObservedUtc);
      setItems(current => current.map(candidate => key(candidate) === key(item)
        ? { ...candidate, state: "acknowledged" as const } : candidate));
      setMessage("Acknowledgement recorded. Refresh to see the server-provided timestamp.");
    } catch (failure) {
      setMessage(failure instanceof Error ? failure.message : "The alert could not be acknowledged.");
    } finally { setBusy(false); }
  }

  const scope = readOverviewScope(location.hash);
  return <section className="alerts-screen fleet-alerts-screen" aria-labelledby="fleet-alerts-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · fleet</p><h2 id="fleet-alerts-heading">Alert inbox</h2><p>Current firing and acknowledged alerts across servers you can read. Select one to investigate or acknowledge it.</p></div></div>
    <div className="alert-filters"><label>State<select value={filter} onChange={event => setFilter(event.target.value as AlertFilter)}><option value="active">Active</option><option value="firing">Firing</option><option value="all">All loaded states</option></select></label><label>Server or rule<input aria-label="Filter loaded alerts" value={search} onChange={event => setSearch(event.target.value)} placeholder="Search loaded alerts" /></label><span>{visible.length} shown · {items.length} loaded</span></div>
    {loading ? <p role="status" className="status-message">Loading fleet alerts…</p> : null}
    {error ? <p role="alert" className="status-message">{error} <button className="secondary-button" type="button" onClick={() => setReload(value => value + 1)}>Retry</button></p> : null}
    {message ? <p role="status" className="status-message">{message}</p> : null}
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Fleet alerts"><div className="table-card-heading"><div><h3>Alerts ({visible.length})</h3><p>Sorted by when each alert fired. Severity is not supplied by the current rules.</p></div></div><div className="table-scroll"><table><caption>Current alerts on authorized servers</caption><thead><tr><th scope="col">State</th><th scope="col">Server</th><th scope="col">Rule</th><th scope="col">Fired</th><th scope="col">Value</th></tr></thead><tbody>{visible.map(item => <tr className={key(item) === key(selected) ? "selected-row" : ""} key={key(item)}><td><span className={`state-pill state-${item.state}`}>{item.state}</span></td><td><button className="table-link" type="button" onClick={() => setSelectedKey(key(item))}>{item.targetName}</button></td><td>{item.ruleName}</td><td>{formatTime(item.firedUtc ?? item.firstObservedUtc)}</td><td>{item.value === null || item.value === undefined ? "Unavailable" : item.value.toLocaleString()}</td></tr>)}</tbody></table></div>{!loading && !error && visible.length === 0 ? <p className="empty-state">No alerts match the loaded fleet evidence.</p> : null}<div className="pager"><span>{nextCursor ? "More alerts are available." : "End of loaded alerts."}</span><button className="secondary-button" type="button" disabled={!nextCursor || loadingMore} onClick={() => void loadMore()}>{loadingMore ? "Loading…" : "Load more"}</button></div></section>
      {selected ? <DetailPane title={selected.ruleName} subtitle={`${selected.targetName} · alert ${selected.alertId}`} actions={selected.state === "firing" && canAcknowledge ? <button className="primary" type="button" disabled={busy} onClick={() => void acknowledge(selected)}>{busy ? "Acknowledging…" : "Acknowledge"}</button> : undefined}><EvidenceStatus label={selected.state} detail={selected.deliverySuppressed ? "Delivery suppressed" : "Delivery enabled"} tone={selected.state === "firing" ? "critical" : "warning"} /><p className="detail-note">{selected.reason ?? "No reason was returned for this alert."}</p><p>First observed: {formatTime(selected.firstObservedUtc)}</p><p>Fired: {formatTime(selected.firedUtc)}</p><p>Acknowledged: {formatTime(selected.acknowledgedUtc)}</p><a href={overviewHref(scope, "alerts", selected.targetId)}>Open server alerts →</a><a href={overviewHref(scope, "activity", selected.targetId)}>Investigate activity →</a>{selected.state === "firing" && !canAcknowledge ? <p className="detail-note">An Operator or Target Administrator role on this server is required to acknowledge.</p> : null}</DetailPane> : <div className="detail-pane empty-state">Select an alert to inspect it.</div>}
    </div>
  </section>;
}

function key(item: FleetAlert | undefined): string | undefined { return item && `${item.targetId}:${item.alertId}`; }
