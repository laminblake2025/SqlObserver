import { useEffect, useId, useRef, useState } from "react";
import { useTimeFormatter } from "../TimeDisplayContext";
import { getDatabaseHealth } from "../features/health/healthApi";
import { DatabaseScopeLoadError, loadDatabaseScopeChoices, type DatabaseScopeChoices } from "./databaseScopeModel";

const emptyChoices: DatabaseScopeChoices = { items: [], complete: false, repositoryTimeUtc: undefined };
type InventoryState = {
  readonly targetId: string;
  readonly requestKey: string;
  readonly selectionKey: string;
  readonly choices: DatabaseScopeChoices;
  readonly loading: boolean;
  readonly error?: string;
  readonly denied?: boolean;
};

export function DatabaseScopeControls({ targetId, databaseId, onChange, refresh }: {
  readonly targetId: string;
  readonly databaseId?: number;
  readonly onChange: (databaseId: number | undefined) => void;
  readonly refresh: number;
}) {
  const descriptionId = useId();
  const missingDescriptionId = useId();
  const timeLabel = useTimeFormatter();
  const [state, setState] = useState<InventoryState>();
  const context = useRef({ target: targetId.toLowerCase(), generation: 0 });
  if (context.current.target !== targetId.toLowerCase()) context.current = { target: targetId.toLowerCase(), generation: context.current.generation + 1 };
  const selectionKey = JSON.stringify([context.current.target, context.current.generation]);
  const requestKey = JSON.stringify([selectionKey, refresh]);
  const owner = useRef(requestKey); owner.current = requestKey;
  const current = state?.selectionKey === selectionKey ? state : undefined;
  const choices = current?.choices ?? emptyChoices;
  const loading = current?.requestKey !== requestKey || current.loading;
  useEffect(() => {
    const controller = new AbortController();
    const ownsRequest = () => !controller.signal.aborted && owner.current === requestKey;
    setState(previous => ({ targetId, requestKey, selectionKey, loading: true,
      choices: previous?.selectionKey === selectionKey ? previous.choices : emptyChoices }));
    void loadDatabaseScopeChoices(targetId, controller.signal, getDatabaseHealth)
      .then(loaded => { if (ownsRequest()) setState({ targetId, requestKey, selectionKey, choices: loaded, loading: false }); })
      .catch(failure => {
        if (!ownsRequest()) return;
        const partial = failure instanceof DatabaseScopeLoadError ? failure.choices : emptyChoices;
        const denied = failure instanceof DatabaseScopeLoadError && (failure.status === 401 || failure.status === 403);
        setState(previous => {
          if (!ownsRequest()) return previous;
          if (denied) return { targetId, requestKey, selectionKey, loading: false, choices: emptyChoices, denied: true,
            error: "Your access to this server's database inventory could not be verified. Refresh after access is restored." };
          const known = new Map((previous?.selectionKey === selectionKey ? previous.choices.items : []).map(item => [item.databaseId, item]));
          for (const item of partial.items) known.set(item.databaseId, item);
          return { targetId, requestKey, selectionKey, loading: false,
            choices: { items: [...known.values()], complete: false,
              repositoryTimeUtc: partial.repositoryTimeUtc ?? (previous?.selectionKey === selectionKey ? previous.choices.repositoryTimeUtc : undefined) },
            error: failure instanceof Error ? failure.message : "Retry when repository access is available." };
        });
      });
    return () => controller.abort();
  }, [targetId, requestKey]);
  const selectionMissing = databaseId !== undefined && !choices.items.some(item => item.databaseId === databaseId);
  return <div className="database-scope-controls">
    <label className="scope-control">Workspace database
      <select value={databaseId === undefined ? "" : String(databaseId)} aria-describedby={selectionMissing ? `${descriptionId} ${missingDescriptionId}` : descriptionId} disabled={current?.denied === true}
        onChange={event => onChange(event.target.value === "" ? undefined : Number(event.target.value))}>
        <option value="">All databases</option>
        {selectionMissing && <option value={String(databaseId)}>Database ID {databaseId} (not in loaded inventory)</option>}
        {choices.items.map(item => <option value={String(item.databaseId)} key={item.databaseId}>{item.name} · ID {item.databaseId}</option>)}
      </select>
    </label>
    <div className="database-scope-meta"><p id={descriptionId} className="database-scope-status diagnostic-workflow-note" role={current?.error ? "alert" : "status"}>
      {current?.error ? current.denied ? current.error : `Database inventory unavailable. ${current.error}${choices.items.length ? " Previously loaded names are retained." : ""}`
        : loading ? choices.repositoryTimeUtc ? `Refreshing inventory · ${choices.items.length} databases loaded.` : "Loading database inventory…"
          : `${choices.complete ? "Loaded inventory" : "Partial inventory"} · ${choices.items.length} databases loaded.`}
    </p>
    <details className="database-inventory-details"><summary>Inventory details</summary>
      {choices.repositoryTimeUtc && <p>Repository snapshot: {timeLabel(choices.repositoryTimeUtc)}.</p>}
      <p>Inventory lists observed databases; missing entries do not mean a database is absent. Reads are bounded to 20 pages.{!loading && !current?.error && !choices.complete && " The 20-page limit was reached; more inventory may be available."}</p>
    </details></div>
    {selectionMissing && <p id={missingDescriptionId} className="database-scope-selection-note" role="status">Selected database ID {databaseId} has not been received in inventory; its filter remains selected.</p>}
    {current?.denied && databaseId !== undefined && <button type="button" className="secondary-button database-scope-clear" onClick={() => onChange(undefined)}>Clear database filter</button>}
  </div>;
}
