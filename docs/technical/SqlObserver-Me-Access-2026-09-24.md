# Caller access for the web interface

Authenticated clients can read `GET /api/v1/me`, optionally with a `targetId`
query parameter. The response separates roles granted anywhere, roles granted
for every target, and roles permitted on the **specified** target. The target
list is not disclosed. Each target decision calls the role-specific
`CanAccess(role, targetId)` check, so a Viewer grant on one server cannot
become Viewer access on another server through the union of role scopes.
Anonymous requests are challenged; malformed and zero target GUIDs are rejected.

The web client validates the closed response and binds it to the selected
target. The Add server actions appear only when the caller has an active,
all-target `TargetAdministrator` grant, matching the registration service's
authorization rule. When there are no monitored servers, a viewer sees the
role needed to add one. The server remains the authority for every action;
the UI decision only removes an action the caller cannot complete.

Two hosted HTTP checks passed for mixed grants on two targets, anonymous and
invalid requests, and all-target grants. Two web behavior checks and TypeScript
checking passed. This is the first role-aware interface use of `/me`; other
target actions still need to consume its target-specific roles during the
interface refactor.
