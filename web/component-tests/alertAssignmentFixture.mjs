import { alpha, beta, alertId, episodeId, auditId, serverNowUtc, nativeUtc, access, snoozeInbox } from './alertSnoozeFixture.mjs';
export { alpha, beta, alertId, episodeId, auditId, serverNowUtc, nativeUtc, access };
export const responders = [{ sid: 'S-1-5-21-101', displayName: 'Alex Rivera' }, { sid: 'S-1-5-21-202', displayName: 'Morgan Chen' }];
export const actorSid = 'S-1-5-21-303';
export const operation = index => `77777777-7777-4777-8777-${String(index + 1).padStart(12, '0')}`;
export function assignmentStatus(overrides = {}) {
  return { targetId: alpha, alertId: alertId(0), episodeId, revision: 7, state: 'firing', serverNowUtc: nativeUtc(serverNowUtc),
    assigneeSid: null, assigneeName: null, assignmentReason: null, assignedBy: null, assignedAtUtc: null,
    eligibleAssignees: responders.map(item => ({ ...item })), canAssign: true, ...overrides };
}
export function assignmentReceipt(command, overrides = {}) {
  return { operationId: command.operationId, auditId, recordedAtUtc: nativeUtc(serverNowUtc),
    revision: command.expectedRevision + 1, assigneeSid: command.assigneeSid, appliedAtUtc: nativeUtc(serverNowUtc), ...overrides };
}
export function assignmentChange(index = 0, overrides = {}) {
  return { operationId: operation(index), episodeId, revision: index + 8, assigneeSid: responders[0].sid,
    assigneeName: responders[0].displayName, previousAssigneeSid: null, previousAssigneeName: null,
    reason: `Assignment change ${index + 1}`, actorSid, appliedAtUtc: nativeUtc(serverNowUtc), ...overrides };
}
export function historyPage({ targetId = alpha, id = alertId(0), total = 51, index = 0 } = {}) {
  const items = Array.from({ length: Math.min(50, Math.max(0, total - index)) }, (_, offset) =>
    assignmentChange(index + offset, { revision: total - index - offset + 7 }));
  return { targetId, alertId: id, items, nextBeforeOperationId: index + items.length < total ? items.at(-1).operationId : null };
}
export const assignmentInbox = (kind, target, options) => snoozeInbox(kind, target, options);
/** Native-shaped immutable replay and private sequence history, not a directory or access-grant service. */
export function assignmentBackend() {
  const statuses = new Map(), operations = new Map(), changes = new Map(); let now = serverNowUtc, roster = responders.map(item => ({ ...item }));
  const key = (target, id) => `${target}:${id}`;
  const stored = (target, id) => {
    if (!statuses.has(key(target, id))) statuses.set(key(target, id), assignmentStatus({ targetId: target, alertId: id }));
    return statuses.get(key(target, id));
  };
  return { statuses, operations, changes, setNow(value) { now = value; }, setRoster(value) { roster = value; },
    read(target, id, allowed = true) { return { ...stored(target, id), serverNowUtc: nativeUtc(now), canAssign: allowed, eligibleAssignees: allowed ? roster : [] }; },
    history(target, id, before) {
      const ledger = changes.get(key(target, id)) ?? [];
      const index = before ? ledger.findIndex(item => item.operationId === before) + 1 : 0;
      if (before && index === 0) throw Object.assign(new Error('Unknown anchor'), { status: 422 });
      const items = ledger.slice(index, index + 50);
      return { targetId: target, alertId: id, items, nextBeforeOperationId: index + items.length < ledger.length ? items.at(-1).operationId : null };
    },
    change(target, id, command, rawBody = JSON.stringify(command)) {
      const opkey = `${target}:${command.operationId}`, replay = operations.get(opkey);
      if (replay) { if (replay.rawBody !== rawBody || replay.id !== id) throw Object.assign(new Error('Changed operation'), { status: 409 }); return replay.receipt; }
      const previous = stored(target, id), assignee = roster.find(item => item.sid === command.assigneeSid);
      if (previous.episodeId !== command.expectedEpisodeId || previous.revision !== command.expectedRevision || command.assigneeSid !== null && !assignee)
        throw Object.assign(new Error('Current episode, revision or approved responder changed'), { status: 409 });
      const next = { ...previous, revision: previous.revision + 1, assigneeSid: assignee?.sid ?? null, assigneeName: assignee?.displayName ?? null,
        assignmentReason: assignee ? command.reason : null, assignedBy: assignee ? actorSid : null, assignedAtUtc: assignee ? nativeUtc(now) : null };
      statuses.set(key(target, id), next);
      const change = assignmentChange(0, { operationId: command.operationId, revision: next.revision, episodeId: previous.episodeId,
        assigneeSid: next.assigneeSid, assigneeName: next.assigneeName, previousAssigneeSid: previous.assigneeSid,
        previousAssigneeName: previous.assigneeName, reason: command.reason, appliedAtUtc: nativeUtc(now) });
      changes.set(key(target, id), [change, ...(changes.get(key(target, id)) ?? [])]);
      const receipt = assignmentReceipt(command, { revision: next.revision, recordedAtUtc: nativeUtc(now), appliedAtUtc: nativeUtc(now) });
      operations.set(opkey, { id, rawBody, receipt }); return receipt;
    } };
}
