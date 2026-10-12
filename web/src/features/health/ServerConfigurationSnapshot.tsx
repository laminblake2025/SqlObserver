import { useEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { ConfigurationCollectionError, getServerConfigurationSnapshot, setServerConfigurationCollection } from "./healthApi";
import type { ServerConfigurationSnapshotResult } from "./healthTypes";
import { canManageAlertRules, getMyAccess } from "../targets/meApi.ts";

type Recovery = { readonly reason: "unknown" | "conflict" | "access" | "refused" | "rate_limited" | "unavailable" };
const unknownOutcome = "The configuration collection outcome is unknown. Cancellation stops waiting and does not roll back a dispatched change. Reload current state before another change.";

export function ServerConfigurationSnapshot({ instanceId, refresh, canManage }: {
  readonly instanceId: string;
  readonly refresh: number;
  readonly canManage: boolean;
}) {
  const formatTime = useTimeFormatter();
  const targetId = instanceId.toLowerCase();
  const mounted = useRef(true);
  const authority = useRef({ allowed: canManage, generation: 0 });
  if (authority.current.allowed !== canManage) authority.current = { allowed: canManage, generation: authority.current.generation + 1 };
  const [retry, setRetry] = useState(0);
  const key = `${targetId}:${refresh}:${retry}`;
  const context = `${key}:${canManage}:${authority.current.generation}`;
  const currentContext = useRef(context); currentContext.current = context;
  const mutation = useRef<{ controller: AbortController; context: string; targetId: string } | undefined>(undefined);
  const [recoveries, setRecoveries] = useState<ReadonlyMap<string, Recovery>>(() => new Map());
  const [pending, setPending] = useState<string>();
  const reloadIntent = useRef<{ targetId: string; retry: number; verifyAccess: boolean } | undefined>(undefined);
  const [paging, setPaging] = useState<{ key: string; index: number;
    cursors: readonly (string | undefined)[] }>({ key: "", index: 0, cursors: [undefined] });
  const current = paging.key === key ? paging :
    { key, index: 0, cursors: [undefined] as readonly (string | undefined)[] };
  const cursor = current.cursors[current.index];
  const [page, setPage] = useState<{ key: string; index: number;
    result: ServerConfigurationSnapshotResult }>();
  const [error, setError] = useState<{ key: string; index: number }>();
  const updating = pending === context;
  const recovery = recoveries.get(targetId);
  const result = page?.key === key && page.index === current.index ? page.result : undefined;
  const failed = error?.key === key && error.index === current.index;

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; mutation.current?.controller.abort(); mutation.current = undefined; };
  }, []);
  useEffect(() => () => {
    const active = mutation.current;
    if (!active || active.context !== context) return;
    mutation.current = undefined; active.controller.abort();
    if (mounted.current) {
      setPending(undefined);
      setRecoveries(previous => new Map(previous).set(active.targetId, { reason: "unknown" }));
    }
  }, [context]);

  useEffect(() => {
    const controller = new AbortController();
    setPage(undefined);
    setError(undefined);
    const verifyAccess = reloadIntent.current?.targetId === targetId && reloadIntent.current.retry === retry && reloadIntent.current.verifyAccess;
    const access = verifyAccess ? getMyAccess(targetId, controller.signal)
      .then(value => canManageAlertRules(value, targetId)).catch(() => false) : Promise.resolve(true);
    void Promise.all([getServerConfigurationSnapshot(instanceId, controller.signal, cursor), access])
      .then(([next, authorized]) => {
        if (next.targetId.toLowerCase() !== instanceId.toLowerCase())
          throw new Error("Configuration snapshot target does not match.");
        if (!controller.signal.aborted && currentContext.current === context) {
          setPage({ key, index: current.index, result: next });
          if (reloadIntent.current?.targetId === targetId && reloadIntent.current.retry === retry) {
            reloadIntent.current = undefined;
            if (authorized) setRecoveries(previous => {
              const next = new Map(previous); next.delete(targetId); return next;
            });
          }
        }
      })
      .catch(() => {
        if (!controller.signal.aborted && currentContext.current === context) {
          if (reloadIntent.current?.targetId === targetId && reloadIntent.current.retry === retry) reloadIntent.current = undefined;
          setError({ key, index: current.index });
        }
      });
    return () => controller.abort();
  }, [instanceId, key, current.index, cursor, context]);

  const reload = () => {
    if (currentContext.current !== context || mutation.current || reloadIntent.current?.targetId === targetId) return;
    reloadIntent.current = { targetId, retry: retry + 1, verifyAccess: recovery?.reason === "access" };
    setRetry(value => value + 1);
  };
  const cancel = () => {
    const active = mutation.current;
    if (!active || active.context !== context) return;
    mutation.current = undefined; active.controller.abort(); setPending(undefined);
    setRecoveries(previous => new Map(previous).set(targetId, { reason: "unknown" }));
  };

  const toggle = async () => {
    if (!canManage || !result || mutation.current || recovery || currentContext.current !== context) return;
    const controller = new AbortController();
    const active = { controller, context, targetId }; mutation.current = active;
    const owned = () => mounted.current && !controller.signal.aborted && mutation.current === active && currentContext.current === context;
    setPending(context);
    try {
      await setServerConfigurationCollection(instanceId, result.targetRevision,
        result.collectorEnabled, !result.collectorEnabled, controller.signal);
      if (owned()) { mutation.current = undefined; setPending(undefined); setRetry(value => value + 1); }
    } catch (failure) {
      if (!owned()) return;
      const status = failure instanceof ConfigurationCollectionError ? failure.status : undefined;
      const reason = !(failure instanceof ConfigurationCollectionError) || failure.outcomeUnknown ? "unknown"
        : status === 409 ? "conflict" : status === 401 || status === 403 ? "access"
          : status === 429 ? "rate_limited" : status === 404 ? "unavailable" : "refused";
      setRecoveries(previous => new Map(previous).set(targetId, { reason }));
    } finally { if (owned()) { mutation.current = undefined; setPending(undefined); } }
  };

  return <section className="bounded-health-section" aria-labelledby="server-configuration-snapshot-heading">
    <div className="bounded-health-heading"><h4 id="server-configuration-snapshot-heading">Current server settings</h4></div>
    <p>Latest complete scan. This view follows the server now, even when the timeline is rewound.</p>
    {failed && <p role="alert">Current settings are unavailable or changed while paging. <button type="button" onClick={reload}>Restart from latest</button></p>}
    {!failed && !result && <p role="status">Loading current settings…</p>}
    {updating && <p role="status">Updating configuration collection. Cancellation stops waiting; a dispatched change may still be applied. <button type="button" onClick={cancel}>Cancel configuration change</button></p>}
    {recovery && <p role="alert">{recovery.reason === "conflict" ? "Server or collection state changed. Reload current state before another change."
      : recovery.reason === "access" ? "Target Administrator access on this server is required for configuration collection changes. Reload current state before another change."
      : recovery.reason === "rate_limited" ? "Too many administrative requests. Wait briefly, then reload current state before another change."
      : recovery.reason === "unavailable" ? "This server is unavailable. Reload current state before another change."
      : recovery.reason === "refused" ? "The configuration collection change was refused. Reload current state before another change."
      : unknownOutcome} <button type="button" disabled={updating || !result && !failed} onClick={reload}>Reload current state</button></p>}
    {!canManage && <p className="table-note">Target Administrator access on this server unlocks configuration collection changes.</p>}
    {result && <>
      <p>{result.runId && result.observedAtUtc
        ? `Observed ${formatTime(result.observedAtUtc)}. ` : "No complete scan yet. "}
        {result.collectorEnabled ? "Collection enabled." : "Collection disabled."}
        {result.lastOutcome ? ` Last outcome: ${result.lastOutcome}.` : ""}</p>
      {canManage && recovery?.reason !== "access" && <p><button type="button" disabled={updating || recovery !== undefined} onClick={() => void toggle()}>
        {updating ? "Updating…" : result.collectorEnabled ? "Disable configuration collection" : "Enable configuration collection"}
      </button>{!result.collectorEnabled && <small> Verify SQL Server read permissions before enabling.</small>}</p>}
      {result.items.length > 0 && <><div className="table-scroll" tabIndex={0} role="region" aria-label="Latest server configuration values"><table><thead><tr>
        <th>Setting</th><th>Configured</th><th>In use</th><th>Type</th>
      </tr></thead><tbody>{result.items.map(item => <tr key={item.configurationId}>
        <th scope="row">{item.configurationName}{item.isAdvanced ? " (advanced)" : ""}</th>
        <td>{item.configuredValue}</td><td>{item.inUseValue}</td><td>{item.sourceType}</td>
      </tr>)}</tbody></table></div><nav className="pager" aria-label="Current server settings pages">
        {current.index > 0 && <button type="button" disabled={updating} onClick={() =>
          setPaging({ ...current, index: current.index - 1 })}>Previous 100</button>}
        <span>Page {current.index + 1}</span>
        {result.nextCursor && <button type="button" disabled={updating} onClick={() =>
          setPaging({ key, index: current.index + 1,
            cursors: [...current.cursors.slice(0, current.index + 1), result.nextCursor!] })}>Next 100</button>}
      </nav></>}
    </>}
  </section>;
}
