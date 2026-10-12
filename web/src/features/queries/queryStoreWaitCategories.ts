export const queryStoreWaitCategories = [
  "Unknown", "CPU", "Worker thread", "Lock", "Latch", "Buffer latch",
  "Buffer I/O", "Compilation", "SQL CLR", "Mirroring", "Transaction",
  "Idle", "Preemptive", "Service Broker", "Transaction log I/O",
  "Network I/O", "Parallelism", "Memory", "User wait", "Tracing",
  "Full-text search",
] as const;

export const queryStoreWaitCategoryNames: Record<number, string> =
  Object.fromEntries(queryStoreWaitCategories.map((name, category) => [category, name]));
