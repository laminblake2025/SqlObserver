/** Shared, bounded transport. Feature modules validate their own response schema. */
export interface ApiRequestOptions {
  readonly maximumResponseBytes?: number;
  readonly timeoutMilliseconds?: number;
}

interface Snapshot {
  readonly status: number;
  readonly statusText: string;
  readonly headers: Headers;
  readonly bytes: Uint8Array | null;
}
interface Flight {
  readonly controller: AbortController;
  readonly result: Promise<Snapshot>;
  consumers: number;
  settled: boolean;
}
interface Cached {
  readonly snapshot: Snapshot;
  readonly expires: number;
  readonly size: number;
}

const maximumCacheEntries = 32;
const maximumCacheBytes = 8 * 1024 * 1024;
const completed = new Map<string, Cached>();
const pending = new Map<string, Flight>();
let generation = 0;
let cacheBytes = 0;
let activeRequests = 0;

/** Discard cached reads and stop new subscribers joining reads started before a write. */
export function clearApiCache(): void {
  generation++;
  completed.clear();
  cacheBytes = 0;
  pending.clear();
}

function abortReason(signal: AbortSignal): unknown {
  return signal.reason ?? new DOMException("The request was cancelled.", "AbortError");
}

function withCancellation<T>(work: Promise<T>, signal: AbortSignal): Promise<T> {
  return new Promise((resolve, reject) => {
    const cancelled = () => reject(abortReason(signal));
    signal.addEventListener("abort", cancelled, { once: true });
    work.then(value => { signal.removeEventListener("abort", cancelled); resolve(value); },
      error => { signal.removeEventListener("abort", cancelled); reject(error); });
    if (signal.aborted) cancelled();
  });
}

async function readSnapshot(response: Response, maximumBytes: number, signal: AbortSignal): Promise<Snapshot> {
  const length = response.headers.get("content-length");
  if (length !== null && (!/^\d+$/.test(length) || Number(length) > maximumBytes)) {
    void response.body?.cancel().catch(() => {});
    throw new Error("The API response is too large or has an invalid length.");
  }
  let bytes: Uint8Array | null = null;
  if (response.body) {
    const reader = response.body.getReader();
    const chunks: Uint8Array[] = [];
    let total = 0;
    try {
      for (;;) {
        const chunk = await withCancellation(reader.read(), signal);
        if (chunk.done) break;
        total += chunk.value.byteLength;
        if (total > maximumBytes) throw new Error("The API response is too large.");
        chunks.push(chunk.value);
      }
      bytes = new Uint8Array(total);
      let offset = 0;
      for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    } catch (error) {
      void reader.cancel().catch(() => {});
      throw error;
    } finally { reader.releaseLock(); }
  }
  return { status: response.status, statusText: response.statusText,
    headers: new Headers(response.headers), bytes };
}

function responseFrom(snapshot: Snapshot): Response {
  return new Response(snapshot.bytes === null ? null : snapshot.bytes.slice(), {
    status: snapshot.status, statusText: snapshot.statusText, headers: snapshot.headers,
  });
}

function cacheLifetime(snapshot: Snapshot, request: RequestInit): number {
  if (snapshot.status !== 200 || request.cache === "no-store" || request.cache === "no-cache" ||
      request.cache === "reload" || !/^application\/json(?:\s*;|$)/i.test(snapshot.headers.get("content-type") ?? "")) return 0;
  const directives = (snapshot.headers.get("cache-control") ?? "").toLowerCase().split(",").map(value => value.trim());
  // Completed caching is explicitly server-approved public JSON only. Authenticated
  // private/no-store telemetry and identity responses never enter this cache.
  if (!directives.includes("public") || directives.some(value => /^(?:private|no-store|no-cache)(?:\s*=|$)/.test(value))) return 0;
  const vary = (snapshot.headers.get("vary") ?? "").toLowerCase().split(",").map(value => value.trim());
  if (vary.some(value => ["*", "cookie", "authorization"].includes(value))) return 0;
  const age = directives.find(value => /^max-age=\d+$/.test(value));
  if (!age) return 0;
  const seconds = Number(age.slice(8)) - Number(snapshot.headers.get("age") ?? 0);
  return Number.isFinite(seconds) ? Math.max(0, Math.min(5_000, seconds * 1000)) : 0;
}

