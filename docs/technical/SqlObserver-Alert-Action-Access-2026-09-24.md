# Target-aware alert acknowledgement in the web client

The alert detail pane shows Acknowledge only when `/api/v1/me` reports an
active Operator or Target Administrator grant for the selected server. This
matches `AlertAdministrationService.WriteAnyAsync`, which checks either role
against the alert target. A role granted on a different server cannot unlock
the action. The detail pane names the roles required when a firing alert is
readable but the action is unavailable. The server continues to authorize each
acknowledgement.

The focused access test covers mixed grants, a matching Operator, a matching
Target Administrator, an unrelated target, and missing access. The existing
alert UI and client tests and TypeScript checking passed. No broad suite was
rerun for this UI change.
