import { useState } from 'react';
import { useBackupSummaryClock } from './useBackupSummaryClock';
import { fleetServerRows, groupFleetServerRows, type FleetGrouping, type FleetSignal } from './fleetGridModel';
import { FleetServerGroupingEditor } from './FleetServerGroupingEditor';
import { backupAssessmentHref, overviewHref } from './overviewModel';
import { healthCheckHref } from '../operations/agentHistoryModel';
import type { OverviewScope, OverviewSnapshot, OverviewTarget, OverviewValue } from './overviewTypes';

export function FleetServerGrid({ snapshot, scope, search, onSearch, formatTime, onGroupingUpdated }: {
  readonly snapshot: OverviewSnapshot;
  readonly scope: OverviewScope;
  readonly search: string;
  readonly onSearch: (value: string) => void;
  readonly formatTime: (value: string) => string;
  readonly onGroupingUpdated: () => void;
}) {
  const [groupBy, setGroupBy] = useState<FleetGrouping>('environment');
  const [editing, setEditing] = useState<OverviewTarget | null>(null);
  const clock = useBackupSummaryClock(snapshot);
  const rows = fleetServerRows(snapshot, search, clock);
  const groups = groupFleetServerRows(rows, groupBy);
  return <section className="panel overview-panel overview-servers">
    <div className="overview-panel-heading">
      <div><p className="eyebrow">AUTHORIZED TARGETS</p><h2>Server grid</h2></div>
      <div className="fleet-grid-controls">
        <label className="inline-search">Group by<select aria-label="Group servers by" value={groupBy} onChange={event => setGroupBy(event.target.value as FleetGrouping)}><option value="environment">Environment</option><option value="tag">Tag</option><option value="none">No grouping</option></select></label>
        <label className="inline-search">Search loaded servers<input aria-label="Search loaded servers" placeholder="Search loaded targets…" value={search} onChange={event => onSearch(event.target.value)} /></label>
      </div>
    </div>
    <p className="table-note">Showing {rows.length} of {snapshot.targets.length} authorized targets, with observed exceptions first within each group. Search covers this overview response only.{groupBy === 'tag' ? ' Servers with multiple tags appear in each matching group.' : ''}</p>
    {editing && <FleetServerGroupingEditor key={`${editing.targetId}:${editing.groupingRevision}`} target={editing}
      onClose={() => setEditing(null)} onSaved={() => { setEditing(null); onGroupingUpdated(); }} />}
    <div className="table-scroll" tabIndex={0} role="region" aria-label="Server health grid"><table>
      <caption>Current alerts, blocking, and backup policy checks; CPU and memory follow the selected window.</caption>
      <thead><tr><th scope="col">Server</th><th scope="col">Attention</th><th scope="col">SQL scheduler CPU</th><th scope="col">OS free via SQL</th><th scope="col">Waits</th><th scope="col">Blocking</th><th scope="col">Backups</th><th scope="col">HA</th><th scope="col">Last SQL core success</th></tr></thead>
      {groups.map(group => <tbody key={group.label ?? 'all'}>
        {group.label && <tr className="fleet-group-row"><th scope="rowgroup" colSpan={9}>{groupBy === 'tag' ? 'Tag' : 'Environment'}: {group.label} <small>· {group.rows.length} servers</small></th></tr>}
        {group.rows.map(row => <tr key={row.target.targetId}>
        <td><a href={overviewHref(scope, 'health', row.target.targetId)}>{row.target.displayName}</a>
          <small>{row.target.environment ?? 'Unassigned'} · {row.target.tags.length ? row.target.tags.join(', ') : 'No tags'}</small>
          {row.target.canManageGrouping && <button type="button" className="table-link" aria-label={`Edit grouping for ${row.target.displayName}`} onClick={() => setEditing(row.target)}>Edit labels</button>}
        </td>
        <td><a href={overviewHref(scope, 'alerts', row.target.targetId)}>{count(row.evidence?.activeAlerts)} active alerts</a><small>{row.attentionCount} observed issues · SQL core {row.evidence?.collectionState?.replaceAll('_', ' ') ?? 'unavailable'}</small></td>
        <SignalCell label="SQL scheduler CPU" signal={row.signals.cpu} href={overviewHref(scope, 'resources', row.target.targetId)} />
        <SignalCell label="OS free via SQL" signal={row.signals.memory} href={overviewHref(scope, 'resources', row.target.targetId)} />
        <SignalCell label="Waits" signal={row.signals.waits} href={overviewHref(scope, 'waits', row.target.targetId)} />
        <SignalCell label="Blocking" signal={row.signals.blocking} href={overviewHref(scope, 'activity', row.target.targetId)} />
        <SignalCell label="Backups" signal={row.signals.backups} href={backupAssessmentHref(scope, row.target.targetId, row.evidence?.backupSummary?.assessedAtUtc ?? null)} />
        <SignalCell label="HA" signal={row.signals.ha} href={healthCheckHref(scope, row.target.targetId, 'availability')} />
        <td>{row.evidence?.lastObservedUtc ? formatTime(row.evidence.lastObservedUtc) : 'Unavailable'}</td>
      </tr>)}</tbody>)}
    </table></div>
    {rows.length === 0 && <p className="empty-state">No loaded servers match this search.</p>}
    <p className="table-note">Dots show evidence state; red marks observed blocking, failed backup checks, backup faults, or disconnected replicas. Backup checks use current evidence and explicit per-database policies; open a backup cell to inspect its assessment time. Unknown and disabled checks have no healthy verdict. SQL scheduler CPU excludes preemptive and hidden-scheduler work. CPU and memory have no threshold-based health verdict. Backup checks do not certify recovery points; HA counts do not establish replica health.</p>
  </section>;
}

function SignalCell({ label, signal, href }: { readonly label: string; readonly signal: FleetSignal; readonly href?: string }) {
  const content = <span className={`fleet-signal fleet-signal-${signal.tone}`} title={signal.detail} aria-label={`${label}: ${signal.detail}`}>
    <span className="fleet-signal-dot" aria-hidden="true" />{signal.text}
  </span>;
  return <td>{href ? <a href={href} title={`Open ${label} evidence`}>{content}</a> : content}</td>;
}

function count(value: OverviewValue | undefined): string {
  if (!value || value.value === null || ['stale', 'unavailable', 'unsupported', 'disabled'].includes(value.state)) return '—';
  return `${value.value.toLocaleString()}${value.state === 'partial' ? '+' : ''}`;
}
