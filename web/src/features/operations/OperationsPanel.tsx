import { useEffect, useReducer, useRef } from "react";
import { getOperationalPage, OperationalApiError } from "./operationsApi";
import { initialOperationsState, operationsReducer, operationKinds } from "./operationsState";
import { buildOperationsRenderModel } from "./operationsRenderModel";
import type { OperationalKind } from "./operationsTypes";

// Render labels are centralized in the model: Backups, SQL Agent failures, TempDB summary, TempDB files, Availability Group replicas, Availability Group databases.

const displayValue = (value: unknown): string => typeof value === "string" || typeof value === "number" || typeof value === "boolean" ? String(value) : value === null ? "—" : JSON.stringify(value);

export function OperationsPanel({ instanceId }: { readonly instanceId: string }) {
  const [cards, dispatch] = useReducer(operationsReducer, undefined, initialOperationsState);
  const controllers = useRef(new Map<OperationalKind, AbortController>());
  useEffect(() => {
    controllers.current.forEach(controller => controller.abort()); controllers.current.clear(); dispatch({ type: "reset" });
    let disposed = false;
    const load = async (kind: OperationalKind, cursor?: string | null) => {
      const controller = new AbortController(); controllers.current.set(kind, controller);
      dispatch({ type: "start", kind, append: cursor !== undefined });
      try {
        const page = await getOperationalPage(instanceId, kind, { cursor, limit: 50, signal: controller.signal });
        if (!disposed) dispatch({ type: "success", kind, page, append: cursor !== undefined });
      } catch (error) {
        if (!disposed && !(error instanceof DOMException && error.name === "AbortError")) { const api = error instanceof OperationalApiError ? error : undefined; dispatch({ type: "failure", kind, error: api?.message ?? "Operational health is temporarily unavailable.", retryable: api?.retryable ?? true }); }
      }
    };
    operationKinds.forEach(kind => { void load(kind); });
    return () => { disposed = true; controllers.current.forEach(controller => controller.abort()); controllers.current.clear(); };
  }, [instanceId]);

  const retry = (kind: OperationalKind) => { controllers.current.get(kind)?.abort(); const controller = new AbortController(); controllers.current.set(kind, controller); dispatch({ type: "start", kind, append: false }); void getOperationalPage(instanceId, kind, { limit: 50, signal: controller.signal }).then(page => dispatch({ type: "success", kind, page, append: false })).catch(error => { if (!(error instanceof DOMException && error.name === "AbortError")) dispatch({ type: "failure", kind, error: error instanceof OperationalApiError ? error.message : "Operational health is temporarily unavailable.", retryable: true }); }); };
  const more = (kind: OperationalKind, cursor: string) => { controllers.current.get(kind)?.abort(); const controller = new AbortController(); controllers.current.set(kind, controller); dispatch({ type: "start", kind, append: true }); void getOperationalPage(instanceId, kind, { limit: 50, cursor, signal: controller.signal }).then(page => dispatch({ type: "success", kind, page, append: true })).catch(error => { if (!(error instanceof DOMException && error.name === "AbortError")) dispatch({ type: "failure", kind, error: error instanceof OperationalApiError ? error.message : "Operational health is temporarily unavailable.", retryable: true }); }); };
  const cancel = (kind: OperationalKind) => { controllers.current.get(kind)?.abort(); controllers.current.delete(kind); dispatch({ type: "cancel", kind }); };
  const model = buildOperationsRenderModel(cards);

  return <section aria-label="Operations"><h2>Operations</h2><div className="operations-grid">{operationKinds.map(kind => { const card = model.cards[kind]; const page = card.page; return <article key={kind} aria-label={card.label}><h3>{card.label}</h3>{card.status === "Loading" ? <><p>Loading</p><button type="button" onClick={() => cancel(kind)}>Cancel</button></> : card.status === "Error" && page === undefined ? <><p role="alert">{card.error ?? "Operational health is temporarily unavailable."}</p>{card.canRetry ? <button type="button" onClick={() => retry(kind)}>Retry</button> : null}</> : page ? <><p>{card.status}{page.truncated ? " · Partial coverage" : ""}</p>{page.state === "NoData" ? <p>No data is available for this target.</p> : page.state === "Unsupported" ? <p>Unsupported by this target.</p> : page.state === "PermissionDenied" ? <p>Permission denied.</p> : null}{page.visibilityScope ? <p>Visibility: {page.visibilityScope}</p> : null}{page.coverageFromUtc || page.coverageToUtc ? <p>Coverage: {page.coverageFromUtc ?? "unknown"} to {page.coverageToUtc ?? "unknown"}</p> : null}{kind === "agent" ? <p>Job messages and source local time are unavailable; first-observed timestamps are UTC.</p> : null}<table><tbody>{card.items.map((item, index) => <tr key={`${kind}-${index}`}>{Object.entries(item).map(([name, value]) => <td key={name}>{displayValue(value)}</td>)}</tr>)}</tbody></table>{card.error ? <p role="alert">{card.error}</p> : null}{card.canLoadMore && page.nextCursor ? <button type="button" disabled={card.loading} onClick={() => more(kind, page.nextCursor!)}>Load more</button> : null}</> : <p>Loading</p>}</article>; })}</div></section>;
}
