import { useEffect, useMemo, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { DetailPane, EvidenceStatus } from "../../components/DiagnosticUi";
import { readOverviewScope } from "../overview/overviewModel";
import { canAcknowledgeAlert, getMyAccess } from "../targets/meApi";
import { acknowledgeAlert, getFleetActiveAlerts } from "./alertApi";
import { AcknowledgementAttempts } from "./acknowledgementAttempts";
import { alertMatchesFilter, type AlertFilter } from "./alertFilter";
import { alertActivityHref, groupFleetAlerts, type FleetAlertGrouping } from "./fleetAlertGrouping";
import type { FleetAlert } from "./alertTypes";
import { alertSnapshotKey, fleetAlertKey, loadFleetAlertJump } from "./fleetAlertJump";
import { alertJumpHref, targetAlertHref, type AlertSelection } from "../../components/commandPaletteModel";
import { alertReasonSummary } from "./alertRuleSignals.ts";

const acknowledgementAttempts = new AcknowledgementAttempts();
export function FleetAlertsPage({ refresh, selection }: { readonly refresh: number; readonly selection?: AlertSelection }) {
  const { mode } = useTimeDisplay();
  const formatTime = (value?: string) => value ? formatDisplayTime(value, mode) : "Not returned";
  const [receivedItems, setItems] = useState<readonly FleetAlert[]>([]);
  const [evidenceKey, setEvidenceKey] = useState("");
  const [successfulGeneration, setSuccessfulGeneration] = useState("");
  const [snapshotUtc, setSnapshotUtc] = useState<string>();
  const [nextCursor, setNextCursor] = useState<string>();
  const [isLoading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string>();
  const [reload, setReload] = useState(0);
  const [filter, setFilter] = useState<AlertFilter>("active");
  const [search, setSearch] = useState("");
  const [grouping, setGrouping] = useState<FleetAlertGrouping>("server");
  const [localSelectedKey, setSelectedKey] = useState<string>();
  const [accessResult, setAccessResult] = useState<{ readonly targetId: string; readonly allowed: boolean }>();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string>();
  const moreRequest = useRef<AbortController | null>(null);
  const acknowledgementRequest = useRef<AbortController | null>(null);
  const requestedKey = selection && fleetAlertKey(selection);
  const selectedKey = requestedKey ?? localSelectedKey;
  const generationKey = JSON.stringify([refresh, reload]);
  const requestKey = JSON.stringify([refresh, reload, requestedKey]);
  const requestIdentity = useRef(requestKey);
  requestIdentity.current = requestKey;
  const items = evidenceKey === generationKey ? receivedItems : [];
  const loading = isLoading || evidenceKey !== generationKey;

  useEffect(() => {
    setSelectedKey(undefined);
    if (requestedKey) { setFilter("active"); setSearch(""); }
  }, [requestedKey]);

  useEffect(() => {
    const controller = new AbortController();
    moreRequest.current?.abort();
    moreRequest.current = null;
    setLoadingMore(false);
    setError(undefined);
    setMessage(undefined);
    // A URL selection within this loaded snapshot leaves keyboard focus and paging intact.
    const sameSnapshot = evidenceKey === generationKey && successfulGeneration === generationKey;
    if (sameSnapshot && (!requestedKey || receivedItems.some(item => key(item) === requestedKey))) {
      setLoading(false);
      return () => controller.abort();
    }
    setLoading(true);
    if (!sameSnapshot) {
      setItems([]);
      setSnapshotUtc(undefined);
      setNextCursor(undefined);
    }
    setEvidenceKey(generationKey);
    void (async () => {
      const page = await loadFleetAlertJump(
        async cursor => {
          if (controller.signal.aborted) throw new Error("Alert loading was cancelled.");
          const page = await getFleetActiveAlerts(controller.signal, 100, cursor);
          if (controller.signal.aborted) throw new Error("Alert loading was cancelled.");
          return page;
        }, requestedKey);
      if (!controller.signal.aborted && requestIdentity.current === requestKey) {
        setItems(page.items);
        setSuccessfulGeneration(generationKey);
        setNextCursor(page.nextCursor);
        setSnapshotUtc(page.snapshotUtc);
        if (requestedKey && !page.found)
          setMessage("That alert is no longer in the loaded current inbox. Browse further or refresh its server alerts.");
      }
    })()
      .catch((failure: unknown) => { if (!controller.signal.aborted && requestIdentity.current === requestKey) setError(failure instanceof Error ? failure.message : "Fleet alerts are unavailable."); })
      .finally(() => { if (!controller.signal.aborted && requestIdentity.current === requestKey) setLoading(false); });
    return () => { controller.abort(); moreRequest.current?.abort(); };
  }, [requestKey]);

  const visible = useMemo(() => items.filter(item =>
    alertMatchesFilter(filter, item.state) &&
    (search === "" || `${item.targetName} ${item.ruleName} ${item.targetId}`.toLowerCase().includes(search.toLowerCase()))
  ), [filter, items, search]);
  const groups = useMemo(() => groupFleetAlerts(visible, grouping), [visible, grouping]);
  const selected = visible.find(item => key(item) === selectedKey) ?? (selectedKey ? undefined : visible[0]);
  const loadedSelected = selectedKey ? items.find(item => key(item) === selectedKey) : undefined;
  const canAcknowledge = selected !== undefined && accessResult?.targetId === selected.targetId.toLowerCase() && accessResult.allowed;
  const actionKey = JSON.stringify([requestKey, key(selected), selected?.firstObservedUtc]);
  const actionIdentity = useRef(actionKey);
  actionIdentity.current = actionKey;
  useEffect(() => {
    acknowledgementRequest.current?.abort();
    acknowledgementRequest.current = null;
    setBusy(false);
    return () => { acknowledgementRequest.current?.abort(); acknowledgementRequest.current = null; };
  }, [actionKey]);

  useEffect(() => {
    const controller = new AbortController();
    setAccessResult(undefined);
    if (selected) {
      void getMyAccess(selected.targetId, controller.signal)
        .then(access => { if (!controller.signal.aborted) setAccessResult({ targetId: selected.targetId.toLowerCase(), allowed: canAcknowledgeAlert(access, selected.targetId) }); })
        .catch(() => { if (!controller.signal.aborted) setAccessResult({ targetId: selected.targetId.toLowerCase(), allowed: false }); });
    }
    return () => controller.abort();
  }, [selected?.targetId, requestKey]);

  async function loadMore() {
    if (!nextCursor || loading || loadingMore || moreRequest.current) return;
    const controller = new AbortController();
    moreRequest.current = controller;
    setLoadingMore(true);
    try {
      const page = await getFleetActiveAlerts(controller.signal, 100, nextCursor);
      if (!controller.signal.aborted && requestIdentity.current === requestKey) {
        if (alertSnapshotKey(page.snapshotUtc) !== alertSnapshotKey(snapshotUtc)) throw new Error("Alert snapshot changed while paging. Refresh the inbox.");
        setItems(current => [...current, ...page.items.filter(item => !current.some(existing => key(existing) === key(item)))]);
        setNextCursor(page.nextCursor);
        setError(undefined);
        if (requestedKey && page.items.some(item => key(item) === requestedKey)) setMessage(undefined);
      }
    } catch (failure) {
      if (!controller.signal.aborted && requestIdentity.current === requestKey) setError(failure instanceof Error ? failure.message : "Fleet alerts are unavailable.");
    } finally { if (!controller.signal.aborted && requestIdentity.current === requestKey) setLoadingMore(false); if (moreRequest.current === controller) moreRequest.current = null; }
  }

  async function acknowledge(item: FleetAlert) {
    if (!canAcknowledge || item.state !== "firing" || busy || acknowledgementRequest.current || key(item) !== key(selected)) return;
    const controller = new AbortController();
    acknowledgementRequest.current = controller;
    setBusy(true);
    setMessage(undefined);
    try {
      await acknowledgeAlert(item.targetId, item.alertId,
        acknowledgementAttempts.tokenFor(item.targetId, item.alertId, item.firstObservedUtc), controller.signal);
      if (controller.signal.aborted || actionIdentity.current !== actionKey) return;
      acknowledgementAttempts.complete(item.targetId, item.alertId, item.firstObservedUtc);
      setItems(current => current.map(candidate => key(candidate) === key(item) && candidate.firstObservedUtc === item.firstObservedUtc
        ? { ...candidate, state: "acknowledged" as const } : candidate));
      setMessage("Acknowledgement recorded. Refresh to see the server-provided timestamp.");
    } catch (failure) {
      if (!controller.signal.aborted && actionIdentity.current === actionKey) setMessage(failure instanceof Error ? failure.message : "The alert could not be acknowledged.");
    } finally { if (acknowledgementRequest.current === controller) acknowledgementRequest.current = null;
      if (!controller.signal.aborted && actionIdentity.current === actionKey) setBusy(false); }
  }

  const scope = readOverviewScope(location.hash);
  const selectAlert = (item: FleetAlert) => { location.hash = alertJumpHref(scope, item); };
  return <section className="alerts-screen fleet-alerts-screen" aria-labelledby="fleet-alerts-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · fleet</p><h2 id="fleet-alerts-heading">Alert inbox</h2><p>Current firing and acknowledged alerts across servers you can read. Select one to investigate or acknowledge it.</p></div></div>
    <div className="alert-filters">
      <label>State<select value={filter} onChange={event => setFilter(event.target.value as AlertFilter)}>
        <option value="active">Active</option><option value="firing">Firing</option><option value="all">All loaded states</option>
      </select></label>
      <label>Server or rule<input aria-label="Filter loaded alerts" value={search}
        onChange={event => setSearch(event.target.value)} placeholder="Search loaded alerts" /></label>
      <label>Group by<select value={grouping} onChange={event => setGrouping(event.target.value as FleetAlertGrouping)}>
        <option value="server">Server</option><option value="rule">Rule</option><option value="none">None</option>
      </select></label>
      <span>{visible.length} shown · {items.length} loaded</span>
    </div>
    {loading ? <p role="status" className="status-message">Loading fleet alerts…</p> : null}
    {error ? <p role="alert" className="status-message">{error} <button className="secondary-button" type="button" onClick={() => setReload(value => value + 1)}>Retry</button></p> : null}
    {message ? <p role="status" className="status-message">{message}</p> : null}
    {!loading && !error && <p className="table-note">Alerts show the latest fleet state, independently of the history window. Repository snapshot: {snapshotUtc ? formatTime(snapshotUtc) : "Not returned"}. URL lookup reads at most five pages / 500 rows; missing or resolved alerts may be unavailable.</p>}
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Fleet alerts">
        <div className="table-card-heading"><div><h3>Alerts ({visible.length})</h3>
          <p>Groups cover loaded alerts only; alerts within each group retain fired-time order. Severity is not supplied by the current rules.</p>
        </div></div>
        <div className="table-scroll" tabIndex={0} role="region" aria-label="Fleet alert observations"><table>
          <caption>Current alerts on authorized servers</caption>
          <thead><tr><th scope="col">State</th><th scope="col">Server</th><th scope="col">Rule</th>
            <th scope="col">Fired</th><th scope="col">Value</th></tr></thead>
          {groups.map(group => <tbody key={group.key}>
            {grouping !== "none" && <tr className="alert-group-row"><th scope="rowgroup" colSpan={5}>
              {group.label} · {group.items.length} loaded
            </th></tr>}
            {group.items.map(item => <tr className={key(item) === key(selected) ? "selected-row" : ""} key={key(item)}>
              <td><span className={`state-pill state-${item.state}`}>{item.state}</span></td>
              <td><button className="table-link" type="button" aria-label={`Select ${item.ruleName} on ${item.targetName}`} aria-pressed={key(item) === key(selected)} onClick={() => selectAlert(item)}>{item.targetName}</button></td>
              <td>{item.ruleName}</td><td>{formatTime(item.firedUtc ?? item.firstObservedUtc)}</td>
              <td>{item.value === null || item.value === undefined ? "Unavailable" : item.value.toLocaleString()}</td>
            </tr>)}
          </tbody>)}
        </table></div>
        {!loading && !error && visible.length === 0 ? <p className="empty-state">No alerts match the loaded fleet evidence.</p> : null}
        <div className="pager"><span>{nextCursor ? "More alerts are available." : "End of loaded alerts."}</span>
          <button className="secondary-button" type="button" disabled={!nextCursor || loading || loadingMore}
            onClick={() => void loadMore()}>{loadingMore ? "Loading…" : "Load more"}</button>
        </div>
      </section>
      {selected ? <DetailPane title={selected.ruleName} subtitle={`${selected.targetName} · alert ${selected.alertId}`}
        actions={selected.state === "firing" && canAcknowledge ? <button className="primary" type="button"
          disabled={busy} onClick={() => void acknowledge(selected)}>{busy ? "Acknowledging…" : "Acknowledge"}</button> : undefined}>
        <EvidenceStatus label={selected.state} detail={selected.deliverySuppressed ? "Delivery suppressed" : "Delivery enabled"}
          tone={selected.state === "firing" ? "critical" : "warning"} />
        <p className="detail-note">{alertReasonSummary(selected.reason, selected.value) ?? "No reason was returned for this alert."}</p>
        <p>First observed: {formatTime(selected.firstObservedUtc)}</p>
        <p>Fired: {formatTime(selected.firedUtc)}</p>
        <p>Acknowledged: {formatTime(selected.acknowledgedUtc)}</p>
        <a href={targetAlertHref(scope, selected.targetId, selected.alertId)}>Open server alerts →</a>
        <a href={alertActivityHref(selected, scope)}>Investigate at alert time →</a>
        {selected.state === "firing" && !canAcknowledge ? <p className="detail-note">
          An Operator or Target Administrator role on this server is required to acknowledge.</p> : null}
      </DetailPane> : <div className="detail-pane empty-state">{loading ? "Loading selected alert evidence…" : error ? "Selected alert evidence is unavailable. Retry the inbox request; no other alert has been selected." : selectedKey
        ? loadedSelected ? <>The selected alert is hidden by the current filters. Clear the filters to show this alert. <button type="button" className="secondary-button" onClick={() => { setFilter("all"); setSearch(""); }}>Show selected alert</button></>
          : "The requested alert is not present in the bounded current inbox. Browse further or refresh; no other alert has been selected."
        : "Select an alert to inspect it."}</div>}
    </div>
  </section>;
}

function key(item: FleetAlert | undefined): string | undefined { return item && fleetAlertKey(item); }
