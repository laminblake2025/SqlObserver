import { fleetServerRows } from './fleetGridModel';
import { overviewHref } from './overviewModel';
import type { OverviewScope, OverviewSnapshot } from './overviewTypes';

export function FleetServerStrip({ snapshot, scope }: {
  readonly snapshot: OverviewSnapshot;
  readonly scope: OverviewScope;
}) {
  const rows = fleetServerRows(snapshot, '');
  return <nav className="fleet-strip" aria-label="Loaded server status">
    <div className="fleet-strip-heading"><strong>Server status</strong><small>{rows.length} loaded · observed exceptions first</small></div>
    <ul className="fleet-strip-list">{rows.map(row => {
      const signals = [
        ['CPU', row.signals.cpu], ['Memory', row.signals.memory], ['Waits', row.signals.waits],
        ['Blocking', row.signals.blocking], ['Backups', row.signals.backups], ['HA', row.signals.ha],
      ] as const;
      const summary = !row.evidence ? 'Evidence unavailable' : row.attentionCount > 0
        ? `${row.attentionCount} observed ${row.attentionCount === 1 ? 'issue' : 'issues'}`
        : row.evidence.gaps.length > 0 ? 'Coverage gaps' : 'No observed exceptions';
      return <li key={row.target.targetId}><a href={overviewHref(scope, 'health', row.target.targetId)}
        aria-label={`${row.target.displayName}: ${summary}. ${signals.map(([label, signal]) => `${label}: ${signal.detail}`).join('. ')}`}>
        <span className="fleet-strip-name">{row.target.displayName}</span>
        <small>{summary}</small>
        <span className="fleet-strip-signals" aria-hidden="true">{signals.map(([label, signal]) =>
          <span className={`fleet-signal fleet-signal-${signal.tone}`} key={label} title={signal.detail}>
            <span className="fleet-signal-dot" />{label}
          </span>)}</span>
      </a></li>;
    })}</ul>
  </nav>;
}
