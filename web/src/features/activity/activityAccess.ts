import { useEffect, useId, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { ActivityRequestError } from "./activityParser.mjs";

export function activityAccessDenied(error: unknown): boolean {
  return (error instanceof ActivityRequestError || error instanceof ApiRequestError) && (error.status === 401 || error.status === 403);
}
export interface ActivityReadOwner {
  readonly source: string; readonly key: string; readonly epoch: number; readonly controller: AbortController;
}
/** Caller HTTP denial is shared; collector permission-denied evidence is not caller authorization. */
export class ActivityReadBoundary {
  epoch = 0;
  denied = false;
  private disposed = false;
  private version = 0;
  private readonly recovered = new Set<string>();
  private readonly owners = new Map<string, ActivityReadOwner>();
  private readonly listeners = new Set<() => void>();
  private readonly invalidators = new Set<() => void>();
  readonly snapshot = () => this.version;
  readonly subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  subscribeInvalidation(listener: () => void): () => void { this.invalidators.add(listener); return () => { this.invalidators.delete(listener); }; }
  allowed(source: string): boolean { return !this.disposed && (!this.denied || this.recovered.has(source)); }
  begin(source: string, key: string, explicit = false): ActivityReadOwner | undefined {
    if (this.disposed || !explicit && !this.allowed(source)) return undefined;
    this.cancel(this.owners.get(key));
    const owner = { source, key, epoch: this.epoch, controller: new AbortController() };
    this.owners.set(key, owner); return owner;
  }
  current(owner: ActivityReadOwner): boolean {
    return !this.disposed && !owner.controller.signal.aborted && owner.epoch === this.epoch && this.owners.get(owner.key) === owner;
  }
  recover(owner: ActivityReadOwner, readSources: readonly string[] = [owner.source]): boolean {
    if (!this.current(owner)) return false;
    if (this.denied && readSources.some(source => !this.recovered.has(source))) {
      for (const source of readSources) this.recovered.add(source);
      this.notify();
    }
    return true;
  }
  finish(owner: ActivityReadOwner): void { if (this.owners.get(owner.key) === owner) this.owners.delete(owner.key); }
  cancel(owner: ActivityReadOwner | undefined): void {
    if (!owner) return;
    owner.controller.abort(); this.finish(owner);
  }
  deny(): void {
    if (this.disposed) return;
    this.epoch++; this.denied = true; this.recovered.clear();
    for (const owner of this.owners.values()) owner.controller.abort();
    this.owners.clear();
    for (const invalidate of this.invalidators) invalidate();
    this.notify();
  }
  dispose(): void {
    this.disposed = true;
    for (const owner of this.owners.values()) owner.controller.abort();
    this.owners.clear();
  }
  activate(): void { this.disposed = false; }
  private notify(): void { this.version++; for (const listener of this.listeners) listener(); }
}
export function useActivityBoundary(context: string): ActivityReadBoundary {
  const identity = useRef<{ context: string; boundary: ActivityReadBoundary } | undefined>(undefined);
  if (identity.current?.context !== context) {
    // Fence ABA before passive effects: the retired boundary cannot accept a late success.
    identity.current?.boundary.dispose(); identity.current = { context, boundary: new ActivityReadBoundary() };
  }
  const boundary = identity.current.boundary;
  useLayoutEffect(() => { boundary.activate(); return () => boundary.dispose(); }, [boundary]);
  useBoundarySnapshot(boundary);
  return boundary;
}
export function useBoundarySnapshot(boundary: ActivityReadBoundary): void {
  useSyncExternalStore(boundary.subscribe, boundary.snapshot, boundary.snapshot);
}
export interface ActivityWindow { readonly fromUtc: string; readonly toUtc: string; }
interface SourceReceipt<T> { readonly boundary: ActivityReadBoundary; readonly epoch: number; readonly generation: number; readonly refresh: number; readonly value: T; readonly window?: ActivityWindow; }
interface SourceAttempt<T> { readonly read: (signal: AbortSignal, explicit?: boolean) => Promise<T>; readonly window?: ActivityWindow; }
interface SourceState<T> { readonly boundary: ActivityReadBoundary; readonly epoch: number; readonly generation: number; readonly receipt?: SourceReceipt<T>; readonly error?: string; readonly loading: boolean; readonly attempt?: SourceAttempt<T>; }

/** Latest automatic reads may be superseded; explicit recovery owns its captured request until completion. */
export function useActivitySource<T>(boundary: ActivityReadBoundary, source: string, identity: string, refresh: number,
  read: ((signal: AbortSignal, explicit?: boolean) => Promise<T>) | undefined, window?: ActivityWindow, readSources?: (value: T) => readonly string[]) {
  useBoundarySnapshot(boundary);
  const ownerKey = useId();
  const scope = useRef({ boundary, identity, generation: 0 });
  if (scope.current.boundary !== boundary || scope.current.identity !== identity)
    scope.current = { boundary, identity, generation: scope.current.generation + 1 };
  const generation = scope.current.generation;
  const latest = useRef({ boundary, generation, refresh, read, window }); latest.current = { boundary, generation, refresh, read, window };
  const owner = useRef<{ ticket: ActivityReadOwner; automatic: boolean; refresh: number } | undefined>(undefined);
  const [state, setState] = useState<SourceState<T>>();
  useLayoutEffect(() => {
    boundary.cancel(owner.current?.ticket); owner.current = undefined; setState(undefined);
    const unsubscribe = boundary.subscribeInvalidation(() => { owner.current = undefined; setState(undefined); });
    return () => { boundary.cancel(owner.current?.ticket); owner.current = undefined; unsubscribe(); };
  }, [boundary, generation]);
  async function load(attempt: SourceAttempt<T>, explicit: boolean) {
    if (latest.current.boundary !== boundary || latest.current.generation !== generation || owner.current) return;
    const ticket = boundary.begin(source, ownerKey, explicit); if (!ticket) return;
    const owned = { ticket, automatic: !explicit, refresh: latest.current.refresh }; owner.current = owned;
    const current = () => boundary.current(ticket) && owner.current === owned && latest.current.boundary === boundary &&
      latest.current.generation === generation && (!owned.automatic || latest.current.refresh === owned.refresh);
    setState(previous => ({ boundary, epoch: ticket.epoch, generation, receipt: previous?.boundary === boundary && previous.epoch === ticket.epoch && previous.generation === generation ? previous.receipt : undefined, loading: true, attempt }));
    try {
      const value = await attempt.read(ticket.controller.signal, explicit);
      if (!current() || !boundary.recover(ticket, readSources?.(value))) return;
      setState({ boundary, epoch: ticket.epoch, generation, receipt: { boundary, epoch: ticket.epoch, generation, refresh: latest.current.refresh, value, window: attempt.window }, loading: false });
    } catch (error) {
      if (!current()) return;
      if (activityAccessDenied(error)) { boundary.deny(); return; }
      setState(previous => ({ boundary, epoch: ticket.epoch, generation, receipt: previous?.receipt, loading: false, attempt,
        error: error instanceof Error ? error.message : "The evidence read is unavailable." }));
    } finally { boundary.finish(ticket); if (owner.current === owned) owner.current = undefined; }
  }
  useEffect(() => {
    if (owner.current?.automatic) { boundary.cancel(owner.current.ticket); owner.current = undefined; }
    if (!owner.current && read && boundary.allowed(source)) void load({ read, window }, false);
  }, [boundary, generation, refresh]);
  const currentState = state?.boundary === boundary && state.epoch === boundary.epoch && state.generation === generation ? state : undefined;
  const receipt = currentState?.receipt;
  return { receipt, value: boundary.allowed(source) ? receipt?.value : undefined, loading: currentState?.loading ?? false, error: currentState?.error,
    denied: !boundary.allowed(source),
    retry: () => { if (!read || owner.current) return; void load(currentState?.attempt ?? { read, window }, true); },
  };
}
