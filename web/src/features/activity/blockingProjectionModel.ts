import type { ActivityPage, BlockingEdge } from "./activityTypes";

export interface BlockingChainGroup {
  readonly key: string;
  readonly label: string;
  readonly edges: readonly BlockingEdge[];
}

export function groupBlockingEdges(edges: readonly BlockingEdge[]): readonly BlockingChainGroup[] {
  const groups = new Map<string, { label: string; edges: BlockingEdge[] }>();
  for (const edge of edges) {
    const root = edge.rootBlockerSessionId;
    const key = root !== undefined ? `session:${root}`
      : edge.blockerSessionId !== undefined ? `unresolved:${edge.blockerSessionId}`
      : `special:${edge.blockerKind}`;
    const label = root !== undefined ? `Head blocker · session ${root}`
      : edge.blockerSessionId !== undefined ? `Unresolved chain · session ${edge.blockerSessionId}`
      : `Unresolved blocker · ${edge.blockerKind}`;
    const group = groups.get(key) ?? { label, edges: [] };
    group.edges.push(edge);
    groups.set(key, group);
  }
  return [...groups.entries()].map(([key, group]) => ({
    key, label: group.label,
    edges: group.edges.sort((left, right) => left.chainDepth - right.chainDepth || left.blockedSessionId - right.blockedSessionId),
  })).sort((left, right) => right.edges.length - left.edges.length || left.key.localeCompare(right.key));
}

export function blockingPageIsComplete(page: ActivityPage<BlockingEdge>): boolean {
  return page.nextCursor === undefined && page.evidence?.freshness === "current"
    && page.evidence.outcome === "succeeded" && !page.evidence.isPartial;
}
