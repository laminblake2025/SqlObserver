import { rankingTrendConcurrency, rankingTrendMaximumRows, rankingTrendRequestKey } from "./rankingTrendModel.ts";
import type { RankingTrendRequest } from "./rankingTrendModel.ts";

export type RankingTrendRead<T> = { readonly status: "queued" | "loading" } | { readonly status: "ready"; readonly value: T } | { readonly status: "error"; readonly message: string };
/** One bounded ranking context; unseen rows are never scheduled by this queue. */
export function createRankingTrendQueue<T>(load: (request: RankingTrendRequest, signal: AbortSignal) => Promise<T>, notify: () => void,
    maximum = rankingTrendMaximumRows, concurrency = rankingTrendConcurrency) {
  const states = new Map<string, RankingTrendRead<T>>();
  const pending: { key: string; request: RankingTrendRequest }[] = [];
  const controller = new AbortController();
  let running = 0;
  const pump = () => {
    while (!controller.signal.aborted && running < concurrency && pending.length > 0) {
      const job = pending.shift()!; running++; states.set(job.key, { status: "loading" }); notify();
      void Promise.resolve().then(() => {
        if (controller.signal.aborted) throw new Error("Cancelled");
        return load(job.request, controller.signal);
      }).then(value => {
        if (!controller.signal.aborted) { states.set(job.key, { status: "ready", value }); notify(); }
      }).catch(() => {
        if (!controller.signal.aborted) { states.set(job.key, { status: "error", message: "Row trend is unavailable. Check collection and repository access, then retry." }); notify(); }
      }).finally(() => { running--; pump(); });
    }
  };
  return {
    read(request: RankingTrendRequest) { return states.get(rankingTrendRequestKey(request)); },
    schedule(request: RankingTrendRequest, retry = false) {
      if (controller.signal.aborted) return false;
      const key = rankingTrendRequestKey(request), previous = states.get(key);
      if (previous && !(retry && previous.status === "error")) return true;
      if (!previous && states.size >= maximum) return false;
      states.set(key, { status: "queued" }); pending.push({ key, request }); notify(); pump(); return true;
    },
    cancel() { controller.abort(); pending.length = 0; },
  };
}
