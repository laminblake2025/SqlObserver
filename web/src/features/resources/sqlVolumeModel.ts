const nonnegativeInteger = /^(?:0|[1-9]\d*)$/u;

export function volumeUsedPercent(totalBytes: string | null,
  availableBytes: string | null): string | null {
  if (totalBytes === null || availableBytes === null ||
      !nonnegativeInteger.test(totalBytes) || !nonnegativeInteger.test(availableBytes)) return null;
  const total = BigInt(totalBytes);
  const available = BigInt(availableBytes);
  if (total === 0n || available > total) return null;
  const hundredths = ((total - available) * 10_000n + total / 2n) / total;
  return `${hundredths / 100n}.${String(hundredths % 100n).padStart(2, "0")}%`;
}

export function volumeEvidenceMessage(state: string, reason: string): string {
  if (reason === "not_collected") return "SQL volume collection has not produced a snapshot yet.";
  if (reason === "evidence_expired") return "The volume snapshot has expired. Wait for a new collection.";
  if (state === "stale") return "The latest SQL-reported volume snapshot is overdue.";
  if (state === "partial") return "Some volume evidence was lost during collection.";
  if (state === "superseded") return "A newer volume snapshot is available. Return to the first page.";
  if (state === "unavailable") return `Volume evidence is unavailable (${reason.replaceAll("_", " ")}).`;
  return "Current SQL-reported volume snapshot.";
}

export function volumeDaysUntilFull(state: string, days: number | null): string {
  if (state === "full") return "Full";
  if (state === "estimated" && days !== null && Number.isFinite(days) && days >= 0)
    return days < 1 ? "<1 day" : `${Math.ceil(days).toLocaleString()} days`;
  if (state === "beyond_horizon") return ">10 years";
  if (state === "not_depleting") return "Not depleting";
  if (state === "unstable_trend") return "Unstable trend";
  if (state === "capacity_changed") return "Capacity changed";
  if (state === "insufficient_history") return "Needs history";
  if (state === "capacity_unknown") return "Capacity unknown";
  return "No estimate";
}
