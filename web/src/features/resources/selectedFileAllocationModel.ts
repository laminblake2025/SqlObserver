import type { DatabaseFileHealthPage, DatabaseFileHealthSummary } from "../health/healthTypes.ts";
import type { ResourceFileSelection } from "./resourceSelectionModel.ts";

export interface SelectedFileAllocationInput {
  readonly targetId: string;
  readonly selection?: ResourceFileSelection;
  readonly databaseId?: number;
  readonly page?: DatabaseFileHealthPage;
  readonly loading: boolean;
  readonly failed: boolean;
  readonly contextCurrent: boolean;
}
export type SelectedFileAllocation = { readonly state: "unavailable"; readonly notice: string }
  | { readonly state: "available"; readonly file: DatabaseFileHealthSummary; readonly attention: boolean; readonly maximum: string; readonly growth: string };

/** Only the selected row in a validated, current loaded page supplies allocation facts. */
export function selectedFileAllocation(input: SelectedFileAllocationInput): SelectedFileAllocation {
  const unavailable = (notice: string): SelectedFileAllocation => ({ state: "unavailable", notice });
  if (!input.contextCurrent) return unavailable("The resource context changed. Allocation evidence is unavailable until the current workspace is loaded.");
  if (!input.selection) return unavailable("Choose a valid database file to inspect its allocation and autogrowth. No file has been substituted.");
  if (input.databaseId !== undefined && input.databaseId !== input.selection.databaseId)
    return unavailable("The selected file belongs to a different database context. No file has been substituted.");
  if (input.failed) return unavailable("The file snapshot could not be loaded. Allocation and autogrowth are unavailable; refresh or retry file page navigation.");
  if (input.loading) return unavailable("Loading the current file snapshot. Allocation and autogrowth are not yet available.");
  if (!input.page || input.page.instanceId.toLowerCase() !== input.targetId.toLowerCase())
    return unavailable("No matching file snapshot is available for this server.");
  const matches = input.page.items.filter(file => file.databaseId === input.selection!.databaseId && file.fileId === input.selection!.fileId);
  if (matches.length !== 1) return unavailable(matches.length === 0
    ? "The selected file is absent from this loaded snapshot page. Browse file pages to locate its allocation; no other file has been substituted."
    : "The loaded file identity is ambiguous. Allocation and autogrowth are unavailable; refresh the snapshot.");
  const file = matches[0]!;
  const growth = file.growthPercent > 0 && BigInt(file.growthBytes) > 0n
    ? `Both ${file.growthBytes} bytes and ${file.growthPercent}% are reported; the growth mode is ambiguous.`
    : file.growthPercent > 0 ? `${file.growthPercent}% per growth event`
      : BigInt(file.growthBytes) > 0n ? `${file.growthBytes} bytes per growth event` : "Autogrowth disabled (zero increment reported)";
  return { state: "available", file,
    attention: input.page.collector.state !== "current" || input.page.collector.hasVisibilityGap || file.collector.state !== "current" || file.collector.hasVisibilityGap,
    maximum: file.maximumSizeBytes ?? "No finite maximum reported (unlimited or unknown)", growth };
}
