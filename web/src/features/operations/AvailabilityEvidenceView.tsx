import { availabilityCoverageMessage, availabilityVisibilityLabel,
  type AvailabilityPage, type AvailabilityStream, type AvailabilityIdentity,
  type AvailabilityReplica, type AvailabilityDatabase } from "./availabilityEvidence.ts";

export function AvailabilityEvidenceView({ page, stream, formatTime }: {
  readonly page: AvailabilityPage;
  readonly stream: AvailabilityStream;
  readonly formatTime: (value: string | null | undefined) => string;
}) {
  return <>
    <p className="activity-evidence" role="status">{availabilityCoverageMessage(page)}</p>
    <dl className="availability-evidence-facts">
      <div><dt>Coverage</dt><dd>{page.state} · {page.evidenceStatus.replaceAll("_", " ")}</dd></div>
      <div><dt>Source observed</dt><dd>{page.sourceObservedAtUtc === null ? "Unknown" : formatTime(page.sourceObservedAtUtc)}</dd></div>
      <div><dt>Collection completed</dt><dd>{page.completedAtUtc === null ? "No receipt" : formatTime(page.completedAtUtc)}</dd></div>
      <div><dt>Run accounting (both streams)</dt><dd>{page.retainedItems} retained / {page.outputItems} captured / {page.sourceRows} source rows</dd></div>
      <div><dt>Capture loss</dt><dd>{page.lossKind.replaceAll("_", " ")}{page.truncated ? " · truncated" : ""}</dd></div>
    </dl>
    {!page.collectorEnabled && page.runId !== null && <p>Collection is currently disabled. These are previously published observations.</p>}
    {page.items.length > 0 && <div className="table-scroll availability-evidence-scroll" role="region"
      aria-label={`${stream === "replicas" ? "Replica" : "Database replica"} evidence table`} tabIndex={0}><table className="availability-evidence-table">
      <caption>{stream === "replicas" ? "Configured AG replicas" : "AG database-replica evidence"} · {page.items.length} rows on this page</caption>
      <thead><tr><th scope="col">Group</th><th scope="col">Replica</th>
        {stream === "databases" && <th scope="col">Database</th>}
        {stream === "replicas" ? <><th scope="col">Role</th><th scope="col">Operational state</th><th scope="col">Connection</th></>
          : <><th scope="col">Synchronization</th><th scope="col">Synchronization health</th><th scope="col">Database state</th>
            <th scope="col">Suspended</th><th scope="col">Log send queue (KB)</th><th scope="col">Log send rate (KB/s)</th>
            <th scope="col">Redo queue (KB)</th><th scope="col">Redo rate (KB/s)</th><th scope="col">Last commit (server local)</th></>}
        <th scope="col">Location</th><th scope="col">Visibility</th><th scope="col">State observed</th><th scope="col">Observer</th>
      </tr></thead>
      <tbody>{page.items.map(item => <tr key={identityKey(item)}>
        <td><Fingerprint value={item.groupFingerprint} /></td><td><Fingerprint value={item.replicaFingerprint} /></td>
        {stream === "databases" && <td><Fingerprint value={(item as AvailabilityDatabase).databaseFingerprint} /></td>}
        {stream === "replicas" ? <ReplicaCells item={item as AvailabilityReplica} /> : <DatabaseCells item={item as AvailabilityDatabase} />}
        <td>{item.replicaIsLocal === null ? "Unknown" : item.replicaIsLocal ? "Local" : "Remote"}</td>
        <td>{availabilityVisibilityLabel(item.visibilityScope)}</td><td>{item.stateObserved ? "Yes" : "No"}</td>
        <td><Fingerprint value={item.observerReplicaFingerprint} /></td>
      </tr>)}</tbody>
    </table></div>}
    {page.hasMore && !page.nextCursor && <p>More rows exist, but continuation is unavailable. Ask an administrator to configure cursor signing.</p>}
    <p className="availability-evidence-note">Opaque fingerprints identify each group, replica and database; expand an identifier for its full value.
      Unknown means the source did not provide usable state. Remote state is what the observing replica last reported.
      {stream === "databases" && " Last commit uses the SQL Server local clock without an offset; the time-zone toggle does not convert it. Queues and this timestamp do not establish an exact RPO."}</p>
  </>;
}

function identityKey(item: AvailabilityIdentity): string {
  return `${item.groupFingerprint}:${item.replicaFingerprint}:${"databaseFingerprint" in item ? item.databaseFingerprint : ""}`;
}
function Fingerprint({ value }: { readonly value: string | null }) {
  return value === null ? <>Unknown</> : <details className="availability-identity"><summary>{value.slice(0, 8)}…</summary><code>{value}</code></details>;
}
function state(value: string | null) { return value === null ? "Unknown" : value.replaceAll("_", " "); }
function ReplicaCells({ item }: { readonly item: AvailabilityReplica }) {
  return <><td>{state(item.role)}</td><td>{state(item.operationalState)}</td><td>{state(item.connectedState)}</td></>;
}
function DatabaseCells({ item }: { readonly item: AvailabilityDatabase }) {
  return <><td>{state(item.synchronizationState)}</td><td>{state(item.synchronizationHealth)}</td><td>{state(item.databaseState)}</td>
    <td>{item.isSuspended === null ? "Unknown" : item.isSuspended ? "Yes" : "No"}</td>
    <td>{item.logSendQueueKb ?? "Unknown"}</td><td>{item.logSendRateKbPerSecond ?? "Unknown"}</td>
    <td>{item.redoQueueKb ?? "Unknown"}</td><td>{item.redoRateKbPerSecond ?? "Unknown"}</td>
    <td>{item.sourceLastCommitLocal === null ? "Unknown" : item.sourceLastCommitLocal.replace("T", " ")}</td></>;
}
