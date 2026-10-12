import type { MaintenanceAdminItem } from "./alertAdminListsApi.ts";
import { maintenanceLocalToUtc, validateMaintenanceReason, type NewMaintenanceWindow } from "./maintenanceAdminActions.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";

export interface MaintenanceEditDraft {
  readonly original: MaintenanceAdminItem;
  readonly initialStartsLocal: string;
  readonly initialEndsLocal: string;
  readonly startsLocal: string;
  readonly endsLocal: string;
  readonly reason: string;
}
function preciseUtc(value: string): bigint {
  return utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
}
function localInput(value: string): string {
  preciseUtc(value);
  const date = new Date(value);
  const pad = (number: number) => String(number).padStart(2, "0");
  return `${String(date.getFullYear()).padStart(4, "0")}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}
export function beginMaintenanceEdit(original: MaintenanceAdminItem): MaintenanceEditDraft {
  if (original.cancelledAtUtc !== null || !Number.isSafeInteger(original.revision) || original.revision < 1)
    throw new Error("Only a configured maintenance window can be edited.");
  const initialStartsLocal = localInput(original.startsAtUtc), initialEndsLocal = localInput(original.endsAtUtc);
  return { original, initialStartsLocal, initialEndsLocal, startsLocal: initialStartsLocal, endsLocal: initialEndsLocal, reason: original.reason };
}
export function prepareMaintenanceEdit(draft: MaintenanceEditDraft): NewMaintenanceWindow {
  const startsAtUtc = draft.startsLocal === draft.initialStartsLocal ? draft.original.startsAtUtc : maintenanceLocalToUtc(draft.startsLocal);
  const endsAtUtc = draft.endsLocal === draft.initialEndsLocal ? draft.original.endsAtUtc : maintenanceLocalToUtc(draft.endsLocal);
  const length = preciseUtc(endsAtUtc) - preciseUtc(startsAtUtc);
  if (length <= 0n || length > 7n * 86400000000n)
    throw new Error("Choose a window longer than zero and no longer than seven days.");
  validateMaintenanceReason(draft.reason);
  return { startsAtUtc, endsAtUtc, reason: draft.reason };
}
