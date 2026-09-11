/** Schedule only after settlement. Cancellation prevents rescheduling obsolete or hidden views. */
export function startLiveRefresh(run: () => Promise<void>, onFailure: () => void, signal: AbortSignal, repeat: boolean,
  schedule: (action: () => void, delay: number) => ReturnType<typeof setTimeout> = setTimeout,
  cancel: (timer: ReturnType<typeof setTimeout>) => void = clearTimeout): void {
  let failures = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const abort = () => { if (timer !== undefined) cancel(timer); };
  signal.addEventListener("abort", abort, { once: true });
  async function tick() {
    if (signal.aborted) return;
    try { await run(); failures = 0; }
    catch { if (!signal.aborted) { failures++; onFailure(); } }
    finally {
      if (!signal.aborted && repeat) timer = schedule(() => void tick(), Math.min(60000, 10000 * 2 ** failures));
      else signal.removeEventListener("abort", abort);
    }
  }
  void tick();
}
