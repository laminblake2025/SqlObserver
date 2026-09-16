# Overview lab installation readback

Verified WIN-QNGOV5GDM24 over its configured SSH connection on September 7, 2026, following the request to install the Overview patch's SQL and PostgreSQL components.

The September 5 replication deployment already installed Overview and subsequent fixes. No missing installation was identified; no binaries, database objects or service settings were changed during this readback.

- SQL Server, SQL Server Agent, PostgreSQL, SqlObserverServer and SqlObserverCollector are running.
- PostgreSQL reports version 18.6. The scoped `reporting.overview_workload_history(uuid,bigint,timestamptz,timestamptz,timestamptz)` function is present.
- The server uses `C:\SqlObserverLab\server-replication-overview-v3-20260905`; its application assembly matches the local validated deployment artifact (SHA256 `D64816D2327159F0A8F0526645FC9A927E37CEFF9AED7FC81A2898331117ECA7`).
- The collector uses `C:\SqlObserverLab\collector-replication-overview-v2-20260905`; its SQL Server infrastructure assembly matches the local validated artifact (SHA256 `BB3145AAAB9C827BC53B7E6AFBED4B589BC868A36A79DED645D9AB856101EE2D`).
- The frontend remains the newer `C:\SqlObserverLab\web-history-pagination-20260905`.
- Authenticated target health returned current evidence at 13:54:20 UTC, with nine core metrics sampled at 13:54:05 UTC, including `engine.start_time_key`. This confirms the collector's startup-marker payload is being accepted by PostgreSQL.

The existing September 5 report records migrations through 40, including Overview migration 35. The restricted server login correctly denied direct migration-ledger reads; this check did not elevate its privileges or independently reread the full ledger.

## Outstanding runtime issue

Authenticated Overview requests for a one-hour fleet window and a five-minute single-server window returned HTTP 504. The target health endpoint succeeded. Installation presence and collection freshness are verified, but Overview is not currently validated as healthy. The timeout requires a separate runtime diagnosis; reinstalling the same verified bundles would not establish a fix.

This issue was subsequently repaired and verified live: see [Overview timeout repair](SqlObserver-Overview-Timeout-Fix-2026-09-07.md).
