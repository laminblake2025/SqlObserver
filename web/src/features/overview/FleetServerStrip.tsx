import { fleetServerRows } from './fleetGridModel';
import { useBackupSummaryClock } from './useBackupSummaryClock';
import { overviewHref } from './overviewModel';
import { retainChartInspectionHref } from '../../components/chartInspectionModel.ts';
import type { OverviewScope, OverviewSnapshot } from './overviewTypes';

export function FleetServerStrip({ snapshot, scope, stickyOffset, busy = false }: {
  readonly snapshot: OverviewSnapshot;
  readonly scope: OverviewScope;
  readonly stickyOffset?: number;
  readonly busy?: boolean;
}) {
  const clock = useBackupSummaryClock(snapshot);
  const rows = fleetServerRows(snapshot, '', clock);
  return <nav className={`fleet-strip${stickyOffset === undefined ? '' : ' workspace-fleet-strip'}`}
    style={stickyOffset === undefined ? undefined : { top: stickyOffset }} aria-label="Loaded server status" aria-busy={busy}>
    <div className="fleet-strip-heading"><strong>Server status</strong><small>{rows.length} loaded · observed exceptions first</small></div>
    <div className="fleet-strip-scroll" tabIndex={0} role="region" aria-label="Loaded fleet server entries"><ul className="fleet-strip-list">{rows.map(row => {
      const signals = [
        ['CPU', row.signals.cpu], ['Memory', row.signals.memory], ['Waits', row.signals.waits],
        ['Blocking', row.signals.blocking], ['Backups', row.signals.backups], ['HA', row.signals.ha],
      ] as const;
      const summary = !row.evidence ? 'Evidence unavailable' : row.attentionCount > 0
        ? `${row.attentionCount} observed ${row.attentionCount === 1 ? 'issue' : 'issues'}`
        : row.evidence.gaps.length > 0 ? 'Coverage gaps' : 'No observed exceptions';
      return <li key={row.target.targetId}><a href={retainChartInspectionHref(overviewHref(scope, 'health', row.target.targetId), location.hash)}
        aria-current={scope.target.toLowerCase() === row.target.targetId.toLowerCase() ? 'location' : undefined}
        aria-label={`${row.target.displayName}: ${summary}. ${signals.map(([label, signal]) => `${label}: ${signal.detail}`).join('. ')}`}>
        <span className="fleet-strip-name">{row.target.displayName}</span>
        <small>{summary}</small>
        <span className="fleet-strip-signals" aria-hidden="true">{signals.map(([label, signal]) =>
          <span className={`fleet-signal fleet-signal-${signal.tone}`} key={label} title={signal.detail}>
            <span className="fleet-signal-dot" />{label}
          </span>)}</span>
      </a></li>;
    })}</ul></div>
    {rows.length === 0 && <p className="table-note">No authorized servers were returned for this fleet view. Missing evidence is not an all-clear signal.</p>}
  </nav>;
}
