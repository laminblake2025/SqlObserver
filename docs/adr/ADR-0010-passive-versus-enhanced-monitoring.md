# ADR-0010: Passive versus enhanced monitoring

- Status: Accepted
- Date: 2026-08-23

## Context

Many supported SQL Server diagnostic views can be read without persistent target changes. Some richer evidence may benefit from a dedicated Extended Events session or a blocked-process threshold, while Query Store availability is controlled by each database owner. Automatically changing those settings would expand blast radius, undermine DBA ownership, and violate the default agentless/read-only posture.

## Decision

Define two operational modes with a hard human-control boundary.

**Passive monitoring** is the default. It uses documented, supported, parameterized read interfaces and does not create or change target objects, configuration, Query Store, Extended Events sessions, blocked-process settings, jobs, indexes, plans, or sessions. Reading an existing supported system source such as the `system_health` Extended Events session is passive when done with bounded permissions and no change to that source.

**Enhanced monitoring** is optional evidence that depends on target-side setup. Each setup or upgrade is a separate, versioned, idempotency-aware, reviewable script with prerequisites, required permissions, exact changes, resource/retention bounds, validation, removal/recovery instructions, and supported-version scope. A DBA independently chooses, reviews, and runs it through their normal change process. SqlObserver may detect and report its state but never applies, repairs, upgrades, enables, or removes it.

Query Store is always DBA-controlled: SqlObserver never enables, disables, clears, resizes, changes capture/retention settings, or forces/unforces a plan. Extended Events sessions and blocked-process settings are never changed automatically. Absence or incompatibility produces an explicit unsupported/degraded state and a documented passive fallback where one exists.

## Consequences

- Default collection maintains a non-mutating target boundary and can be reviewed as read-only.
- Enhanced evidence has deliberate DBA ownership and a visible configuration trail rather than hidden automation.
- Onboarding cannot promise complete feature coverage; UIs, alerts, APIs, and MCP must distinguish unavailable, passive fallback, and enhanced evidence.
- Enhanced scripts need security, load, upgrade, removal, version, and permission testing but remain outside service execution.
- The product must not nag or repeatedly attempt to “repair” a DBA's decision not to enable an enhancement.
