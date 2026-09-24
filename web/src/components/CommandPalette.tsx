import { useEffect, useMemo, useRef, useState } from "react";
import { listObservationTargets } from "../features/targets/targetApi";
import type { ObservationTargetSummary } from "../features/targets/targetTypes";
import { overviewHref } from "../features/overview/overviewModel";
import type { OverviewScope } from "../features/overview/overviewTypes";

const pageSize = 100;
const maximumPages = 20;

export function CommandPalette({ open, onClose, scope }: {
  readonly open: boolean;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState("");
  const [targets, setTargets] = useState<readonly ObservationTargetSummary[]>([]);
  const [loading, setLoading] = useState(false);
  const [moreAvailable, setMoreAvailable] = useState(false);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (!open) return;
    const previous = document.activeElement;
    dialog.current?.showModal();
    search.current?.focus();
    return () => {
      dialog.current?.close();
      if (previous instanceof HTMLElement) previous.focus();
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setQuery("");
    setTargets([]);
    setLoading(true);
    setMoreAvailable(false);
    setError(false);
    void (async () => {
      const collected: ObservationTargetSummary[] = [];
      const visited = new Set<string>();
      let cursor: string | undefined;
      for (let pageNumber = 0; pageNumber < maximumPages; pageNumber += 1) {
        const page = await listObservationTargets(controller.signal, cursor, pageSize);
        if (controller.signal.aborted) return;
        collected.push(...page.items);
        setTargets([...collected]);
        if (!page.nextCursor) return;
        if (visited.has(page.nextCursor)) throw new Error("Server paging repeated a cursor.");
        visited.add(page.nextCursor);
        cursor = page.nextCursor;
        if (pageNumber === maximumPages - 1) setMoreAvailable(true);
      }
    })().catch(() => {
      if (!controller.signal.aborted) setError(true);
    }).finally(() => {
      if (!controller.signal.aborted) setLoading(false);
    });
    return () => controller.abort();
  }, [open]);

  const matches = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    return targets.filter(target => !needle || [target.displayName, target.instanceKey, target.host, target.instanceId]
      .some(value => value.toLocaleLowerCase().includes(needle)))
      .sort((left, right) => left.displayName.localeCompare(right.displayName) || left.instanceId.localeCompare(right.instanceId))
      .slice(0, 10);
  }, [targets, query]);

  const jump = (target: ObservationTargetSummary) => {
    location.hash = overviewHref(scope, "health", target.instanceId);
    onClose();
  };

  return <dialog ref={dialog} className="command-palette" aria-labelledby="command-palette-title" onCancel={onClose}>
    <div className="command-palette-header">
      <div><h2 id="command-palette-title">Jump to server</h2><p>Search servers you can access by name, host, key, or ID.</p></div>
      <button type="button" className="secondary-button" onClick={onClose} aria-label="Close server search">Close</button>
    </div>
    <input ref={search} type="search" aria-label="Search servers" placeholder="Search servers…" value={query}
      onChange={event => setQuery(event.target.value)}
      onKeyDown={event => {
        if (event.key === "ArrowDown") {
          event.preventDefault();
          dialog.current?.querySelector<HTMLButtonElement>(".command-palette-result")?.focus();
        }
      }} />
    <p className="command-palette-status" role="status">
      {loading ? `Searching authorized servers… ${targets.length} loaded` : error ? "Server search is incomplete. Try again later." : moreAvailable ? "Showing the first 2,000 authorized servers. Use Servers to browse further." : `${targets.length} authorized servers searched`}
    </p>
    <div className="command-palette-results" aria-label="Matching servers">
      {matches.map(target => <button key={target.instanceId} className="command-palette-result" type="button" onClick={() => jump(target)}
        onKeyDown={event => {
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
          event.preventDefault();
          const buttons = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>(".command-palette-result") ?? []);
          const index = buttons.indexOf(event.currentTarget);
          (buttons[index + (event.key === "ArrowDown" ? 1 : -1)] ?? (event.key === "ArrowUp" ? search.current : buttons[0]))?.focus();
        }}>
        <strong>{target.displayName}</strong><span>{target.host} · {target.instanceKey}</span>
      </button>)}
      {!loading && matches.length === 0 && <p className="command-palette-empty">No matching servers in the loaded results.</p>}
    </div>
  </dialog>;
}
