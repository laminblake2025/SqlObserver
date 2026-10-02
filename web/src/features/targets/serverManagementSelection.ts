import { overviewHref } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import type { ObservationTargetSummary } from "./targetTypes";

export type ServerManagementSelection =
  | { readonly state: "selected"; readonly instanceId: string }
  | { readonly state: "invalid"; readonly message: string };

export function readServerManagementSelection(hash: string): ServerManagementSelection | undefined {
  const [page, query] = hash.replace(/^#\/?/u, "").split("?");
  if (page !== "servers") return undefined;
  const parameters = new URLSearchParams(query);
  if (!parameters.has("serverSettings")) return undefined;
  const flags = parameters.getAll("serverSettings"), targets = parameters.getAll("target");
  const instanceId = targets[0];
  if (flags.length !== 1 || flags[0] !== "1" || targets.length !== 1 || instanceId === undefined ||
      !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu.test(instanceId) ||
      instanceId === "00000000-0000-0000-0000-000000000000") {
    return { state: "invalid", message: "The server settings selection in this URL is invalid. Open settings from the server list." };
  }
  return { state: "selected", instanceId: instanceId.toLowerCase() };
}

export function serverSettingsHref(scope: OverviewScope, instanceId: string): string {
  return `${overviewHref(scope, "servers", instanceId)}&serverSettings=1`;
}

export function mergeManagedTarget(targets: readonly ObservationTargetSummary[], updated: ObservationTargetSummary): readonly ObservationTargetSummary[] {
  // Editing an off-page target must not insert it into this bounded list page.
  // A late receipt must not replace a newer configuration already loaded here.
  return targets.flatMap(target => {
    if (target.instanceId.toLowerCase() !== updated.instanceId.toLowerCase() ||
        target.configurationRevision > updated.configurationRevision) return [target];
    return updated.lifecycle === "retired" ? [] : [updated];
  });
}
