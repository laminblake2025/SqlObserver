import { getAlertRulesForAdmin } from "../alerts/alertRuleAdminApi.ts";
import { ruleThresholds } from "./chartReferenceModel.ts";

export async function readChartRuleThresholds(targetId: string, signal: AbortSignal) {
  let cursor: string | undefined, listingStartedUtc: string | undefined;
  const rules = [], seen = new Set<string>(), cursors = new Set<string>();
  for (let pageNumber = 0; pageNumber < 4; pageNumber++) {
    const page = await getAlertRulesForAdmin(targetId, signal, cursor);
    if (listingStartedUtc !== undefined && page.listingStartedUtc !== listingStartedUtc) throw new Error("Rule listing changed during paging.");
    listingStartedUtc = page.listingStartedUtc;
    for (const rule of page.items) {
      if (seen.has(rule.ruleId.toLowerCase())) throw new Error("Rule listing repeated an identity.");
      seen.add(rule.ruleId.toLowerCase()); rules.push(rule);
    }
    cursor = page.nextCursor;
    if (!cursor) break;
    if (cursors.has(cursor)) throw new Error("Rule listing repeated a cursor.");
    cursors.add(cursor);
  }
  return { thresholds: ruleThresholds(targetId, rules), truncated: cursor !== undefined };
}
