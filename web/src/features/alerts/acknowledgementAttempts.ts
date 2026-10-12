export class AcknowledgementAttempts {
  private readonly operations = new Map<string, string>();

  tokenFor(targetId: string, alertId: string, firstObservedUtc: string): string {
    const key = JSON.stringify([targetId.toLowerCase(), alertId.toLowerCase(), firstObservedUtc]);
    const existing = this.operations.get(key);
    if (existing) return existing;
    const operationId = crypto.randomUUID();
    this.operations.set(key, operationId);
    return operationId;
  }

  complete(targetId: string, alertId: string, firstObservedUtc: string): void {
    this.operations.delete(JSON.stringify([targetId.toLowerCase(), alertId.toLowerCase(), firstObservedUtc]));
  }
}
