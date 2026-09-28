# Buffer Node PLE collection contract

The existing `engine.core` metric `engine.page_life_expectancy_seconds` is the
Buffer Manager aggregate. It cannot represent individual NUMA nodes: its
output contract allows one undimensioned row for that key, and the M4
repository writer rejects duplicate metric keys before persistence. Keep the
aggregate metric and the applied M4 contract unchanged.

The pinned `engine.buffer-node-ple.sqlserver15-17-windows.v1.sql` source reads
`sys.dm_os_performance_counters` Buffer Node PLE by numeric node ID. Its reader
accepts ordered, distinct nodes 0–1023, nonnegative seconds, one UTC capture
time, at most 128 nodes, and at most 64 KiB of response evidence. A 129th row
or byte cap withholds the entire set so a partial node inventory cannot be
presented as complete. Source code and a focused native SQL Server 2022 test
validate this foundation; it is not scheduled or persisted yet.

Publication needs a separate versioned `engine.buffer-nodes` collector and
fenced repository commit path. Use a stable `(metric key, node_id dimension)`
identity, reject duplicate nodes and divergent replay atomically, retain
visibility gaps on truncated or invalid reads, and grant no direct table
write. A target-scoped read projection should return per-node series in the
shared Resources time window, with the Buffer Manager aggregate labelled
separately. Qualify the pinned source and least-privilege permission on SQL
Server 2019 and 2025 before enabling schedules by default.

Source semantics: [Microsoft's Buffer Node counters](https://learn.microsoft.com/en-us/sql/relational-databases/performance-monitor/sql-server-buffer-node?view=sql-server-ver17)
describe one instance per NUMA node and PLE in seconds. [Microsoft's memory
monitoring guidance](https://learn.microsoft.com/en-us/sql/relational-databases/performance-monitor/monitor-memory-usage?view=sql-server-ver17)
distinguishes the overall Buffer Manager PLE from each Buffer Node value.
