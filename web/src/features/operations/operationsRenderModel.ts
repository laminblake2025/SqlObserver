import type { OperationsState } from "./operationsState.ts";
import { operationKinds } from "./operationsState.ts";
import type { OperationalKind, OperationalPage, OperationalState } from "./operationsTypes.ts";

export type OperationsRenderStatus = OperationalState | "Loading" | "Error";
export interface OperationsRenderCard {
  readonly kind: OperationalKind;
  readonly label: string;
  readonly status: OperationsRenderStatus;
  readonly page?: OperationalPage;
  readonly loading: boolean;
  readonly items: readonly Record<string, unknown>[];
  readonly error?: string;
  readonly retryable: boolean;
  readonly canRetry: boolean;
  readonly canLoadMore: boolean;
  readonly canCancel: boolean;
  readonly inertLabels: readonly string[];
}
export interface OperationsRenderModel { readonly cards: Readonly<Record<OperationalKind, OperationsRenderCard>>; }

const labels: Record<OperationalKind, string> = {
  backups: "Backups", agent: "SQL Agent failures", "tempdb-summary": "TempDB summary", "tempdb-files": "TempDB files",
  "ag-replicas": "Availability Group replicas", "ag-databases": "Availability Group databases",
};

export function buildOperationsRenderModel(state: OperationsState): OperationsRenderModel {
  const cards = Object.fromEntries(operationKinds.map(kind => {
    const card = state[kind];
    const page = card.page;
    const status: OperationsRenderStatus = card.error !== undefined && page === undefined ? "Error" : card.loading ? "Loading" : page?.state ?? "Error";
    const items = page?.items ?? [];
    const inertLabels = items.flatMap(item => Object.values(item).filter((value): value is string => typeof value === "string"));
    return [kind, { kind, label: labels[kind], status, page, loading: card.loading, items, error: card.error, retryable: card.retryable === true, canRetry: card.error !== undefined && card.retryable === true, canLoadMore: card.loading !== true && page?.hasMore === true && typeof page.nextCursor === "string", canCancel: card.loading, inertLabels } satisfies OperationsRenderCard];
  })) as unknown as Record<OperationalKind, OperationsRenderCard>;
  return { cards };
}
