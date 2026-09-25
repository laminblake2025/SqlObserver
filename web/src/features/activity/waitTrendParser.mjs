import { ActivityRequestError, readBoundedBody, safeStatusMessage } from "./activityParser.mjs";

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
  if (typeof value !== "string" || value.length > 64 ||
      !/(?:Z|\+00:00)$/u.test(value) || !Number.isFinite(Date.parse(value))) throw invalid();
  return value;
}
function count(value) {
  if (!Number.isSafeInteger(value) || value < 0) throw invalid();
  return value;
}

export function parseWaitTrend(value, expectedInstanceId, window) {
  const body = record(value);
  if (typeof body.instanceId !== "string" || body.instanceId.toLowerCase() !== expectedInstanceId.toLowerCase()) throw invalid();
  const fromUtc = utc(body.fromUtc), toUtc = utc(body.toUtc), repositoryTimeUtc = utc(body.repositoryTimeUtc);
  if (Date.parse(fromUtc) !== Date.parse(window.fromUtc) || Date.parse(toUtc) !== Date.parse(window.toUtc)) throw invalid();
  if (!Array.isArray(body.points) || body.points.length > maximumPoints || body.points.length % 7 !== 0) throw invalid();
  const bucketCategories = new Map();
  const bucketQuality = new Map();
  const earliest = Math.floor(Date.parse(fromUtc) / 300000) * 300000;
  const last = Math.floor((Date.parse(toUtc) - 1) / 300000) * 300000;
  if (body.points.length !== (Math.floor((last - earliest) / 300000) + 1) * 7) throw invalid();
  const points = body.points.map(item => {
    const point = record(item);
    const bucketStartUtc = utc(point.bucketStartUtc);
    const instant = Date.parse(bucketStartUtc);
    if (instant < earliest || instant >= Date.parse(toUtc) || instant % 300000 !== 0 ||
        !categorySet.has(point.category)) throw invalid();
    const seen = bucketCategories.get(instant) ?? new Set();
    if (seen.has(point.category)) throw invalid();
    seen.add(point.category);
    bucketCategories.set(instant, seen);
    const runCount = count(point.runCount), missingSummaryRuns = count(point.missingSummaryRuns);
    const partialRuns = count(point.partialRuns), incomparableTypes = count(point.incomparableTypes);
    const comparableTypes = count(point.comparableTypes);
    if (missingSummaryRuns > runCount || partialRuns > runCount) throw invalid();
    const quality = `${runCount}/${missingSummaryRuns}/${partialRuns}`;
    if (bucketQuality.has(instant) && bucketQuality.get(instant) !== quality) throw invalid();
    bucketQuality.set(instant, quality);
    const waitMilliseconds = point.waitMilliseconds === null ? null : point.waitMilliseconds;
    if (waitMilliseconds !== null && (typeof waitMilliseconds !== "string" ||
        !/^(?:0|[1-9]\d{0,29})$/u.test(waitMilliseconds) || runCount === 0 ||
        missingSummaryRuns !== 0 || partialRuns !== 0 || incomparableTypes !== 0)) throw invalid();
    return { bucketStartUtc, category: point.category, waitMilliseconds,
      runCount, missingSummaryRuns, partialRuns, incomparableTypes, comparableTypes };
  });
  if ([...bucketCategories.values()].some(seen => seen.size !== categories.length)) throw invalid();
  for (let instant = earliest; instant <= last; instant += 300000)
    if (bucketCategories.get(instant)?.size !== categories.length) throw invalid();
  return { instanceId: body.instanceId, fromUtc, toUtc, repositoryTimeUtc, points };
}

export async function getServerWaitTrend(instanceId, window, signal) {
  const from = Date.parse(window.fromUtc), to = Date.parse(window.toUtc);
  if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from || to - from > 24 * 3600000) throw new ActivityRequestError("Invalid wait trend window.");
  const params = new URLSearchParams({ fromUtc: window.fromUtc, toUtc: window.toUtc });
  let response;
  try {
    response = await fetch(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity/waits/trend?${params}`,
      { credentials: "same-origin", headers: { Accept: "application/json" }, cache: "no-store", signal });
  } catch (error) {
    if (signal.aborted) throw error;
    throw new ActivityRequestError("Wait trend is temporarily unavailable.");
  }
  if (!response.ok) throw new ActivityRequestError(safeStatusMessage(response.status));
  const length = response.headers.get("content-length");
  if (length !== null && (!/^\d+$/u.test(length) || Number(length) > maximumBytes)) throw invalid();
  let body;
  try { body = JSON.parse(await readBoundedBody(response, signal, maximumBytes)); }
  catch (error) { if (error instanceof ActivityRequestError) throw error; throw invalid(); }
  return parseWaitTrend(body, instanceId, window);
}
