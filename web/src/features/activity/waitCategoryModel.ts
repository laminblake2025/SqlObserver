import type { ActivityWait } from "./activityTypes";

export type WaitCategory = "Lock" | "I/O" | "CPU/signal" | "Memory" | "Parallelism" | "Log" | "Other";

export interface WaitCategoryTotal {
  readonly category: WaitCategory;
  readonly waitMilliseconds: string;
  readonly waitTypes: number;
}

export interface WaitCategorySummary {
  readonly categories: readonly WaitCategoryTotal[];
  readonly idleTypesOmitted: number;
  readonly incomparableTypes: number;
}

const idleTypes = new Set([
  "LAZYWRITER_SLEEP", "SQLTRACE_BUFFER_FLUSH", "SQLTRACE_INCREMENTAL_FLUSH_SLEEP",
  "SQLTRACE_WAIT_ENTRIES", "FT_IFTS_SCHEDULER_IDLE_WAIT", "XE_DISPATCHER_WAIT",
  "REQUEST_FOR_DEADLOCK_SEARCH", "LOGMGR_QUEUE", "ONDEMAND_TASK_QUEUE",
  "CHECKPOINT_QUEUE", "XE_TIMER_EVENT",
]);

export function waitCategory(waitType: string): WaitCategory | "Idle" {
  const type = waitType.toUpperCase();
  if (type.startsWith("SLEEP_") || idleTypes.has(type)) return "Idle";
  if (type.startsWith("LCK_M_")) return "Lock";
  if (type === "WRITELOG" || type === "LOGBUFFER" || type.startsWith("LOGMGR_") || type === "LOG_RATE_GOVERNOR") return "Log";
  if (type.startsWith("PAGEIOLATCH_") || type === "IO_COMPLETION" || type === "ASYNC_IO_COMPLETION" || type === "BACKUPIO" || type === "WRITE_COMPLETION") return "I/O";
  if (type === "SOS_SCHEDULER_YIELD") return "CPU/signal";
  if (type.startsWith("RESOURCE_SEMAPHORE") || type === "MEMORY_ALLOCATION_EXT") return "Memory";
  if (type === "CXPACKET" || type === "CXCONSUMER" || type.startsWith("CXSYNC_") || type === "EXCHANGE") return "Parallelism";
  return "Other";
}

export function groupWaitDeltas(items: readonly ActivityWait[]): WaitCategorySummary {
  const totals = new Map<WaitCategory, { waitMilliseconds: bigint; waitTypes: number }>();
  let idleTypesOmitted = 0;
  let incomparableTypes = 0;
  for (const item of items) {
    const category = waitCategory(item.waitType);
    if (category === "Idle") { idleTypesOmitted++; continue; }
    if (!item.baselineAvailable || item.resetDetected || item.waitTimeMillisecondsDelta === undefined) {
      incomparableTypes++;
      continue;
    }
    const value = BigInt(item.waitTimeMillisecondsDelta);
    if (value === 0n) continue;
    const previous = totals.get(category) ?? { waitMilliseconds: 0n, waitTypes: 0 };
    totals.set(category, { waitMilliseconds: previous.waitMilliseconds + value, waitTypes: previous.waitTypes + 1 });
  }
  const categories = Array.from(totals, ([category, value]) => ({
    category, waitMilliseconds: value.waitMilliseconds.toString(), waitTypes: value.waitTypes,
  })).sort((a, b) => {
    const left = BigInt(a.waitMilliseconds);
    const right = BigInt(b.waitMilliseconds);
    return left === right ? a.category.localeCompare(b.category) : left > right ? -1 : 1;
  });
  return { categories, idleTypesOmitted, incomparableTypes };
}
