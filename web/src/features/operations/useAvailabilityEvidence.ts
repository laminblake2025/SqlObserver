import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { workspaceUtcTimestamp } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { assertAvailabilitySnapshot, availabilityUtcKey, type AvailabilityPage, type AvailabilityStream } from "./availabilityEvidence";
import { AvailabilityEvidenceError, getAvailabilityEvidence } from "./availabilityEvidenceApi";

interface Attempt { readonly input: string; readonly index: number; readonly cursor?: string; readonly latest: boolean }
interface Position {
  readonly page: AvailabilityPage; readonly first: AvailabilityPage; readonly input: string;
  readonly index: number; readonly cursors: readonly (string | undefined)[]; readonly held: boolean; readonly autoKey: string;
}
interface StreamState { readonly position?: Position; readonly loading: boolean; readonly error?: unknown; readonly attempt?: Attempt; readonly autoKey?: string }
type State = Record<AvailabilityStream, StreamState>;
/** A mounted workspace keeps only its access latch across tab unmounts.
 * Received pages and request controllers remain local to the panel. */
export interface AvailabilityEvidenceAccessOwner { deniedAuthority?: string }
const streams: readonly AvailabilityStream[] = ["replicas", "databases"];
const empty = (): State => ({ replicas: { loading: false }, databases: { loading: false } });
function utcIdentity(value?: string) {
  try { return value === undefined ? undefined : availabilityUtcKey(workspaceUtcTimestamp(value)); }
  catch { return undefined; }
}
function windowIdentity(scope: OverviewScope) {
  return scope.range === "custom" ? JSON.stringify(["custom", utcIdentity(scope.from), utcIdentity(scope.to)]) : scope.range;
}

/** The two independently paged publications share one protected access owner.
 * A native denial fences siblings before React can publish another receipt. */
