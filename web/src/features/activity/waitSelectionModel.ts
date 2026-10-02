import { overviewHref, readOverviewScope } from '../overview/overviewModel.ts';
import type { OverviewScope } from '../overview/overviewTypes';
import { queryStoreWaitCategories } from '../queries/queryStoreWaitCategories.ts';
import { utcMicroseconds } from '../../utcTimestamp.ts';

const guid = (value: string) => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value)
  && value !== '00000000-0000-0000-0000-000000000000';
export function validWaitCategory(value: number): boolean {
  return Number.isSafeInteger(value) && value >= 0 && value < queryStoreWaitCategories.length && value !== 11;
}

/** Undefined uses the default; null keeps a malformed selection explicit. */
export function readWaitCategory(hash: string, expectedTargetId?: string): number | null | undefined {
  const [path, query] = hash.replace(/^#\/?/u, '').split('?');
  const params = new URLSearchParams(query);
  const categories = params.getAll('waitCategory');
  if (!categories.length) return undefined;
  const targets = params.getAll('target');
  if (path !== 'waits' || targets.length !== 1 || !guid(targets[0]!) ||
      expectedTargetId !== undefined && targets[0]!.toLowerCase() !== expectedTargetId.toLowerCase() ||
      categories.length !== 1 || !/^(?:0|[1-9]\d?)$/u.test(categories[0]!) || !validWaitCategory(Number(categories[0])) ||
      params.getAll('range').length !== 1 || !['1h','6h','24h','7d','custom'].includes(params.get('range')!) ||
      ['from','to','databaseId','compare'].some(name => params.getAll(name).length > 1) ||
      params.has('databaseId') && (!/^[1-9]\d{0,4}$/u.test(params.get('databaseId')!) || Number(params.get('databaseId')) > 32767) ||
      params.has('compare') && !['0','1'].includes(params.get('compare')!)) return null;
  if (params.get('range') === 'custom') {
    try {
      const from = utcMicroseconds(params.get('from')), to = utcMicroseconds(params.get('to'));
      if (from >= to || to - from > 31n * 86400n * 1000000n) return null;
    } catch { return null; }
  }
  return Number(categories[0]);
}

export function waitCategoryHref(scope: OverviewScope, category?: number): string {
  if (!guid(scope.target)) throw new Error('Select an authorized server before choosing a wait category.');
  const href = overviewHref(scope, 'waits');
  if (category === undefined) return href;
  if (!validWaitCategory(category)) throw new Error('Choose a supported Query Store wait category.');
  return `${href}&waitCategory=${category}`;
}

export function retainWaitCategoryHref(href: string, currentHash: string): string {
  if (href.split('?')[0] !== '#/waits') return href;
  const scope = readOverviewScope(href);
  const category = readWaitCategory(currentHash, scope.target);
  if (category === null || category === undefined) return href;
  try { return waitCategoryHref(scope, category); } catch { return href; }
}
