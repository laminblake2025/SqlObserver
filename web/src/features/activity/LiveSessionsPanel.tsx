import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { activityAccessDenied, useActivityBoundary, useBoundarySnapshot, type ActivityReadBoundary } from "./activityAccess.ts";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { readLive, type LivePage, type LiveRow, type LiveSnapshot, type QueryDetail } from "./liveActivityApi";
import { liveRequestKey, valueForLiveRequest } from "./liveEvidenceScope";
import { buildBlockingTree, rowsForBlockingGroup } from "./blockingModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { readOverviewScope } from "../overview/overviewModel.ts";
import { canonicalUtcTimestamp } from "../../utcTimestamp.ts";
import { ActivitySessionInspector } from "./ActivitySessionInspector";
import { SnapshotCalendar } from "./SnapshotCalendar";
import { activitySelectionExpired, activitySelectionHref, activityUtc, readActivitySelection, type ActivitySelection } from "./activitySelectionModel.ts";
import { loadActivityCapture, type ActivityCapture } from "./activityCaptureModel.ts";
import { ActivitySessionWorkspace } from "./ActivitySessionWorkspace";
import { readTriageThresholds, saveTriageThresholds, type TriageThresholds } from "./sessionTriageModel.ts";

// The trigger accepts recently discovered events for up to five minutes. Keep
// enough room after the event time for that bounded discovery window plus
// repository/capture latency so the exact triggered snapshot is returned.
const DEADLOCK_CAPTURE_LOOKAHEAD_MS = 10 * 60 * 1000;

