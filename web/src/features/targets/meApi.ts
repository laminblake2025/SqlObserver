export type ApplicationRole =
  | "Viewer" | "Operator" | "TargetAdministrator" | "SecurityAdministrator"
  | "Auditor" | "CollectorService" | "QueryTextReader";

export interface MyAccess {
  readonly active: boolean;
  readonly grantedRoles: readonly ApplicationRole[];
  readonly allTargetRoles: readonly ApplicationRole[];
  readonly targetId: string | null;
  readonly targetRoles: readonly ApplicationRole[];
}

const validRoles = new Set<ApplicationRole>([
  "Viewer", "Operator", "TargetAdministrator", "SecurityAdministrator",
  "Auditor", "CollectorService", "QueryTextReader",
]);
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function parseMyAccess(value: unknown, expectedTargetId: string | null): MyAccess {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error("Access information is unavailable.");
  const source = value as Record<string, unknown>;
  if (Object.keys(source).sort().join("|") !== "active|allTargetRoles|grantedRoles|targetId|targetRoles"
      || typeof source.active !== "boolean") throw new Error("Access information is unavailable.");
  const roles = (field: string): ApplicationRole[] => {
    const list = source[field];
    if (!Array.isArray(list) || list.length > 16 || list.some(role => typeof role !== "string" || !validRoles.has(role as ApplicationRole))
        || new Set(list).size !== list.length) throw new Error("Access information is unavailable.");
    return list as ApplicationRole[];
  };
  const grantedRoles = roles("grantedRoles");
  const allTargetRoles = roles("allTargetRoles");
  const targetRoles = roles("targetRoles");
  const targetId = source.targetId;
  if (targetId !== null && (typeof targetId !== "string" || !guid.test(targetId))) throw new Error("Access information is unavailable.");
  if ((expectedTargetId === null && targetId !== null) || (expectedTargetId !== null && (typeof targetId !== "string" || targetId.toLowerCase() !== expectedTargetId.toLowerCase())))
    throw new Error("Access information is unavailable.");
  if (allTargetRoles.some(role => !grantedRoles.includes(role)) || targetRoles.some(role => !grantedRoles.includes(role))
      || targetId === null && targetRoles.length !== 0
      || !source.active && (grantedRoles.length !== 0 || allTargetRoles.length !== 0 || targetRoles.length !== 0))
    throw new Error("Access information is unavailable.");
  return { active: source.active, grantedRoles, allTargetRoles, targetId, targetRoles };
}

export function canRegisterTarget(access: MyAccess | undefined): boolean {
  return access?.active === true && access.allTargetRoles.includes("TargetAdministrator");
}

export function canAcknowledgeAlert(access: MyAccess | undefined, targetId: string): boolean {
  return access?.active === true && access.targetId?.toLowerCase() === targetId.toLowerCase()
    && (access.targetRoles.includes("Operator") || access.targetRoles.includes("TargetAdministrator"));
}

export function canReadQueryText(access: MyAccess | undefined, targetId: string): boolean {
  return access?.active === true && access.targetId?.toLowerCase() === targetId.toLowerCase()
    && access.targetRoles.includes("QueryTextReader")
    && (["Viewer", "Operator", "TargetAdministrator"] as const).some(role => access.targetRoles.includes(role));
}

export async function getMyAccess(targetId: string | null, signal: AbortSignal): Promise<MyAccess> {
  if (targetId !== null && !guid.test(targetId)) throw new Error("Access information is unavailable.");
  const query = targetId === null ? "" : `?targetId=${encodeURIComponent(targetId)}`;
  const response = await fetch(`/api/v1/me${query}`, { credentials: "same-origin", headers: { Accept: "application/json" }, signal });
  if (!response.ok) throw new Error("Access information is unavailable.");
  return parseMyAccess(await response.json(), targetId);
}
