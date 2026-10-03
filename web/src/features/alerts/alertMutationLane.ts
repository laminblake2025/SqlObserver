export type AlertMutationKind = "acknowledge" | "snooze" | "assignment";
/** One synchronous writer per received alert detail, including all retry paths. */
export class AlertMutationLane {
  private key = "";
  private owner: { readonly token: object; readonly kind: AlertMutationKind } | undefined;
  private readonly changed: () => void;
  public constructor(changed: () => void = () => {}) { this.changed = changed; }
  public setContext(key: string): void { if (key !== this.key) { this.key = key; this.owner = undefined; } }
  public claim(key: string, token: object, kind: AlertMutationKind): boolean {
    if (key !== this.key || this.owner) return false;
    this.owner = { token, kind }; this.changed(); return true;
  }
  public release(token: object): void { if (this.owner?.token === token) { this.owner = undefined; this.changed(); } }
  public kind(key: string): AlertMutationKind | undefined { return key === this.key ? this.owner?.kind : undefined; }
  public busy(key: string): boolean { return this.kind(key) !== undefined; }
  public reset(): void { this.owner = undefined; }
}