export function LiveSessionsPanel({ compact = false, instanceId, displayName, initialHistoryAtUtc, initialHistoryEventId, selectedWindow, selectedDatabaseId, onSelectDatabase, historyUnavailableReason, scope, selectionHash, canReadText = false, defaultHistorical = false, refreshToken = 0, clockTick = 0, workspacePaused = false, onSelectTimeMode, access: sharedAccess }: { compact?: boolean; instanceId: string; displayName: string; initialHistoryAtUtc?: string; initialHistoryEventId?: string; selectedWindow?: { readonly fromUtc: string; readonly toUtc: string }; selectedDatabaseId?: number; onSelectDatabase?: (databaseId: number | undefined) => void; historyUnavailableReason?: string; scope?: OverviewScope; selectionHash?: string; canReadText?: boolean; defaultHistorical?: boolean; refreshToken?: number; clockTick?: number; workspacePaused?: boolean; onSelectTimeMode?: (mode: "live" | "history") => void; access?: ActivityReadBoundary }) {
  const { mode: timeMode } = useTimeDisplay();
  const timeLabel = (value: string | number) => formatDisplayTime(value, timeMode);
  const urlEnabled = scope !== undefined && selectionHash !== undefined;
  const requested = readActivitySelection(selectionHash ?? "", instanceId);
  const contextKey = JSON.stringify([instanceId.toLowerCase(), scope?.range, scope?.from, scope?.to]);
  const [localPin, setLocalPin] = useState<{ contextKey: string; capture: ActivitySelection }>();
  const capture = requested ?? (localPin?.contextKey === contextKey ? localPin.capture : undefined);
  const captureKey = capture ? JSON.stringify([capture.snapshotId, capture.observedUtc]) : "";
  const selectionKey = JSON.stringify([captureKey, capture?.identity]);
  const localAccess = useActivityBoundary(contextKey), access = sharedAccess ?? localAccess;
  useBoundarySnapshot(access);
  const ownerId = useId(), accessEpoch = access.epoch;
  const metadataAllowed = access.allowed("live-sessions"), historyAllowed = access.allowed("session-history");
  const [accessRetry, setAccessRetry] = useState(0);
  const [historyAccessRetry, setHistoryAccessRetry] = useState(0);
  const retrySources = useRef({ metadata: false, history: false });
  const [independentMinute, setIndependentMinute] = useState(false);
  const expiredSelection = capture !== undefined && activitySelectionExpired(capture, Date.now());
  const invalidSelection = requested === null;
  const [page, setPage] = useState<ActivityCapture>();
  const [pendingCapture, setPendingCapture] = useState<{ page: ActivityCapture; key: string }>();
  const interaction = useRef(false);
  const [thresholds, setThresholds] = useState<TriageThresholds>(() => readTriageThresholds(instanceId));
  useEffect(() => { setThresholds(readTriageThresholds(instanceId)); }, [instanceId]);
  const [inspectorTab, setInspectorTab] = useState<"overview" | "chain" | "sql">("overview");
  const [localDatabase, setLocalDatabase] = useState(selectedDatabaseId === undefined ? "" : String(selectedDatabaseId));
  const database = onSelectDatabase ? selectedDatabaseId === undefined ? "" : String(selectedDatabaseId) : localDatabase;
  const [login, setLogin] = useState(""); const [application, setApplication] = useState(""); const [status, setStatus] = useState("");
  const [idle, setIdle] = useState(true); const [system, setSystem] = useState(true); const [blocked, setBlocked] = useState(false);
  const [sort, setSort] = useState("priority"); const [descending, setDescending] = useState(true);
  const [localBlockingGroupKey, setBlockingGroupKey] = useState("");
  const blockingGroupKey = capture ? capture.chain ?? "" : localBlockingGroupKey;
  const requestedHistoryEnd = parseHistoryAtUtc(initialHistoryAtUtc);
  const expiredEvent = requestedHistoryEnd !== undefined && requestedHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS <= Date.now() - 24 * 3600000;
  const sessionHistoryReason = expiredEvent ? "The linked deadlock is older than the 24-hour session snapshot retention window." : requestedHistoryEnd === undefined ? historyUnavailableReason : undefined;
  const [mode, setMode] = useState<"live" | "history">(() => capture || (requestedHistoryEnd !== undefined && !expiredEvent) || (defaultHistorical && selectedWindow && !sessionHistoryReason) ? "history" : "live"); const [paused, setPaused] = useState(false);
  const [visible, setVisible] = useState(!document.hidden); const [refresh, setRefresh] = useState(0);
  const [loading, setLoading] = useState(false); const [error, setError] = useState<string>(); const [errorKey, setErrorKey] = useState<string>();
  const [hours, setHours] = useState(0.25); const [history, setHistory] = useState<LiveSnapshot[]>([]);
  const [historyReceived, setHistoryReceived] = useState(false);
  const [historyEnd, setHistoryEnd] = useState(() => requestedHistoryEnd === undefined ? Date.now() : requestedHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS); const [minute, setMinute] = useState<number>();
  const [preferredSnapshotId, setPreferredSnapshotId] = useState<string | null | undefined>(undefined);
  const [cursor, setCursor] = useState<string>(); const [, setCursorTrail] = useState<(string | undefined)[]>([]);
  const [storedSelection, setSelection] = useState<{ row: LiveRow; observed: string; snapshot: string; contextKey: string; selectionKey?: string }>();
  const selection = metadataAllowed && storedSelection?.contextKey === contextKey && !invalidSelection && !expiredSelection &&
    (storedSelection.selectionKey === undefined || storedSelection.selectionKey === selectionKey) ? storedSelection : undefined;
  const [selectionMessage, setSelectionMessage] = useState<string>();
  const [restoration, setRestoration] = useState<{ key: string; loading: boolean; message?: string }>();
  const [detail, setDetail] = useState<QueryDetail>(); const [detailError, setDetailError] = useState<string>();
  const [detailAccessGeneration, setDetailAccessGeneration] = useState<number>();
  const [queryLoading, setQueryLoading] = useState(false);
  const [databaseOptions, setDatabaseOptions] = useState<LivePage["databases"]>([]);
  const [pageKey, setPageKey] = useState<string>();
  const selectedHistoryWindow = requestedHistoryEnd !== undefined ? undefined : selectedWindow;
  const historyFrom = selectedHistoryWindow ? Date.parse(selectedHistoryWindow.fromUtc) : historyEnd - hours * 3600000;
  const historyTo = selectedHistoryWindow ? Date.parse(selectedHistoryWindow.toUtc) : historyEnd;
  const queryRequest = useRef<AbortController | undefined>(undefined);
  const textAccessRef = useRef({ allowed: canReadText, generation: 0 });
  if (textAccessRef.current.allowed !== canReadText) textAccessRef.current = {
    allowed: canReadText, generation: textAccessRef.current.generation + 1,
  };
  const visibleDetail = metadataAllowed && canReadText && detailAccessGeneration === textAccessRef.current.generation ? detail : undefined;
  const inspectionKeyRef = useRef(`${contextKey}/${selectionKey}`);
  inspectionKeyRef.current = `${contextKey}/${selectionKey}`;
  const loadedPageScope = useRef<{ filterKey: string; cursor?: string } | undefined>(undefined);
  const appliedHistoryAtUtc = useRef(initialHistoryAtUtc);
  const manual = useRef(false);
  const consecutiveFailures = useRef(0);
  const retryAfter = useRef(0);
  const lastRequestKey = useRef("");
  useLayoutEffect(() => {
    const clear = () => {
      queryRequest.current?.abort(); queryRequest.current = undefined;
      setPage(undefined); setPageKey(undefined); setHistory([]); setHistoryReceived(false); setSelection(undefined);
      setPendingCapture(undefined); setLocalPin(undefined); interaction.current = false;
      setDetail(undefined); setDetailError(undefined); setDetailAccessGeneration(undefined); setQueryLoading(false);
      setDatabaseOptions([]); setCursor(undefined); setCursorTrail([]); setRestoration(undefined);
      setSelectionMessage(undefined); setBlockingGroupKey(""); setLoading(false); setError(undefined); setErrorKey(undefined);
      setPreferredSnapshotId(undefined); setMinute(undefined); loadedPageScope.current = undefined;
      lastRequestKey.current = ""; consecutiveFailures.current = 0; retryAfter.current = 0;
      retrySources.current = { metadata: false, history: false };
    };
    return access.subscribeInvalidation(clear);
  }, [access]);
  useEffect(() => { manual.current = true; }, [refreshToken]);
  useEffect(() => {
    if (!canReadText) {
      queryRequest.current?.abort(); queryRequest.current = undefined;
      setDetail(undefined); setDetailError(undefined); setDetailAccessGeneration(undefined); setQueryLoading(false);
    }
  }, [canReadText]);
  // API cursor filters stay inclusive and immutable. Presentation filters never
  // remove a root or change which captured pages supply the ranking.
  const filterKey = JSON.stringify({ contextKey, includeIdle: true, includeSystem: true, sort: "session" });
  const readableHistory = historyAllowed ? history : [];
  const snapshots = new Map(readableHistory.map(value => [Math.floor(Date.parse(value.observedUtc) / 60000), value]));
  const eventSnapshot = initialHistoryEventId === undefined ? undefined : readableHistory.find(snapshot => snapshot.deadlockEventId?.toLowerCase() === initialHistoryEventId.toLowerCase());
  const missingEventAssociation = initialHistoryEventId !== undefined && !eventSnapshot && !capture && !independentMinute;
  const selectedSnapshot: LiveSnapshot | undefined = capture ? { id: capture.snapshotId, observedUtc: capture.observedUtc, truncated: false } : preferredSnapshotId === undefined
    ? eventSnapshot ?? (missingEventAssociation || minute === undefined ? undefined : snapshots.get(minute))
    : preferredSnapshotId === null ? (missingEventAssociation || minute === undefined ? undefined : snapshots.get(minute)) : readableHistory.find(snapshot => snapshot.id === preferredSnapshotId);
  const requestMode = capture ? "history" : mode;
  const requestKey = liveRequestKey({ mode: requestMode, filterKey, snapshotId: selectedSnapshot?.id, cursor, windowKey: capture ? captureKey : mode === "history" ? `${historyFrom}/${historyTo}` : undefined });
  const activeTick = requestMode === "live" && !cursor && !paused && !workspacePaused ? clockTick : 0;
  const requestKeyRef = useRef(requestKey);
  requestKeyRef.current = requestKey;
  const matchingCapture = Boolean(capture && page?.snapshotId?.toLowerCase() === capture.snapshotId && page.observedUtc &&
    sameObservation(page.observedUtc, capture.observedUtc));
  const displayPage = !metadataAllowed || invalidSelection || expiredSelection ? undefined : valueForLiveRequest(page, pageKey, requestKey) ??
    ((matchingCapture || !capture && requestMode === "live") && loadedPageScope.current?.filterKey === filterKey && loadedPageScope.current.cursor === cursor ? page : undefined);
  const displayError = valueForLiveRequest(error, errorKey, requestKey);
  const blockingGroups = buildBlockingTree(displayPage?.rows ?? []);
  const selectedBlockingGroup = blockingGroups.find(group => group.key === blockingGroupKey);
  const restoreKey = `${contextKey}/${selectionKey}`;
  const currentRestoration = metadataAllowed && restoration?.key === restoreKey ? restoration : undefined;
  const stillCurrent = () => {
    if (inspectionKeyRef.current !== restoreKey) return false;
    if (!urlEnabled) return true;
    const actualScope = readOverviewScope(location.hash);
    const actualContext = JSON.stringify([actualScope.target.toLowerCase(), actualScope.range, actualScope.from, actualScope.to]);
    const actual = readActivitySelection(location.hash, instanceId);
    const actualCaptureKey = actual ? JSON.stringify([actual.snapshotId, actual.observedUtc]) : "";
    return location.hash.split("?")[0] === "#/activity" && actualContext === contextKey &&
      (JSON.stringify([actualCaptureKey, actual?.identity]) === selectionKey || actual === undefined && localPin?.contextKey === contextKey);
  };
  useEffect(() => {
    const changed = () => setVisible(!document.hidden);
    document.addEventListener("visibilitychange", changed);
    return () => { document.removeEventListener("visibilitychange", changed); queryRequest.current?.abort(); };
  }, []);
  useEffect(() => { setCursor(undefined); setCursorTrail([]); }, [filterKey, mode, capture ? undefined : minute]);
  useEffect(() => {
    if (!metadataAllowed && !retrySources.current.metadata) return;
    const controller = new AbortController();
    if (!capture || expiredSelection || invalidSelection) {
      queryRequest.current?.abort();
      setSelection(previous => previous?.contextKey === contextKey && previous.selectionKey === undefined && !invalidSelection ? previous : undefined);
      setDetail(undefined); setDetailError(undefined); setQueryLoading(false); setRestoration(undefined);
      if (!capture) setBlockingGroupKey("");
      return () => controller.abort();
    }
    if (!capture.identity) {
      queryRequest.current?.abort(); setSelection(undefined); setDetail(undefined); setDetailError(undefined); setQueryLoading(false);
      setRestoration({ key: restoreKey, loading: false });
      return () => controller.abort();
    }
    if (selection?.selectionKey === selectionKey || matchingCapture && page?.rows.some(row => row.identity.toLowerCase() === capture.identity)) {
      const row = selection?.row ?? page!.rows.find(row => row.identity.toLowerCase() === capture.identity)!;
        if (!selection) { queryRequest.current?.abort(); setDetail(undefined); setDetailError(undefined); setQueryLoading(false); setInspectorTab(row.blocker || page!.rows.some(value => value.blocker === row.sessionId) ? "chain" : "overview"); }
        setSelection(previous => previous?.selectionKey === selectionKey && previous.contextKey === contextKey ? previous :
          { row, observed: capture.observedUtc, snapshot: capture.snapshotId, contextKey, selectionKey });
        setRestoration({ key: restoreKey, loading: false });
        return () => controller.abort();
    }
    queryRequest.current?.abort(); setSelection(undefined); setDetail(undefined); setDetailError(undefined); setQueryLoading(false);
    setRestoration({ key: restoreKey, loading: true });
    // URL restoration reads metadata with inclusive session filters. Protected
    // query text is loaded only after a separate deliberate action.
    const restoredFilterKey = filterKey;
    const ticket = access.begin("live-sessions", `${ownerId}-restore`, retrySources.current.metadata);
    if (!ticket) return () => controller.abort();
    retrySources.current.metadata = false;
    void loadActivityCapture((parameters, signal) => readLive<LivePage>(instanceId, "", parameters, signal), ticket.controller.signal, { id: capture.snapshotId, observedUtc: capture.observedUtc })
      .then(result => {
        if (!access.current(ticket) || !stillCurrent() || !access.recover(ticket)) return;
        const row = result.rows.find(row => row.identity.toLowerCase() === capture.identity);
        // The bounded metadata lookup has already fulfilled this read. Do not
        // treat the restored cursor as a second manual page request.
        manual.current = false;
        loadedPageScope.current = { filterKey: restoredFilterKey };
        setPage(result); setCursor(undefined); setCursorTrail([]); setPendingCapture(undefined);
        setPageKey(liveRequestKey({ mode: "history", filterKey: restoredFilterKey, snapshotId: capture.snapshotId,
          windowKey: captureKey }));
        setDatabaseOptions(previous => mergeDatabaseOptions(previous, result.databases));
        if (row) { setSelection({ row, snapshot: capture.snapshotId, observed: capture.observedUtc, contextKey, selectionKey }); setInspectorTab(row.blocker || result.rows.some(value => value.blocker === row.sessionId) ? "chain" : "overview"); }
        setRestoration({ key: restoreKey, loading: false, message: row ? undefined :
          result.snapshotId === null ? "The requested session snapshot is unavailable or no longer retained. No current snapshot has been substituted."
          : `The requested session was not found in this captured snapshot. Filters, paging bounds, and truncated capture can exclude evidence; no other request has been selected.${result.limited ? " The 11-page / 512-row lookup bound was reached." : ""}` });
      }).catch((failure: unknown) => {
        if (!access.current(ticket) || !stillCurrent()) return;
        if (activityAccessDenied(failure)) { access.deny(); return; }
        setPage(undefined); setPageKey(undefined); setSelection(undefined);
        setRestoration({ key: restoreKey, loading: false, message: failure instanceof Error ? failure.message : "The requested session snapshot is unavailable." });
      }).finally(() => access.finish(ticket));
    return () => { controller.abort(); access.cancel(ticket); };
  }, [access, accessEpoch, accessRetry, restoreKey, expiredSelection, invalidSelection, refreshToken]);
  useEffect(() => {
    if (appliedHistoryAtUtc.current === initialHistoryAtUtc) return;
    appliedHistoryAtUtc.current = initialHistoryAtUtc;
    const nextHistoryEnd = parseHistoryAtUtc(initialHistoryAtUtc);
    setCursor(undefined); setCursorTrail([]); setHistory([]); setMinute(undefined); setError(undefined); setErrorKey(undefined);
    setPreferredSnapshotId(undefined); setIndependentMinute(false);
    if (nextHistoryEnd === undefined) {
      setMode("live"); setHistoryEnd(Date.now());
    } else {
      setMode(nextHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS <= Date.now() - 24 * 3600000 ? "live" : "history"); setHistoryEnd(nextHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS);
    }
  }, [initialHistoryAtUtc]);
  useEffect(() => {
    if (mode !== "history") return;
    const ticket = access.begin("session-history", `${ownerId}-history`, retrySources.current.history);
    if (!ticket) return;
    retrySources.current.history = false;
    const controller = ticket.controller; setHistory([]); setHistoryReceived(false); setError(undefined); setErrorKey(undefined);
    void readLive<LiveSnapshot[]>(instanceId, "/history", new URLSearchParams({ from: selectedHistoryWindow?.fromUtc ?? new Date(historyFrom).toISOString(), to: selectedHistoryWindow?.toUtc ?? new Date(historyTo).toISOString() }), controller.signal)
      .then(value => {
        if (!access.current(ticket) || !access.recover(ticket)) return;
        const event = initialHistoryEventId === undefined ? undefined : value.find(snapshot => snapshot.deadlockEventId?.toLowerCase() === initialHistoryEventId.toLowerCase());
        const latest = selectedHistoryWindow && initialHistoryEventId === undefined ? value.reduce<LiveSnapshot | undefined>((last, item) => !last || Date.parse(item.observedUtc) > Date.parse(last.observedUtc) ? item : last, undefined) : undefined;
        setHistory(value); setHistoryReceived(true);
        setPreferredSnapshotId(capture?.snapshotId ?? (independentMinute ? null : event?.id ?? latest?.id ?? null));
        if (!independentMinute) setMinute(Math.floor(Date.parse(capture?.observedUtc ?? event?.observedUtc ?? latest?.observedUtc ?? initialHistoryAtUtc ?? new Date(historyTo).toISOString()) / 60000));
      })
      .catch((failure: unknown) => {
        if (!access.current(ticket)) return;
        if (activityAccessDenied(failure)) { access.deny(); return; }
        setError("Historical snapshots are unavailable."); setErrorKey(requestKeyRef.current);
      }).finally(() => access.finish(ticket));
    return () => access.cancel(ticket);
  }, [access, accessEpoch, historyAccessRetry, instanceId, mode, captureKey, historyFrom, historyTo, initialHistoryAtUtc, initialHistoryEventId, refresh, refreshToken]);
  useEffect(() => {
    if (invalidSelection || expiredSelection || capture?.identity && !selection || !visible || (requestMode === "history" && !selectedSnapshot)) { setLoading(false); return; }
    if (!metadataAllowed && !retrySources.current.metadata) { setLoading(false); return; }
    if (capture && displayPage && loadedPageScope.current?.filterKey === filterKey && loadedPageScope.current.cursor === cursor &&
        !manual.current) {
      setPageKey(requestKey); lastRequestKey.current = requestKey; setLoading(false); return;
    }
    if (mode === "live" && paused && !manual.current && lastRequestKey.current === requestKey) { setLoading(false); return; }
    if (mode === "live" && workspacePaused && !manual.current && lastRequestKey.current === requestKey) { setLoading(false); return; }
    if (mode === "live" && !manual.current && lastRequestKey.current === requestKey && Date.now() < retryAfter.current) return;
    lastRequestKey.current = requestKey;
    const explicitAttempt = manual.current;
    manual.current = false;
    let disposed = false;
    const pendingTicket = access.begin("live-sessions", `${ownerId}-metadata`, retrySources.current.metadata);
    if (!pendingTicket) return;
    const ticket = pendingTicket;
    retrySources.current.metadata = false;
    const controller = ticket.controller;
    async function poll() {
      setLoading(true);
      try {
        const next = await loadActivityCapture((parameters, signal) => readLive<LivePage>(instanceId, "", parameters, signal), controller.signal, requestMode === "history" ? selectedSnapshot : undefined);
        if (!disposed && access.current(ticket) && requestKeyRef.current === requestKey && access.recover(ticket)) {
          loadedPageScope.current = { filterKey, cursor };
          if (next.partial && displayPage && !displayPage.partial) {
            setError(next.coverageMessage ?? "Metadata paging failed; the previously received capture remains displayed."); setErrorKey(requestKey);
          } else if (displayPage && requestMode === "live" && interaction.current && !explicitAttempt) {
            setPendingCapture({ page: next, key: requestKey });
          } else { setPage(next); setPageKey(requestKey); setPendingCapture(undefined); setError(undefined); setErrorKey(undefined); }
          setDatabaseOptions(previous => mergeDatabaseOptions(previous, next.databases));
          if (capture && next.snapshotId === null) setRestoration({ key: restoreKey, loading: false,
            message: "The requested session snapshot is unavailable or no longer retained. No current snapshot has been substituted." });
        }
      }
      finally {
        if (!disposed && access.current(ticket)) {
          setLoading(false);
        }
      }
    }
    void poll().then(() => {
      if (!access.current(ticket)) return;
      consecutiveFailures.current = 0;
      retryAfter.current = 0;
    }).catch((failure: unknown) => {
      if (!disposed && access.current(ticket) && requestKeyRef.current === requestKey) {
        if (activityAccessDenied(failure)) { access.deny(); return; }
        consecutiveFailures.current++;
        retryAfter.current = Date.now() + Math.min(60_000, 10_000 * 2 ** consecutiveFailures.current);
        setError("Refresh failed for the current request.");
        setErrorKey(requestKey);
      }
    }).finally(() => access.finish(ticket));
    return () => { disposed = true; access.cancel(ticket); };
  }, [access, accessEpoch, accessRetry, instanceId, filterKey, mode, captureKey, invalidSelection, expiredSelection, selectedSnapshot?.id, selection?.selectionKey, paused, workspacePaused, visible, refresh, refreshToken, activeTick, cursor, hours, historyEnd]);
  const earliestMinute = Math.floor(historyFrom / 60000);
  const latestMinute = Math.floor(historyTo / 60000);
  const gap = mode === "history" && !selectedSnapshot;
  function firstPage() { setCursor(undefined); setCursorTrail([]); }
  function publishCapture(next?: ActivitySelection): boolean {
    if (!urlEnabled || !scope) return false;
    try {
      location.hash = activitySelectionHref({ ...scope, target: instanceId }, next);
      setSelectionMessage(undefined);
      return true;
    } catch {
      setSelectionMessage("This captured observation cannot be shared inside the selected time window. Its local detail is preserved; choose a window containing its observation time before sharing.");
      return false;
    }
  }
  function clearSessionSelection() {
    queryRequest.current?.abort(); setSelection(undefined); setDetail(undefined); setDetailError(undefined);
    setLocalPin(undefined); setPendingCapture(undefined);
    setBlockingGroupKey(""); setRestoration(undefined); setSelectionMessage(undefined);
    publishCapture(undefined);
  }
  function selectChain(chain: string) {
    setBlockingGroupKey(chain);
    if (!displayPage?.snapshotId || !displayPage.observedUtc) return;
    // A native control can fire after Inspect writes the hash but before its
    // React render. Preserve the URL's exact captured identity in that interval.
    const current = readActivitySelection(location.hash, instanceId);
    const sameUrlCapture = current && current.snapshotId === displayPage.snapshotId.toLowerCase() &&
      sameObservation(current.observedUtc, displayPage.observedUtc);
    const inspectedIdentity = selection?.snapshot.toLowerCase() === displayPage.snapshotId.toLowerCase() &&
      sameObservation(selection.observed, displayPage.observedUtc) ? selection.row.identity : undefined;
    const preservedIdentity = sameUrlCapture ? current?.identity : !urlEnabled || current === undefined ? inspectedIdentity : undefined;
    const next = { snapshotId: displayPage.snapshotId, observedUtc: displayPage.observedUtc,
      ...(preservedIdentity !== undefined ? { identity: preservedIdentity } : {}),
      ...(chain ? { chain } : {}) };
    if (publishCapture(next) && selection && preservedIdentity?.toLowerCase() === selection.row.identity.toLowerCase()) setSelection({ ...selection,
      selectionKey: JSON.stringify([JSON.stringify([next.snapshotId.toLowerCase(), canonicalObservation(next.observedUtc)]), next.identity?.toLowerCase()]) });
  }
  function selectMinute(value: number) {
    setLocalPin(undefined); setPendingCapture(undefined); setIndependentMinute(true); setPreferredSnapshotId(null); setMinute(value); firstPage();
    if (!metadataAllowed) { retrySources.current.metadata = true; setAccessRetry(value => value + 1); }
    queryRequest.current?.abort(); setSelection(undefined); setDetail(undefined); setDetailError(undefined); setBlockingGroupKey("");
    const snapshot = snapshots.get(value);
    if (snapshot) publishCapture({ snapshotId: snapshot.id, observedUtc: snapshot.observedUtc });
    else publishCapture(undefined);
  }
  async function loadCapturedSql(observation: NonNullable<typeof selection>) {
    if (!metadataAllowed || !textAccessRef.current.allowed || !stillCurrent()) return;
    queryRequest.current?.abort();
    const ticket = access.begin("live-sessions", `${ownerId}-query`);
    if (!ticket) return;
    const controller = ticket.controller; queryRequest.current = controller;
    const expected = inspectionKeyRef.current;
    const accessGeneration = textAccessRef.current.generation;
    const mayPublish = () => access.current(ticket) && access.allowed("live-sessions") && textAccessRef.current.allowed &&
      textAccessRef.current.generation === accessGeneration && inspectionKeyRef.current === expected && stillCurrent();
    setDetail(undefined); setDetailError(undefined); setQueryLoading(true);
    if (!observation.row.queryId) { setDetail({ state: observation.row.queryState, text: null }); setDetailAccessGeneration(accessGeneration); setQueryLoading(false); access.finish(ticket); return; }
    try {
      const value = await readLive<QueryDetail>(instanceId, "/query", new URLSearchParams({ snapshot: observation.snapshot, identity: observation.row.identity }), controller.signal);
      if (mayPublish()) { setDetail(value); setDetailAccessGeneration(accessGeneration); }
    } catch (failure) {
      if (mayPublish()) {
        // Query Text Reader denial is narrower than the shared metadata role.
        if (activityAccessDenied(failure) && (failure as { status?: number }).status === 401) { access.deny(); return; }
        setDetail(undefined); setDetailAccessGeneration(undefined);
        setDetailError(failure instanceof Error ? failure.message : "Query detail unavailable.");
      }
    } finally { if (mayPublish()) setQueryLoading(false); access.finish(ticket); }
  }
  async function open(row: LiveRow) {
    if (!displayPage?.snapshotId || !displayPage.observedUtc) return;
    queryRequest.current?.abort();
    const next = { snapshotId: displayPage.snapshotId, observedUtc: displayPage.observedUtc, identity: row.identity,
      ...(blockingGroupKey ? { chain: blockingGroupKey } : {}) };
    const published = publishCapture(next);
    if (!published) setLocalPin({ contextKey, capture: next });
    setPendingCapture(undefined);
    setInspectorTab(row.blocker || displayPage.rows.some(value => value.blocker === row.sessionId) ? "chain" : "overview");
    const observation = { row, snapshot: displayPage.snapshotId, observed: displayPage.observedUtc, contextKey,
      ...(published ? { selectionKey: JSON.stringify([JSON.stringify([next.snapshotId.toLowerCase(), canonicalObservation(next.observedUtc)]), row.identity.toLowerCase()]) } : {}) };
    setSelection(observation); setDetail(undefined); setDetailError(undefined); setQueryLoading(false);
  }
  function retrySessionAccess() {
    retrySources.current = { metadata: !metadataAllowed, history: mode === "history" && !historyAllowed };
    manual.current = true; setAccessRetry(value => value + 1);
    if (retrySources.current.history) setHistoryAccessRetry(value => value + 1);
  }
  const sameCaptureSelection = selection && displayPage?.snapshotId === selection.snapshot && sameObservation(selection.observed, displayPage.observedUtc ?? "") ? selection : undefined;
  const presentationFiltered = Boolean(database || login || application || status || !idle || !system || blocked || blockingGroupKey);
  const sessionDetails = sameCaptureSelection && displayPage && <ActivitySessionInspector row={sameCaptureSelection.row} capture={displayPage} thresholds={thresholds} tab={inspectorTab} onTab={setInspectorTab} onClose={clearSessionSelection} onInspect={row => void open(row)} onSelectChain={selectChain} selectedDatabaseId={selectedDatabaseId} filtered={presentationFiltered} canReadText={canReadText} detail={visibleDetail} detailError={detailError} queryLoading={queryLoading} onLoadSql={() => void loadCapturedSql(sameCaptureSelection)} timeLabel={timeLabel}/>;
  return <section className={`activity-capture-screen${compact ? " compact" : ""}`} aria-label={mode === "history" ? "Session history" : "Live sessions"} aria-busy={loading}>
    {(!metadataAllowed || mode === "history" && !historyAllowed) && <p role="alert">Session access was denied. Received sessions, history, selected details and captured SQL were removed. Automatic reads stay stopped until this source is explicitly retried. <button type="button" disabled={loading} onClick={retrySessionAccess}>Retry session access</button></p>}
    <div className="activity-capture-heading"><h3 title={displayName}>{requestMode === "history" ? "Captured activity" : "Current activity"}</h3><div className="toolbar">
      <button type="button" aria-pressed={mode === "live" && !capture} onClick={() => { clearSessionSelection(); setPreferredSnapshotId(null); setMode("live"); firstPage(); onSelectTimeMode?.("live"); }}>Live</button>
      <button type="button" aria-pressed={mode === "history" || Boolean(capture)} disabled={Boolean(sessionHistoryReason) && !capture} onClick={() => { clearSessionSelection(); setPreferredSnapshotId(null); if (!selectedHistoryWindow) setHistoryEnd(Date.now()); setMode("history"); onSelectTimeMode?.("history"); }}>History</button>
      <button type="button" disabled={requestMode !== "live"} aria-pressed={paused} onClick={() => { if (paused) firstPage(); setPaused(!paused); }}>{paused ? "Resume" : "Pause"}</button>
      <button type="button" disabled={loading || !visible} onClick={() => { manual.current = true; if (mode === "history" && !selectedHistoryWindow && !capture) setHistoryEnd(Date.now()); else { firstPage(); setRefresh(value => value + 1); } }}>Refresh now</button>
    </div></div>
    <div className="activity-capture-status" role="status"><span>Observed: {gap ? "No snapshot in this minute" : displayPage?.observedUtc ? timeLabel(displayPage.observedUtc) : loading ? "Loading capture…" : "Unavailable"}</span><span className={`activity-coverage${displayPage?.partial || displayPage?.truncated ? " partial" : ""}`}>{displayPage ? displayPage.state === "unavailable" ? "Collection unavailable" : displayPage.partial ? "Partial metadata coverage" : displayPage.truncated ? "Truncated capture · all retained metadata loaded" : "All captured metadata loaded" : "Coverage unavailable"}</span><span>{requestMode === "history" ? "Historical evidence · automatic refresh paused" : paused ? "Paused" : workspacePaused ? "Workspace live updates paused" : !visible ? "Hidden · refresh paused" : "Refresh every 10 seconds"}{loading ? " · Refreshing…" : ""}{displayPage?.state === "stale" || displayPage && displayError ? " · Stale evidence" : ""}</span></div>
    {displayPage?.coverageMessage && <p role="status" className="evidence-callout">{displayPage.coverageMessage}</p>}
    {displayPage?.truncated && <p role="status">Collection was truncated at 512 rows. Missing sessions may exist.</p>}
    {displayError && <p role="alert">{displayError}{displayError === "Refresh failed for the current request." && (requestMode === "history" ? " Automatic refresh is paused. Select Refresh now to retry this captured snapshot." : paused || workspacePaused || !visible ? " Automatic refresh is paused. Select Refresh now to retry this request." : " Automatic live retries use backoff.")}</p>}
    {pendingCapture?.key === requestKey && <p className="evidence-callout" role="status">A newer capture is ready. Session order is held while you inspect the list. <button type="button" onClick={() => { if (metadataAllowed && pendingCapture.key === requestKey) { setPage(pendingCapture.page); setPageKey(pendingCapture.key); setPendingCapture(undefined); setError(undefined); setErrorKey(undefined); } }}>Apply capture update</button></p>}
    {requested !== undefined && <div className="activity-capture-status">{invalidSelection ? <p role="alert">The requested session selection is invalid for this server and time window. No current snapshot has been substituted.</p> : expiredSelection ? <p role="status">The requested session snapshot is outside the 24-hour retention window. No current snapshot has been substituted.</p> : <span>Selected capture pinned · workspace Live/Rewind mode is unchanged</span>}<button className="secondary-button" type="button" onClick={clearSessionSelection}>Clear session selection</button></div>}
    {currentRestoration?.loading && <p role="status">Restoring the requested session from at most 11 pages / 512 captured rows. Protected SQL is not loaded automatically.</p>}
    {currentRestoration?.message && <p className="empty-state" role="status">{currentRestoration.message}</p>}
    {selectionMessage && <p className="table-note" role="status">{selectionMessage}</p>}
    {mode === "history" && initialHistoryAtUtc && <p className="evidence-callout">Opened for the deadlock event at {timeLabel(initialHistoryAtUtc)}. Historical snapshots are minute-granular for cadence evidence; a deadlock-triggered capture is selected automatically when available.</p>}
    {mode === "history" && missingEventAssociation && historyAllowed && historyReceived && <p role="status">No retained capture is associated with the requested deadlock event. No same-minute snapshot has been substituted. Choose a minute deliberately to inspect an independent capture.</p>}
    {mode === "history" && initialHistoryEventId && independentMinute && <p className="table-note">Independent minute capture selected deliberately. Its observation does not establish an association with the requested deadlock event.</p>}
    {sessionHistoryReason && mode === "live" && <p className="evidence-callout">{sessionHistoryReason} Showing current sessions.</p>}
    {gap && metadataAllowed && <p className="empty-state">No successful snapshot was captured in this minute.</p>}
    {displayPage && !gap && <ActivitySessionWorkspace capture={displayPage} database={database} login={login} application={application} status={status} includeIdle={idle} includeSystem={system} blockedOnly={blocked} chainRows={blockingGroupKey ? selectedBlockingGroup ? rowsForBlockingGroup(displayPage.rows, selectedBlockingGroup) : [] : undefined} chainKey={blockingGroupKey} selectedIdentity={sameCaptureSelection?.row.identity} inspector={sessionDetails || undefined} onInspect={row => void open(row)} thresholds={thresholds} onThresholds={value => { saveTriageThresholds(instanceId, value); setThresholds(value); }} sort={sort} onSort={setSort} descending={descending} onInteraction={active => { interaction.current = active; }} onClearFilters={() => { setLogin(""); setApplication(""); setStatus(""); setIdle(true); setSystem(true); setBlocked(false); selectChain(""); }}/>}
    <details className="supporting-evidence"><summary>History & capture selection</summary>
      {selectedHistoryWindow && <p>Selected window: {timeLabel(selectedHistoryWindow.fromUtc)} to {timeLabel(selectedHistoryWindow.toUtc)}. Snapshots older than 24 hours are outside retention.</p>}
      {mode === "history" ? <div className="toolbar">{!selectedHistoryWindow && <label>History window <select value={hours} onChange={event => setHours(Number(event.target.value))}><option value={0.25}>15 minutes</option><option value={1}>1 hour</option><option value={6}>6 hours</option><option value={24}>24 hours</option></select></label>}
        <button type="button" disabled={minute === undefined || minute <= earliestMinute} onClick={() => { if (minute !== undefined) selectMinute(minute - 1); }}>Previous minute</button><SnapshotCalendar minute={minute} earliestMinute={earliestMinute} latestMinute={latestMinute} onSelect={selectMinute}/><button type="button" disabled={minute === undefined || minute >= latestMinute} onClick={() => { if (minute !== undefined) selectMinute(minute + 1); }}>Next minute</button></div> : <p>Select History to browse retained captures. Live reads include idle and system sessions.</p>}
      {selectedSnapshot?.deadlockEventId && <p>Deadlock-triggered snapshot.</p>}
    </details>
    <details className="supporting-evidence session-filter-disclosure"><summary>Advanced session filters{presentationFiltered ? " · filters applied" : ""}</summary><div className="live-filters">
      <label>Database <select value={database} onChange={event => { const value = event.target.value; if (onSelectDatabase) onSelectDatabase(value === "" ? undefined : Number(value)); else setLocalDatabase(value); }}><option value="">All databases</option>{database !== "" && (!metadataAllowed || !databaseOptions.some(db => String(db.id) === database)) && <option value={database}>Database ID {database} · name unavailable</option>}{metadataAllowed && databaseOptions.map(db => <option key={db.id} value={db.id}>{db.name ?? `Database ${db.id}`}</option>)}</select></label>
      <label>Login <input maxLength={128} value={login} onChange={event => setLogin(event.target.value)}/></label><label>Client application <input maxLength={128} value={application} onChange={event => setApplication(event.target.value)}/></label><label>Status <input maxLength={60} value={status} onChange={event => setStatus(event.target.value)}/></label>
      <label><input type="checkbox" checked={blocked} onChange={event => setBlocked(event.target.checked)}/> Blocked only</label><label><input type="checkbox" checked={idle} onChange={event => setIdle(event.target.checked)}/> Include idle sessions</label><label><input type="checkbox" checked={system} onChange={event => setSystem(event.target.checked)}/> Include system sessions</label><label><input type="checkbox" checked={descending} onChange={event => setDescending(event.target.checked)}/> Descending metric order</label>
      <label>Blocking chain (capture)<select value={blockingGroupKey} onChange={event => selectChain(event.target.value)}><option value="">All captured sessions</option>{blockingGroupKey && !selectedBlockingGroup && <option value={blockingGroupKey}>Selected chain unavailable: {blockingGroupKey}</option>}{blockingGroups.map(group => <option key={group.key} value={group.key}>{group.rootLabel}</option>)}</select></label><button type="button" className="secondary-button" disabled={!blockingGroupKey} onClick={() => selectChain("")}>Clear chain filter</button>
    </div></details>
    <details className="supporting-evidence"><summary>Snapshot identity & qualifications</summary><p>Snapshot: {displayPage?.snapshotId ?? "Unavailable"}. Observed: {displayPage?.observedUtc ?? "Unavailable"}. {displayPage ? `${displayPage.pagesRead} validated native pages · ${displayPage.rows.length} captured request rows.` : "Metadata coverage unavailable."} Captured rows retain their exact native identity. Counts describe distinct session lifetimes in this capture; attention and blocked counts can overlap. Collection is bounded to 512 rows and metadata reads to 11 pages.</p><p>Cumulative CPU is milliseconds, not CPU percentage. Rates require a matching interval baseline. Elapsed time is not wait duration. Missing sessions and captures cannot establish an all-clear result.</p></details>
  </section>;
}

function mergeDatabaseOptions(current: LivePage["databases"], incoming: LivePage["databases"]): LivePage["databases"] {
  const options = new Map(current.map(database => [database.id, database]));
  for (const database of incoming) options.set(database.id, database);
  return [...options.values()].sort((left, right) => left.id - right.id);
}

function parseHistoryAtUtc(value?: string): number | undefined {
  if (value === undefined || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(value)) return undefined;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function sameObservation(left: string, right: string): boolean {
  try { return activityUtc(left) === activityUtc(right); } catch { return false; }
}
function canonicalObservation(value: string): string {
  return canonicalUtcTimestamp(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
}
