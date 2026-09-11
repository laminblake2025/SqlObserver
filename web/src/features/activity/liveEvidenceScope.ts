export type LiveRequestMode = "live" | "history";

export interface LiveRequestScope {
  readonly mode: LiveRequestMode;
  readonly filterKey: string;
  readonly snapshotId?: string;
  readonly cursor?: string;
  readonly windowKey?: string;
}

export function liveRequestKey(scope: LiveRequestScope): string {
  return JSON.stringify({
    mode: scope.mode,
    filterKey: scope.filterKey,
    snapshotId: scope.snapshotId ?? "",
    cursor: scope.cursor ?? "",
    windowKey: scope.windowKey ?? "",
  });
}

export function valueForLiveRequest<T>(value: T | undefined, valueKey: string | undefined, requestKey: string): T | undefined {
  return valueKey === requestKey ? value : undefined;
}