function forgetCached(key: string): void {
  const value = completed.get(key);
  if (value) { cacheBytes -= value.size; completed.delete(key); }
}

function remember(key: string, snapshot: Snapshot, lifetime: number): void {
  const size = snapshot.bytes?.byteLength ?? 0;
  if (lifetime <= 0 || size > maximumCacheBytes) return;
  forgetCached(key);
  while (completed.size >= maximumCacheEntries || cacheBytes + size > maximumCacheBytes) {
    const oldest = completed.keys().next().value;
    if (oldest === undefined) break;
    forgetCached(oldest);
  }
  completed.set(key, { snapshot, size, expires: Date.now() + lifetime });
  cacheBytes += size;
}

function subscribe(flight: Flight, key: string | null, signal?: AbortSignal | null): Promise<Response> {
  flight.consumers++;
  return new Promise((resolve, reject) => {
    let finished = false;
    const finish = () => {
      if (finished) return false;
      finished = true;
      signal?.removeEventListener("abort", cancelled);
      flight.consumers--;
      if (flight.consumers === 0 && !flight.settled) {
        flight.controller.abort();
        if (key !== null && pending.get(key) === flight) pending.delete(key);
      }
      return true;
    };
    const cancelled = () => { if (finish()) reject(abortReason(signal!)); };
    signal?.addEventListener("abort", cancelled, { once: true });
    flight.result.then(snapshot => { if (finish()) resolve(responseFrom(snapshot)); },
      error => { if (finish()) reject(error); });
    if (signal?.aborted) cancelled();
  });
}

export async function apiFetch(path: string, init: RequestInit = {}, options: ApiRequestOptions = {}): Promise<Response> {
  if (!/^\/api\/v[12](?:\/|\?|$)/.test(path) || /[\u0000-\u0020\\#]/.test(path))
    throw new Error("The API request must use a local API path.");
  const maximumBytes = options.maximumResponseBytes ?? 4 * 1024 * 1024;
  const timeout = options.timeoutMilliseconds ?? 30_000;
  if (!Number.isSafeInteger(maximumBytes) || maximumBytes < 1 || maximumBytes > 16 * 1024 * 1024 ||
      !Number.isSafeInteger(timeout) || timeout < 1 || timeout > 120_000)
    throw new Error("The API request bounds are invalid.");
  if (init.signal?.aborted) throw abortReason(init.signal);
  const method = (init.method ?? "GET").toUpperCase();
  const read = method === "GET";
  if (!read) clearApiCache();
  const revision = generation;
  const headers = new Headers(init.headers);
  if (!headers.has("accept")) headers.set("accept", "application/json");
  const key = read ? JSON.stringify([revision, path, [...headers].sort(), maximumBytes, timeout, init.cache ?? "default"]) : null;
  if (key !== null) {
    const cached = completed.get(key);
    if (cached && cached.expires > Date.now()) return responseFrom(cached.snapshot);
    forgetCached(key);
    const existing = pending.get(key);
    if (existing) return subscribe(existing, key, init.signal);
  }
  if (activeRequests >= 64) throw new Error("Too many API requests are already running. Try again shortly.");
  activeRequests++;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(new DOMException("The API request timed out.", "TimeoutError")), timeout);
  const flight: Flight = { controller, consumers: 0, settled: false, result: Promise.resolve().then(async () => {
    try {
      if (controller.signal.aborted) throw abortReason(controller.signal);
      const response = await withCancellation(globalThis.fetch(path, {
        ...init, method, headers, credentials: "same-origin", mode: "same-origin", redirect: "error", signal: controller.signal,
      }), controller.signal);
      if (response.status === 401 || response.status === 403) clearApiCache();
      const snapshot = await readSnapshot(response, maximumBytes, controller.signal);
      if (read && key !== null && revision === generation) remember(key, snapshot, cacheLifetime(snapshot, init));
      return snapshot;
    } finally {
      clearTimeout(timer);
      activeRequests--;
      if (!read) clearApiCache();
      flight.settled = true;
      if (key !== null && pending.get(key) === flight) pending.delete(key);
    }
  }) };
  if (key !== null) pending.set(key, flight);
  return subscribe(flight, key, init.signal);
}
