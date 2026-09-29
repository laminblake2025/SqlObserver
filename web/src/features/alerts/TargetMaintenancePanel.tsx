import { useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { DetailPane } from "../../components/DiagnosticUi";
import { getMaintenanceForAdmin, type MaintenanceAdminItem } from "./alertAdminListsApi";
import { useAlertAdminList } from "./useAlertAdminList";

const windowId = (item: MaintenanceAdminItem) => item.windowId;

export function TargetMaintenancePanel({ instanceId, displayName, onClose, refresh }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly refresh: number;
}) {
  const formatTime = useTimeFormatter();
  const list = useAlertAdminList(instanceId, refresh, getMaintenanceForAdmin, windowId);
  const [selectedId, setSelectedId] = useState<string>();
  const selected = list.items.find(item => item.windowId === selectedId) ?? list.items[0];

  return <section className="alerts-screen" aria-labelledby="maintenance-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · {displayName}</p><h2 id="maintenance-heading">Maintenance windows</h2><p>Review scheduled and cancelled one-off windows. These windows suppress delivery, while collection and alert evaluation continue.</p></div><button className="secondary-button" onClick={onClose} type="button">Back to alerts</button></div>
    {list.loading ? <p role="status" className="status-message">Loading maintenance windows…</p> : null}
    {list.error ? <div role="alert" className="status-message">{list.error} <button className="secondary-button" type="button" onClick={list.retry}>Retry</button></div> : null}
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Configured maintenance windows"><div className="table-card-heading"><div><h3>Windows ({list.items.length} loaded)</h3><p>Newest start time first. Cancelled windows remain visible for review.</p></div></div><div className="table-scroll"><table><caption>Maintenance windows for {displayName}</caption><thead><tr><th scope="col">State</th><th scope="col">Starts</th><th scope="col">Ends</th><th scope="col">Reason</th></tr></thead><tbody>{list.items.map(item => <tr key={item.windowId} className={item.windowId === selected?.windowId ? "selected-row" : ""}><td><span className="state-pill">{item.cancelledAtUtc ? "Cancelled" : "Configured"}</span></td><td><button className="table-link" type="button" onClick={() => setSelectedId(item.windowId)}>{formatTime(item.startsAtUtc)}</button></td><td>{formatTime(item.endsAtUtc)}</td><td>{item.reason}</td></tr>)}</tbody></table></div>{!list.loading && !list.error && list.items.length === 0 ? <p className="empty-state">No maintenance windows are configured for this server.</p> : null}<div className="pager"><span>{list.nextCursor ? "More windows are available." : "End of loaded windows."}</span><button className="secondary-button" type="button" disabled={!list.nextCursor || list.loadingMore} onClick={() => void list.loadMore()}>{list.loadingMore ? "Loading…" : "Load more"}</button></div></section>
      {selected ? <DetailPane title="Maintenance window" subtitle={`Revision ${selected.revision} · ${displayName}`}><dl className="rule-details"><div><dt>Starts</dt><dd>{formatTime(selected.startsAtUtc)}</dd></div><div><dt>Ends</dt><dd>{formatTime(selected.endsAtUtc)}</dd></div><div><dt>Reason</dt><dd>{selected.reason}</dd></div><div><dt>Cancelled</dt><dd>{selected.cancelledAtUtc ? formatTime(selected.cancelledAtUtc) : "No"}</dd></div></dl><p className="table-note">Window identifier: {selected.windowId}</p></DetailPane> : <div className="detail-pane empty-state">Select a window to inspect its configuration.</div>}
    </div>
  </section>;
}
