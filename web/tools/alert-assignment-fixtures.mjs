// Synthetic read defaults for existing built-dashboard journeys. Production never imports this module.
export function handleAlertAssignmentFixture(url, req, res, targets, send) {
  if (req.method !== 'GET') return false;
  const match = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/([^/]+)\/assignment(\/history)?$/u.exec(url.pathname);
  if (!match || !targets.some(target => target.instanceId === match[1])) return false;
  const [, targetId, alertId, history] = match;
  if (history) {
    send(res, { targetId, alertId, items: [], nextBeforeOperationId: null });
    return true;
  }
  const canAssign = req.headers.cookie?.includes('alertOperatorFixture=1') ?? false;
  send(res, { targetId, alertId, episodeId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee', revision: 5,
    state: 'firing', serverNowUtc: new Date().toISOString(), assigneeSid: null, assigneeName: null,
    assignmentReason: null, assignedBy: null, assignedAtUtc: null, canAssign,
    eligibleAssignees: canAssign ? [{ sid: 'S-1-5-21-100-200-300-1101', displayName: 'SYNTHETIC approved responder' }] : [] });
  return true;
}
