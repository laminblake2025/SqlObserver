import { responseFailure } from "../../api/responseFailure.ts";
import type { OverviewSnapshot, OverviewPoint } from './overviewTypes';
import { readBoundedBody } from '../activity/activityParser.mjs';

export async function getOverview(target: string, window: {fromUtc: string; toUtc: string}, signal: AbortSignal): Promise<OverviewSnapshot> {
  const params = new URLSearchParams(window); if (target) params.set('targetId',target);
  const response = await fetch(`/api/v1/overview?${params}`, {credentials:'same-origin', headers:{Accept:'application/json'},signal});
  if (!response.ok) throw responseFailure(response, response.status === 403 ? 'This server selection is unavailable or outside your access.' : response.status === 400 ? 'The selected time range is invalid.' : response.status === 504 ? 'Overview took too long to load. Try a shorter time range or a single server.' : 'Overview evidence is temporarily unavailable.');
  const value = JSON.parse(await readBoundedBody(response, signal)) as OverviewSnapshot;
  if (!value || !Array.isArray(value.targets) || !Array.isArray(value.evidence) || (value.targetId ?? '') !== target || Date.parse(value.fromUtc) !== Date.parse(window.fromUtc) || Date.parse(value.toUtc) !== Date.parse(window.toUtc) || !Number.isFinite(Date.parse(value.refreshedAtUtc))) throw new Error('Overview returned a mismatched scope or time range.');
  const ids = new Set(value.targets.map(t => t.targetId));
  if (value.evidence.some(e => !ids.has(e.targetId) || (target && e.targetId !== target) || !Array.isArray(e.series) || !Array.isArray(e.resources) || !Array.isArray(e.issues))) throw new Error('Overview returned invalid server evidence.');
  const timestamp = (v: unknown) => typeof v === 'string' && Number.isFinite(Date.parse(v));
  const numeric = (v: unknown) => v === null || typeof v === 'number' && Number.isFinite(v) && v >= 0;
    for (const evidence of value.evidence) {
    if (typeof evidence.displayName !== 'string' || typeof evidence.collectionState !== 'string' || !Array.isArray(evidence.gaps) || evidence.gaps.some((g:unknown)=>typeof g!=='string')) throw new Error('Invalid Overview coverage.');
    for (const measurement of [evidence.activeAlerts,evidence.blockedSessions,evidence.deadlocks])
      if (!measurement || !numeric(measurement.value) || typeof measurement.state !== 'string') throw new Error('Invalid Overview summary.');
    for (const series of evidence.series)
      if (series.targetId !== evidence.targetId || typeof series.metric !== 'string' || typeof series.label !== 'string' || typeof series.unit !== 'string' || typeof series.state !== 'string' || (series.dimension !== null && typeof series.dimension !== 'string') || !Array.isArray(series.points) || series.points.length>1000 || series.points.some((p:OverviewPoint)=>!timestamp(p.timeUtc)||!numeric(p.value)||!Number.isSafeInteger(p.samples)||p.samples<0)) throw new Error('Invalid Overview series.');
    for (const resource of evidence.resources)
      if (resource.targetId !== evidence.targetId || typeof resource.label !== 'string' || !numeric(resource.value)) throw new Error('Invalid Overview resource.');
    for (const issue of evidence.issues)
      if (issue.targetId !== evidence.targetId || !['alerts','activity','deadlocks','health','operations','analytics','queries'].includes(issue.destination) || typeof issue.title !== 'string' || typeof issue.detail !== 'string' || !Number.isSafeInteger(issue.priority)) throw new Error('Invalid Overview issue.');
  }
  return value;
}
