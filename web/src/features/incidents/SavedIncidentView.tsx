import { useId } from "react";
import type { SavedIncident } from "./incidentModel.ts";
import { SavedIncidentReportActions } from "./SavedIncidentReportActions.tsx";

export function SavedIncidentView({ incident, formatTime, href, onClose, compact = false }: {
  readonly incident: SavedIncident; readonly formatTime: (value: string) => string;
  readonly href: string; readonly onClose: () => void; readonly compact?: boolean;
}) {
  const headingId = useId();
  const Heading = compact ? "h2" : "h4";
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
    {incident.evidence.map(packet => <details key={`${packet.occurredAtUtc}:${packet.packetId}`}>
      <summary>{formatTime(packet.occurredAtUtc)} · {packet.evidenceKind} · {packet.visibilityState}</summary>
      <p>Packet {packet.packetId} · Confidence {packet.confidence === null ? "Unknown" : `${Math.round(packet.confidence*100)}%`}</p>
      <p>Source digest: <code>{packet.sourceSha256}</code></p>
      <pre tabIndex={0} role="region" aria-label={`Saved evidence packet ${packet.packetId}`}>{JSON.stringify(packet.evidence,null,2)}</pre>
    </details>)}
    <SavedIncidentReportActions key={`${incident.targetId}:${incident.threadId}`} incident={incident} formatTime={formatTime} compact={compact}/>
  </section>;
}
