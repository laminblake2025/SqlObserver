import { useEffect, useRef, useState } from "react";
import { DetailPane } from "../../components/DiagnosticUi";
import { getDestinationsForAdmin, type DestinationAdminItem } from "./alertAdminListsApi";
import { useAlertAdminList } from "./useAlertAdminList";
import { adminSelectionMessage } from "./adminSelectionModel.ts";
import { DestinationAdminForm } from "./DestinationAdminForm";

const destinationId = (item: DestinationAdminItem) => item.destinationId;
const kindLabel = (kind: DestinationAdminItem["kind"]) => kind === "https-webhook" ? "HTTPS webhook" : "Windows Event Log";

export function TargetDestinationsPanel({ instanceId, displayName, onClose, refresh, canManage, selectedId, onSelect }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly refresh: number; readonly canManage: boolean; readonly selectedId?: string | null; readonly onSelect: (id: string) => void;
}) {
  const list = useAlertAdminList(instanceId, refresh, getDestinationsForAdmin, destinationId, selectedId);
  const [editingSelection, setEditingSelection] = useState<{ identity: string; id: string }>();
  const [actionMessage, setActionMessage] = useState<{ identity: string; text: string }>();
  const selected = selectedId === undefined ? list.items[0] : list.items.find(item => item.destinationId.toLowerCase() === selectedId?.toLowerCase());
  const selectionMessage = adminSelectionMessage(selectedId, Boolean(selected), list.findingSelection, Boolean(list.nextCursor));
  const selectionKey = `${instanceId.toLowerCase()}:${selectedId ?? (selectedId === null ? "invalid" : "default")}`;
  const context = JSON.stringify([selectionKey, canManage]);
  const generation = useRef({ context, epoch: 0 });
  if (generation.current.context !== context) generation.current = { context, epoch: generation.current.epoch + 1 };
  const identity = JSON.stringify([context, generation.current.epoch]);
  const activeSelection = useRef(identity);
  activeSelection.current = identity;
  const editing = editingSelection?.identity === identity && selected?.destinationId === editingSelection.id ? selected : undefined;
  useEffect(() => { setEditingSelection(undefined); }, [selectionKey]);
  useEffect(() => { if (!canManage) setEditingSelection(undefined); }, [canManage]);

  return <section className="alerts-screen" aria-labelledby="destinations-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · {displayName}</p><h2 id="destinations-heading">Notification destinations</h2><p>Review enabled and approved notification destinations for this server.</p></div><div className="form-actions"><button className="secondary-button" onClick={list.retry} type="button">Refresh list</button><button className="secondary-button" onClick={onClose} type="button">Back to alerts</button></div></div>
    {list.loading ? <p role="status" className="status-message">Loading destinations…</p> : null}
    {selectionMessage ? <p role="status" className="status-message">{selectionMessage}</p> : null}
    {list.error ? <div role="alert" className="status-message">{list.error} <button className="secondary-button" type="button" onClick={list.retry}>Retry</button></div> : null}
    {actionMessage?.identity === identity ? <p role="status" className="status-message">{actionMessage.text}</p> : null}
    {canManage && !list.loading ? <DestinationAdminForm key={`${identity}:${editing ? `${editing.destinationId}:${editing.revision}` : "create"}`}
      instanceId={instanceId} destination={editing} onCancel={() => setEditingSelection(undefined)}
      onSaved={(id, action) => {
        if (activeSelection.current !== identity) return;
        // A verified command deliberately selects its own destination. Bind its
        // status to that resulting context, while unrelated navigation/revocation
        // immediately hides the old status before effects run.
        const nextContext = JSON.stringify([`${instanceId.toLowerCase()}:${id.toLowerCase()}`, canManage]);
        const nextIdentity = JSON.stringify([nextContext, generation.current.epoch + (nextContext === context ? 0 : 1)]);
        onSelect(id); setEditingSelection(undefined);
        const text = action === "create" ? "Destination configured. Approval is required before delivery."
          : action === "approve" ? "Destination approved. The list is refreshing."
            : action === "disable" ? "Destination disabled. The list is refreshing."
              : "Destination updated. Approval is required for the new revision.";
        setActionMessage({ identity: nextIdentity, text });
        list.retry();
      }} /> : null}
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Configured destinations"><div className="table-card-heading"><div><h3>Destinations ({list.items.length} loaded)</h3><p>Approval is stored state. Delivery also depends on current configuration checks.</p></div></div><div className="table-scroll" tabIndex={0} role="region" aria-label="Notification destinations table"><table><caption>Notification destinations for {displayName}</caption><thead><tr><th scope="col">State</th><th scope="col">Kind</th><th scope="col">Approval</th><th scope="col">Revision</th></tr></thead><tbody>{list.items.map(item => <tr key={item.destinationId} className={item.destinationId === selected?.destinationId ? "selected-row" : ""}><td><span className="state-pill">{item.enabled ? "Enabled" : "Disabled"}</span></td><td><button className="table-link" type="button" aria-pressed={item.destinationId === selected?.destinationId} onClick={() => { onSelect(item.destinationId); setEditingSelection(undefined); }}>{kindLabel(item.kind)}</button></td><td>{item.approved ? "Approved" : "Not approved"}</td><td>{item.revision}</td></tr>)}</tbody></table></div>{!list.loading && !list.error && list.items.length === 0 ? <p className="empty-state">No destinations are configured for this server.</p> : null}<div className="pager"><span>{list.nextCursor ? "More destinations are available." : "End of loaded destinations."}</span><button className="secondary-button" type="button" disabled={!list.nextCursor || list.loadingMore} onClick={() => void list.loadMore()}>{list.loadingMore ? "Loading…" : "Load more"}</button></div></section>
      {selected ? <DetailPane title={kindLabel(selected.kind)} subtitle={`Revision ${selected.revision} · ${displayName}`} actions={canManage ? <button className="secondary-button" type="button" onClick={() => setEditingSelection({ identity, id: selected.destinationId })}>Manage destination</button> : undefined}><dl className="rule-details"><div><dt>Enabled</dt><dd>{selected.enabled ? "Yes" : "No"}</dd></div><div><dt>Stored approval</dt><dd>{selected.approved ? "Approved" : "Not approved"}</dd></div></dl><p className="table-note">Destination identifier: {selected.destinationId}</p><p className="detail-note">Approval does not guarantee delivery. Configuration is checked again before sending.</p></DetailPane> : <div className="detail-pane empty-state">Select a destination to inspect its state.</div>}
    </div>
  </section>;
}
