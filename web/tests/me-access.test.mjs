import test from "node:test";
import assert from "node:assert/strict";
import { canRegisterTarget, getMyAccess, parseMyAccess } from "../src/features/targets/meApi.ts";

const target = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const scoped = { active: true, grantedRoles: ["TargetAdministrator", "Viewer"], allTargetRoles: [], targetId: target, targetRoles: ["Viewer"] };

test("server access is bound to the selected target and only an all-target administrator can register", () => {
  assert.equal(canRegisterTarget(parseMyAccess(scoped, target)), false);
  assert.equal(canRegisterTarget(parseMyAccess({ ...scoped, allTargetRoles: ["TargetAdministrator"] }, target)), true);
  assert.throws(() => parseMyAccess(scoped, other), /Access information/);
  assert.throws(() => parseMyAccess({ ...scoped, targetRoles: ["Auditor"] }, target), /Access information/);
  assert.throws(() => parseMyAccess({ ...scoped, active: false }, target), /Access information/);
  assert.throws(() => parseMyAccess({ ...scoped, scope: "all" }, target), /Access information/);
  assert.deepEqual(parseMyAccess({ active: true, grantedRoles: [], allTargetRoles: [], targetId: null, targetRoles: [] }, null).targetRoles, []);
});

test("access lookup sends the selected target and refuses an unrelated response", async () => {
  const previous = globalThis.fetch;
  try {
    let requested;
    globalThis.fetch = async (url, options) => {
      requested = { url, options };
      return new Response(JSON.stringify(scoped), { status: 200, headers: { "content-type": "application/json" } });
    };
    const signal = new AbortController().signal;
    assert.equal((await getMyAccess(target, signal)).targetId, target);
    assert.equal(requested.url, `/api/v1/me?targetId=${target}`);
    assert.equal(requested.options.credentials, "same-origin");
    assert.equal(requested.options.signal, signal);
    await assert.rejects(() => getMyAccess(other, signal), /Access information/);
  } finally { globalThis.fetch = previous; }
});
