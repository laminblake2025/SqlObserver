import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { validIncidentId, validateIncidentWindow, type IncidentWindow } from "./incidentModel.ts";

export interface WorkspaceIncidentWindow { readonly window?: IncidentWindow; readonly message?: string }

/** Save the explicit workspace time context without repairing ambiguous URL input. */
export function resolveWorkspaceIncidentWindow(hash: string, targetId: string, now: number): WorkspaceIncidentWindow {
  const parameters = new URLSearchParams(hash.split("?")[1]);
  try {
    if (!validIncidentId(targetId) || parameters.getAll("target").length !== 1 || parameters.get("target")?.toLowerCase() !== targetId.toLowerCase() ||
        ["range", "from", "to", "databaseId", "compare"].some(key => parameters.getAll(key).length > 1) ||
        parameters.has("databaseId") && (!/^[1-9]\d{0,4}$/u.test(parameters.get("databaseId")!) || Number(parameters.get("databaseId")) > 32767) ||
        parameters.has("compare") && !["0", "1"].includes(parameters.get("compare")!)) throw new Error("Choose an unambiguous server and time context before opening an incident.");
    const range = parameters.get("range") ?? "24h";
    if (!["1h", "6h", "24h", "7d", "custom"].includes(range) || !Number.isFinite(now)) throw new Error("Choose a supported time range before opening an incident.");
    let window: IncidentWindow;
    if (range === "custom") {
      const from = parameters.get("from"), to = parameters.get("to");
      utcMicroseconds(from); utcMicroseconds(to);
      window = { fromUtc: canonicalUtcTimestamp(from!), toUtc: canonicalUtcTimestamp(to!) };
    } else {
      if (parameters.has("from") || parameters.has("to")) throw new Error("Choose an unambiguous time context before opening an incident.");
      const hours = { "1h": 1, "6h": 6, "24h": 24, "7d": 168 }[range as "1h" | "6h" | "24h" | "7d"];
      window = { fromUtc: new Date(now - hours * 3600000).toISOString(), toUtc: new Date(now).toISOString() };
    }
    validateIncidentWindow(window, now);
    return { window };
  } catch (reason) {
    return { message: reason instanceof Error ? reason.message : "Choose a past window of at most seven days before opening an incident." };
  }
}
