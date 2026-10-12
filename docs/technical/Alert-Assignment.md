# Alert assignment

Assignment records responsibility for an alert episode. It does not acknowledge, resolve or snooze the alert, suppress notifications, or grant access. The current owner is independent of collector/evaluator state. A new firing episode starts unassigned; earlier assignment history remains available.

## Approved responders

The application has no Windows user directory. An administrator supplies a bounded approved responder roster in `AlertAssignment:Assignees`, with stable Windows SID, safe display label, enabled status and explicit target scope. This is an application ownership policy, not a claim about current Active Directory membership or an authorization grant. Entries excluded by `SqlObserver:Authorization:DisabledActorSids` cannot receive new assignments. An empty roster offers no invented recipients and explains the configuration needed.

Only an active Operator or Target Administrator on the alert's exact target can assign, reassign or unassign. The same target's permitted readers can inspect current assignment and history. Candidate identities are disclosed only to callers authorized to mutate that target. Roster changes are configuration changes applied on process restart, consistent with the existing authorization configuration.

For example, replace the illustrative SID and target UUID below with approved identities from your environment. `Enabled` must be explicitly true; omitted approval stays disabled. `AllTargets: true` may be used instead of enumerated targets, never together with a nonempty target list. The server validates the complete roster at startup.

```json
{
  "AlertAssignment": {
    "Assignees": [
      {
        "Sid": "S-1-5-21-100-200-300-1101",
        "DisplayName": "Approved on-call DBA",
        "Enabled": true,
        "AllTargets": false,
        "TargetIds": ["11111111-1111-4111-8111-111111111111"]
      }
    ]
  }
}
```

## Current state, commands and history

`GET /api/v1/observation-targets/{targetId}/alerts/{alertId}/assignment` reads current episode, rule-state revision, repository time, owner and change metadata. This is separate from the retained inbox receipt and historical dashboard time window. Its eligible candidates are the current server-approved target roster.

`POST` to the same path requires an operation UUID, expected episode UUID, positive expected revision, explicit `assigneeSid` (null to unassign), and a reason of at most 512 UTF-8 bytes without control characters. A selected SID must be approved for the target on first application. Display names and approval evidence are server-owned. Assigning a different approved owner is reassignment; all changes are audited.

The server derives the payload digest. Exact retries bind operation, actor, target, alert, episode, revision, selected SID or null and reason. Immutable receipt replay precedes current-episode/revision and recipient-eligibility checks. Revoking or renaming a candidate cannot turn an exact retry into a new assignment. A replay receipt describes the original result rather than claiming that owner is still current. New intentions need fresh current state and a new operation identity.

Successful commands increment rule-state revision without changing firing/acknowledgement, snooze or evaluator evidence. Current episode/state/revision checks and native target/row locks serialize competing commands. Assignment history records old and new identities/labels, actor, reason, operation, episode, applied time and resulting revision.

`GET /assignment/history?limit=50&beforeOperationId={uuid}` reads immutable changes newest first across episodes for this alert. The optional anchor must belong to that target and alert. Native sequence numbers stay inside the repository; clients use the immutable operation UUID for continuation. The dashboard discloses its bounded retained history and offers explicit older-page reads.

## Dashboard behavior and qualification

Confirmed POST completion is followed by an independent current-state GET. Failed readback retries GET only. Uncertain POST retries retain the complete command, including explicit null for unassignment. A shared mutation owner excludes acknowledgement, snooze and assignment writes, including their retries. Denial, target/selection changes and unmount cancel requests and fence late results. Failure to refresh cannot silently discard an uncertain operation.

Current assignment and change history have distinct receipt times and labels. Retained alert state is unchanged. Owning qualification covers configured eligibility, native PostgreSQL lifecycle/replay/concurrency/history, HTTP-to-repository behavior and synthetic browser journeys. Actual final results and screenshots are recorded in the dashboard handoff after integration.
