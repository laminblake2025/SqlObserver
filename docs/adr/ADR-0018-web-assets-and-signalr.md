# ADR-0018: Web assets and SignalR invalidation

## Status

- Status: Proposed
- This record is not Accepted. It describes a safe default pending browser,
  accessibility, and deployment qualification.

## Context

The Server owns the browser API, built web assets, SignalR, authentication, and
RBAC. Browser assets must be tied to the product that served them, and real-time
notifications must not become an unbounded diagnostic-data channel. Clients may
disconnect or reconnect, so correctness must come from bounded authorized API
reads rather than trusting event delivery.

## Invariants from accepted architecture

- The web client is React/strict TypeScript served by `SqlObserver.Server`; the
  Server remains the policy and authorization boundary.
- All projections are target-scoped and bounded. Diagnostic text, plans, XML,
  errors, and job content are sensitive untrusted data and are not exposed in
  logs, URLs, or unrestricted transport messages.
- UTC is the persisted/protocol time basis, and authentication does not itself
  grant target access.

## Recommended defaults (not yet decisions)

Serve immutable, server-hosted SPA assets with content-hashed filenames and a
versioned manifest. Bind the manifest and asset digests to the build/product
identity, use long-lived immutable caching for hashed files, and keep the HTML
shell revalidatable so a deployment can move clients to a coherent asset set.
Reject missing or mismatched manifest entries at build/startup validation.

Use SignalR only for target-scoped invalidation notifications: an event may
identify an authorized target, projection/category, revision or sequence, and
UTC time, but never contain raw diagnostic rows, query text, plan/XML, provider
errors, or arbitrary JSON. On reconnect or a missed sequence, the client must
re-fetch through the normal authorized bounded API and tolerate duplicate
notifications. Invalidation is not a source of truth and does not require a
durable queue in the initial slice.

Prefer strict origin/cookie protections, a restrictive content-security policy,
safe output encoding, and explicit browser limits. Any asset CDN or cross-origin
transport is deferred until its identity, cache invalidation, and threat model
are approved.

## Owner decisions still required

- Set the accessibility conformance target and supported browser/version patch
  floors, including assistive-technology combinations.
- Confirm authentication mode, cookie/session policy, proxy/load-balancer
  topology, and whether any customer CDN is permitted.
- Choose asset retention/rollback behavior and capacity/SLO targets for API and
  SignalR connections.
- Approve which projection categories may trigger invalidation and whether
  sensitive-content features can ever be enabled in production.

## Alternatives and consequences

- Mutable asset URLs: simpler deployment, but stale browser caches can mix
  incompatible bundles and weaken rollback diagnosis.
- External CDN: lower origin load, but adds origin trust, cache purge,
  customer-network, and artifact-integrity dependencies.
- Polling only: fewer connection and proxy concerns, but slower UX and more
  repeated API load.
- SignalR raw payloads: lower read latency, but leaks sensitive data and makes
  authorization, replay, size, and stale-client behavior substantially harder.
- Durable event queue: stronger delivery semantics, but adds lifecycle and
  backpressure complexity not justified for invalidation-only hints.

## Downstream implementation and migration gates

Frontend build checks must verify strict typing, manifest completeness, digest
binding, safe cache headers, CSP, and no sensitive fields in event DTOs. Server
tests must prove target authorization before subscription and event delivery,
cross-target isolation, reconnect/resync, sequence gaps, duplicate handling,
size limits, cancellation, and denial behavior. Browser evidence must cover
the supported Edge/Chrome floors, keyboard/screen-reader workflows, locale and
time-zone display, and failure/recovery. No database migration is implied for
volatile invalidation messages; any durable notification state needs a separate
reviewed migration and ADR. These tests are gates, not release support claims.
