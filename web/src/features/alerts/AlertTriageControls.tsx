import { useTimeFormatter } from "../../TimeDisplayContext";
import type { useAlertInbox } from "./useAlertInbox.ts";
export function AlertTriageControls({ inbox, label, onLatest, mutationBusy = false }: { readonly inbox: ReturnType<typeof useAlertInbox>; readonly label: string; readonly onLatest: () => void; readonly mutationBusy?: boolean }) {
  const time = useTimeFormatter();
  return <>
    {inbox.error && <p role="alert" className="status-message">{inbox.error} {inbox.canRetry && <button type="button" onClick={inbox.retry} disabled={inbox.loading}>Retry alert read</button>}</p>}
    <p className="alert-triage-receipt">{inbox.receipts.length ? `Native row-time cutoff: ${time(inbox.snapshotUtc!)} · ${inbox.receipts.length} received page(s), ${inbox.rows.length} loaded rows. Last page received ${time(inbox.receipts.at(-1)!.receivedAtUtc)}.` : "No alert receipt is available."}</p>
    <p className="alert-triage-note">{inbox.retained ? "Retained alert investigation; workspace polling preserves these received pages. " : "Latest inbox mode; idle pages follow workspace polling. "}Alerts remain current-state, server-wide reads independent of Rewind and database selection. The cutoff excludes newer fired/first-observed rows; it does not freeze alert state, names or permissions between page reads. Groups and filters cover loaded rows only.</p>
    <nav className="pager alert-triage-controls" aria-label={`${label} reads`}>
      <span>{inbox.nextCursor ? "More alerts are available." : "End of loaded alerts."} Maximum 20 pages / 2,000 retained rows.</span>
      <button type="button" className="secondary-button" disabled={!inbox.nextCursor || inbox.loading} onClick={inbox.more}>{inbox.loading && inbox.rows.length ? "Loading…" : "Load more"}</button>
      <button type="button" className="secondary-button" disabled={inbox.loading || mutationBusy} onClick={onLatest}>Return {label} to latest</button>
      {inbox.loading && <button type="button" className="secondary-button" onClick={inbox.cancel}>Cancel alert read</button>}
    </nav>
  </>;
}
