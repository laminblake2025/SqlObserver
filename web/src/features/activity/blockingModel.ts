import type { LiveRow } from "./liveActivityApi";

export type BlockerValue = number | null | undefined;

export interface BlockingTreeNode {
  readonly sessionId: number;
  readonly rows: readonly LiveRow[];
  readonly children: readonly BlockingTreeNode[];
}

export interface BlockingTreeGroup {
  readonly key: string;
  readonly rootLabel: string;
  readonly rootLoaded: boolean;
  readonly issue?: string;
  readonly nodes: readonly BlockingTreeNode[];
  readonly unresolvedRows: readonly LiveRow[];
}

export function isBlockingRelationship(value: BlockerValue): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value !== 0;
}

export function formatBlockingTarget(value: BlockerValue): string {
  if (!isBlockingRelationship(value)) return "—";
  return value > 0 ? `session ${value}` : `special SQL blocker (${value})`;
}

// A session can have several requests, but it has one place in a tree only if
// all visible blocking requests agree on its blocker. Keep conflicting and
// cyclic evidence visible without inventing a head blocker.
export function buildBlockingTree(rows: readonly LiveRow[]): readonly BlockingTreeGroup[] {
  const relationships = rows.filter(row => isBlockingRelationship(row.blocker));
  const bySession = new Map<number, LiveRow[]>();
  for (const row of relationships) {
    const current = bySession.get(row.sessionId) ?? [];
    current.push(row);
    bySession.set(row.sessionId, current);
  }
  const loaded = new Set(rows.map(row => row.sessionId));
  const groups = new Map<string, { label: string; issue?: string; rows: LiveRow[] }>();
  const blockerFor = (sessionId: number): number | undefined => {
    const blockers = new Set(bySession.get(sessionId)?.map(row => row.blocker));
    return blockers.size === 1 ? [...blockers][0] ?? undefined : undefined;
  };
  for (const row of relationships) {
    const path: number[] = [];
    let current = row.sessionId;
    let key: string;
    let label: string;
    let issue: string | undefined;
    while (true) {
      if (path.includes(current)) {
        const cycle = path.slice(path.indexOf(current));
        key = `cycle:${Math.min(...cycle)}`;
        label = `Cycle involving session ${Math.min(...cycle)}`;
        issue = "Cycle reported in loaded relationships; head blocker cannot be resolved.";
        break;
      }
      path.push(current);
      const blocker = blockerFor(current);
      if (blocker === undefined) {
        key = `ambiguous:${current}`;
        label = `Session ${current} has multiple blockers`;
        issue = "Loaded requests disagree about the blocker; chain cannot be resolved.";
        break;
      }
      if (blocker < 0) {
        key = `special:${blocker}`;
        label = formatBlockingTarget(blocker);
        break;
      }
      if (!bySession.has(blocker)) {
        key = `session:${blocker}`;
        label = `Session ${blocker}`;
        break;
      }
      current = blocker;
    }
    const group = groups.get(key) ?? { label, issue, rows: [] };
    group.rows.push(row);
    groups.set(key, group);
  }
  return [...groups.entries()].sort(([left], [right]) => left.localeCompare(right)).map(([key, group]) => {
    if (group.issue) return { key, rootLabel: group.label, rootLoaded: false, issue: group.issue, nodes: [], unresolvedRows: group.rows };
    const groupedRows = new Map<number, LiveRow[]>();
    for (const row of group.rows) groupedRows.set(row.sessionId, [...(groupedRows.get(row.sessionId) ?? []), row]);
    const children = new Map<number, number[]>();
    for (const [sessionId] of groupedRows) {
      const blocker = blockerFor(sessionId)!;
      children.set(blocker, [...(children.get(blocker) ?? []), sessionId]);
    }
    const build = (parent: number): BlockingTreeNode[] => (children.get(parent) ?? []).sort((a, b) => a - b).map(sessionId => ({
      sessionId,
      rows: groupedRows.get(sessionId)!,
      children: build(sessionId),
    }));
    const root = Number(key.split(":")[1]);
    return { key, rootLabel: group.label, rootLoaded: key.startsWith("session:") && loaded.has(root), nodes: build(root), unresolvedRows: [] };
  });
}
