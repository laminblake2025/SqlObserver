export type SqlVolumeEvidenceState = "current" | "stale" | "partial" | "superseded" | "unavailable";

export interface SqlVolumeItem {
  readonly volumeKey: string;
  readonly identityKind: "volume_id" | "mount_point" | "file_scoped_unknown";
  readonly mappedFileCount: number;
  readonly totalBytes: string | null;
  readonly availableBytes: string | null;
  readonly observedAtUtc: string;
}

export interface SqlVolumePage {
  readonly instanceId: string;
  readonly targetRevision: number;
  readonly snapshotRunId: string | null;
  readonly state: SqlVolumeEvidenceState;
  readonly reason: string;
  readonly completedAtUtc: string | null;
  readonly lossKind: string | null;
  readonly minimumLostItems: number | null;
  readonly hasVisibilityGap: boolean;
  readonly repositoryTimeUtc: string;
  readonly items: readonly SqlVolumeItem[];
  readonly nextCursor: string | null;
}

export async function getSqlVolumePage(instanceId: string, signal: AbortSignal,
  cursor?: string): Promise<SqlVolumePage> {
  const parameters = new URLSearchParams({ limit: "25" });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  let response: Response;
  try {
    response = await fetch(
      `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/resources/volumes?${parameters}`,
      { credentials: "same-origin", headers: { Accept: "application/json" }, signal },
    );
  } catch (error: unknown) {
    if (signal.aborted) throw error;
    throw new Error("SQL-reported volume capacity is temporarily unavailable.");
  }
  if (!response.ok) {
    const message = response.status === 403 ? "You are not authorized to view volume capacity for this server."
      : response.status === 404 ? "This server is no longer available."
        : response.status === 400 && cursor !== undefined ? "The volume snapshot changed. Return to the first page."
          : "The volume capacity request failed. Refresh and retry.";
    throw new Error(message);
  }
  return (await response.json()) as SqlVolumePage;
}
