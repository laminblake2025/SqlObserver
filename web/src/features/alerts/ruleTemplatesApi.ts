import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "./alertRuntimeParser.ts";
import { notificationId } from "./alertSnoozeModel.ts";
import { parseProductCatalog, parseTemplateVersion, parseDeploymentStatus, parseNativePage, parseCommandReceipt,
  commandResource, positive, type TemplateVersion, type DeploymentSummary, type BatchReceipt, type NativePage,
  type TemplateCommand, type DeploymentMember } from "./ruleTemplatesModel.ts";
const library = "/api/v1/alerts/rule-templates", jobs = "/api/v1/alerts/rule-deployments";
const id = (value: string) => { if (!notificationId(value)) throw new Error("Choose a valid template or deployment identity."); return value; };
async function request(path: string, signal: AbortSignal, method = "GET", body?: unknown) {
  const response = await apiFetch(path, { method, signal, cache: "no-store", headers: { Accept: "application/json", ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }) }, { maximumResponseBytes: 2 * 1024 * 1024 });
  if (!response.ok) throw new ApiRequestError([401, 403].includes(response.status) ? "Rule template access was denied. Restore the required Target Administrator grants and explicitly retry access."
    : response.status === 409 ? "The preview, job or version changed. Refresh its exact current state before a new intention."
      : response.status === 404 ? "The exact template, version or deployment no longer exists. Review its selection before a new operation."
      : [400, 422].includes(response.status) ? "The server refused this template operation. Review the settings and complete group preview."
        : "Rule template configuration is temporarily unavailable.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("The rule template response could not be verified.");
  return readJsonBounded(response, 2 * 1024 * 1024);
}
export async function getTemplateCatalog(signal: AbortSignal) { return parseProductCatalog(await request(library + "/catalog", signal)); }
export async function getTemplateVersion(templateId: string, version: number | undefined, signal: AbortSignal) {
  if (version !== undefined && !positive(version)) throw new Error("Choose a valid immutable version.");
  return parseTemplateVersion(await request(library + "/" + id(templateId) + (version === undefined ? "" : "?version=" + version), signal), templateId, version);
}
export async function getDeploymentStatus(deploymentId: string, signal: AbortSignal) { return parseDeploymentStatus(await request(jobs + "/" + id(deploymentId), signal), deploymentId); }
export async function getTemplatePage(signal: AbortSignal, prior: readonly NativePage<TemplateVersion>[] = [], before?: string) {
  return parseNativePage<TemplateVersion>(await request(library + "?limit=50" + (before ? "&beforeTemplateId=" + id(before) : ""), signal), "templates", prior, before);
}
export async function getVersionPage(templateId: string, signal: AbortSignal, prior: readonly NativePage<TemplateVersion>[] = [], before?: number) {
  if (before !== undefined && !positive(before)) throw new Error("Choose a valid version history anchor.");
  return parseNativePage<TemplateVersion>(await request(library + "/" + id(templateId) + "/versions?limit=50" + (before === undefined ? "" : "&beforeVersion=" + before), signal), "versions", prior, before, templateId);
}
export async function getDeploymentPage(signal: AbortSignal, prior: readonly NativePage<DeploymentSummary>[] = [], before?: string) {
  return parseNativePage<DeploymentSummary>(await request(jobs + "?limit=50" + (before ? "&beforeDeploymentId=" + id(before) : ""), signal), "deployments", prior, before);
}
export async function getBatchHistory(deploymentId: string, signal: AbortSignal, prior: readonly NativePage<BatchReceipt>[] = [], before?: string) {
  return parseNativePage<BatchReceipt>(await request(jobs + "/" + id(deploymentId) + "/history?limit=50" + (before ? "&beforeOperationId=" + id(before) : ""), signal), "history", prior, before, deploymentId);
}
export async function sendTemplateCommand(command: TemplateCommand, signal: AbortSignal, members?: readonly DeploymentMember[]) {
  if (!notificationId(command.body.operationId)) throw new Error("An operation identity is required.");
  const resource = commandResource(command); id(resource.id);
  const path = command.kind === "create_template" ? library : command.kind === "publish_template" ? library + "/" + id(command.templateId)
    : command.kind === "preview" ? jobs + "/preview" : command.kind === "rollback" ? jobs + "/" + id(command.sourceDeploymentId) + "/rollback-preview"
      : jobs + "/" + id(command.deploymentId) + "/" + command.kind;
  return parseCommandReceipt(await request(path, signal, command.kind === "publish_template" ? "PUT" : "POST", command.body), command, members);
}
