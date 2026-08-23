# SqlObserver terminology

This vocabulary gives the product an original, internally consistent language. Terms describe intended concepts; their presence does not imply that the runtime feature exists in the current scaffold.

| Term | Meaning |
| --- | --- |
| **Observation target** | A registered Microsoft SQL Server endpoint and its monitoring policy. PostgreSQL is never an observation target. |
| **Capability profile** | A timestamped, version-aware statement of features, permissions, platform facts, and safe fallbacks detected for an observation target. |
| **Collector contract** | Versioned metadata and behavior bounds for one kind of collection, including identity, permissions, intervals, cost, fallback, and output schema. |
| **Observation cycle** | One scheduled, lease-owned execution of a collector contract for one target. |
| **Signal sample** | A bounded, timestamped measurement emitted by an observation cycle. |
| **Diagnostic event** | A discrete item such as a deadlock or job outcome whose identity and payload are stored separately from regular metric samples. |
| **Health snapshot** | A bounded application projection of recent signals and events for a target; it is not a live connection to that target. |
| **Metric window** | An explicit UTC interval and aggregation used to summarize or compare signal samples. |
| **Evidence packet** | A correlated, read-only set of signals, events, alerts, and metadata relevant to a diagnostic question. |
| **Incident thread** | A time-bounded correlation of evidence packets and alert transitions. It is distinct from a ticket or automated remediation workflow. |
| **Passive monitoring** | Collection through supported read interfaces that makes no configuration or persisted state change on the monitored SQL Server. |
| **Enhanced monitoring** | Optional evidence made available only after a DBA independently reviews and runs a separate setup script. SqlObserver never applies that setup. |
| **Fallback path** | A declared, tested lower-capability collection method selected when a preferred supported interface is unavailable. It is not silent emulation. |
| **Visibility gap** | An explicit interval or scope for which evidence is missing, rejected, truncated, delayed, or unsupported. |
| **Repository** | The PostgreSQL 18.x data store owned by SqlObserver for configuration and observations. It is not the monitored engine. |
| **Worker lease** | A short PostgreSQL-backed ownership claim with expiry and fencing used to prevent concurrent work for the same scheduling key. |
| **Administrative write** | A user- or service-initiated mutation of SqlObserver configuration or state that requires authorization and audit. It never means a target-database write. |
| **MCP diagnostic tool** | A named, allowlisted read-only operation served through SqlObserver application services with authorization, bounds, and audit. |

## Naming rules

- Use `target` only for a monitored SQL Server and `repository` only for PostgreSQL.
- Use UTC in storage, contracts, logs, audit, leases, and internal calculations. A localized time is presentation only and must retain its UTC reference.
- Use `unsupported`, `degraded`, `truncated`, and `visibility gap` explicitly; do not turn absence of evidence into a healthy value.
- Do not call enhanced setup automatic, required, or self-healing.
- Do not describe an MCP tool as an agent with authority to act. Tools return bounded diagnostic data only.
- Prefer the terms above in source identifiers and documentation when the concepts match; add new terms here when a durable domain concept appears.

## Clean-room naming boundary

Do not import proprietary feature names, alert names, dashboard labels, report titles, API vocabulary, schema identifiers, UI copy, icons, screenshots, or taxonomies from another monitoring product. Renaming copied structures is still copying. The complete boundary is in [clean-room-boundary.md](clean-room-boundary.md) and [ADR-0009](../adr/ADR-0009-clean-room-boundary.md).
