import { useEffect, useState } from "react";
import { acknowledgeAlert, getActiveAlerts } from "./alertApi";
import type { ActiveAlert } from "./alertTypes";

export function TargetAlertsPanel({ instanceId, displayName, onClose }: { instanceId: string; displayName: string; onClose: () => void }) {
  const [items, setItems] = useState<readonly ActiveAlert[]>([]);
  const [message, setMessage] = useState<string>();
  const [nextCursor, setNextCursor] = useState<string>();
  const [busy, setBusy] = useState<string>();
  useEffect(() => { const controller = new AbortController(); void getActiveAlerts(instanceId, controller.signal).then((page) => { setItems(page.items); setNextCursor(page.nextCursor); }).catch((error: unknown) => { if (!controller.signal.aborted) setMessage(error instanceof Error ? error.message : "Alerts are unavailable."); }); return () => controller.abort(); }, [instanceId]);
  async function nextPage() { if (!nextCursor) return; const controller = new AbortController(); try { const page = await getActiveAlerts(instanceId, controller.signal, 100, nextCursor); setItems(page.items); setNextCursor(page.nextCursor); } catch (error: unknown) { setMessage(error instanceof Error ? error.message : "Alerts are unavailable."); } }
  async function acknowledge(item: ActiveAlert) { const controller = new AbortController(); setBusy(item.alertId); try { await acknowledgeAlert(instanceId, item.alertId, controller.signal); setItems((current) => current.map((candidate) => candidate.alertId === item.alertId ? { ...candidate, state: "acknowledged" } : candidate)); } catch (error: unknown) { setMessage(error instanceof Error ? error.message : "The alert could not be acknowledged."); } finally { setBusy(undefined); } }
  return <section className="projection-panel" aria-labelledby="alerts-heading"><div className="section-heading"><div><p className="eyebrow">M8</p><h2 id="alerts-heading">Active alerts — {displayName}</h2></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>{message ? <p role="alert">{message}</p> : items.length === 0 ? <p>No active alerts. Evaluation continues during maintenance windows.</p> : <><ul>{items.map((item) => <li key={item.alertId}><strong>{item.state}</strong> · {item.value ?? "collector health"}{item.deliverySuppressed ? " · delivery suppressed" : ""}{item.state === "firing" ? <button className="secondary-button" disabled={busy === item.alertId} onClick={() => void acknowledge(item)} type="button">Acknowledge</button> : null}</li>)}</ul>{nextCursor ? <button className="secondary-button" onClick={() => void nextPage()} type="button">Next</button> : null}</>}</section>;
}
