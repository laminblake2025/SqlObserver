import assert from 'node:assert/strict';
import test from 'node:test';
import { assignmentCommand, parseAssignmentStatus, parseAssignmentReceipt, parseAssignmentHistory, parseAssignmentProjection,
  validAssigneeSid, safeAssignmentName } from '../src/features/alerts/alertAssignmentModel.ts';
import { getAlertAssignment, changeAlertAssignment, getAlertAssignmentHistory } from '../src/features/alerts/alertAssignmentApi.ts';
import { AlertMutationLane } from '../src/features/alerts/alertMutationLane.ts';
import { parseActiveAlerts } from '../src/features/alerts/alertRuntimeParser.ts';
import { alpha, beta, alertId, episodeId, responders, operation, assignmentStatus, assignmentReceipt,
  assignmentChange, historyPage, assignmentBackend, assignmentInbox } from '../component-tests/alertAssignmentFixture.mjs';
const current = () => parseAssignmentStatus(assignmentStatus(), alpha, alertId(0));
test('approved assignment freezes operation, episode, revision, SID and exact safe reason; unassignment is explicit null', () => {
  for (const sid of [responders[0].sid, null]) assert.deepEqual(assignmentCommand(current(), sid, ' Investigate ', operation(0)),
    { operationId: operation(0), expectedEpisodeId: episodeId, expectedRevision: 7, assigneeSid: sid, reason: ' Investigate ' });
  assert.throws(() => assignmentCommand(current(), 'S-1-5-21-999', 'Investigate'));
  assert.throws(() => assignmentCommand({ ...current(), canAssign: false }, responders[0].sid, 'Investigate'));
});
test('native SID and Unicode display labels use syntax and UTF8 bounds without claiming directory identity', () => {
  assert.equal(validAssigneeSid('S-1-5-21-101'), true);
  assert.equal(validAssigneeSid('S-1-5-' + Array.from({ length: 40 }, () => '1').join('-')), true, 'Native syntax permits multiple numeric segments within the byte bound');
  for (const value of ['someone@example.com', 's-1-5-21-101', ' S-1-5-21-101', 'S-1-5-21-101\n', 'S-1-' + '1'.repeat(180)]) assert.equal(validAssigneeSid(value), false);
  assert.equal(safeAssignmentName('é'.repeat(64)), true);
  for (const value of ['', ' ', 'é'.repeat(65), 'a'.repeat(129), 'A\nB', '\ud800']) assert.equal(safeAssignmentName(value), false);
  assert.equal(safeAssignmentName('<img src=x>'), true);
});
test('current status verifies source, native microseconds, episode/revision, candidate visibility and approved roster bounds', () => {
  assert.equal(current().serverNowUtc, '2026-09-26T13:00:00.123456Z');
  for (const change of [{ targetId: beta }, { alertId: alpha }, { episodeId: null }, { revision: 0 }, { revision: 2 ** 53 },
    { serverNowUtc: '2026-09-26T13:00:00.1234561+00:00' }, { canAssign: false }, { eligibleAssignees: [...responders, responders[0]] },
    { eligibleAssignees: Array.from({ length: 257 }, (_, i) => ({ sid: 'S-1-5-21-' + i, displayName: 'Responder ' + i })) },
    { assigneeSid: responders[0].sid, assigneeName: null }, { assignmentReason: 'Owner absent' }, { assignedBy: responders[0].sid },
    { assignedAtUtc: '2026-09-26T13:00:00Z' }, { state: 'critical' }]) assert.throws(() => parseAssignmentStatus(assignmentStatus(change), alpha, alertId(0)));
  assert.equal(parseAssignmentStatus(assignmentStatus({ eligibleAssignees: [], canAssign: false }), alpha, alertId(0)).canAssign, false);
});
test('current unassigned metadata and optional legacy received row projection do not fabricate identity', () => {
  const status = current(); assert.equal(status.assigneeSid, undefined); assert.equal(status.assignedBy, undefined);
  assert.deepEqual(parseAssignmentProjection({}), { assigneeSid: undefined, assigneeName: undefined, assignmentReason: undefined, assignedBy: undefined, assignedAtUtc: undefined });
  const row = assignmentInbox('target', alpha, { legacy: true, projectionOverrides: { assigneeSid: responders[0].sid, assigneeName: responders[0].displayName } });
  assert.equal(parseActiveAlerts(row, alpha).items[0].assigneeName, responders[0].displayName);
  assert.equal(parseActiveAlerts(row, alpha).items[0].episodeId, undefined);
});
test('immutable receipt binds exact operation and nullable assignee; no current owner is inferred', () => {
  for (const sid of [responders[0].sid, null]) {
    const command = assignmentCommand(current(), sid, 'Investigation', operation(0)), receipt = assignmentReceipt(command);
    assert.equal(parseAssignmentReceipt(receipt, command).assigneeSid, sid);
    for (const change of [{ operationId: operation(1) }, { assigneeSid: sid === null ? responders[0].sid : null },
      { revision: 7 }, { revision: command.expectedRevision + 2 }, { auditId: 'bad' }, { appliedAtUtc: '2026-02-30T12:00:00Z' }]) assert.throws(() => parseAssignmentReceipt({ ...receipt, ...change }, command));
    if (sid === null) { const missing = { ...receipt }; delete missing.assigneeSid; assert.throws(() => parseAssignmentReceipt(missing, command)); }
  }
});
test('history uses native private sequence even with equal times/random UUID order and backwards server clock', () => {
  const page = historyPage({ total: 3 }); page.items[0].operationId = operation(3); page.items[1].operationId = operation(8);
  page.items[2].appliedAtUtc = '2026-09-27T13:00:00.1234560+00:00';
  assert.deepEqual(parseAssignmentHistory(page, alpha, alertId(0)).items.map(item => item.operationId), page.items.map(item => item.operationId));
});
test('history validates exact retained operation anchor, duplicate protection, source binding and full native pages', () => {
  const first = parseAssignmentHistory(historyPage(), alpha, alertId(0)), before = first.nextBeforeOperationId;
  const second = historyPage({ index: 50 });
  assert.equal(parseAssignmentHistory(second, alpha, alertId(0), [first], before).items.length, 1);
  for (const page of [{ ...second, targetId: beta }, { ...second, items: [assignmentChange(0)] },
    { ...second, nextBeforeOperationId: second.items[0].operationId }, { ...second, items: [assignmentChange(50, { actorSid: 'User' })] },
    { ...second, items: [assignmentChange(50, { previousAssigneeSid: responders[0].sid })] }])
    assert.throws(() => parseAssignmentHistory(page, alpha, alertId(0), [first], before));
  assert.throws(() => parseAssignmentHistory(second, alpha, alertId(0), [first], operation(2)));
  assert.throws(() => parseAssignmentHistory({ ...historyPage(), nextBeforeOperationId: operation(1) }, alpha, alertId(0)));
});
test('durable replay survives roster revocation and preserves old receipt without substituting a later owner', () => {
  const backend = assignmentBackend(), command = assignmentCommand(current(), responders[0].sid, 'Investigate', operation(0));
  const receipt = backend.change(alpha, alertId(0), command);
  const reassignment = assignmentCommand(parseAssignmentStatus(backend.read(alpha, alertId(0)), alpha, alertId(0)), responders[1].sid, 'Handoff', operation(1));
  backend.change(alpha, alertId(0), reassignment); backend.setRoster([]);
  assert.deepEqual(backend.change(alpha, alertId(0), command), receipt);
  assert.equal(parseAssignmentReceipt(receipt, command).revision, command.expectedRevision + 1, 'Historical replay validates against the captured command even after later revisions');
  assert.equal(backend.read(alpha, alertId(0)).assigneeSid, responders[1].sid); assert.equal(backend.operations.size, 2);
  assert.throws(() => backend.change(alpha, alertId(0), { ...command, reason: 'Changed' }));
});
test('unassignment clears current metadata while retaining immutable actor/reason and previous owner in history', () => {
  const backend = assignmentBackend(); backend.change(alpha, alertId(0), assignmentCommand(current(), responders[0].sid, 'Take investigation', operation(0)));
  const command = assignmentCommand(parseAssignmentStatus(backend.read(alpha, alertId(0)), alpha, alertId(0)), null, 'Release owner', operation(1));
  backend.change(alpha, alertId(0), command); const status = backend.read(alpha, alertId(0));
  for (const field of ['assigneeSid', 'assigneeName', 'assignmentReason', 'assignedBy', 'assignedAtUtc']) assert.equal(status[field], null);
  assert.equal(backend.history(alpha, alertId(0)).items[0].reason, 'Release owner');
  assert.equal(backend.history(alpha, alertId(0)).items[0].previousAssigneeSid, responders[0].sid);
});
test('synchronous shared lane excludes acknowledgement, snooze and assignment; old release cannot clear context ABA owner', () => {
  const lane = new AlertMutationLane(), a = {}, b = {}, c = {}; lane.setContext('A');
  assert.equal(lane.claim('A', a, 'assignment'), true); assert.equal(lane.claim('A', b, 'acknowledge'), false);
  lane.release(b); assert.equal(lane.busy('A'), true); lane.setContext('B'); lane.setContext('A');
  assert.equal(lane.claim('A', c, 'snooze'), true); lane.release(a); assert.equal(lane.kind('A'), 'snooze');
  lane.release(c); assert.equal(lane.claim('A', b, 'acknowledge'), true);
});
test('native API sends explicit null body and GET-only current/history with exact native anchors', async () => {
  const oldFetch = globalThis.fetch, calls = [], command = assignmentCommand(current(), null, 'Release', operation(0));
  globalThis.fetch = async (url, options) => { calls.push({ url: String(url), options });
    const payload = options.method === 'POST' ? assignmentReceipt(command) : String(url).includes('/history?') ? historyPage({ total: 0 }) : assignmentStatus();
    return new Response(JSON.stringify(payload), { headers: { 'content-type': 'application/json' } }); };
  try {
    const signal = new AbortController().signal; await changeAlertAssignment(alpha, alertId(0), command, signal);
    await getAlertAssignment(alpha, alertId(0), signal); await getAlertAssignmentHistory(alpha, alertId(0), signal);
    assert.deepEqual(JSON.parse(calls[0].options.body), command); assert.equal(calls[0].options.headers.get('content-type'), 'application/json');
    assert.equal(calls[1].options.method, 'GET'); assert.equal(calls[2].options.method, 'GET');
    assert.equal(calls[2].url, `/api/v1/observation-targets/${alpha}/alerts/${alertId(0)}/assignment/history?limit=50`);
  } finally { globalThis.fetch = oldFetch; }
});
for (const status of [401, 403, 409, 422, 503]) test(`assignment APIs retain HTTP ${status} for boundary ownership decisions`, async () => {
  const oldFetch = globalThis.fetch; globalThis.fetch = async () => new Response('{}', { status });
  try { const signal = new AbortController().signal;
    await assert.rejects(() => getAlertAssignment(alpha, alertId(0), signal), failure => failure.status === status);
    await assert.rejects(() => changeAlertAssignment(alpha, alertId(0), assignmentCommand(current(), null, 'Release', operation(0)), signal), failure => failure.status === status);
  } finally { globalThis.fetch = oldFetch; }
});
