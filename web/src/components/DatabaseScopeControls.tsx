import { useEffect, useId, useState } from "react";
import { useTimeFormatter } from "../TimeDisplayContext";
import { getDatabaseHealth } from "../features/health/healthApi";
import { DatabaseScopeLoadError, loadDatabaseScopeChoices, type DatabaseScopeChoices } from "./databaseScopeModel";

const emptyChoices: DatabaseScopeChoices = { items: [], complete: false, repositoryTimeUtc: undefined };
type InventoryState = {
  readonly targetId: string;
  readonly requestKey: string;
  readonly choices: DatabaseScopeChoices;
  readonly loading: boolean;
  readonly error?: string;
};

export function DatabaseScopeControls({ targetId, databaseId, onChange, refresh }: {
  readonly targetId: string;
  readonly databaseId?: number;
  readonly onChange: (databaseId: number | undefined) => void;
  readonly refresh: number;
}) {
  const descriptionId = useId();
  const timeLabel = useTimeFormatter();
  const [state, setState] = useState<InventoryState>();
  const requestKey = JSON.stringify([targetId, refresh]);
  const current = state?.targetId === targetId ? state : undefined;
  const choices = current?.choices ?? emptyChoices;
  const loading = current?.requestKey !== requestKey || current.loading;
  useEffect(() => {
    const controller = new AbortController();
    setState(previous => ({ targetId, requestKey, loading: true,
      choices: previous?.targetId === targetId ? previous.choices : emptyChoices }));
    void loadDatabaseScopeChoices(targetId, controller.signal, getDatabaseHealth)
      .then(loaded => { if (!controller.signal.aborted) setState({ targetId, requestKey, choices: loaded, loading: false }); })
      .catch(failure => {
        if (controller.signal.aborted) return;
        const partial = failure instanceof DatabaseScopeLoadError ? failure.choices : emptyChoices;
        setState(previous => {
          const known = new Map((previous?.targetId === targetId ? previous.choices.items : []).map(item => [item.databaseId, item]));
          for (const item of partial.items) known.set(item.databaseId, item);
          return { targetId, requestKey, loading: false,
            choices: { items: [...known.values()], complete: false,
              repositoryTimeUtc: partial.repositoryTimeUtc ?? (previous?.targetId === targetId ? previous.choices.repositoryTimeUtc : undefined) },
            error: failure instanceof Error ? failure.message : "Retry when repository access is available." };
        });
      });
    return () => controller.abort();
  }, [targetId, requestKey]);
  const selectionMissing = databaseId !== undefined && !choices.items.some(item => item.databaseId === databaseId);
  return <div className="database-scope-controls">
    <label className="scope-control">Workspace database
      <select value={databaseId === undefined ? "" : String(databaseId)} aria-describedby={descriptionId}
        onChange={event => onChange(event.target.value === "" ? undefined : Number(event.target.value))}>
        <option value="">All databases</option>
        {selectionMissing && <option value={String(databaseId)}>Database ID {databaseId} (not in loaded inventory)</option>}
        {choices.items.map(item => <option value={String(item.databaseId)} key={item.databaseId}>{item.name} · ID {item.databaseId}</option>)}
      </select>
    </label>
    <p id={descriptionId} className="database-scope-status" role={current?.error ? "alert" : "status"}>
      {current?.error ? `Database inventory unavailable. ${current.error} Previously loaded names are retained.`
        : loading ? "Loading database inventory…"
          : `${choices.complete ? "Loaded inventory" : "Partial inventory"} · ${choices.items.length} databases loaded${choices.complete ? "" : " · 20-page limit reached"}.`}
      {choices.repositoryTimeUtc && <> Repository snapshot: {timeLabel(choices.repositoryTimeUtc)}.</>}
      {databaseId !== undefined && selectionMissing && " The selected database remains in scope."}
      {" Inventory lists observed databases; missing entries do not mean a database is absent."}
    </p>
  </div>;
}
