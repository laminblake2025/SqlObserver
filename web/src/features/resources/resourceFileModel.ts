// SQL Server file I/O counters are cumulative since their source reset. Keep
// the division in BigInt so a long-running server does not lose precision.
export function lifetimeAverageStallMilliseconds(stall: string | null, count: string): string | null {
  if (stall === null || !/^(?:0|[1-9]\d*)$/u.test(stall) ||
      !/^(?:0|[1-9]\d*)$/u.test(count)) return null;
  const operations = BigInt(count);
  if (operations === 0n) return null;
  const hundredths = (BigInt(stall) * 100n + operations / 2n) / operations;
  return `${hundredths / 100n}.${String(hundredths % 100n).padStart(2, "0")}`;
}

export function fileSizeGib(bytes: string): string | null {
  if (!/^(?:0|[1-9]\d*)$/u.test(bytes)) return null;
  const gib = 1024n * 1024n * 1024n;
  const hundredths = (BigInt(bytes) * 100n + gib / 2n) / gib;
  return `${hundredths / 100n}.${String(hundredths % 100n).padStart(2, "0")} GiB`;
}
