import { fleetServerRows, type FleetSignal } from './fleetGridModel';
import { overviewHref } from './overviewModel';
import type { OverviewScope, OverviewSnapshot, OverviewValue } from './overviewTypes';

export function FleetServerGrid({ snapshot, scope, search, onSearch, formatTime }: {
  readonly snapshot: OverviewSnapshot;
  readonly scope: OverviewScope;
  readonly search: string;
  readonly onSearch: (value: string) => void;
  readonly formatTime: (value: string) => string;
}) {
  const rows = fleetServerRows(snapshot, search);
  return <section className="panel overview-panel overview-servers">
    <div className="overview-panel-heading">
      <div><p className="eyebrow">AUTHORIZED TARGETS</p><h2>Server grid</h2></div>
      <label className="inline-search">Search loaded servers<input aria-label="Search loaded servers" placeholder="Search loaded targets…" value={search} onChange={event => onSearch(event.target.value)} /></label>
    </div>
    <p className="table-note">Showing {rows.length} of {snapshot.targets.length} authorized targets, with observed exceptions first. Search covers this overview response only.</p>
    <div className="table-scroll"><table>
      <caption>Current alerts and blocking; other signals show the latest available source evidence.</caption>
      <thead><tr><th scope="col">Server</th><th scope="col">Attention</th><th scope="col">Host CPU</th><th scope="col">Host memory</th><th scope="col">Waits</th><th scope="col">Blocking</th><th scope="col">Backups</th><th scope="col">HA</th><th scope="col">Last SQL core success</th></tr></thead>
      <tbody>{rows.map(row => <tr key={row.target.targetId}>
        <td><a href={overviewHref(scope, 'health', row.target.targetId)}>{row.target.displayName}</a><small>{row.target.targetId}</small></td>
        <td><a href={overviewHref(scope, 'alerts', row.target.targetId)}>{count(row.evidence?.activeAlerts)} active alerts</a><small>{row.attentionCount} observed issues · SQL core {row.evidence?.collectionState?.replaceAll('_', ' ') ?? 'unavailable'}</small></td>
        <SignalCell label="Host CPU" signal={row.signals.cpu} />
        <SignalCell label="Host memory" signal={row.signals.memory} />
        <SignalCell label="Waits" signal={row.signals.waits} />
        <SignalCell label="Blocking" signal={row.signals.blocking} />
        <SignalCell label="Backups" signal={row.signals.backups} />
        <SignalCell label="HA" signal={row.signals.ha} />
        <td>{row.evidence?.lastObservedUtc ? formatTime(row.evidence.lastObservedUtc) : 'Unavailable'}</td>
      </tr>)}</tbody>
    </table></div>
    {rows.length === 0 && <p className="empty-state">No loaded servers match this search.</p>}
    <p className="table-note">Dots show evidence state; red blocking means blocked sessions were observed. CPU and memory have no threshold-based health verdict. Backup and HA counts do not establish RPO or replica health.</p>
  </section>;
}

function SignalCell({ label, signal }: { readonly label: string; readonly signal: FleetSignal }) {
  return <td><span className={`fleet-signal fleet-signal-${signal.tone}`} title={signal.detail} aria-label={`${label}: ${signal.detail}`}>
    <span className="fleet-signal-dot" aria-hidden="true" />{signal.text}
  </span></td>;
}

function count(value: OverviewValue | undefined): string {
  if (!value || value.value === null || ['stale', 'unavailable', 'unsupported', 'disabled'].includes(value.state)) return '—';
  return `${value.value.toLocaleString()}${value.state === 'partial' ? '+' : ''}`;
}
