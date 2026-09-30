import type { SavedIncident } from "./incidentModel.ts";

export function SavedIncidentView({ incident, formatTime, href, onClose }: {
  readonly incident: SavedIncident; readonly formatTime: (value: string) => string;
  readonly href: string; readonly onClose: () => void;
}) {
  return <section className="panel saved-incident" aria-labelledby="saved-incident-title">
    <div className="section-heading"><div><p className="eyebrow">Saved investigation</p><h4 id="saved-incident-title">{incident.title}</h4></div>
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
      <pre>{JSON.stringify(packet.evidence,null,2)}</pre>
    </details>)}
  </section>;
}
