export interface ActivityEvidence {
  readonly runId: string;
  readonly targetRevision: string;
  readonly collectorId: string;
  readonly freshness: string;
  readonly outcome: string;
  readonly reason: string;
  readonly isPartial: boolean;
  readonly completedAtUtc: string;
  readonly loss?: { readonly kind: string; readonly minimumLostItems: number; readonly countIsExact: boolean; readonly minimumLostBytes: number };
}

export interface ActivitySession {
  readonly sessionId: number;
  readonly status: string;
  readonly isUserProcess: boolean;
  readonly databaseId?: number;
  readonly openTransactionCount: number;
  readonly cpuMilliseconds: string;
  readonly memoryUsagePages: string;
  readonly reads: string;
  readonly writes: string;
  readonly logicalReads: string;
  readonly totalElapsedMilliseconds: string;
  readonly observedAtUtc: string;
}

export interface ActivityRequest {
  readonly sessionId: number;
  readonly requestId: number;
  readonly status: string;
  readonly command: string;
  readonly databaseId?: number;
  readonly cpuMilliseconds: string;
  readonly totalElapsedMilliseconds: string;
  readonly reads: string;
  readonly writes: string;
  readonly logicalReads: string;
  readonly rowCount: string;
  readonly percentComplete: number;
  readonly observedAtUtc: string;
}

export interface ActivityWait {
  readonly waitType: string;
  readonly waitingTasksCount: string;
  readonly waitTimeMilliseconds: string;
  readonly maximumWaitTimeMilliseconds: string;
  readonly signalWaitTimeMilliseconds: string;
  readonly baselineAvailable: boolean;
  readonly resetDetected: boolean;
  readonly waitingTasksDelta?: string;
  readonly waitTimeMillisecondsDelta?: string;
  readonly signalWaitTimeMillisecondsDelta?: string;
  readonly observedAtUtc: string;
}

export interface BlockingEdge {
  readonly blockedSessionId: number;
  readonly blockerKind: string;
  readonly blockerSessionId?: number;
  readonly waitType: string;
  readonly waitingTaskCount: string;
  readonly waitDurationMilliseconds: string;
  readonly rootBlockerSessionId?: number;
  readonly chainDepth: number;
  readonly chainState: string;
  readonly observedAtUtc: string;
}

export interface ActivityPage<T> {
  readonly repositoryTimeUtc: string;
  readonly evidence?: ActivityEvidence;
  readonly baselineRunId?: string;
  readonly maximumChainDepth?: number;
  readonly maximumGraphNodes?: number;
  readonly fromUtc?: string;
  readonly toUtc?: string;
  readonly items: readonly T[];
  readonly nextCursor?: string;
}

export interface BlockingHistoryItem {
  readonly evidence: ActivityEvidence;
  readonly edge: BlockingEdge;
}

export interface ActivitySnapshot {
  readonly sessions: ActivityPage<ActivitySession>;
  readonly requests: ActivityPage<ActivityRequest>;
  readonly waits: ActivityPage<ActivityWait>;
  readonly blocking: ActivityPage<BlockingEdge>;
  readonly history: ActivityPage<BlockingHistoryItem>;
}
