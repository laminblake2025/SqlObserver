import { useEffect, useRef, useState } from "react";
import { getCpuCollectionStatus, setCpuCollectionStatus, type CpuCollectionStatus } from "./cpuCollectionApi.ts";

/** The keyed instance fences every read/write response to its target revision and authority. */
export function CpuCollectionControl(props: { readonly targetId: string; readonly targetRevision: number; readonly canManage: boolean; readonly accessContext: string }) {
  return <Control key={JSON.stringify([props.targetId.toLowerCase(), props.targetRevision, props.canManage, props.accessContext])} {...props}/>;
}
function Control({ targetId, targetRevision, canManage }: { readonly targetId: string; readonly targetRevision: number; readonly canManage: boolean }) {
  const [status, setStatus] = useState<CpuCollectionStatus>(), [busy, setBusy] = useState(false), [error, setError] = useState<string>(), [notice, setNotice] = useState<string>();
  const alive = useRef(false), owner = useRef<AbortController | undefined>(undefined);
  async function load() {
    if (!alive.current || owner.current) return;
    const controller = new AbortController(); owner.current = controller; setBusy(true); setStatus(undefined); setError(undefined);
    try { const value = await getCpuCollectionStatus(targetId, targetRevision, controller.signal); if (alive.current && owner.current === controller) { setStatus(value); setNotice(undefined); } }
    catch (failure) { if (alive.current && owner.current === controller) setError(failure instanceof Error ? failure.message : "CPU collection status is unavailable."); }
    finally { if (alive.current && owner.current === controller) { owner.current = undefined; setBusy(false); } }
  }
  async function change() {
    if (!alive.current || owner.current || !status || !canManage) return;
    const expected = status, controller = new AbortController(); owner.current = controller; setBusy(true); setStatus(undefined); setError(undefined); setNotice("Collection change pending; its outcome is not confirmed yet.");
    try {
      const value = await setCpuCollectionStatus(targetId, targetRevision, expected.collectorEnabled, !expected.collectorEnabled, controller.signal);
      if (alive.current && owner.current === controller) { setStatus(value); setNotice(`CPU component collection ${value.collectorEnabled ? "enabled" : "disabled"}. Retained history is unchanged; new observations still require valid source evidence.`); }
    } catch (failure) { if (alive.current && owner.current === controller) { setNotice(undefined); setError(failure instanceof Error ? failure.message : "The collection change outcome is unknown. Reload current state."); } }
    finally { if (alive.current && owner.current === controller) { owner.current = undefined; setBusy(false); } }
  }
  useEffect(() => { alive.current = true; void load(); return () => { alive.current = false; owner.current?.abort(); }; }, []);
  return <section className="panel cpu-collection-control" aria-label="CPU component collection"><h3>CPU component collection</h3>
    <p>Optional collection is disabled by default. Enabling it schedules SQL-process and target-host measurements; successful source association and permissions are still required.</p>
    <p role="status">{busy ? "Reading or changing CPU collection state…" : status ? `Collection ${status.collectorEnabled ? "enabled" : "disabled"} · schedule revision ${status.scheduleRevision}` : "Current collection state is unknown."}</p>
    {notice && <p role="status">{notice}</p>}{error && <p role="alert">{error}</p>}
    <div className="screen-actions"><button className="secondary-button" type="button" disabled={busy} onClick={() => void load()}>Reload CPU collection state</button>
      {canManage && <button className="secondary-button" type="button" disabled={busy || !status} onClick={() => void change()}>{status?.collectorEnabled ? "Disable" : "Enable"} CPU component collection</button>}
    </div>{!canManage && <p>A Target Administrator for this server can change collection.</p>}
  </section>;
}
