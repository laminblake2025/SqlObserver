import { useEffect, useMemo, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { DetailPane, EvidenceStatus } from "../../components/DiagnosticUi";
import { readOverviewScope } from "../overview/overviewModel";
import { canAcknowledgeAlert, getMyAccess } from "../targets/meApi";
import { ApiRequestError } from "../../apiClient.ts";
import { useAlertInbox } from "./useAlertInbox.ts";
import { useAlertAcknowledgement } from "./useAlertAcknowledgement.ts";
import { AlertTriageControls } from "./AlertTriageControls.tsx";
import "../../components/diagnosticWorkflow.css";
import { alertMatchesFilter, type AlertFilter } from "./alertFilter";
import { alertActivityHref, groupFleetAlerts, type FleetAlertGrouping } from "./fleetAlertGrouping";
import type { FleetAlert } from "./alertTypes";
import { fleetAlertKey } from "./fleetAlertJump";
import { alertJumpHref, targetAlertHref, type AlertSelection } from "../../components/commandPaletteModel";
import { alertReasonSummary } from "./alertRuleSignals.ts";
import { AlertSnoozeControls } from "./AlertSnoozeControls.tsx";
import { AlertAssignmentControls } from "./AlertAssignmentControls.tsx";
import { useAlertMutationLane } from "./useAlertMutationLane.ts";

export function FleetAlertsPage({ refresh, selection }: { readonly refresh: number; readonly selection?: AlertSelection }) {
  const { mode } = useTimeDisplay();
  const formatTime = (value?: string) => value ? formatDisplayTime(value, mode) : "Not returned";
  const scope = readOverviewScope(location.hash);
  const context = JSON.stringify(["fleet", scope.range, scope.from, scope.to, scope.databaseId, scope.compare]);
  const requestedKey = selection && fleetAlertKey(selection);
  const inbox = useAlertInbox("fleet", "", context, refresh, requestedKey);
  const items = inbox.rows;
  const loading = inbox.loading;
  const error = inbox.error;
  const selectedKey = requestedKey;
  const [filter, setFilter] = useState<AlertFilter>("active");
  const [search, setSearch] = useState("");
  const [grouping, setGrouping] = useState<FleetAlertGrouping>("server");
  const [accessResult, setAccessResult] = useState<{ readonly key: string; readonly allowed: boolean }>();
  useEffect(() => { if (requestedKey) { setFilter("active"); setSearch(""); } }, [requestedKey]);
  const visible = useMemo(() => items.filter(item => alertMatchesFilter(filter, item.state) &&
    (search === "" || `${item.targetName} ${item.ruleName} ${item.targetId}`.toLowerCase().includes(search.toLowerCase()))), [filter, items, search]);
  const groups = useMemo(() => groupFleetAlerts(visible, grouping), [visible, grouping]);
  const selected = visible.find(item => key(item) === selectedKey) ?? (selectedKey ? undefined : visible[0]);
  const loadedSelected = selectedKey ? items.find(item => key(item) === selectedKey) : undefined;
  const accessKey = JSON.stringify([context, inbox.denial, selected?.targetId.toLowerCase()]);
  const canAcknowledge = selected !== undefined && accessResult?.key === accessKey && accessResult.allowed;
  useEffect(() => {
    const controller = new AbortController();
    setAccessResult(previous => previous?.key === accessKey ? previous : undefined);
    if (selected) void getMyAccess(selected.targetId, controller.signal)
      .then(access => { if (!controller.signal.aborted) setAccessResult({ key: accessKey, allowed: canAcknowledgeAlert(access, selected.targetId) }); })
      .catch(failure => { if (!controller.signal.aborted) {
        setAccessResult({ key: accessKey, allowed: false });
        if (failure instanceof ApiRequestError && [401, 403].includes(failure.status)) inbox.deny();
      } });
    return () => controller.abort();
  }, [accessKey, refresh]);
  const notificationKey = JSON.stringify([context, inbox.denial, key(selected), selected?.firstObservedUtc, selected?.episodeId]);
  const mutationLane = useAlertMutationLane(notificationKey);
  const mutationBusy = mutationLane.busy(notificationKey);
  const notificationBusy = mutationLane.kind(notificationKey) === "snooze" || mutationLane.kind(notificationKey) === "assignment";
  const acknowledgement = useAlertAcknowledgement(context, inbox.denial, selected, canAcknowledge, inbox.pinMutation, inbox.deny, inbox.acknowledged, mutationLane, notificationKey);
  const message = acknowledgement.message;
  const busy = acknowledgement.busy;
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
    {message ? <p role="status" className="status-message">{message}</p> : null}
    <p className="table-note">URL lookup reads at most five pages / 500 rows; missing or resolved alerts may be unavailable.</p>
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Fleet alerts">
        <div className="table-card-heading"><div><h3>Alerts ({visible.length})</h3>
          <p>Groups cover loaded alerts only; alerts within each group retain fired-time order. Severity is not supplied by the current rules.</p>
        </div></div>
        <div className="table-scroll" tabIndex={0} role="region" aria-label="Fleet alert observations"><table>
          <caption>Current alerts on authorized servers</caption>
          <thead><tr><th scope="col">State</th><th scope="col">Server</th><th scope="col">Rule</th>
            <th scope="col">Fired / first observed</th><th scope="col">Value</th></tr></thead>
          {groups.map(group => <tbody key={group.key}>
            {grouping !== "none" && <tr className="alert-group-row"><th scope="rowgroup" colSpan={5}>
              {group.label} · {group.items.length} loaded
            </th></tr>}
            {group.items.map(item => <tr className={key(item) === key(selected) ? "selected-row" : ""} key={key(item)}>
              <td><span className={`state-pill state-${item.state}`}>{item.state}</span></td>
              <td><button className="table-link" type="button" aria-label={`Select ${item.ruleName} on ${item.targetName}`} aria-pressed={key(item) === key(selected)} onClick={() => selectAlert(item)}>{item.targetName}</button></td>
              <td>{item.ruleName}</td><td>{item.firedUtc ? formatTime(item.firedUtc) : `${formatTime(item.firstObservedUtc)} · first observed`}</td>
              <td>{item.value === null || item.value === undefined ? "Unavailable" : item.value.toLocaleString()}</td>
            </tr>)}
          </tbody>)}
        </table></div>
        {!loading && !error && visible.length === 0 ? <p className="empty-state">No alerts match the loaded fleet evidence.</p> : null}
        <AlertTriageControls inbox={inbox} label="inbox" mutationBusy={notificationBusy} mutationBlocked={() => ["snooze", "assignment"].includes(mutationLane.kind(notificationKey) ?? "")}
          onLatest={() => { if (["snooze", "assignment"].includes(mutationLane.kind(notificationKey) ?? "")) return; acknowledgement.cancel(); inbox.latest(); }} />
      </section>
      {selected ? <DetailPane title={selected.ruleName} subtitle={`${selected.targetName} · alert ${selected.alertId}`}
        actions={selected.state === "firing" && canAcknowledge ? <button className="primary" type="button"
          disabled={busy || mutationBusy} onClick={() => { if (!mutationBusy) acknowledgement.acknowledge(); }}>{busy ? "Acknowledging…" : "Acknowledge"}</button> : undefined}>
        <EvidenceStatus label={selected.state} detail={selected.deliverySuppressed ? "Received row: delivery suppressed" : "Received row: delivery enabled"}
          tone={selected.state === "firing" ? "critical" : "warning"} />
        <div className="alert-triage-actions">
          {busy && <button type="button" onClick={acknowledgement.cancel}>Cancel acknowledgement</button>}
          {acknowledgement.canRetry && <button type="button" disabled={mutationBusy}
            onClick={() => { if (!mutationBusy) acknowledgement.retry(); }}>Retry acknowledgement</button>}
        </div>
        <p className="detail-note">{alertReasonSummary(selected.reason, selected.value) ?? "No reason was returned for this alert."}</p>
        <p>First observed: {formatTime(selected.firstObservedUtc)}</p>
        <p>Fired: {formatTime(selected.firedUtc)}</p>
        <p>Acknowledged: {formatTime(selected.acknowledgedUtc)}</p>
        <AlertSnoozeControls key={notificationKey} selected={selected} allowed={canAcknowledge} otherMutationBusy={mutationBusy} hold={inbox.pinMutation} deny={inbox.deny} mutationLane={mutationLane} mutationKey={notificationKey} /><AlertAssignmentControls key={`assignment:${notificationKey}`} selected={selected} allowed={canAcknowledge} hold={inbox.hold} pinMutation={inbox.pinMutation} deny={inbox.deny} mutationLane={mutationLane} mutationKey={notificationKey} />
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
