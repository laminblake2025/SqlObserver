# Alert notification snooze

Snooze suppresses notifications for the current alert episode. The alert stays visible and firing or acknowledged; collection and evaluation continue. A new episode does not inherit the previous snooze. Maintenance suppression remains independent.

An Operator or Target Administrator on the alert's target can snooze or unsnooze. Readers can inspect current status. Target authorization uses the role's own scope, including for mixed grants.

## HTTP contract

`GET /api/v1/observation-targets/{targetId}/alerts/{alertId}/snooze` returns the current episode, rule-state revision, repository UTC time, snooze metadata and effective delivery suppression. This is a current read, independent of the dashboard's historical window and retained inbox receipt.

`POST` to the same path requires an operation UUID, expected episode UUID, positive expected revision, strict UTC microsecond-aligned `endsAtUtc`, and a reason. The deadline must be future and at most 24 hours from repository time on the first application. `POST` to the corresponding `/unsnooze` path uses the same preconditions and required reason, without an end time.

The server derives the request digest. Exact retries bind operation, actor, target, alert, episode, revision, action, deadline and reason. Replay returns the original audit and applied-result receipt before checking the current revision or deadline. A retry after expiry cannot extend the deadline; a divergent payload conflicts. A new intention needs a new operation identifier and freshly read episode/revision.

The dashboard offers 15 minutes, 1 hour, 8 hours and 24 hours, with 1 hour selected initially. Uncertain retries keep the complete captured command. Once POST is confirmed, readback retries use GET rather than replaying POST. An observed expiry asks for authoritative status instead of inventing server state.

## Repository and delivery behavior

Forward migration 0349 stores episode-scoped snooze metadata separately from evaluator-owned suppression state, together with immutable operation receipts. A successful human mutation increments rule-state revision without changing the alert's firing/acknowledged state.

Mutations take the existing exclusive target advisory dispatch fence and lock the rule state. Delivery dispatch holds the shared form of that fence through the external adapter side effect, so snooze and dispatch have a defined order. A snoozed delivery releases its exact lease and retains queue identity, original due time and attempt count. Expiry or unsnooze permits later delivery without erasing retry backoff or bypassing other suppression rules.

## Evidence scope

The owning validation includes real PostgreSQL mutation, replay, authorization, evaluation and dispatch concurrency checks, an HTTP-to-repository journey, and browser journeys on the final dashboard build. Browser captures use clearly synthetic data. No SQL Server collector change is required by this feature; browser fixtures alone do not certify repository behavior. The dashboard handoff records actual completed checks and any remaining limitations after integration.
