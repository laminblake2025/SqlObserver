import type { AnalyticsSurface } from "./analyticsTypes.ts";

export function analyticsSummaryRow(surface: AnalyticsSurface, item: Readonly<Record<string, unknown>>, time: (value: string) => string): Readonly<Record<string, unknown>> {
  const at = (key: string) => typeof item[key] === "string" ? time(item[key] as string) : null;
  const evidence = { ...item };
  switch (surface) {
    case "jobs": case "backfill": return { "Job": item.jobId, "Task": item.jobKind, "Status": item.status, "Requested": at("requestedAtUtc"), "Attempt": item.attempt, "Evidence": evidence };
    case "host/status": return { "Host": item.hostName, "Binding": item.bindingState, "Collection": item.capabilityState, "Observed": at("observedAtUtc"), "Evidence": evidence };
    case "host/metrics": return { "Metric": item.metricKey, "Value": item.value, "Observed": at("observedAtUtc"), "Evidence": evidence };
    case "replication/status": case "replication/evidence": return { "Observed": at("observedAtUtc"), "Agent state": item.synchronizationState === "disabled" ? "Stopped" : item.synchronizationState,
      "Pending commands": item.pendingCommands, "Latency (seconds)": item.latencySeconds, "Coverage": item.coverage, "Evidence": evidence };
    case "diagnostics/search": return { "Event": item.eventKind, "Severity": item.severity, "Occurred": at("occurredAtUtc"), "Evidence": evidence };
    case "incidents": return { "Investigation": item.threadId, "Opened": at("openedAtUtc"), "Closed": at("closedAtUtc"), "Generation": item.currentGeneration, "Evidence": evidence };
    case "evidence-packets": return { "Evidence kind": item.evidenceKind, "Observed": at("occurredAtUtc"), "Visibility": item.visibilityState, "Confidence": item.confidence === "unknown" ? null : item.confidence, "Evidence": evidence };
  }
}
