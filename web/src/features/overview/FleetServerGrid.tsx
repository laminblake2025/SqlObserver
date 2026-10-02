import { useEffect, useRef, useState } from 'react';
import { useBackupSummaryClock } from './useBackupSummaryClock';
import { fleetServerRows, groupFleetServerRows, type FleetGrouping, type FleetSignal } from './fleetGridModel';
import { FleetServerGroupingEditor, type GroupingRecoveryReason } from './FleetServerGroupingEditor';
import { backupAssessmentHref, overviewHref } from './overviewModel';
import { healthCheckHref } from '../operations/agentHistoryModel';
import type { OverviewScope, OverviewSnapshot, OverviewTarget, OverviewValue } from './overviewTypes';

interface GroupingSelection { readonly targetId: string; readonly revision: number; readonly sequence: number; }
interface GroupingRecovery {
  readonly targetId: string;
  readonly reason: GroupingRecoveryReason;
  readonly editorSequence?: number;
  readonly reloadSnapshot?: OverviewSnapshot;
}

export function FleetServerGrid({ snapshot, scope, search, onSearch, formatTime, onGroupingUpdated }: {
  readonly snapshot: OverviewSnapshot;
  readonly scope: OverviewScope;
  readonly search: string;
  readonly onSearch: (value: string) => void;
  readonly formatTime: (value: string) => string;
  readonly onGroupingUpdated: () => void;
}) {
  const [groupBy, setGroupBy] = useState<FleetGrouping>('environment');
  const [editing, setEditing] = useState<GroupingSelection | null>(null);
  const [recoveries, setRecoveries] = useState<ReadonlyMap<string, GroupingRecovery>>(() => new Map());
  const [notice, setNotice] = useState('');
  const sequence = useRef(0);
  const mounted = useRef(true);
  const currentSnapshot = useRef(snapshot); currentSnapshot.current = snapshot;
  const currentTarget = editing ? snapshot.targets.find(target => target.targetId.toLowerCase() === editing.targetId) : undefined;
  const recovery = editing ? recoveries.get(editing.targetId) : undefined;
  const canKeepEditor = editing !== null && currentTarget !== undefined && currentTarget.canManageGrouping
    && currentTarget.lifecycle !== 'retired' && currentTarget.groupingRevision === editing.revision
    && (!recovery || recovery.editorSequence === editing.sequence);
  const editorKey = editing && canKeepEditor ? JSON.stringify([editing.targetId, editing.revision, editing.sequence]) : undefined;
  const currentEditor = useRef(editorKey); currentEditor.current = editorKey;
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => {
    if (!editing || currentTarget?.canManageGrouping && currentTarget.lifecycle !== 'retired' && currentTarget.groupingRevision === editing.revision) return;
    setEditing(null);
    setNotice(currentTarget?.canManageGrouping && currentTarget.lifecycle !== 'retired'
      ? 'Server labels changed. The old draft was discarded. Open Edit labels to load the current revision.'
      : 'Grouping access is unavailable. The draft was discarded. Target Administrator access on this server unlocks grouping changes.');
  }, [editing, currentTarget]);
  useEffect(() => {
    setRecoveries(previous => {
      const next = new Map(previous);
      for (const [id, recovery] of previous) {
        if (!snapshot.targets.some(target => target.targetId.toLowerCase() === id)
          || recovery.reloadSnapshot && recovery.reloadSnapshot !== snapshot) next.delete(id);
      }
      return next.size === previous.size ? previous : next;
    });
  }, [snapshot]);
  const abandon = (targetId: string) => {
    if (!mounted.current || !currentSnapshot.current.targets.some(target => target.targetId.toLowerCase() === targetId)) return;
    setRecoveries(previous => new Map(previous).set(targetId, { targetId, reason: 'unknown' }));
  };
  const reload = (targetId: string) => {
    setEditing(previous => previous?.targetId === targetId ? null : previous);
    setRecoveries(previous => new Map(previous).set(targetId, {
      targetId, reason: previous.get(targetId)?.reason ?? 'unknown', reloadSnapshot: currentSnapshot.current,
    }));
    setNotice('The draft was discarded. Reloading current server labels; open Edit labels after the refreshed overview arrives.');
    onGroupingUpdated();
  };
  const edit = (target: OverviewTarget) => {
    const id = target.targetId.toLowerCase();
    if (!target.canManageGrouping || target.lifecycle === 'retired' || recoveries.has(id)) return;
    sequence.current += 1;
    setNotice('');
    setEditing({ targetId: id, revision: target.groupingRevision, sequence: sequence.current });
  };
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
    {notice && <p role="status" className="table-note">{notice}</p>}
    {[...recoveries].map(([id, recovery]) => {
      const target = snapshot.targets.find(candidate => candidate.targetId.toLowerCase() === id);
      if (!target) return null;
      if (canKeepEditor && editing !== null && editing.targetId === id && recovery.editorSequence === editing.sequence) return null;
      const loading = recovery.reloadSnapshot === snapshot;
      return <div key={id} className="table-note"><p role="status">{loading
        ? `Loading current labels for ${target.displayName}. Editing remains unavailable until the refreshed overview arrives.`
        : recovery.reason === 'conflict' ? `Labels for ${target.displayName} changed elsewhere. Reload server labels before editing that server again.`
        : recovery.reason === 'access' ? `Administrative access for ${target.displayName} was rejected. Target Administrator access and reloaded server labels are required before another change.`
        : recovery.reason === 'unavailable' ? `Settings for ${target.displayName} are unavailable. Reload server labels before editing that server again.`
        : `A dispatched label change for ${target.displayName} may still have been applied. Cancellation does not roll it back. Reload server labels before editing that server again.`}</p>
        <button type="button" className="secondary-button" disabled={loading} aria-label={`Reload server labels for ${target.displayName}`} onClick={() => reload(id)}>Reload server labels</button></div>;
    })}
    {editing && currentTarget && editorKey && <FleetServerGroupingEditor key={editorKey} target={currentTarget}
      onClose={() => { if (currentEditor.current === editorKey) setEditing(null); }}
      onSaved={() => { if (!mounted.current || currentEditor.current !== editorKey) return; currentEditor.current = undefined;
        setEditing(null); setNotice('Server labels saved. Refreshing current grouping.'); onGroupingUpdated(); }}
      onReload={() => { if (currentEditor.current === editorKey) reload(editing.targetId); }}
      onRecoveryRequired={reason => {
        if (!mounted.current || currentEditor.current !== editorKey) return;
        setRecoveries(previous => new Map(previous).set(editing.targetId, {
          targetId: editing.targetId, reason, editorSequence: editing.sequence,
        }));
      }}
      onAbandoned={() => abandon(editing.targetId)} />}
    <div className="table-scroll" tabIndex={0} role="region" aria-label="Server health grid"><table>
      <caption>Current alerts, blocking, and backup policy checks; CPU and memory follow the selected window.</caption>
      <thead><tr><th scope="col">Server</th><th scope="col">Attention</th><th scope="col">SQL scheduler CPU</th><th scope="col">OS free via SQL</th><th scope="col">Waits</th><th scope="col">Blocking</th><th scope="col">Backups</th><th scope="col">HA</th><th scope="col">Last SQL core success</th></tr></thead>
      {groups.map(group => <tbody key={group.label ?? 'all'}>
        {group.label && <tr className="fleet-group-row"><th scope="rowgroup" colSpan={9}>{groupBy === 'tag' ? 'Tag' : 'Environment'}: {group.label} <small>· {group.rows.length} servers</small></th></tr>}
        {group.rows.map(row => <tr key={row.target.targetId}>
        <td><a href={overviewHref(scope, 'health', row.target.targetId)}>{row.target.displayName}</a>
          <small>{row.target.environment ?? 'Unassigned'} · {row.target.tags.length ? row.target.tags.join(', ') : 'No tags'}</small>
          {row.target.canManageGrouping && row.target.lifecycle !== 'retired' && <button type="button" className="table-link" disabled={recoveries.has(row.target.targetId.toLowerCase())} aria-label={`Edit grouping for ${row.target.displayName}`} onClick={() => edit(row.target)}>Edit labels</button>}
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
