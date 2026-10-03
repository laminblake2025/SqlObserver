/** A catalog denial invalidates every read that could restore its retained labels. */
export class TargetCatalogAccess {
  private epoch = 0;
  private denied = false;
  private readonly pending = new Set<AbortController>();

  /** Also fence native mutation receipts that finish before a denied subtree unmounts. */
  public receiptGuard(): () => boolean {
    const epoch = this.epoch;
    return () => !this.denied && epoch === this.epoch;
  }

  public begin() {
    if (this.denied) return undefined;
    const controller = new AbortController();
    const epoch = this.epoch;
    this.pending.add(controller);
    return {
      signal: controller.signal,
      current: () => !controller.signal.aborted && !this.denied && epoch === this.epoch,
      cancel: () => { this.pending.delete(controller); controller.abort(); },
    };
  }

  public deny(): void {
    this.denied = true;
    this.invalidate();
  }

  /** Only an explicit user retry starts a new authority generation. */
  public retry(): void {
    this.invalidate();
    this.denied = false;
  }

  public dispose(): void { this.invalidate(); }

  private invalidate(): void {
    this.epoch++;
    for (const controller of this.pending) controller.abort();
    this.pending.clear();
  }
}
