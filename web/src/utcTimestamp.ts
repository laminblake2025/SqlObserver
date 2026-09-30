/** Compare UTC timestamps at PostgreSQL precision without rounding through Date. */
export function utcMicroseconds(value: unknown): bigint {
  if (typeof value !== "string") throw new Error("Use a valid UTC timestamp.");
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,6}))?(?:Z|\+00:00)$/.exec(value);
  if (!match || value.startsWith("0000-")) throw new Error("Use a valid UTC timestamp.");
  const base=match[1]!; const time=Date.parse(`${base}Z`);
  if (!Number.isFinite(time) || new Date(time).toISOString().slice(0,19)!==base) throw new Error("Use a valid UTC timestamp.");
  return BigInt(time)*1000n+BigInt((match[2]??"").padEnd(6,"0"));
}

export function canonicalUtcTimestamp(value: string): string {
  utcMicroseconds(value);
  return /\.\d{4,6}(?:Z|\+00:00)$/.test(value) ? value.replace(/\+00:00$/,"Z") : new Date(value).toISOString();
}
