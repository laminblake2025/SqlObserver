import type { DeadlockDetail } from "./deadlockTypes";

export interface DeadlockGraphNode {
  readonly sessionId: number;
  readonly isVictim: boolean;
  readonly x: number;
  readonly y: number;
}

export interface DeadlockGraphPoint {
  readonly x: number;
  readonly y: number;
}

export interface DeadlockGraphEdge {
  readonly waiterSessionId: number;
  readonly blockerSessionId: number;
  readonly resourceCategory: string;
  readonly lockMode: string;
  readonly path: string;
  readonly start: DeadlockGraphPoint;
  readonly end: DeadlockGraphPoint;
  readonly label: DeadlockGraphPoint;
}

export interface DeadlockGraph {
  readonly width: number;
  readonly height: number;
  readonly nodes: readonly DeadlockGraphNode[];
  readonly edges: readonly DeadlockGraphEdge[];
}

const NODE_WIDTH = 156;
const NODE_HEIGHT = 84;
const HALF_NODE_WIDTH = NODE_WIDTH / 2;
const HALF_NODE_HEIGHT = NODE_HEIGHT / 2;
const EDGE_LANE_GAP = 48;

/**
 * Lays out every participant referenced by a bounded deadlock detail. The
 * relationship direction remains waiter -> blocker; it is never inferred from
 * participant order or from the victim flag.
 */
export function buildDeadlockGraph(detail: DeadlockDetail): DeadlockGraph {
  const victimBySession = new Map(detail.participants.map((participant) => [participant.sessionId, participant.isVictim]));
  const ids = new Set<number>(detail.participants.map((participant) => participant.sessionId));
  for (const relation of detail.relations) {
    ids.add(relation.waiterSessionId);
    ids.add(relation.blockerSessionId);
  }
  const sessionIds = [...ids].sort((left, right) => left - right);
  const columns = Math.max(1, Math.min(4, Math.ceil(Math.sqrt(sessionIds.length))));
  const rows = Math.max(1, Math.ceil(sessionIds.length / columns));
  const relationGroups = new Map<string, number[]>();
  detail.relations.forEach((relation, index) => {
    const key = relation.waiterSessionId === relation.blockerSessionId
      ? `self:${relation.waiterSessionId}`
      : `pair:${Math.min(relation.waiterSessionId, relation.blockerSessionId)}:${Math.max(relation.waiterSessionId, relation.blockerSessionId)}`;
    const group = relationGroups.get(key);
    if (group) group.push(index);
    else relationGroups.set(key, [index]);
  });
  const maximumLane = Math.max(0, ...[...relationGroups.entries()].filter(([key]) => !key.startsWith("self:")).map(([, indices]) => (indices.length - 1) / 2 * EDGE_LANE_GAP));
  const selfGroupSize = Math.max(0, ...[...relationGroups.entries()].filter(([key]) => key.startsWith("self:")).map(([, indices]) => indices.length));
  const maximumSelfRadius = selfGroupSize === 0 ? 0 : HALF_NODE_WIDTH + 44 + Math.floor((selfGroupSize - 1) / 2) * 34;
  const horizontalPadding = Math.max(220, HALF_NODE_WIDTH + maximumLane + 24, maximumSelfRadius + 24);
  const verticalPadding = Math.max(110, HALF_NODE_HEIGHT + maximumLane + 24);
  const width = Math.max(760, columns * 260 + horizontalPadding * 2);
  const height = Math.max(300, rows * 200 + verticalPadding * 2);
  const nodes = sessionIds.map((sessionId, index) => ({
    sessionId,
    isVictim: victimBySession.get(sessionId) === true,
    x: horizontalPadding + (index % columns) * 260,
    y: verticalPadding + Math.floor(index / columns) * 200,
  }));
  const nodeBySession = new Map(nodes.map((node) => [node.sessionId, node]));
  const edgeLanes = new Map<number, { readonly lane: number; readonly slot: number }>();
  for (const indices of relationGroups.values()) {
    const center = (indices.length - 1) / 2;
    indices.forEach((index, slot) => edgeLanes.set(index, { lane: (slot - center) * EDGE_LANE_GAP, slot }));
  }
  return {
    width,
    height,
    nodes,
    edges: detail.relations.map((relation, index) => {
      const from = nodeBySession.get(relation.waiterSessionId)!;
      const to = nodeBySession.get(relation.blockerSessionId)!;
      const lane = edgeLanes.get(index)!;
      const geometry = relation.waiterSessionId === relation.blockerSessionId
        ? selfEdgeGeometry(from, lane.slot)
        : directedEdgeGeometry(from, to, relation.waiterSessionId < relation.blockerSessionId ? from : to, relation.waiterSessionId < relation.blockerSessionId ? to : from, lane.lane);
      return {
        waiterSessionId: relation.waiterSessionId,
        blockerSessionId: relation.blockerSessionId,
        resourceCategory: relation.resourceCategory,
        lockMode: relation.lockMode,
        ...geometry,
      };
    }),
  };
}

