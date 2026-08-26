# ADR-0013: Host observation boundary

- Status: Accepted
Date: 2026-08-25

## Context

M10 host metrics are collected by the Windows Collector under its service identity. A SQL target
and the host on which it runs are not interchangeable identities: one host may serve multiple
targets, and a target may move. Host observations therefore need a revisioned target-to-host
binding and a profile that records the exact capabilities authorized for that revision.

Windows providers expose hostile and identifying text (machine names, volume labels, provider
messages, and paths). The host telemetry contract must not carry that text. Host identity and
volume identity are domain-separated SHA-256 fingerprints; the output contains only those opaque
keys and bounded numeric values.

## Decision

The host collector has a source-neutral application port and a Windows infrastructure adapter.
The adapter may issue only the fixed CPU, memory, logical-volume-space, queue, read-latency, and
write-latency query kinds. Every operation uses the service identity, a five-second command
deadline, a 30-second default cadence (never below ten seconds), a 256-row cap, and a 256 KiB
output cap. Target and profile revision mismatches, denied/unreachable/unsupported states, and
unproven cancellation terminate collection without output (fail closed).

`host.metrics` v1 requires CPU utilization, available and committed memory, and for each logical
volume free/total bytes, queue length, and read/write latency. Volumes are keyed only by opaque
fingerprints. Retry count and circuit state are explicit result metadata; provider exception text
is never copied to telemetry or logs.

## Consequences

Host access remains passive and least-privileged. The infrastructure adapter is injectable so WMI
and performance-data implementations can be platform-tested without a live machine. Integrators
must register the host collector in the existing composition root and persist its bounded output
through the normal collector ingestion path; this ADR intentionally does not change shared
registration or database migrations.
