import { apiFetch, ApiRequestError } from "../../apiClient.ts";
export interface DatabaseFileIoTrendPoint {
  readonly bucketStartUtc: string;
  readonly sourceRows: number;
  readonly comparableRows: number;
  readonly baselineRows: number;
  readonly resetRows: number;
  readonly gapRows: number;
  readonly legacyRows: number;
  readonly readOperations: string | null;
  readonly readStallMilliseconds: string | null;
  readonly writeOperations: string | null;
  readonly writeStallMilliseconds: string | null;
}

export interface DatabaseFileIoTrend {
  readonly targetId: string;
  readonly databaseId: number;
  readonly fileId: number;
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly semantics: "file_counter_delta";
  readonly revisionScope: "current";
  readonly bucketSeconds: number;
  readonly points: readonly DatabaseFileIoTrendPoint[];
}

export async function getDatabaseFileIoTrend(instanceId: string, databaseId: number,
  fileId: number, fromUtc: string, toUtc: string, signal: AbortSignal): Promise<DatabaseFileIoTrend> {
  const parameters = new URLSearchParams({ fromUtc, toUtc });
  const response = await apiFetch(
    `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/resources/files/${databaseId}/${fileId}/trend?${parameters}`,
    { credentials: "same-origin", headers: { Accept: "application/json" }, signal },
  );
  if (!response.ok) throw new ApiRequestError(response.status === 403
    ? "You are not authorized to view this file trend."
    : response.status === 400 ? "Choose a UTC window of at most seven days."
      : "File I/O history is unavailable.", response.status);
  return await response.json() as DatabaseFileIoTrend;
}
