const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const zero = '00000000-0000-0000-0000-000000000000';
export interface RuleTemplateSelection {
  readonly templateId?: string | null;
  readonly templateVersion?: number | null;
  readonly deploymentId?: string | null;
}
function validId(value: string): boolean { return uuid.test(value) && value !== zero; }
function parameters(hash: string): URLSearchParams {
  if (hash.split('?')[0] !== '#/rule-templates') throw new Error('Choose the Rule templates page.');
  return new URLSearchParams(hash.split('?')[1]);
}
export function readRuleTemplateSelection(hash: string): RuleTemplateSelection {
  if (hash.split('?')[0] !== '#/rule-templates') return {};
  const p = parameters(hash), result: { templateId?: string | null; templateVersion?: number | null; deploymentId?: string | null } = {};
  const ids = p.getAll('templateId'), versions = p.getAll('templateVersion');
  const id = ids[0], version = versions[0];
  if (ids.length || versions.length) {
    const n = Number(version);
    if (ids.length !== 1 || versions.length !== 1 || id === undefined || version === undefined || !validId(id) || !/^[1-9][0-9]*$/.test(version) || !Number.isSafeInteger(n)) {
      result.templateId = null; result.templateVersion = null;
    } else { result.templateId = id.toLowerCase(); result.templateVersion = n; }
  }
  const jobs = p.getAll('deploymentId');
  const job = jobs[0];
  if (jobs.length) result.deploymentId = jobs.length === 1 && job !== undefined && validId(job) ? job.toLowerCase() : null;
  return result;
}
export function ruleTemplateSelectionHref(hash: string, selection: { readonly templateId?: string; readonly templateVersion?: number; readonly deploymentId?: string }): string {
  const old = parameters(hash), p = new URLSearchParams();
  for (const key of ['templateId', 'templateVersion', 'deploymentId']) for (const value of old.getAll(key)) p.append(key, value);
  if (selection.templateId !== undefined || selection.templateVersion !== undefined) {
    if (!selection.templateId || !validId(selection.templateId) || !Number.isSafeInteger(selection.templateVersion) || selection.templateVersion! < 1)
      throw new Error('Choose an exact template and positive version.');
    p.set('templateId', selection.templateId.toLowerCase()); p.set('templateVersion', String(selection.templateVersion));
  }
  if (selection.deploymentId !== undefined) {
    if (!validId(selection.deploymentId)) throw new Error('Choose a valid deployment.');
    p.set('deploymentId', selection.deploymentId.toLowerCase());
  }
  return `#/rule-templates${p.size ? '?' + p : ''}`;
}
