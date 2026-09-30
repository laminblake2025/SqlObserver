import { useEffect, useState } from 'react';
import type { OverviewSnapshot } from './overviewTypes';

// Expire current verdicts even when Live polling is paused.
export function useBackupSummaryClock(snapshot: OverviewSnapshot): number {
  const [clock, setClock] = useState(Date.now());
  useEffect(() => {
    const now = Date.now();
    // Wake on the first browser millisecond after the repository's microsecond boundary.
    const expiries = snapshot.evidence.map(e => e.backupSummary ? Date.parse(e.backupSummary.validUntilUtc) + 1 : NaN).filter(at => at > now);
    if (!expiries.length) return;
    const timer = window.setTimeout(() => setClock(Date.now()), Math.min(...expiries) - now);
    return () => window.clearTimeout(timer);
  }, [snapshot, clock]);
  return Math.max(clock, Date.now());
}