export function useAvailabilityEvidence(instanceId: string, targetRevision: number, scope: OverviewScope,
  atUtc: string | undefined, refresh: number, accessContext = "", accessOwner?: AvailabilityEvidenceAccessOwner) {
  const authority = JSON.stringify([instanceId.toLowerCase(), targetRevision, accessContext]);
  const windowKey = windowIdentity(scope), context = JSON.stringify([authority, windowKey]);
  const autoKey = JSON.stringify([utcIdentity(atUtc), refresh]);
  const fence = useRef({ context });
  if (fence.current.context !== context) fence.current = { context };
  const frame = fence.current;
  const current = useRef({ context, authority, atUtc, autoKey });
  current.current = { context, authority, atUtc, autoKey };
  const mounted = useRef(false), epoch = useRef(0);
  const standalone = useRef(location.hash === "");
  const owners = useRef(new Map<AvailabilityStream, { controller: AbortController; automatic: boolean }>());
  const localAccessOwner = useRef<AvailabilityEvidenceAccessOwner>({});
  const denial = accessOwner ?? localAccessOwner.current;
  const [state, setState] = useState<{ context: string; value: State; denied: boolean }>({ context, value: empty(), denied: denial.deniedAuthority === authority });
  const received = useRef(state); received.current = state;

  function ownsRoute(hash = location.hash) {
    // Isolated consumers without a workspace URL retain the same empty route.
    if (!hash) return standalone.current;
    const [path, query] = hash.replace(/^#\/?/u, "").split("?");
    const params = new URLSearchParams(query), target = params.getAll("target"), check = params.getAll("check"), range = params.getAll("range");
    if (path !== "operations" || target.length !== 1 || target[0]!.toLowerCase() !== instanceId.toLowerCase() ||
      check.length !== 1 || check[0] !== "availability" || range.length !== 1 || range[0] !== scope.range) return false;
    if (range[0] !== "custom") return !params.has("from") && !params.has("to");
    const from = params.getAll("from"), to = params.getAll("to");
    return from.length === 1 && to.length === 1 && utcIdentity(from[0]) !== undefined && utcIdentity(to[0]) !== undefined &&
      JSON.stringify(["custom", utcIdentity(from[0]), utcIdentity(to[0])]) === windowKey;
  }
  function owns() { return mounted.current && fence.current === frame && current.current.context === context && ownsRoute(); }
  function publish(value: State, denied = denial.deniedAuthority === authority) {
    const next = { context, value, denied }; received.current = next; setState(next);
  }
  function change(stream: AvailabilityStream, update: (old: StreamState) => StreamState) {
    const before = received.current.context === context ? received.current.value : empty();
    publish({ ...before, [stream]: update(before[stream]) });
  }
  function stop() {
    epoch.current++;
    for (const owner of owners.current.values()) owner.controller.abort();
    owners.current.clear();
  }
  useLayoutEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; stop(); };
  }, []);
  useLayoutEffect(() => {
    stop();
    if (denial.deniedAuthority !== undefined && denial.deniedAuthority !== authority) denial.deniedAuthority = undefined;
    publish(empty());
    const retireRoute = (event: HashChangeEvent) => {
      // Read each event's destination to fence an A→B→A transition even when
      // React receives only its final URL. Database/compare edits remain owned.
      if (!ownsRoute(new URL(event.newURL).hash)) { stop(); publish(empty()); }
    };
    window.addEventListener("hashchange", retireRoute);
    return () => { stop(); window.removeEventListener("hashchange", retireRoute); };
  }, [context]);

  async function execute(stream: AvailabilityStream, attempt: Attempt, automatic = false) {
    if (!owns() || denial.deniedAuthority !== undefined || owners.current.has(stream)) return;
    const before = received.current.context === context ? received.current.value[stream].position : undefined;
    const owner = { controller: new AbortController(), automatic }, version = epoch.current, requestKey = current.current.autoKey;
    owners.current.set(stream, owner);
    change(stream, () => ({ position: !automatic && before ? { ...before, held: true } : before,
      loading: true, attempt, autoKey: requestKey }));
    const active = () => owns() && denial.deniedAuthority === undefined && epoch.current === version &&
      owners.current.get(stream) === owner && !owner.controller.signal.aborted && (!automatic || current.current.autoKey === requestKey);
    try {
      const page = await getAvailabilityEvidence(instanceId, stream, attempt.input, owner.controller.signal, attempt.cursor);
      if (!active()) return;
      if (page.targetRevision !== targetRevision) throw new AvailabilityEvidenceError("The AG target revision changed. Return to latest to read the current revision.", true);
      assertAvailabilitySnapshot(attempt.latest || !before ? page : before.first, page);
      const cursors = attempt.latest || !before ? [undefined] : before.cursors.slice();
      if (!attempt.latest) cursors[attempt.index] = attempt.cursor;
      change(stream, () => ({ loading: false, autoKey: requestKey, position: {
        page, first: attempt.latest || !before ? page : before.first, input: attempt.input,
        index: attempt.index, cursors, held: !attempt.latest, autoKey: requestKey,
      } }));
    } catch (error) {
      if (!active()) return;
      if (error instanceof AvailabilityEvidenceError && (error.status === 401 || error.status === 403)) {
        denial.deniedAuthority = authority; stop(); publish(empty(), true); return;
      }
      // Transport, malformed and expired-cursor failures retain actual facts.
      // Retry reuses the attempted cutoff/cursor; latest is an explicit action.
      change(stream, old => ({ ...old, loading: false, error, attempt, autoKey: requestKey }));
    } finally { if (owners.current.get(stream) === owner) owners.current.delete(stream); }
  }
  useEffect(() => {
    if (atUtc === undefined || denial.deniedAuthority !== undefined || !owns()) return;
    for (const stream of streams) {
      const old = received.current.context === context ? received.current.value[stream] : undefined;
      if (old?.position?.held || old?.position?.autoKey === autoKey || old?.autoKey === autoKey && old.error !== undefined || owners.current.has(stream)) continue;
      void execute(stream, { input: atUtc, index: 0, latest: true }, true);
    }
    return () => {
      for (const stream of streams) {
        const owner = owners.current.get(stream);
        if (owner?.automatic) { owner.controller.abort(); owners.current.delete(stream); }
      }
    };
  }, [context, autoKey]);

  function result(stream: AvailabilityStream) {
    const visible = state.context === context && ownsRoute() && denial.deniedAuthority === undefined;
    const old = visible ? state.value[stream] : undefined, position = old?.position;
    function navigate(index: number, cursor?: string) {
      if (!owns() || !position || received.current.value[stream].position !== position || index < 0 || index > 2048 || index > 0 && !cursor) return;
      void execute(stream, { input: position.input, index, cursor, latest: false });
    }
    return { page: position?.page, index: position?.index ?? 0, held: position?.held ?? false,
      loading: old?.loading ?? false, error: old?.error,
      first: () => navigate(0), previous: () => navigate((position?.index ?? 0) - 1, position?.cursors[(position?.index ?? 0) - 1]),
      next: (cursor: string) => navigate((position?.index ?? 0) + 1, cursor),
      returnLatest: () => { if (current.current.atUtc !== undefined) void execute(stream, { input: current.current.atUtc, index: 0, latest: true }); },
      retry: () => { if (old?.attempt && received.current.value[stream].attempt === old.attempt) void execute(stream, old.attempt); },
    };
  }
  function cancel() {
    if (!owns() || denial.deniedAuthority !== undefined) return;
    const before = received.current.value; stop();
    publish(Object.fromEntries(streams.map(stream => [stream, before[stream].loading ? {
      ...before[stream], loading: false, position: before[stream].position ? { ...before[stream].position!, held: true } : undefined,
      error: new AvailabilityEvidenceError("Waiting cancelled. Received AG evidence remains; retry keeps this stream's cutoff and cursor."),
    } : before[stream]])) as State);
  }
  function retryAccess() {
    if (!owns() || current.current.atUtc === undefined) return;
    denial.deniedAuthority = undefined; publish(empty(), false);
    for (const stream of streams) void execute(stream, { input: current.current.atUtc, index: 0, latest: true });
  }
  return { contextKey: context, replicas: result("replicas"), databases: result("databases"),
    denied: denial.deniedAuthority === authority && state.denied, cancel, retryAccess };
}
export type AvailabilityEvidenceRead = ReturnType<typeof useAvailabilityEvidence>;
