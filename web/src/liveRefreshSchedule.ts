export function refreshCadence(manualRefresh: number, liveTick: number) {
  return {
    slow: manualRefresh + Math.floor(liveTick / 6),
    blocking: manualRefresh + Math.floor(liveTick / 3),
  };
}

export function wakeLiveTick(liveTick: number): number {
  return Math.ceil((liveTick + 1) / 6) * 6;
}

export interface WorkspaceClockHost {
  setInterval(callback: () => void, milliseconds: number): number;
  clearInterval(timer: number): void;
  addVisibilityListener(callback: () => void): void;
  removeVisibilityListener(callback: () => void): void;
  isHidden(): boolean;
}

export function startWorkspaceClock(host: WorkspaceClockHost, tick: () => void, wake: () => void): () => void {
  const onInterval = () => { if (!host.isHidden()) tick(); };
  const onVisible = () => { if (!host.isHidden()) wake(); };
  const timer = host.setInterval(onInterval, 10_000);
  host.addVisibilityListener(onVisible);
  return () => {
    host.clearInterval(timer);
    host.removeVisibilityListener(onVisible);
  };
}
