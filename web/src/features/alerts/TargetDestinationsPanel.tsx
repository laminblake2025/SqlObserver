import { useState } from "react";
import { DetailPane } from "../../components/DiagnosticUi";
import { getDestinationsForAdmin, type DestinationAdminItem } from "./alertAdminListsApi";
import { useAlertAdminList } from "./useAlertAdminList";

const destinationId = (item: DestinationAdminItem) => item.destinationId;
const kindLabel = (kind: DestinationAdminItem["kind"]) => kind === "https-webhook" ? "HTTPS webhook" : "Windows Event Log";

export function TargetDestinationsPanel({ instanceId, displayName, onClose, refresh }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly refresh: number;
}) {
  const list = useAlertAdminList(instanceId, refresh, getDestinationsForAdmin, destinationId);
  const [selectedId, setSelectedId] = useState<string>();
  const selected = list.items.find(item => item.destinationId === selectedId) ?? list.items[0];

  return <section className="alerts-screen" aria-labelledby="destinations-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · {displayName}</p><h2 id="destinations-heading">Notification destinations</h2><p>Review enabled and approved notification destinations for this server.</p></div><button className="secondary-button" onClick={onClose} type="button">Back to alerts</button></div>
    {list.loading ? <p role="status" className="status-message">Loading destinations…</p> : null}
    {list.error ? <div role="alert" className="status-message">{list.error} <button className="secondary-button" type="button" onClick={list.retry}>Retry</button></div> : null}
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Configured destinations"><div className="table-card-heading"><div><h3>Destinations ({list.items.length} loaded)</h3><p>Approval is stored state. Delivery also depends on current configuration checks.</p></div></div><div className="table-scroll"><table><caption>Notification destinations for {displayName}</caption><thead><tr><th scope="col">State</th><th scope="col">Kind</th><th scope="col">Approval</th><th scope="col">Revision</th></tr></thead><tbody>{list.items.map(item => <tr key={item.destinationId} className={item.destinationId === selected?.destinationId ? "selected-row" : ""}><td><span className="state-pill">{item.enabled ? "Enabled" : "Disabled"}</span></td><td><button className="table-link" type="button" onClick={() => setSelectedId(item.destinationId)}>{kindLabel(item.kind)}</button></td><td>{item.approved ? "Approved" : "Not approved"}</td><td>{item.revision}</td></tr>)}</tbody></table></div>{!list.loading && !list.error && list.items.length === 0 ? <p className="empty-state">No destinations are configured for this server.</p> : null}<div className="pager"><span>{list.nextCursor ? "More destinations are available." : "End of loaded destinations."}</span><button className="secondary-button" type="button" disabled={!list.nextCursor || list.loadingMore} onClick={() => void list.loadMore()}>{list.loadingMore ? "Loading…" : "Load more"}</button></div></section>
      {selected ? <DetailPane title={kindLabel(selected.kind)} subtitle={`Revision ${selected.revision} · ${displayName}`}><dl className="rule-details"><div><dt>Enabled</dt><dd>{selected.enabled ? "Yes" : "No"}</dd></div><div><dt>Stored approval</dt><dd>{selected.approved ? "Approved" : "Not approved"}</dd></div></dl><p className="table-note">Destination identifier: {selected.destinationId}</p><p className="detail-note">Approval does not guarantee delivery. Configuration is checked again before sending.</p></DetailPane> : <div className="detail-pane empty-state">Select a destination to inspect its state.</div>}
    </div>
  </section>;
}
