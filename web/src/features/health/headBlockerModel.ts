import type { ActivityPage, BlockingEdge } from "../activity/activityTypes";
import { blockingPageIsComplete } from "../activity/blockingProjectionModel.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";

export interface HeadBlockerFinding { readonly sessionId: number; readonly blockedSessions: number; readonly maximumWaitMilliseconds: string; }
export function headBlockerFindings(page: ActivityPage<BlockingEdge>, targetRevision: number) {
  const roots = new Map<number, { sessions: Set<number>; maximum: bigint }>();
  let unresolvedEdges = 0;
  const time = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
  try {
    if (!page.evidence || page.evidence.collectorId !== "blocking.current" || page.evidence.targetRevision !== String(targetRevision) || !Number.isSafeInteger(targetRevision) || targetRevision < 1)
      return { items: [] as HeadBlockerFinding[], unresolvedEdges: page.items.length, complete: false, comparable: false };
    time(page.evidence.completedAtUtc);
    for (const edge of page.items) {
      if (edge.chainState !== "resolved" || edge.blockerKind !== "session" || !Number.isSafeInteger(edge.blockerSessionId) || edge.blockerSessionId! < 1 || edge.blockerSessionId! > 32767 ||
          !Number.isSafeInteger(edge.rootBlockerSessionId) || edge.rootBlockerSessionId! < 1 || edge.rootBlockerSessionId! > 32767 ||
          !Number.isSafeInteger(edge.blockedSessionId) || edge.blockedSessionId < 1 || edge.blockedSessionId > 32767 ||
          edge.rootBlockerSessionId === edge.blockedSessionId || !Number.isSafeInteger(edge.chainDepth) || edge.chainDepth < 1 || edge.chainDepth > (page.maximumChainDepth ?? 32) || !/^\d{1,24}$/u.test(edge.waitDurationMilliseconds)) {
        unresolvedEdges++; continue;
      }
      time(edge.observedAtUtc);
      const root = roots.get(edge.rootBlockerSessionId!) ?? { sessions: new Set<number>(), maximum: 0n };
      root.sessions.add(edge.blockedSessionId); root.maximum = root.maximum > BigInt(edge.waitDurationMilliseconds) ? root.maximum : BigInt(edge.waitDurationMilliseconds);
      roots.set(edge.rootBlockerSessionId!, root);
    }
    const items = [...roots].map(([sessionId, root]) => ({ sessionId, blockedSessions: root.sessions.size, maximumWaitMilliseconds: String(root.maximum) }))
      .sort((a, b) => b.blockedSessions - a.blockedSessions || (BigInt(a.maximumWaitMilliseconds) > BigInt(b.maximumWaitMilliseconds) ? -1 : BigInt(a.maximumWaitMilliseconds) < BigInt(b.maximumWaitMilliseconds) ? 1 : a.sessionId - b.sessionId));
    return { items, unresolvedEdges, comparable: true, complete: blockingPageIsComplete(page) && page.evidence.reason === "completed" && unresolvedEdges === 0 && page.evidence.loss === undefined };
  } catch { return { items: [] as HeadBlockerFinding[], unresolvedEdges: page.items.length, complete: false, comparable: false }; }
}
