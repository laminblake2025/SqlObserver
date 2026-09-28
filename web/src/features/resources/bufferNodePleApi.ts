export interface BufferNodePleNode {
  readonly nodeId: number;
  readonly sampleCount: number;
  readonly lastObservedAtUtc: string;
}

export interface BufferNodePlePoint {
  readonly bucketStartUtc: string;
  readonly sampleCount: number;
  readonly minimumSeconds: number;
  readonly averageSeconds: number;
  readonly maximumSeconds: number;
}

export interface BufferNodePleWindow {
  readonly targetId: string;
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly semantics: "numa_node_page_life_expectancy_gauge";
  readonly unit: "seconds";
  readonly revisionScope: "current";
  readonly bucketSeconds: 3600;
  readonly selectedNodeId: number | null;
  readonly visibilityGapCount: number;
  readonly nodes: readonly BufferNodePleNode[];
  readonly points: readonly BufferNodePlePoint[];
  readonly aggregatePoints: readonly BufferNodePlePoint[];
}

function object(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function integer(value: unknown, minimum: number, maximum: number): value is number {
  return Number.isSafeInteger(value) && (value as number) >= minimum && (value as number) <= maximum;
}

export function parseBufferNodePleWindow(value: unknown, targetId: string,
  fromUtc: string, toUtc: string, requestedNodeId: number | null): BufferNodePleWindow {
  if (!object(value) || typeof value.targetId !== "string" ||
      value.targetId.toLowerCase() !== targetId.toLowerCase() ||
      typeof value.fromUtc !== "string" || typeof value.toUtc !== "string" ||
      Date.parse(value.fromUtc) !== Date.parse(fromUtc) ||
      Date.parse(value.toUtc) !== Date.parse(toUtc) ||
      value.semantics !== "numa_node_page_life_expectancy_gauge" ||
      value.unit !== "seconds" || value.revisionScope !== "current" ||
      value.bucketSeconds !== 3600 ||
      !integer(value.visibilityGapCount, 0, Number.MAX_SAFE_INTEGER) ||
      !Array.isArray(value.nodes) || value.nodes.length > 128 ||
      !Array.isArray(value.points) || value.points.length > 169 ||
      !Array.isArray(value.aggregatePoints) || value.aggregatePoints.length > 169)
    throw new Error("Buffer Node PLE evidence did not match the selected server and window.");
  const nodes = value.nodes as unknown[];
  const points = value.points as unknown[];
  const aggregatePoints = value.aggregatePoints as unknown[];
  const seen = new Set<number>();
  if (nodes.some(node => !object(node) || !integer(node.nodeId, 0, 1023) ||
      !integer(node.sampleCount, 1, Number.MAX_SAFE_INTEGER) ||
      typeof node.lastObservedAtUtc !== "string" ||
      !Number.isFinite(Date.parse(node.lastObservedAtUtc)) ||
      Date.parse(node.lastObservedAtUtc) < Date.parse(fromUtc) ||
      Date.parse(node.lastObservedAtUtc) >= Date.parse(toUtc) ||
      seen.has(node.nodeId) || !seen.add(node.nodeId)) ||
      (value.selectedNodeId !== null && !integer(value.selectedNodeId, 0, 1023)) ||
      (value.selectedNodeId === null && points.length > 0) ||
      value.selectedNodeId !== (requestedNodeId ?? (nodes.length ?
        (nodes[0] as Record<string, unknown>).nodeId : null)))
    throw new Error("Buffer Node PLE node identities are invalid.");
  for (const series of [points, aggregatePoints]) {
    let previousBucket = -Infinity;
    if (series.some(point => {
      if (!object(point) || typeof point.bucketStartUtc !== "string") return true;
      const bucket = Date.parse(point.bucketStartUtc);
      if (!Number.isFinite(bucket) || bucket < Math.floor(Date.parse(fromUtc) / 3_600_000) * 3_600_000 ||
          bucket >= Date.parse(toUtc) || bucket <= previousBucket ||
          !integer(point.sampleCount, 1, 25_000) ||
          typeof point.minimumSeconds !== "number" || !Number.isFinite(point.minimumSeconds) ||
          typeof point.averageSeconds !== "number" || !Number.isFinite(point.averageSeconds) ||
          typeof point.maximumSeconds !== "number" || !Number.isFinite(point.maximumSeconds) ||
          point.minimumSeconds < 0 || point.minimumSeconds > point.averageSeconds ||
          point.averageSeconds > point.maximumSeconds) return true;
      previousBucket = bucket;
      return false;
    })) throw new Error("Buffer Node PLE hourly gauges are invalid.");
  }
  return value as unknown as BufferNodePleWindow;
}

export async function getBufferNodePleWindow(targetId: string, fromUtc: string,
  toUtc: string, nodeId: number | null, signal: AbortSignal): Promise<BufferNodePleWindow> {
  const parameters = new URLSearchParams({ fromUtc, toUtc });
  if (nodeId !== null) parameters.set("nodeId", String(nodeId));
  const response = await fetch(
    `/api/v1/observation-targets/${encodeURIComponent(targetId)}/resources/buffer-nodes/ple?${parameters}`,
    { credentials: "same-origin", headers: { Accept: "application/json" }, signal },
  );
  if (!response.ok) throw new Error(response.status === 403
    ? "You are not authorized to view Buffer Node PLE."
    : response.status === 400 ? "Choose a valid NUMA node and a UTC window of seven days or less."
      : "Buffer Node PLE history is unavailable.");
  return parseBufferNodePleWindow(await response.json() as unknown,
    targetId, fromUtc, toUtc, nodeId);
}
