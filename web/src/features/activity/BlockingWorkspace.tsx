import { useState, type ReactNode } from "react";
import { DiagnosticIcon } from "../../components/WorkspaceNavigation";
import { formatBlockingTarget, type BlockingTreeGroup, type BlockingTreeNode } from "./blockingModel";
import type { LivePage, LiveRow } from "./liveActivityApi";

export function BlockingWorkspace({ page, groups, selectedGroupKey, selectedIdentity, onSelectGroup, onInspect, filtered, paged, children, selectedDatabaseId, coverage }: {
  readonly page: LivePage; readonly groups: readonly BlockingTreeGroup[]; readonly selectedGroupKey: string;
  readonly selectedIdentity?: string; readonly onSelectGroup: (key: string) => void; readonly onInspect: (row: LiveRow) => void;
  readonly filtered: boolean; readonly paged: boolean; readonly children?: ReactNode;
  readonly selectedDatabaseId?: number; readonly coverage?: string;
}) {
  const [expanded, setExpanded] = useState(false);
  const shown = selectedGroupKey ? groups.filter(group => group.key === selectedGroupKey) : expanded ? groups : groups.slice(0, 1);
  const node = (row: LiveRow, root = false) => <button type="button" className={`blocking-node${root ? " root" : ""}`}
    aria-pressed={row.identity === selectedIdentity} aria-label={`Inspect captured session ${row.sessionId}/${row.requestId ?? "idle"}`}
    onClick={() => onInspect(row)}><DiagnosticIcon name="sessions"/><strong>SPID {row.sessionId}{root ? " · Head blocker" : ""}</strong>
    <span>{row.databaseName ?? (row.databaseId === null ? "Database unavailable" : `Database ${row.databaseId}`)} · {row.status}</span>
    <span>{root ? row.command ?? "Command unavailable" : row.waitType ?? "Wait unavailable"}</span>{selectedDatabaseId !== undefined && row.databaseId !== selectedDatabaseId && <small>Context outside selected database</small>}</button>;
  const branch = (item: BlockingTreeNode, depth = 0): ReactNode => <div className="blocking-graph-branch" key={item.sessionId}>
    {node(item.rows[0]!)}{item.rows.length > 1 && <details><summary>{item.rows.length} captured requests</summary>{item.rows.slice(1).map(row => <div key={row.identity}>{node(row)}</div>)}</details>}
    {item.children.length > 0 && (depth < 2 || expanded ? <div className="blocking-node-children">{item.children.map(child => branch(child, depth + 1))}</div>
      : <button type="button" onClick={() => setExpanded(true)}>Show deeper relationships</button>)}
  </div>;
  return <section className="blocking-graph-panel" aria-label="Blocking tree · loaded snapshot"><div className="section-heading"><h4>Blocking chain</h4>
        {groups.length > 1 && <button type="button" className="secondary-button" onClick={() => setExpanded(!expanded)}>{expanded ? "First chain" : `All ${groups.length} chains`}</button>}</div>
        <p className="blocking-scope" role="status">{coverage ?? "Loaded captured metadata"}{filtered ? " · Filtered list; graph retains captured context" : ""}{paged || page.nextCursor ? " · Paged evidence" : ""}{page.truncated ? " · Truncated capture" : ""}. Missing sessions may change the apparent root.</p>
        {shown.length === 0 ? <p className="empty-state">{page.state === "unavailable" ? "Blocking relationships are unavailable for this observation." : selectedGroupKey ? "The selected chain is absent from this loaded page." : "No blocking relationships in the loaded rows. This does not establish an all-clear signal."}</p> :
          <div className="blocking-graph-roots">{shown.map(group => {
            const rootId = group.key.startsWith("session:") ? Number(group.key.slice(8)) : undefined;
            const root = rootId === undefined ? undefined : page.rows.find(row => row.sessionId === rootId);
            return <div className="blocking-graph-root" key={group.key}>
              {root ? node(root, true) : <div className="blocking-node root"><strong>{group.rootLabel}</strong><span>{group.issue ?? (rootId ? "Root row missing from this page" : "Unresolved SQL blocker")}</span></div>}
              {group.issue && <p role="status">{group.issue}</p>}
              <div className="blocking-node-children">{(expanded ? group.nodes : group.nodes.slice(0, 3)).map(item => branch(item))}</div>
              {!expanded && group.nodes.length > 3 && <button type="button" className="secondary-button" onClick={() => setExpanded(true)}>+{group.nodes.length - 3} direct blocked sessions</button>}
              {group.unresolvedRows.length > 0 && <details open><summary>Unresolved relationships ({group.unresolvedRows.length})</summary>{group.unresolvedRows.map(row => <div key={row.identity}>{node(row)}<small>Waits on {formatBlockingTarget(row.blocker)}</small></div>)}</details>}
              <button type="button" className="secondary-button blocking-chain-action" aria-label={`Filter chain: ${group.rootLabel}`} onClick={() => onSelectGroup(group.key)}>Filter to this chain</button>
            </div>;
          })}</div>}
        <p className="table-note">Edges and details use the same captured snapshot. Missing and unresolved evidence remains incomplete. Elapsed time is not wait duration.</p>{children}
      </section>;
}