function directedEdgeGeometry(
  from: DeadlockGraphNode,
  to: DeadlockGraphNode,
  canonicalFrom: DeadlockGraphNode,
  canonicalTo: DeadlockGraphNode,
  lane: number,
): { readonly path: string; readonly start: DeadlockGraphPoint; readonly end: DeadlockGraphPoint; readonly label: DeadlockGraphPoint } {
  const canonicalDirection = unitVector(canonicalFrom, canonicalTo);
  const normal = point(-canonicalDirection.y, canonicalDirection.x);
  const start = boundaryPoint(from, to, normal, lane);
  const end = boundaryPoint(to, from, normal, lane);
  const midpoint = point((start.x + end.x) / 2, (start.y + end.y) / 2);
  const control = point(midpoint.x + normal.x * lane, midpoint.y + normal.y * lane);
  const labelOffset = lane === 0 ? -12 : lane / 2 + Math.sign(lane) * 8;
  const label = point(midpoint.x + normal.x * labelOffset, midpoint.y + normal.y * labelOffset);
  return {
    path: lane === 0 ? `M ${formatPoint(start)} L ${formatPoint(end)}` : `M ${formatPoint(start)} Q ${formatPoint(control)} ${formatPoint(end)}`,
    start,
    end,
    label,
  };
}

function selfEdgeGeometry(node: DeadlockGraphNode, slot: number): { readonly path: string; readonly start: DeadlockGraphPoint; readonly end: DeadlockGraphPoint; readonly label: DeadlockGraphPoint } {
  const side = slot % 2 === 0 ? 1 : -1;
  const ring = Math.floor(slot / 2);
  const radius = HALF_NODE_WIDTH + 44 + ring * 34;
  const verticalOffset = HALF_NODE_HEIGHT * 0.45;
  const start = point(node.x + side * HALF_NODE_WIDTH, node.y - verticalOffset);
  const end = point(node.x + side * HALF_NODE_WIDTH, node.y + verticalOffset);
  const outerX = node.x + side * radius;
  const controlTop = point(outerX, node.y - verticalOffset - 26);
  const controlBottom = point(outerX, node.y + verticalOffset + 26);
  return {
    path: `M ${formatPoint(start)} C ${formatPoint(controlTop)} ${formatPoint(controlBottom)} ${formatPoint(end)}`,
    start,
    end,
    label: point(outerX + side * 12, node.y - 8),
  };
}

function boundaryPoint(from: DeadlockGraphNode, to: DeadlockGraphNode, offsetDirection?: DeadlockGraphPoint, lane = 0): DeadlockGraphPoint {
  const delta = point(to.x - from.x, to.y - from.y);
  if (delta.x === 0 && delta.y === 0) return point(from.x + HALF_NODE_WIDTH, from.y);
  const scale = Math.min(
    delta.x === 0 ? Number.POSITIVE_INFINITY : HALF_NODE_WIDTH / Math.abs(delta.x),
    delta.y === 0 ? Number.POSITIVE_INFINITY : HALF_NODE_HEIGHT / Math.abs(delta.y),
  );
  const boundary = point(from.x + delta.x * scale, from.y + delta.y * scale);
  if (!offsetDirection || lane === 0) return boundary;
  const onVerticalEdge = Math.abs(Math.abs(boundary.x - from.x) - HALF_NODE_WIDTH) < 0.001;
  if (onVerticalEdge) {
    const offset = clamp(offsetDirection.y * lane, -HALF_NODE_HEIGHT + 8, HALF_NODE_HEIGHT - 8);
    return point(boundary.x, clamp(boundary.y + offset, from.y - HALF_NODE_HEIGHT + 8, from.y + HALF_NODE_HEIGHT - 8));
  }
  const offset = clamp(offsetDirection.x * lane, -HALF_NODE_WIDTH + 8, HALF_NODE_WIDTH - 8);
  return point(clamp(boundary.x + offset, from.x - HALF_NODE_WIDTH + 8, from.x + HALF_NODE_WIDTH - 8), boundary.y);
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, value));
}

function unitVector(from: DeadlockGraphNode, to: DeadlockGraphNode): DeadlockGraphPoint {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const length = Math.hypot(dx, dy);
  return point(dx / length, dy / length);
}

function point(x: number, y: number): DeadlockGraphPoint {
  return { x, y };
}

function formatPoint(value: DeadlockGraphPoint): string {
  return `${formatNumber(value.x)} ${formatNumber(value.y)}`;
}

function formatNumber(value: number): string {
  return Number(value.toFixed(2)).toString();
}
