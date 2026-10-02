import { apiFetch } from "../../apiClient.ts";
import { ActivityRequestError, readBoundedBody, safeStatusMessage } from "./activityParser.mjs";
import { utcMicroseconds } from "../../utcTimestamp.ts";

const categories = ["Lock", "I/O", "CPU/signal", "Memory", "Parallelism", "Log", "Other"];
const categorySet = new Set(categories);
const maximumBytes = 1024 * 1024;
const maximumPoints = 7 * 289;
const invalid = () => new ActivityRequestError("Wait trend returned an invalid response.");

function record(value) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw invalid();
  return value;
}
function utc(value) {
  try { instant(value); } catch { throw invalid(); }
  return value;
}
function instant(value) {
  if (typeof value !== 'string') throw invalid();
  return utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, '$1'));
}
const bucketLength = 300_000_000n;
const floorBucket = value => (value >= 0n ? value / bucketLength : (value - bucketLength + 1n) / bucketLength) * bucketLength;
function count(value) {
  if (!Number.isSafeInteger(value) || value < 0) throw invalid();
  return value;
}

export function parseWaitTrend(value, expectedInstanceId, window) {
  const body = record(value);
  if (typeof body.instanceId !== "string" || body.instanceId.toLowerCase() !== expectedInstanceId.toLowerCase()) throw invalid();
  const fromUtc = utc(body.fromUtc), toUtc = utc(body.toUtc), repositoryTimeUtc = utc(body.repositoryTimeUtc);
  const from = instant(fromUtc), to = instant(toUtc);
  if (from !== instant(window.fromUtc) || to !== instant(window.toUtc) || to <= from || to - from > 86_400_000_000n) throw invalid();
  if (!Array.isArray(body.points) || body.points.length > maximumPoints || body.points.length % 7 !== 0) throw invalid();
  const bucketCategories = new Map();
  const bucketQuality = new Map();
  const earliest = floorBucket(from), last = floorBucket(to - 1n);
  if (body.points.length !== Number((last - earliest) / bucketLength + 1n) * 7) throw invalid();
  const points = body.points.map(item => {
    const point = record(item);
    const bucketStartUtc = utc(point.bucketStartUtc);
    const at = instant(bucketStartUtc);
    if (at < earliest || at >= to || at % bucketLength !== 0n ||
        !categorySet.has(point.category)) throw invalid();
    const seen = bucketCategories.get(at) ?? new Set();
    if (seen.has(point.category)) throw invalid();
    seen.add(point.category);
    bucketCategories.set(at, seen);
    const runCount = count(point.runCount), missingSummaryRuns = count(point.missingSummaryRuns);
    const partialRuns = count(point.partialRuns), incomparableTypes = count(point.incomparableTypes);
    const comparableTypes = count(point.comparableTypes);
    if (missingSummaryRuns > runCount || partialRuns > runCount) throw invalid();
    const quality = `${runCount}/${missingSummaryRuns}/${partialRuns}`;
    if (bucketQuality.has(at) && bucketQuality.get(at) !== quality) throw invalid();
    bucketQuality.set(at, quality);
    const waitMilliseconds = point.waitMilliseconds === null ? null : point.waitMilliseconds;
    if (waitMilliseconds !== null && (typeof waitMilliseconds !== "string" ||
        !/^(?:0|[1-9]\d{0,29})$/u.test(waitMilliseconds) || runCount === 0 ||
        missingSummaryRuns !== 0 || partialRuns !== 0 || incomparableTypes !== 0)) throw invalid();
    return { bucketStartUtc, category: point.category, waitMilliseconds,
      runCount, missingSummaryRuns, partialRuns, incomparableTypes, comparableTypes };
  });
  if ([...bucketCategories.values()].some(seen => seen.size !== categories.length)) throw invalid();
  for (let at = earliest; at <= last; at += bucketLength)
    if (bucketCategories.get(at)?.size !== categories.length) throw invalid();
  return { instanceId: body.instanceId, fromUtc, toUtc, repositoryTimeUtc, points };
}

export async function getServerWaitTrend(instanceId, window, signal) {
  try {
    const from = instant(window.fromUtc), to = instant(window.toUtc);
    if (to <= from || to - from > 86_400_000_000n) throw invalid();
  } catch { throw new ActivityRequestError("Invalid wait trend window."); }
  const params = new URLSearchParams({ fromUtc: window.fromUtc, toUtc: window.toUtc });
  let response;
  try {
    response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity/waits/trend?${params}`,
      { credentials: "same-origin", headers: { Accept: "application/json" }, cache: "no-store", signal });
  } catch (error) {
    if (signal.aborted) throw error;
    throw new ActivityRequestError("Wait trend is temporarily unavailable.");
  }
  if (!response.ok) throw new ActivityRequestError(safeStatusMessage(response.status), response.status);
  const length = response.headers.get("content-length");
  if (length !== null && (!/^\d+$/u.test(length) || Number(length) > maximumBytes)) throw invalid();
  let body;
  try { body = JSON.parse(await readBoundedBody(response, signal, maximumBytes)); }
  catch (error) { if (error instanceof ActivityRequestError) throw error; throw invalid(); }
  return parseWaitTrend(body, instanceId, window);
}
