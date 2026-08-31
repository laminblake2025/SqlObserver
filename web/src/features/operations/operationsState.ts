import type { OperationalKind, OperationalPage } from "./operationsTypes";

export interface OperationsCardState { readonly page?: OperationalPage; readonly loading: boolean; readonly error?: string; readonly retryable?: boolean; }
export type OperationsState = Record<OperationalKind, OperationsCardState>;
export type OperationsAction =
  | { readonly type: "reset" }
  | { readonly type: "start"; readonly kind: OperationalKind; readonly append: boolean }
  | { readonly type: "cancel"; readonly kind: OperationalKind }
  | { readonly type: "success"; readonly kind: OperationalKind; readonly page: OperationalPage; readonly append: boolean }
  | { readonly type: "failure"; readonly kind: OperationalKind; readonly error: string; readonly retryable: boolean };

export const operationKinds: readonly OperationalKind[] = ["backups", "agent", "tempdb-summary", "tempdb-files", "ag-replicas", "ag-databases"];
export const initialOperationsState = (): OperationsState => Object.fromEntries(operationKinds.map(kind => [kind, { loading: true }])) as OperationsState;
export function operationsReducer(state: OperationsState, action: OperationsAction): OperationsState {
  if (action.type === "reset") return initialOperationsState();
  if (action.type === "start") return { ...state, [action.kind]: { ...state[action.kind], loading: true, error: undefined } };
  if (action.type === "cancel") return { ...state, [action.kind]: { ...state[action.kind], loading: false } };
  if (action.type === "failure") return { ...state, [action.kind]: { ...state[action.kind], loading: false, error: action.error, retryable: action.retryable } };
  const previous = state[action.kind].page;
  const page = action.append && previous ? { ...action.page, items: [...previous.items, ...action.page.items] } : action.page;
  return { ...state, [action.kind]: { page, loading: false, error: undefined, retryable: undefined } };
}
