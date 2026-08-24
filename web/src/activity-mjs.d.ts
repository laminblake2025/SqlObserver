declare module "*.mjs" {
  import type { ActivityEvidence, ActivityPage, ActivityRequest, ActivitySession, ActivityWait, BlockingEdge, BlockingHistoryItem } from "./features/activity/activityTypes";
  export const pageLimit: number;
  export const maximumResponseBytes: number;
  export class ActivityRequestError extends Error {}
  export function getPage<T>(url: string, parseItem: (value: unknown) => T, expectedInstanceId: string, signal: AbortSignal): Promise<ActivityPage<T>>;
  export function parsePage<T>(value: unknown, parseItem: (value: unknown) => T, expectedInstanceId?: string): ActivityPage<T>;
  export function parseEvidence(value: unknown): ActivityEvidence;
  export function parseSession(value: unknown): ActivitySession;
  export function parseRequest(value: unknown): ActivityRequest;
  export function parseWait(value: unknown): ActivityWait;
  export function parseEdge(value: unknown): BlockingEdge;
  export function parseHistory(value: unknown): BlockingHistoryItem;
  export function readBoundedBody(response: Response, signal: AbortSignal, maximumBytes?: number): Promise<string>;
  export function safeCursor(value: unknown): string;
  export function safeStatusMessage(status: number): string;
  export function parseDeadlockPage(value: unknown, expectedTargetId?: string): import("./features/deadlocks/deadlockTypes").DeadlockPage;
  export function parseDeadlockDetail(value: unknown, expectedTargetId: string, expectedEventId: string): import("./features/deadlocks/deadlockTypes").DeadlockDetail;
}
