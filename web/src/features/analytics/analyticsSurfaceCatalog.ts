import type { AnalyticsSurface } from "./analyticsTypes";

export interface AnalyticsSurfaceOption {
  readonly value: AnalyticsSurface;
  readonly label: string;
}

export const analyticsSurfaceCatalog = [
  { value: "incidents", label: "Incidents" },
  { value: "jobs", label: "Job inventory" },
  { value: "backfill", label: "Backfill status" },
  { value: "host/status", label: "Host status" },
  { value: "host/metrics", label: "Host metrics" },
  { value: "replication/status", label: "Replication status" },
  { value: "replication/evidence", label: "Replication evidence" },
  { value: "diagnostics/search", label: "Diagnostics search" },
  { value: "evidence-packets", label: "Evidence packets" },
] as const satisfies readonly AnalyticsSurfaceOption[];

export function analyticsSurfaceLabel(surface: AnalyticsSurface): string {
  return analyticsSurfaceCatalog.find((option) => option.value === surface)?.label ?? surface;
}
