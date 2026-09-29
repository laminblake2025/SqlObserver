import type { OverviewEvidence, OverviewResource, OverviewSnapshot, OverviewTarget, OverviewValue } from './overviewTypes';

export interface FleetServerRow {
  readonly target: OverviewTarget;
  readonly evidence: OverviewEvidence | undefined;
  readonly attentionCount: number;
  readonly signals: {
    readonly cpu: FleetSignal;
    readonly memory: FleetSignal;
    readonly waits: FleetSignal;
    readonly blocking: FleetSignal;
    readonly backups: FleetSignal;
    readonly ha: FleetSignal;
  };
}

export interface FleetSignal {
  readonly text: string;
  readonly tone: 'observed' | 'partial' | 'unknown' | 'attention';
  readonly detail: string;
}

export type FleetGrouping = 'environment' | 'tag' | 'none';
export interface FleetServerGroup { readonly label: string | null; readonly rows: readonly FleetServerRow[]; }

export function groupFleetServerRows(rows: readonly FleetServerRow[], by: FleetGrouping): FleetServerGroup[] {
  if (by === 'none') return [{ label: null, rows }];
  const groups = new Map<string, FleetServerRow[]>();
  for (const row of rows) {
    const labels = by === 'environment' ? [row.target.environment ?? 'Unassigned']
      : row.target.tags.length ? row.target.tags : ['Untagged'];
    for (const label of labels) {
      const group = groups.get(label) ?? [];
      group.push(row);
      groups.set(label, group);
    }
  }
  return [...groups].map(([label, groupedRows]) => ({ label, rows: groupedRows }));
}

const unknown = (detail: string): FleetSignal => ({ text: '—', tone: 'unknown', detail });
const knownState = (state: string): FleetSignal['tone'] =>
  state === 'observed' || state === 'complete' || state === 'current' ? 'observed' :
    state === 'partial' || state === 'degraded' ? 'partial' : 'unknown';

function resource(evidence: OverviewEvidence | undefined, prefix: string): OverviewResource | undefined {
  return evidence?.resources.filter(item => item.label === prefix || item.label.startsWith(`${prefix} · `))
    .filter(item => item.value !== null)
    .sort((left, right) => (right.observedAtUtc ?? '').localeCompare(left.observedAtUtc ?? ''))[0];
}

function resourceSignal(item: OverviewResource | undefined, description: string, unit: string): FleetSignal {
  if (!item || item.value === null) return unknown(`${description}: no usable observation`);
  const tone = knownState(item.state);
  if (tone === 'unknown') return unknown(`${description}: evidence ${item.state}`);
  const text = `${item.value.toLocaleString(undefined, { maximumFractionDigits: 1 })} ${unit}`;
  return { text, tone, detail: `${description}: ${text}; evidence ${item.state}` };
}

function countSignal(value: OverviewValue | undefined, description: string): FleetSignal {
  if (!value || value.value === null) return unknown(`${description}: no usable observation`);
  const tone = knownState(value.state);
  if (tone === 'unknown') return unknown(`${description}: evidence ${value.state}`);
  return {
    text: `${value.value.toLocaleString()}${tone === 'partial' ? '+' : ''}`,
    tone: value.value > 0 ? 'attention' : tone,
    detail: `${description}: ${value.value}; evidence ${value.state}`,
  };
}

function operationalSignal(evidence: OverviewEvidence | undefined, item: OverviewResource | undefined,
  description: string, unit: string, issueTitle: string, exceptionLabel: string): FleetSignal {
  const base = resourceSignal(item, description, unit);
  if (base.tone === 'unknown' || !evidence?.issues.some(issue => issue.title === issueTitle)) return base;
  return { ...base, text: `${exceptionLabel} · ${base.text}`, tone: 'attention',
    detail: `${issueTitle} was observed; ${base.detail}` };
}

function signals(evidence: OverviewEvidence | undefined): FleetServerRow['signals'] {
  const waits = evidence?.resources.filter(item => item.label.startsWith('Wait · ') && item.value !== null && knownState(item.state) !== 'unknown')
    .sort((left, right) => (right.value ?? 0) - (left.value ?? 0))[0];
  const backup = resource(evidence, 'Backup evidence');
  const ha = resource(evidence, 'Availability replicas');
  return {
    cpu: resourceSignal(resource(evidence, 'SQL scheduler CPU · selected window'), 'SQL scheduler CPU in selected window', '%'),
    memory: resourceSignal(resource(evidence, 'OS available memory via SQL · selected window'), 'OS available memory reported by SQL Server in selected window', 'GiB'),
    waits: resourceSignal(waits, waits ? `Largest observed wait delta: ${waits.label.slice('Wait · '.length)}` : 'Wait delta', 's'),
    blocking: countSignal(evidence?.blockedSessions, 'Currently blocked sessions'),
    backups: operationalSignal(evidence, backup, 'Backup records observed; this does not establish RPO health', 'records', 'Damaged backup', 'Damaged'),
    ha: operationalSignal(evidence, ha, 'Availability replicas observed; this does not establish replica health', 'replicas', 'Availability replica disconnected', 'Disconnected'),
  };
}

export function fleetServerRows(snapshot: OverviewSnapshot, search: string): FleetServerRow[] {
  const byTarget = new Map(snapshot.evidence.map(item => [item.targetId, item]));
  const term = search.trim().toLocaleLowerCase();
  return snapshot.targets.filter(target => `${target.displayName} ${target.targetId} ${target.environment ?? ''} ${target.tags.join(' ')}`.toLocaleLowerCase().includes(term))
    .map(target => {
      const evidence = byTarget.get(target.targetId);
      return { target, evidence, attentionCount: evidence?.issues.length ?? 0, signals: signals(evidence) };
    })
    .sort((left, right) => {
      const firstPriority = (row: FleetServerRow) => row.evidence?.issues.reduce((minimum, issue) => Math.min(minimum, issue.priority), Infinity) ?? Infinity;
      const count = (row: FleetServerRow, key: 'activeAlerts' | 'blockedSessions' | 'deadlocks') => row.evidence?.[key].value ?? 0;
      return firstPriority(left) - firstPriority(right) ||
        count(right, 'activeAlerts') - count(left, 'activeAlerts') ||
        count(right, 'blockedSessions') - count(left, 'blockedSessions') ||
        count(right, 'deadlocks') - count(left, 'deadlocks') ||
        Number(Boolean(right.evidence?.gaps.length)) - Number(Boolean(left.evidence?.gaps.length)) ||
        left.target.displayName.localeCompare(right.target.displayName) ||
        left.target.targetId.localeCompare(right.target.targetId);
    });
}
