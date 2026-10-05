import { useEffect, useId, useRef, useState } from "react";
import { getLifecycleCollectionStatus, setLifecycleCollectionStatus, LifecycleCollectionWriteError, type LifecycleCollectionStatus } from "./lifecycleCollectionApi.ts";
import "./lifecycleCollectionControl.css";

interface Props {
  readonly targetId: string;
  readonly targetRevision: number;
  readonly canManage: boolean;
  readonly accessContext: string;
}
/** A new keyed owner fences target/revision/authority changes, including A -> B -> A. */
export function LifecycleCollectionControl(props: Props) {
  return <Control key={JSON.stringify([props.targetId.toLowerCase(), props.targetRevision, props.canManage, props.accessContext])} {...props} />;
}
function Control({ targetId, targetRevision, canManage }: Props) {
  const heading = useId(), alive = useRef(false), owner = useRef<AbortController | undefined>(undefined);
  const [status, setStatus] = useState<LifecycleCollectionStatus>();
  const [pending, setPending] = useState<"read" | "write">();
  const [error, setError] = useState<string>(), [notice, setNotice] = useState<string>();
  const [writeDenied, setWriteDenied] = useState(false);
  const owns = (request: AbortController) => alive.current && owner.current === request;
  async function load() {
    if (!alive.current || owner.current) return;
    const request = new AbortController(); owner.current = request;
    setPending("read"); setStatus(undefined); setError(undefined); setNotice(undefined);
    try { const value = await getLifecycleCollectionStatus(targetId, targetRevision, request.signal); if (owns(request)) setStatus(value); }
    catch (failure) { if (owns(request)) setError(failure instanceof Error ? failure.message : "Lifecycle collection state is unavailable."); }
    finally { if (owns(request)) { owner.current = undefined; setPending(undefined); } }
  }
  async function change() {
    if (!alive.current || owner.current || !status || !canManage || writeDenied) return;
    const expected = status, request = new AbortController(); owner.current = request;
    setPending("write"); setStatus(undefined); setError(undefined); setNotice(undefined);
    try {
      const value = await setLifecycleCollectionStatus(expected, !expected.collectorEnabled, request.signal);
      if (owns(request)) {
        setStatus(value);
        setNotice(`Lifecycle collection ${value.collectorEnabled ? "enabled" : "disabled"}. Retained history is unchanged; new observations still depend on source access.`);
      }
    } catch (failure) {
      if (owns(request)) {
        if (failure instanceof LifecycleCollectionWriteError && [401, 403].includes(failure.status ?? 0)) setWriteDenied(true);
        setError(failure instanceof Error ? failure.message : "The collection change outcome is unknown. Read current state before another change.");
      }
    } finally { if (owns(request)) { owner.current = undefined; setPending(undefined); } }
  }
  useEffect(() => {
    alive.current = true; void load();
    return () => { alive.current = false; owner.current?.abort(); owner.current = undefined; };
  }, []);
  return <section className="panel lifecycle-collection-control" aria-labelledby={heading} aria-busy={pending !== undefined}>
    <div className="lifecycle-collection-heading"><h4 id={heading}>Server lifecycle collection</h4><span className="lifecycle-collection-badge">Optional · off by default</span></div>
    <p>Collect sampled engine identity and startup evidence to detect changes between observations. Exact occurrence time, cause, physical AG failover and uninterrupted coverage are not established.</p>
    <p className="lifecycle-collection-state" role="status">{pending === "write" ? "Collection change pending; the outcome is not confirmed."
      : pending === "read" ? "Reading current lifecycle collection state…"
      : status ? `Collection ${status.collectorEnabled ? "enabled" : "disabled"} · schedule revision ${status.scheduleRevision}`
      : "Current collection state is unknown."}</p>
    {notice && <p role="status">{notice}</p>}
    {error && <p className="lifecycle-collection-error" role="alert">{error}</p>}
    <div className="lifecycle-collection-actions">
      <button className="secondary-button" type="button" disabled={pending !== undefined} onClick={() => void load()}>Read current lifecycle state</button>
      {canManage && !writeDenied && <button className="secondary-button" type="button" disabled={pending !== undefined || !status} onClick={() => void change()}>
        {status?.collectorEnabled ? "Disable" : "Enable"} lifecycle collection
      </button>}
    </div>
    {(!canManage || writeDenied) && <p className="lifecycle-collection-permission">A Target Administrator for this server can change collection.{writeDenied ? " Refresh your authority context after access is restored." : ""}</p>}
  </section>;
}
