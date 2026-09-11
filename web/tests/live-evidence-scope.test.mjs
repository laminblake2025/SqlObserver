import assert from "node:assert/strict";
import test from "node:test";
import { liveRequestKey, valueForLiveRequest } from "../src/features/activity/liveEvidenceScope.ts";

const baseScope = {
  mode: "live",
  filterKey: JSON.stringify({ database: "", login: "", blocked: false }),
  cursor: undefined,
};

test("a successful page and transient error remain visible for the same request scope", () => {
  const requestKey = liveRequestKey(baseScope);
  const page = { snapshotId: "snapshot-1", rows: ["row"] };
  const error = "Refresh failed";

  assert.deepEqual(valueForLiveRequest(page, requestKey, requestKey), page);
  assert.equal(valueForLiveRequest(error, requestKey, requestKey), error);
});

test("changed filters, pagination, mode, and history gaps hide prior request evidence", () => {
  const requestKey = liveRequestKey(baseScope);
  const page = { snapshotId: "snapshot-1", rows: ["row"] };
  const changedScopes = [
    { ...baseScope, filterKey: JSON.stringify({ database: "5", login: "", blocked: false }) },
    { ...baseScope, cursor: "next-page" },
    { ...baseScope, mode: "history" },
    { ...baseScope, mode: "history", snapshotId: "snapshot-1" },
    { ...baseScope, mode: "history", windowKey: "history-window-2" },
  ];

  for (const scope of changedScopes) {
    const changedKey = liveRequestKey(scope);
    assert.notEqual(changedKey, requestKey);
    assert.equal(valueForLiveRequest(page, requestKey, changedKey), undefined);
  }

  const historicalPageKey = liveRequestKey({ ...baseScope, mode: "history", snapshotId: "snapshot-1" });
  const historyGapKey = liveRequestKey({ ...baseScope, mode: "history" });
  assert.notEqual(historyGapKey, historicalPageKey);
  assert.equal(valueForLiveRequest(page, historicalPageKey, historyGapKey), undefined);
});
