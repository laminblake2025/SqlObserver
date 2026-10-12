import { useId } from "react";
import type { SavedIncident, SavedIncidentEvidence } from "./incidentModel.ts";
import { SavedIncidentReportActions } from "./SavedIncidentReportActions.tsx";
import { summarySavedEvidence } from "./savedEvidenceModel.ts";

export function SavedIncidentView({ incident, formatTime, href, onClose, canRead, compact = false }: {
  readonly incident: SavedIncident; readonly formatTime: (value: string) => string;
  readonly href: string; readonly onClose: () => void; readonly canRead: boolean; readonly compact?: boolean;
}) {
  const headingId = useId();
  const Heading = compact ? "h2" : "h4";
  const EvidenceHeading = compact ? "h3" : "h5";
  const coverage = (state: SavedIncidentEvidence["visibilityState"]) => incident.evidence.filter(packet => packet.visibilityState === state).length;
  if (!canRead) return <section className={`panel incident-workspace${compact ? " incident-workspace-compact" : ""}`} aria-label="Saved investigation">
    <p role="status">Viewer, Operator or Target Administrator access is required to read and export this saved investigation.</p>
    <button type="button" onClick={onClose}>Close investigation</button>
  </section>;
  return <section className={`panel saved-incident incident-workspace${compact ? " incident-workspace-compact" : ""}`} aria-labelledby={headingId}>
    <div className="section-heading"><div><p className="eyebrow">Saved investigation</p><Heading id={headingId}>{incident.title}</Heading></div>
      <button type="button" onClick={onClose}>Close investigation</button></div>
    <p>Window: {formatTime(incident.fromUtc)} to {formatTime(incident.toUtc)}. Saved {formatTime(incident.savedAtUtc)}.</p>
    <p className="incident-notes">{incident.notes || "No notes were recorded."}</p>
    <p><a href={href}>Open the saved window and share this investigation</a></p>
    <p className="evidence-footer">{incident.evidence.length} evidence packet{incident.evidence.length===1 ? "" : "s"} · Server revision {incident.targetRevision} · Incident {incident.threadId}</p>
    {incident.evidenceTruncated && <p role="status">This capture reached its size or packet limit. It preserves a bounded set of the most recent evidence, not the entire window.</p>}
    {incident.evidence.length === 0 && <p role="status">No analytics evidence packets existed for this window when it was saved. This does not mean the server was healthy.</p>}
    {incident.evidence.some(packet => packet.visibilityState !== "complete") && <p role="status">Some saved packets have partial or unavailable coverage. Review each packet's evidence status.</p>}
    {incident.evidence.length > 0 ? <section className="saved-evidence-overview" aria-labelledby={`${headingId}-evidence`}>
      <EvidenceHeading id={`${headingId}-evidence`}>Evidence overview</EvidenceHeading>
      <p>Recorded packet coverage: {coverage("complete")} complete · {coverage("partial")} partial · {coverage("unavailable")} unavailable · {coverage("unsupported")} unsupported.</p>
      <p className="evidence-footer">Source references identify the retained inputs. They do not establish a root cause or describe every observation in the saved window.</p>
    </section> : null}
    {incident.evidence.map(packet => <SavedEvidencePacket key={`${packet.occurredAtUtc}:${packet.packetId}`}
      incident={incident} packet={packet} formatTime={formatTime} compact={compact} />)}
    <SavedIncidentReportActions key={`${incident.targetId}:${incident.threadId}`} incident={incident} formatTime={formatTime} canRead={canRead} compact={compact}/>
  </section>;
}

function SavedEvidencePacket({ incident, packet, formatTime, compact }: {
  readonly incident: SavedIncident; readonly packet: SavedIncidentEvidence;
  readonly formatTime: (value: string) => string; readonly compact: boolean;
}) {
  const headingId = useId();
  const Heading = compact ? "h3" : "h5";
  const ReferenceHeading = compact ? "h4" : "h6";
  const summary = summarySavedEvidence(incident, packet);
  return <article className="saved-evidence-packet" aria-labelledby={headingId}>
    <Heading id={headingId}>{formatTime(packet.occurredAtUtc)} · {packet.evidenceKind}</Heading>
    <p className={`saved-evidence-coverage saved-evidence-${packet.visibilityState}`}>Recorded packet coverage: {packet.visibilityState}</p>
    <p>Confidence {packet.confidence === null ? "Unknown" : `${Math.round(packet.confidence * 100)}%`}. This is a recorded score, not a probability of root cause.</p>
    {summary.state === "fallback" ? <p className="saved-evidence-summary-gap">{summary.reason}</p> : <>
      {summary.state === "unavailable" ? <p className="saved-evidence-summary-gap">This packet records {packet.visibilityState} coverage. Its retained references do not establish complete evidence. Inspect the raw saved evidence and source provenance for the recorded limitation. New observations require a new investigation; reloading this saved capture does not replace its evidence.</p> : null}
      <ReferenceHeading>Recorded source references</ReferenceHeading>
      {summary.references.length === 0 ? <p>No source references were recorded in this packet.</p> : <ul className="saved-evidence-references">
        {summary.references.map((reference, index) => <li key={`${reference.type}:${reference.identity}:${reference.occurredAtUtc}:${index}`}>
          <strong>{reference.label}</strong><code>{reference.identity}</code><span>Recorded {formatTime(reference.occurredAtUtc)}</span>
        </li>)}
      </ul>}
      <p className="evidence-footer">Reference identities are opaque recorded identifiers.</p>
      {summary.producerTruncated ? <p className="saved-evidence-summary-gap">The producer marked this packet as truncated. Its retained references are a bounded subset, independently of this investigation's capture limit.</p> : null}
      {summary.tombstones.length > 0 ? <>
        <ReferenceHeading>Recorded tombstones</ReferenceHeading>
        <p className="evidence-footer">These source markers are preserved as recorded; they do not establish a current collector state.</p>
        <ul className="saved-evidence-tombstones">{summary.tombstones.map((value, index) => <li key={`${index}:${value}`}>{value}</li>)}</ul>
      </> : null}
      <p className="evidence-footer">Producer algorithm {summary.algorithm} · Generation {summary.generation}</p>
    </>}
    <details className="saved-evidence-provenance">
      <summary>Saved packet provenance</summary>
      <dl>
        <div><dt>Packet ID</dt><dd><code>{packet.packetId}</code></dd></div>
        <div><dt>Source run</dt><dd>{packet.sourceRunId === null ? "Not recorded" : <code>{packet.sourceRunId}</code>}</dd></div>
        <div><dt>Source cutoff</dt><dd>{packet.sourceCutoffUtc === null ? "Not recorded" : <>{formatTime(packet.sourceCutoffUtc)}<code>{packet.sourceCutoffUtc}</code></>}</dd></div>
        <div><dt>Source digest</dt><dd><code>{packet.sourceSha256}</code></dd></div>
        <div><dt>Identity digest</dt><dd><code>{packet.identitySha256}</code></dd></div>
        <div><dt>Source cutoff digest</dt><dd><code>{packet.sourceCutoffSha256}</code></dd></div>
      </dl>
      <p className="evidence-footer">The source cutoff is a recorded source boundary, separate from packet occurrence time. A missing run or cutoff is not inferred from another timestamp. The source digest is not a checksum of this JSON packet.</p>
    </details>
    <details className="saved-evidence-raw"><summary>Raw saved evidence</summary>
      <pre tabIndex={0} role="region" aria-label={`Saved evidence packet ${packet.packetId}`}>{JSON.stringify(packet.evidence, null, 2)}</pre>
    </details>
  </article>;
}
