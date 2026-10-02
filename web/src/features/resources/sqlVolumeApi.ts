import { apiFetch } from "../../apiClient.ts";
import { invalidSqlVolumeResponseMessage, isSqlVolumeCursor, parseSqlVolumeResponse, sqlVolumeMaximumResponseBytes, type SqlVolumeResponseBinding } from "./sqlVolumeResponseModel.ts";
export type SqlVolumeEvidenceState = "current" | "stale" | "partial" | "superseded" | "unavailable";
export type SqlVolumeTrendState = "capacity_unknown" | "full" | "capacity_changed" | "insufficient_history" | "not_depleting" | "unstable_trend" | "beyond_horizon" | "estimated" | "evidence_unavailable";
export type SqlVolumeLossKind = "none" | "source_row_limit" | "response_byte_limit" | "output_validation_failure" | "ingestion_rejection" | "blocking_graph_limit" | "duplicate_overlap" | "visibility_incomplete";

export interface SqlVolumeItem {
  readonly volumeKey: string;
  readonly identityKind: "volume_id" | "mount_point" | "file_scoped_unknown";
  readonly mappedFileCount: number;
  readonly totalBytes: string | null;
  readonly availableBytes: string | null;
  readonly observedAtUtc: string;
  readonly trendState: SqlVolumeTrendState;
  readonly trendSampleDays: number;
  readonly daysUntilFull: number | null;
}

export interface SqlVolumePage {
  readonly instanceId: string;
  readonly targetRevision: number;
  readonly snapshotRunId: string | null;
  readonly state: SqlVolumeEvidenceState;
  readonly reason: string;
  readonly completedAtUtc: string | null;
  readonly lossKind: SqlVolumeLossKind | null;
  readonly minimumLostItems: number | null;
  readonly hasVisibilityGap: boolean;
  readonly repositoryTimeUtc: string;
  readonly items: readonly SqlVolumeItem[];
  readonly nextCursor: string | null;
}

export async function getSqlVolumePage(instanceId: string, signal: AbortSignal,
  cursor: string | undefined, binding: SqlVolumeResponseBinding): Promise<SqlVolumePage> {
  if (signal.aborted) throw signal.reason;
  if (cursor !== undefined && !isSqlVolumeCursor(cursor)) throw new Error("The volume snapshot changed. Return to the first page.");
  const parameters = new URLSearchParams({ limit: "25" });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  let response: Response;
  try {
    response = await apiFetch(
      `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/resources/volumes?${parameters}`,
      { credentials: "same-origin", headers: { Accept: "application/json" }, signal },
      { maximumResponseBytes: sqlVolumeMaximumResponseBytes },
    );
  } catch (error: unknown) {
    if (signal.aborted) throw error;
    throw new Error(error instanceof Error && /response is too large|invalid length/u.test(error.message)
      ? invalidSqlVolumeResponseMessage : "SQL-reported volume capacity is temporarily unavailable.");
  }
  if (signal.aborted) throw signal.reason;
  if (!response.ok) {
    const message = response.status === 403 ? "You are not authorized to view volume capacity for this server."
      : response.status === 404 ? "This server is no longer available."
        : response.status === 400 && cursor !== undefined ? "The volume snapshot changed. Return to the first page."
          : "The volume capacity request failed. Refresh and retry.";
    throw new Error(message);
  }
  try {
    if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("Invalid content type.");
    const bytes = await response.arrayBuffer();
    if (signal.aborted) throw signal.reason;
    return parseSqlVolumeResponse(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)), instanceId, binding, cursor);
  } catch (error) {
    if (signal.aborted) throw signal.reason ?? error;
    throw new Error(invalidSqlVolumeResponseMessage);
  }
}
