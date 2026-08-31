export function parseDeadlockPage(input: unknown, expectedTargetId?: string): import("./deadlockTypes").DeadlockPage;
export function parseDeadlockDetail(input: unknown, expectedTargetId: string, expectedEventId: string): import("./deadlockTypes").DeadlockDetail;
export function normalizeDeadlockLockMode(input: unknown): string;
