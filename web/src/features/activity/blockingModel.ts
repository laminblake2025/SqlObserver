export type BlockerValue = number | null | undefined;

export function isBlockingRelationship(value: BlockerValue): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value !== 0;
}

export function formatBlockingTarget(value: BlockerValue): string {
  if (!isBlockingRelationship(value)) return "—";
  return value > 0 ? `session ${value}` : `special SQL blocker (${value})`;
}
