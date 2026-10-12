import { buildBlockingTree, isBlockingRelationship, rowsForBlockingGroup } from "./blockingModel.ts";
import type { LiveRow } from "./liveActivityApi.ts";

export interface TriageThresholds { elapsedSeconds: number; cpuMsPerSecond: number; ioPerSecond: number; logicalReadsPerSecond: number; }
export const defaultTriageThresholds: TriageThresholds = { elapsedSeconds: 30, cpuMsPerSecond: 500, ioPerSecond: 1000, logicalReadsPerSecond: 10000 };
export function validTriageThresholds(value: unknown): value is TriageThresholds {
  return Boolean(value && typeof value === "object" && Object.keys(defaultTriageThresholds).every(key => {
    const number = (value as Record<string, unknown>)[key]; return typeof number === "number" && Number.isSafeInteger(number) && number > 0 && number <= 1e12;
  }));
}
export function readTriageThresholds(target: string): TriageThresholds {
  try { const stored = JSON.parse(localStorage.getItem(`sqlobserver.activityTriage.v1.${target.toLowerCase()}`) ?? "null"); if (stored?.version === 1 && validTriageThresholds(stored.thresholds)) return stored.thresholds; } catch { /* Browser storage is optional. */ }
  return { ...defaultTriageThresholds };
}
export function saveTriageThresholds(target: string, thresholds: TriageThresholds): void {
  if (!validTriageThresholds(thresholds)) throw new Error("Thresholds must be positive whole numbers, at most 1,000,000,000,000.");
  try { localStorage.setItem(`sqlobserver.activityTriage.v1.${target.toLowerCase()}`, JSON.stringify({ version: 1, thresholds })); } catch { /* Session preferences remain usable without storage. */ }
}
// Keep counters as integers even above Number.MAX_SAFE_INTEGER. Sampling seconds
// are converted to an exact decimal fraction, including exponential notation.
function secondsFraction(seconds: number): [bigint, bigint] {
  const [mantissa, exponent = "0"] = seconds.toString().split("e");
  const [whole, fraction = ""] = mantissa!.split(".");
  const power = Number(exponent) - fraction.length;
  const digits = BigInt(whole! + fraction);
  return power >= 0 ? [digits * 10n ** BigInt(power), 1n] : [digits, 10n ** BigInt(-power)];
}
export function intervalRate(counter: string, seconds: number): bigint | undefined {
  if (!/^\d{1,41}$/u.test(counter) || !Number.isFinite(seconds) || seconds <= 0) return undefined;
  const [numerator, denominator] = secondsFraction(seconds);
  return BigInt(counter) * 1000n * denominator / numerator; // thousandths / second
}
export function rowRates(row: LiveRow) {
  const delta = row.delta;
  return delta ? { cpu: intervalRate(delta.cpuMs, delta.seconds), io: intervalRate((BigInt(delta.reads) + BigInt(delta.writes)).toString(), delta.seconds), logical: intervalRate(delta.logicalReads, delta.seconds) } : { cpu: undefined, io: undefined, logical: undefined };
}
export function formatRate(rate: bigint | undefined): string {
  if (rate === undefined) return "Rate unavailable";
  const scales: [bigint, string][] = [[1000000000000n, "B"], [1000000000n, "M"], [1000000n, "k"]];
  const [scale, suffix] = scales.find(([scale]) => rate >= scale) ?? [1000n, ""];
  const tenth = rate * 10n / scale;
  return `${tenth / 10n}${tenth % 10n ? `.${tenth % 10n}` : ""}${suffix}`;
}
export function formatElapsed(value: string): string {
  const ms = BigInt(value); if (ms < 1000n) return `${ms}ms`;
  const seconds = ms / 1000n; if (seconds < 60n) return `${seconds}s`;
  if (seconds < 3600n) return `${seconds / 60n}m ${seconds % 60n}s`;
  return `${seconds / 3600n}h ${seconds / 60n % 60n}m`;
}
export interface TriageSignal { label: string; detail: string; tone: "blocking" | "workload" | "muted"; }
export interface TriageSession { key: string; sessionId: number; rows: LiveRow[]; representative: LiveRow; signals: TriageSignal[]; impact: number; tier: number; exceedance: bigint; active: boolean; user: boolean; blocked: boolean; blocking: boolean; }
export function buildSessionTriage(rows: readonly LiveRow[], thresholds: TriageThresholds): TriageSession[] {
  const lifetimes = new Map<number, Set<string>>();
  for (const row of rows) { const known = lifetimes.get(row.sessionId) ?? new Set<string>(); known.add(JSON.stringify([row.engineStartup, row.sessionLogin])); lifetimes.set(row.sessionId, known); }
  const children = new Map<number, Set<number>>();
  for (const row of rows) if (row.blocker && row.blocker > 0 && row.blocker !== row.sessionId) {
    const set = children.get(row.blocker) ?? new Set<number>(); set.add(row.sessionId); children.set(row.blocker, set);
  }
  const impactOf = (id: number) => {
    const visited = new Set([id]), pending = [...(children.get(id) ?? [])];
    while (pending.length) { const next = pending.pop()!; if (visited.has(next)) continue; visited.add(next); pending.push(...(children.get(next) ?? [])); }
    return visited.size - 1;
  };
  const issues = new Map<number, string>();
  for (const group of buildBlockingTree(rows)) {
    const issue = group.issue ? group.key.startsWith("cycle:") ? "Blocking cycle" : group.key.startsWith("lifetime:") ? "Session lifetime unresolved" : "Conflicting blockers" : group.key.startsWith("session:") && !group.rootLoaded ? "Root unavailable" : group.key.startsWith("special:") ? "Special SQL blocker" : undefined;
    if (issue) for (const row of rowsForBlockingGroup(rows, group)) issues.set(row.sessionId, issue);
  }
  const groupRows = new Map<string, LiveRow[]>();
  for (const row of rows) {
    const key = JSON.stringify([row.sessionId, row.engineStartup, row.sessionLogin]);
    groupRows.set(key, [...(groupRows.get(key) ?? []), row]);
  }
  const evaluate = (row: LiveRow) => {
    const signals: TriageSignal[] = [], rates = rowRates(row); let exceedance = 0n;
    const active = row.requestId !== null;
    const impact = lifetimes.get(row.sessionId)!.size > 1 ? 0 : impactOf(row.sessionId);
    if (impact) signals.push({ label: `Blocking ${impact} session${impact === 1 ? "" : "s"}`, detail: "Distinct downstream sessions in the loaded capture; incomplete evidence may omit affected sessions.", tone: "blocking" });
    if (isBlockingRelationship(row.blocker)) signals.push({ label: row.blocker > 0 ? `Blocked by ${row.blocker}` : `Special blocker ${row.blocker}`, detail: row.waitType ?? "Wait type unavailable", tone: "blocking" });
    if (issues.has(row.sessionId)) signals.push({ label: issues.get(row.sessionId)!, detail: "Head blocker cannot be established from the loaded relationships.", tone: "blocking" });
    if (lifetimes.get(row.sessionId)!.size > 1) signals.push({ label: "Session lifetime unresolved", detail: "Multiple captured lifetimes share this SPID; affected sessions cannot be assigned to an exact lifetime.", tone: "blocking" });
    const flag = (value: bigint | undefined, threshold: number, label: string, detail: string) => {
      if (value === undefined || value < BigInt(threshold) * 1000n) return;
      signals.push({ label, detail, tone: "workload" }); const ratio = value / BigInt(threshold); if (ratio > exceedance) exceedance = ratio;
    };
    if (active) {
      if (row.isUser) flag(BigInt(row.elapsedMs), thresholds.elapsedSeconds, `Running ${formatElapsed(row.elapsedMs)}`, `Request elapsed ≥ ${thresholds.elapsedSeconds}s; this is not wait duration.`);
      flag(rates.cpu, thresholds.cpuMsPerSecond, `CPU ${formatRate(rates.cpu)} ms/s`, `CPU ≥ ${thresholds.cpuMsPerSecond} ms/s over ${row.delta?.seconds}s.`);
      flag(rates.io, thresholds.ioPerSecond, `I/O ${formatRate(rates.io)} ops/s`, `Physical reads + writes ≥ ${thresholds.ioPerSecond}/s over ${row.delta?.seconds}s.`);
      flag(rates.logical, thresholds.logicalReadsPerSecond, `Logical reads ${formatRate(rates.logical)}/s`, `Logical reads ≥ ${thresholds.logicalReadsPerSecond}/s over ${row.delta?.seconds}s.`);
    }
    const tier = impact ? 0 : signals.some(signal => signal.tone === "blocking") ? 1 : signals.length ? 2 : row.isUser && active ? 3 : row.isUser ? 4 : 5;
    return { row, signals, tier, exceedance, impact };
  };
  const compare = (a: ReturnType<typeof evaluate>, b: ReturnType<typeof evaluate>) => a.tier - b.tier || b.impact - a.impact || compareBig(b.exceedance, a.exceedance) || a.row.identity.localeCompare(b.row.identity);
  return [...groupRows].map(([key, members]) => {
    const evaluated = members.map(evaluate).sort(compare), best = evaluated[0]!;
    const signals = [...new Map(evaluated.flatMap(item => item.signals).map(signal => [signal.label, signal])).values()];
    const user = members.some(row => row.isUser), active = members.some(row => row.requestId !== null);
    return { key, sessionId: best.row.sessionId, rows: evaluated.map(item => item.row), representative: best.row, signals: signals.length ? signals : [{ label: user ? active ? "Active" : "Idle" : "Background", detail: "No triage threshold exceeded in available evidence; this is not a health determination.", tone: "muted" as const }], impact: best.impact, tier: best.tier, exceedance: best.exceedance, active, user, blocked: members.some(row => isBlockingRelationship(row.blocker)), blocking: best.impact > 0 };
  }).sort((a, b) => a.tier - b.tier || b.impact - a.impact || compareBig(b.exceedance, a.exceedance) || a.sessionId - b.sessionId || a.key.localeCompare(b.key));
}
export const compareBig = (a: bigint, b: bigint) => a < b ? -1 : a > b ? 1 : 0;
