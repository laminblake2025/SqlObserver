import { useEffect, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { readLive, type LivePage, type LiveRow, type LiveSnapshot, type QueryDetail } from "./liveActivityApi";
import { liveRequestKey, valueForLiveRequest } from "./liveEvidenceScope";
import { buildBlockingTree, formatBlockingTarget, rowsForBlockingGroup, type BlockingTreeGroup, type BlockingTreeNode } from "./blockingModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { readOverviewScope } from "../overview/overviewModel.ts";
import { canonicalUtcTimestamp } from "../../utcTimestamp.ts";
import { activitySelectionExpired, activitySelectionHref, activityUtc, loadActivitySelection, readActivitySelection,
  validateActivitySelectionPage, type ActivitySelection } from "./activitySelectionModel.ts";

// The trigger accepts recently discovered events for up to five minutes. Keep
// enough room after the event time for that bounded discovery window plus
// repository/capture latency so the exact triggered snapshot is returned.
const DEADLOCK_CAPTURE_LOOKAHEAD_MS = 10 * 60 * 1000;

export function LiveSessionsPanel({ instanceId, displayName, initialHistoryAtUtc, initialHistoryEventId, selectedWindow, selectedDatabaseId, onSelectDatabase, historyUnavailableReason, scope, selectionHash, canReadText = false, defaultHistorical = false, refreshToken = 0, clockTick = 0, workspacePaused = false, onSelectTimeMode }: { instanceId: string; displayName: string; initialHistoryAtUtc?: string; initialHistoryEventId?: string; selectedWindow?: { readonly fromUtc: string; readonly toUtc: string }; selectedDatabaseId?: number; onSelectDatabase?: (databaseId: number | undefined) => void; historyUnavailableReason?: string; scope?: OverviewScope; selectionHash?: string; canReadText?: boolean; defaultHistorical?: boolean; refreshToken?: number; clockTick?: number; workspacePaused?: boolean; onSelectTimeMode?: (mode: "live" | "history") => void }) {
  const { mode: timeMode } = useTimeDisplay();
  const timeLabel = (value: string | number) => formatDisplayTime(value, timeMode);
  const urlEnabled = scope !== undefined && selectionHash !== undefined;
  const requested = readActivitySelection(selectionHash ?? "", instanceId);
  const capture = requested ?? undefined;
  const captureKey = capture ? JSON.stringify([capture.snapshotId, capture.observedUtc]) : "";
  const selectionKey = JSON.stringify([captureKey, capture?.identity]);
  const contextKey = JSON.stringify([instanceId.toLowerCase(), scope?.range, scope?.from, scope?.to, selectedDatabaseId]);
  const expiredSelection = capture !== undefined && activitySelectionExpired(capture, Date.now());
  const invalidSelection = requested === null;
  const [page, setPage] = useState<LivePage>();
  const [localDatabase, setLocalDatabase] = useState(selectedDatabaseId === undefined ? "" : String(selectedDatabaseId));
  const database = onSelectDatabase ? selectedDatabaseId === undefined ? "" : String(selectedDatabaseId) : localDatabase;
  const [login, setLogin] = useState(""); const [application, setApplication] = useState(""); const [status, setStatus] = useState("");
  const [idle, setIdle] = useState(false); const [system, setSystem] = useState(false); const [blocked, setBlocked] = useState(false);
  const [sort, setSort] = useState("cpu"); const [descending, setDescending] = useState(true);
  const [localBlockingGroupKey, setBlockingGroupKey] = useState("");
  const blockingGroupKey = capture ? capture.chain ?? "" : localBlockingGroupKey;
  const requestedHistoryEnd = parseHistoryAtUtc(initialHistoryAtUtc);
  const expiredEvent = requestedHistoryEnd !== undefined && requestedHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS <= Date.now() - 24 * 3600000;
  const sessionHistoryReason = expiredEvent ? "The linked deadlock is older than the 24-hour session snapshot retention window." : requestedHistoryEnd === undefined ? historyUnavailableReason : undefined;
  const [mode, setMode] = useState<"live" | "history">(() => capture || (requestedHistoryEnd !== undefined && !expiredEvent) || (defaultHistorical && selectedWindow && !sessionHistoryReason) ? "history" : "live"); const [paused, setPaused] = useState(false);
  const [visible, setVisible] = useState(!document.hidden); const [refresh, setRefresh] = useState(0);
  const [loading, setLoading] = useState(false); const [error, setError] = useState<string>(); const [errorKey, setErrorKey] = useState<string>();
  const [hours, setHours] = useState(0.25); const [history, setHistory] = useState<LiveSnapshot[]>([]);
  const [historyEnd, setHistoryEnd] = useState(() => requestedHistoryEnd === undefined ? Date.now() : requestedHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS); const [minute, setMinute] = useState<number>();
  const [preferredSnapshotId, setPreferredSnapshotId] = useState<string | null | undefined>(undefined);
  const [cursor, setCursor] = useState<string>(); const [cursorTrail, setCursorTrail] = useState<(string | undefined)[]>([]);
  const [storedSelection, setSelection] = useState<{ row: LiveRow; observed: string; snapshot: string; contextKey: string; selectionKey?: string }>();
  const selection = storedSelection?.contextKey === contextKey && !invalidSelection && !expiredSelection &&
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
  const visibleDetail = canReadText && detailAccessGeneration === textAccessRef.current.generation ? detail : undefined;
  const inspectionKeyRef = useRef(`${contextKey}/${selectionKey}`);
  inspectionKeyRef.current = `${contextKey}/${selectionKey}`;
  const loadedPageScope = useRef<{ filterKey: string; cursor?: string } | undefined>(undefined);
  const appliedHistoryAtUtc = useRef(initialHistoryAtUtc);
  const manual = useRef(false);
  const consecutiveFailures = useRef(0);
  const retryAfter = useRef(0);
  useEffect(() => { manual.current = true; }, [refreshToken]);
  useEffect(() => {
    if (!canReadText) {
      queryRequest.current?.abort(); queryRequest.current = undefined;
      setDetail(undefined); setDetailError(undefined); setDetailAccessGeneration(undefined); setQueryLoading(false);
    }
  }, [canReadText]);
  const lastRequestKey = useRef("");
  const filterKey = JSON.stringify({ database, login, application, status, idle, system, blocked, sort, descending });
  const snapshots = new Map(history.map(value => [Math.floor(Date.parse(value.observedUtc) / 60000), value]));
  const eventSnapshot = initialHistoryEventId === undefined ? undefined : history.find(snapshot => snapshot.deadlockEventId === initialHistoryEventId);
  const selectedSnapshot: LiveSnapshot | undefined = capture ? { id: capture.snapshotId, observedUtc: capture.observedUtc, truncated: false } : preferredSnapshotId === undefined
    ? eventSnapshot ?? (minute === undefined ? undefined : snapshots.get(minute))
    : preferredSnapshotId === null ? (minute === undefined ? undefined : snapshots.get(minute)) : history.find(snapshot => snapshot.id === preferredSnapshotId);
  const requestMode = capture ? "history" : mode;
  const requestKey = liveRequestKey({ mode: requestMode, filterKey, snapshotId: selectedSnapshot?.id, cursor, windowKey: capture ? captureKey : mode === "history" ? `${historyFrom}/${historyTo}` : undefined });
  const activeTick = requestMode === "live" && !cursor && !paused && !workspacePaused ? clockTick : 0;
  const requestKeyRef = useRef(requestKey);
  requestKeyRef.current = requestKey;
  const matchingCapture = Boolean(capture && page?.snapshotId?.toLowerCase() === capture.snapshotId && page.observedUtc &&
    sameObservation(page.observedUtc, capture.observedUtc));
  const displayPage = invalidSelection || expiredSelection ? undefined : valueForLiveRequest(page, pageKey, requestKey) ??
    (matchingCapture && loadedPageScope.current?.filterKey === filterKey && loadedPageScope.current.cursor === cursor ? page : undefined);
  const displayError = valueForLiveRequest(error, errorKey, requestKey);
  const blockingGroups = buildBlockingTree(displayPage?.rows ?? []);
  const selectedBlockingGroup = blockingGroups.find(group => group.key === blockingGroupKey);
  const visibleRows = blockingGroupKey === "" ? displayPage?.rows ?? []
    : rowsForBlockingGroup(displayPage?.rows ?? [], selectedBlockingGroup);
  const restoreKey = `${contextKey}/${selectionKey}`;
  const currentRestoration = restoration?.key === restoreKey ? restoration : undefined;
  const apiFilters = (allRows = false) => {
    const parameters = new URLSearchParams({ includeIdle: String(allRows || idle), includeSystem: String(allRows || system),
      blockedOnly: String(!allRows && blocked), sort, descending: String(descending) });
    for (const [key, value] of Object.entries({ databaseId: database, login: allRows ? "" : login,
      application: allRows ? "" : application, status: allRows ? "" : status })) if (value) parameters.set(key, value);
    return parameters;
  };
  const stillCurrent = () => {
    if (inspectionKeyRef.current !== restoreKey) return false;
    if (!urlEnabled) return true;
    const actualScope = readOverviewScope(location.hash);
    const actualContext = JSON.stringify([actualScope.target.toLowerCase(), actualScope.range, actualScope.from, actualScope.to, actualScope.databaseId]);
    const actual = readActivitySelection(location.hash, instanceId);
    const actualCaptureKey = actual ? JSON.stringify([actual.snapshotId, actual.observedUtc]) : "";
    return location.hash.split("?")[0] === "#/activity" && actualContext === contextKey &&
      JSON.stringify([actualCaptureKey, actual?.identity]) === selectionKey;
  };
  useEffect(() => {
    const changed = () => setVisible(!document.hidden);
    document.addEventListener("visibilitychange", changed);
    return () => { document.removeEventListener("visibilitychange", changed); queryRequest.current?.abort(); };
  }, []);
  useEffect(() => { setCursor(undefined); setCursorTrail([]); }, [filterKey, mode, capture ? undefined : minute]);
  useEffect(() => {
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
      if (selectedDatabaseId === undefined || row.databaseId === selectedDatabaseId) {
        if (!selection) { queryRequest.current?.abort(); setDetail(undefined); setDetailError(undefined); setQueryLoading(false); }
        setSelection(previous => previous?.selectionKey === selectionKey && previous.contextKey === contextKey ? previous :
          { row, observed: capture.observedUtc, snapshot: capture.snapshotId, contextKey, selectionKey });
        setRestoration({ key: restoreKey, loading: false });
        return () => controller.abort();
      }
    }
    queryRequest.current?.abort(); setSelection(undefined); setDetail(undefined); setDetailError(undefined); setQueryLoading(false);
    setRestoration({ key: restoreKey, loading: true });
    // URL restoration reads metadata with inclusive session filters. Protected
    // query text is loaded only after a separate deliberate action.
    setLogin(""); setApplication(""); setStatus(""); setBlocked(false); setIdle(true); setSystem(true);
    const filters = apiFilters(true);
    const restoredFilterKey = JSON.stringify({ database, login: "", application: "", status: "", idle: true, system: true, blocked: false, sort, descending });
    void loadActivitySelection(capture, filters, (parameters, signal) => readLive<LivePage>(instanceId, "", parameters, signal), controller.signal)
      .then(result => {
        if (controller.signal.aborted || !stillCurrent()) return;
        const row = result.row && (selectedDatabaseId === undefined || result.row.databaseId === selectedDatabaseId) ? result.row : undefined;
        // The bounded metadata lookup has already fulfilled this read. Do not
        // treat the restored cursor as a second manual page request.
        manual.current = false;
        loadedPageScope.current = { filterKey: restoredFilterKey, cursor: result.cursor };
        setPage(result.page); setCursor(result.cursor); setCursorTrail([...result.cursorTrail]);
        setPageKey(liveRequestKey({ mode: "history", filterKey: restoredFilterKey, snapshotId: capture.snapshotId,
          cursor: result.cursor, windowKey: captureKey }));
        setDatabaseOptions(previous => mergeDatabaseOptions(previous, result.page.databases));
        if (row) setSelection({ row, snapshot: capture.snapshotId, observed: capture.observedUtc, contextKey, selectionKey });
        setRestoration({ key: restoreKey, loading: false, message: row ? undefined :
          result.page.snapshotId === null ? "The requested session snapshot is unavailable or no longer retained. No current snapshot has been substituted."
          : `The requested session was not found in this captured snapshot. Filters, paging bounds, and truncated capture can exclude evidence; no other request has been selected.${result.limited ? " The 11-page / 512-row lookup bound was reached." : ""}` });
      }).catch((failure: unknown) => {
        if (controller.signal.aborted || !stillCurrent()) return;
        setPage(undefined); setPageKey(undefined); setSelection(undefined);
        setRestoration({ key: restoreKey, loading: false, message: failure instanceof Error ? failure.message : "The requested session snapshot is unavailable." });
      });
    return () => controller.abort();
  }, [restoreKey, expiredSelection, invalidSelection, refreshToken]);
  useEffect(() => {
    if (appliedHistoryAtUtc.current === initialHistoryAtUtc) return;
    appliedHistoryAtUtc.current = initialHistoryAtUtc;
    const nextHistoryEnd = parseHistoryAtUtc(initialHistoryAtUtc);
    setCursor(undefined); setCursorTrail([]); setHistory([]); setMinute(undefined); setError(undefined); setErrorKey(undefined);
    setPreferredSnapshotId(undefined);
    if (nextHistoryEnd === undefined) {
      setMode("live"); setHistoryEnd(Date.now());
    } else {
      setMode(nextHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS <= Date.now() - 24 * 3600000 ? "live" : "history"); setHistoryEnd(nextHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS);
    }
  }, [initialHistoryAtUtc]);
  useEffect(() => {
    if (mode !== "history") return;
    const controller = new AbortController(); setHistory([]); setError(undefined); setErrorKey(undefined);
    void readLive<LiveSnapshot[]>(instanceId, "/history", new URLSearchParams({ from: selectedHistoryWindow?.fromUtc ?? new Date(historyFrom).toISOString(), to: selectedHistoryWindow?.toUtc ?? new Date(historyTo).toISOString() }), controller.signal)
      .then(value => { if (!controller.signal.aborted) { const event = initialHistoryEventId === undefined ? undefined : value.find(snapshot => snapshot.deadlockEventId === initialHistoryEventId); const latest = selectedHistoryWindow ? value.reduce<LiveSnapshot | undefined>((last, item) => !last || Date.parse(item.observedUtc) > Date.parse(last.observedUtc) ? item : last, undefined) : undefined; setHistory(value); setPreferredSnapshotId(capture?.snapshotId ?? event?.id ?? latest?.id ?? null); setMinute(Math.floor(Date.parse(capture?.observedUtc ?? event?.observedUtc ?? latest?.observedUtc ?? initialHistoryAtUtc ?? new Date(historyTo).toISOString()) / 60000)); } })
      .catch(() => { if (!controller.signal.aborted) { setError("Historical snapshots are unavailable."); setErrorKey(requestKeyRef.current); } });
    return () => controller.abort();
  }, [instanceId, mode, captureKey, historyFrom, historyTo, initialHistoryAtUtc, initialHistoryEventId, refresh, refreshToken]);
  useEffect(() => {
    if (invalidSelection || expiredSelection || capture?.identity && !selection || !visible || (requestMode === "history" && !selectedSnapshot)) { setLoading(false); return; }
    if (capture && displayPage && loadedPageScope.current?.filterKey === filterKey && loadedPageScope.current.cursor === cursor &&
        !manual.current) {
      setPageKey(requestKey); lastRequestKey.current = requestKey; setLoading(false); return;
    }
    if (mode === "live" && paused && !manual.current && lastRequestKey.current === requestKey) { setLoading(false); return; }
    if (mode === "live" && workspacePaused && !manual.current && lastRequestKey.current === requestKey) { setLoading(false); return; }
    if (mode === "live" && !manual.current && lastRequestKey.current === requestKey && Date.now() < retryAfter.current) return;
    lastRequestKey.current = requestKey;
    manual.current = false;
    let disposed = false;
    const controller = new AbortController();
    async function poll() {
      setLoading(true);
      const filters = JSON.parse(filterKey) as { database: string; login: string; application: string; status: string; idle: boolean; system: boolean; blocked: boolean; sort: string; descending: boolean };
      const parameters = new URLSearchParams({ includeIdle: String(filters.idle), includeSystem: String(filters.system), blockedOnly: String(filters.blocked), sort: filters.sort, descending: String(filters.descending) });
      for (const [key, value] of Object.entries({ databaseId: filters.database, login: filters.login, application: filters.application, status: filters.status, snapshot: requestMode === "history" ? selectedSnapshot?.id : undefined, cursor })) if (value) parameters.set(key, value);
      try {
        const next = await readLive<LivePage>(instanceId, "", parameters, controller.signal);
        if (capture) validateActivitySelectionPage(next, capture);
        if (!disposed && requestKeyRef.current === requestKey) {
          loadedPageScope.current = { filterKey, cursor };
          setPage(next); setPageKey(requestKey); setError(undefined); setErrorKey(undefined);
          setDatabaseOptions(previous => mergeDatabaseOptions(previous, next.databases));
          if (capture && next.snapshotId === null) setRestoration({ key: restoreKey, loading: false,
            message: "The requested session snapshot is unavailable or no longer retained. No current snapshot has been substituted." });
        }
      }
      finally {
        if (!disposed) {
          setLoading(false);
        }
      }
    }
    void poll().then(() => {
      consecutiveFailures.current = 0;
      retryAfter.current = 0;
    }).catch(() => {
      if (!disposed && !controller.signal.aborted && requestKeyRef.current === requestKey) {
        consecutiveFailures.current++;
        retryAfter.current = Date.now() + Math.min(60_000, 10_000 * 2 ** consecutiveFailures.current);
        setError("Refresh failed for the current request; retrying with backoff.");
        setErrorKey(requestKey);
      }
    });
    return () => { disposed = true; controller.abort(); };
  }, [instanceId, filterKey, mode, captureKey, invalidSelection, expiredSelection, selectedSnapshot?.id, selection?.selectionKey, paused, workspacePaused, visible, refresh, refreshToken, activeTick, cursor, hours, historyEnd]);
  const earliestMinute = Math.floor(historyFrom / 60000);
  const latestMinute = Math.floor(historyTo / 60000);
  const minuteOptions = Array.from({ length: latestMinute - earliestMinute + 1 }, (_, index) => latestMinute - index);
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
    setPreferredSnapshotId(null); setMinute(value); firstPage();
    queryRequest.current?.abort(); setSelection(undefined); setDetail(undefined); setDetailError(undefined); setBlockingGroupKey("");
    const snapshot = snapshots.get(value);
    if (snapshot) publishCapture({ snapshotId: snapshot.id, observedUtc: snapshot.observedUtc });
    else publishCapture(undefined);
  }
  async function loadCapturedSql(observation: NonNullable<typeof selection>) {
    if (!textAccessRef.current.allowed || !stillCurrent()) return;
    queryRequest.current?.abort(); const controller = new AbortController(); queryRequest.current = controller;
    const expected = inspectionKeyRef.current;
    const accessGeneration = textAccessRef.current.generation;
    const mayPublish = () => !controller.signal.aborted && textAccessRef.current.allowed &&
      textAccessRef.current.generation === accessGeneration && inspectionKeyRef.current === expected && stillCurrent();
    setDetail(undefined); setDetailError(undefined); setQueryLoading(true);
    if (!observation.row.queryId) { setDetail({ state: observation.row.queryState, text: null }); setDetailAccessGeneration(accessGeneration); setQueryLoading(false); return; }
    try {
      const value = await readLive<QueryDetail>(instanceId, "/query", new URLSearchParams({ snapshot: observation.snapshot, identity: observation.row.identity }), controller.signal);
      if (mayPublish()) { setDetail(value); setDetailAccessGeneration(accessGeneration); }
    } catch (failure) {
      if (mayPublish()) setDetailError(failure instanceof Error ? failure.message : "Query detail unavailable.");
    } finally { if (mayPublish()) setQueryLoading(false); }
  }
  async function open(row: LiveRow) {
    if (!displayPage?.snapshotId || !displayPage.observedUtc) return;
    queryRequest.current?.abort();
    const next = { snapshotId: displayPage.snapshotId, observedUtc: displayPage.observedUtc, identity: row.identity,
      ...(blockingGroupKey ? { chain: blockingGroupKey } : {}) };
    const published = publishCapture(next);
    const observation = { row, snapshot: displayPage.snapshotId, observed: displayPage.observedUtc, contextKey,
      ...(published ? { selectionKey: JSON.stringify([JSON.stringify([next.snapshotId.toLowerCase(), canonicalObservation(next.observedUtc)]), row.identity.toLowerCase()]) } : {}) };
    setSelection(observation); setDetail(undefined); setDetailError(undefined); setQueryLoading(false);
  }
  return <section className="live-sessions panel" aria-label={mode === "history" ? "Session history" : "Live sessions"} aria-busy={loading}>
    <div className="health-heading-row"><div><p className="eyebrow">Activity · {displayName}</p><h3>{mode === "history" ? "Session history" : "Live sessions"}</h3></div><div className="toolbar">
      <button aria-pressed={mode === "live" && !capture} onClick={() => { clearSessionSelection(); setPreferredSnapshotId(null); setMode("live"); firstPage(); onSelectTimeMode?.("live"); }}>Live</button>
      <button aria-pressed={mode === "history" || Boolean(capture)} disabled={Boolean(sessionHistoryReason) && !capture} onClick={() => { clearSessionSelection(); setPreferredSnapshotId(null); if (!selectedHistoryWindow) setHistoryEnd(Date.now()); setMode("history"); onSelectTimeMode?.("history"); }}>History</button>
      <button disabled={mode !== "live"} aria-pressed={paused} onClick={() => { if (paused) firstPage(); setPaused(!paused); }}>{paused ? "Resume" : "Pause"}</button>
      <button disabled={loading || !visible} onClick={() => { manual.current = true; if (mode === "history" && !selectedHistoryWindow) setHistoryEnd(Date.now()); else { firstPage(); setRefresh(value => value + 1); } }}>Refresh now</button>
    </div></div>
    <div className="live-filters">
      <label>Database <select value={database} onChange={event => { const value = event.target.value; if (onSelectDatabase) onSelectDatabase(value === "" ? undefined : Number(value)); else setLocalDatabase(value); }}><option value="">All databases</option>{database !== "" && !databaseOptions.some(db => String(db.id) === database) ? <option value={database}>Database ID {database} · name unavailable</option> : null}{databaseOptions.map(db => <option key={db.id} value={db.id}>{db.name ?? `Database ${db.id}`}</option>)}</select></label>
      <label><input type="checkbox" checked={blocked} onChange={event => setBlocked(event.target.checked)} /> Blocked only</label>
      <label>Sort <select value={sort} onChange={event => setSort(event.target.value)}>{[["cpu", "CPU time"], ["memory", "Memory"], ["reads", "Reads"], ["writes", "Writes"], ["logicalReads", "Logical reads"], ["elapsed", "Elapsed time"], ["session", "Session ID"]].map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
      <label className="blocking-chain-select">Blocking chain (loaded page)<select value={blockingGroupKey} onChange={event => selectChain(event.target.value)}>
        <option value="">All loaded session rows</option>
        {blockingGroupKey !== "" && !selectedBlockingGroup ? <option value={blockingGroupKey}>Selected chain unavailable: {blockingGroupKey}</option> : null}
        {blockingGroups.map(group => <option key={group.key} value={group.key}>{group.rootLabel}</option>)}
      </select></label>
      <button className="secondary-button blocking-chain-clear" type="button" disabled={blockingGroupKey === ""} onClick={() => selectChain("")}>Clear chain filter</button>
    </div>
    <details className="supporting-evidence"><summary>Advanced session filters{login || application || status || idle || system ? " · filters applied" : ""}</summary><div className="live-filters">
      <label>Login <input maxLength={128} value={login} onChange={event => setLogin(event.target.value)} /></label>
      <label>Client application <input maxLength={128} value={application} onChange={event => setApplication(event.target.value)} /></label>
      <label>Status <input maxLength={60} value={status} onChange={event => setStatus(event.target.value)} /></label>
      <label><input type="checkbox" checked={idle} onChange={event => setIdle(event.target.checked)} /> Include idle sessions</label>
      <label><input type="checkbox" checked={system} onChange={event => setSystem(event.target.checked)} /> Include system sessions</label>
      <label><input type="checkbox" checked={descending} onChange={event => setDescending(event.target.checked)} /> Descending</label>
    </div></details>
    {mode === "history" && initialHistoryAtUtc && <p className="evidence-callout">Opened for the deadlock event at {timeLabel(initialHistoryAtUtc)}. Historical snapshots are minute-granular for cadence evidence; a deadlock-triggered capture is selected automatically when available.</p>}
    {sessionHistoryReason && mode === "live" && <p className="evidence-callout">{sessionHistoryReason} Showing current sessions.</p>}
    {mode === "history" && selectedHistoryWindow && <p className="evidence-callout">Session snapshots in the selected window: {timeLabel(selectedHistoryWindow.fromUtc)} to {timeLabel(selectedHistoryWindow.toUtc)}. Snapshots older than 24 hours are outside retention.</p>}
    {mode === "history" && <div className="toolbar">{!selectedHistoryWindow && <label>History window <select value={hours} onChange={event => setHours(Number(event.target.value))}><option value={0.25}>15 minutes</option><option value={1}>1 hour</option><option value={6}>6 hours</option><option value={24}>24 hours</option></select></label>}
      <button disabled={minute === undefined || minute <= earliestMinute} onClick={() => { if (minute !== undefined) selectMinute(minute - 1); }}>Previous minute</button>
      <label>Exact snapshot ({timeMode === "local" ? "local" : "UTC"}) <select value={minute ?? ""} onChange={event => selectMinute(Number(event.target.value))}><option value="" disabled>Select minute</option>{minuteOptions.map(value => <option key={value} value={value}>{timeLabel(snapshots.get(value)?.observedUtc ?? value * 60000)}{snapshots.has(value) ? "" : " · gap"}{snapshots.get(value)?.deadlockEventId ? " · deadlock-triggered" : ""}</option>)}</select></label>
      <button disabled={minute === undefined || minute >= latestMinute} onClick={() => { if (minute !== undefined) selectMinute(minute + 1); }}>Next minute</button></div>}
    {requested !== undefined && <div className="evidence-callout">
      {invalidSelection ? <p role="alert">The requested session selection is invalid for this server and time window. No current snapshot has been substituted.</p>
        : expiredSelection ? <p role="status">The requested session snapshot is outside the 24-hour retention window. No current snapshot has been substituted.</p>
        : <p>Selected capture: {capture!.snapshotId} · observed {timeLabel(capture!.observedUtc)}. This table is pinned to that captured snapshot; workspace Live/Rewind time mode is unchanged.</p>}
      <button className="secondary-button" type="button" onClick={clearSessionSelection}>Clear session selection</button>
    </div>}
    {currentRestoration?.loading && <p role="status">Restoring the requested session from at most 11 pages / 512 captured rows. Protected SQL is not loaded automatically.</p>}
    {currentRestoration?.message && <p className="empty-state" role="status">{currentRestoration.message}</p>}
    {selectionMessage && <p className="table-note" role="status">{selectionMessage}</p>}
    <p role="status">{requestMode === "history" ? "Historical evidence · automatic refresh paused" : paused ? "Paused" : workspacePaused ? "Workspace live updates paused" : !visible ? "Hidden · refresh paused" : cursor ? "Browsing snapshot pages · refresh paused" : "Refresh every 10 seconds"}{loading ? " · Refreshing…" : ""}. Observed: {gap ? "No snapshot in this minute" : displayPage?.observedUtc ? timeLabel(displayPage.observedUtc) : "Unavailable"}. Snapshot: {displayPage?.snapshotId ?? "Unavailable"}. {selectedSnapshot?.deadlockEventId ? "Deadlock-triggered snapshot." : ""} {displayPage?.state === "stale" || (displayPage !== undefined && displayError) ? "Stale evidence." : ""}</p>
    {displayError && <p role="alert">{displayError}</p>}{displayPage?.truncated && <p role="status">Collection was truncated at 512 rows. Missing sessions may exist.</p>}
    <p>Cumulative CPU is milliseconds, not CPU percentage. Reads, writes and logical reads are cumulative counts. Memory is session memory for idle sessions and granted request memory for active requests. Short requests between samples may not appear.</p>
    {displayPage && <BlockingSnapshot page={displayPage} groups={blockingGroups} selectedGroupKey={blockingGroupKey} onSelectGroup={selectChain} />}
    {displayPage && <p className="blocking-chain-counts" role="status">Showing {visibleRows.length} of {displayPage.rows.length} loaded session rows{blockingGroupKey !== "" ? ` · Blocking chain: ${selectedBlockingGroup?.rootLabel ?? blockingGroupKey}` : ""}. Chain filtering is local to this loaded page and preserves the server-side filters and row order. Other pages, excluded sessions, and truncated capture can contain additional chain members.</p>}
    {displayPage && blockingGroupKey !== "" && !selectedBlockingGroup ? <p role="status" className="empty-state">Selected blocking chain is not present in this loaded page. No rows match; clear the chain filter or browse another snapshot/page.</p> : null}
    {gap ? <p className="empty-state">No successful snapshot was captured in this minute.</p> : <div className="live-table-scroll" tabIndex={0} role="region" aria-label="Loaded session observations"><table><caption>{mode === "history" ? "Historical" : "Live"} sessions · loaded snapshot for {displayName}</caption><thead><tr>{["Session / request", "Database", "Login", "Client host / application", "Status / command", "CPU ms", "Memory bytes", "Logical reads", "Reads / writes", "Elapsed ms", "Wait / blocker", "Details"].map(title => <th key={title} scope="col">{title}</th>)}</tr></thead><tbody>{visibleRows.map(row => <tr key={row.identity}>
      <td>{row.sessionId} / {row.requestId ?? "idle"}</td><td>{row.databaseName ?? row.databaseId ?? "—"}</td><td>{row.login ?? "—"}</td><td>{row.clientHost ?? "—"}<br/>{row.application ?? "—"}</td><td>{row.status}<br/>{row.command}</td><td>{row.cpuMs}</td><td>{row.memoryBytes}</td><td>{row.logicalReads}</td><td>{row.reads} / {row.writes}</td><td>{row.elapsedMs}</td><td>{row.waitType ?? "—"} / {formatBlockingTarget(row.blocker)}</td><td><button type="button" aria-pressed={selection?.row.identity === row.identity && selection.snapshot === displayPage?.snapshotId} aria-controls={selection ? "session-details" : undefined} onClick={() => void open(row)}>Inspect {row.sessionId}/{row.requestId ?? "idle"}</button></td>
    </tr>)}</tbody></table>{displayPage && visibleRows.length === 0 && (blockingGroupKey === "" || selectedBlockingGroup) ? <p className="empty-state">{displayPage.state === "unavailable" ? "Collection evidence is unavailable for this observation." : "No sessions match these filters."}</p> : null}</div>}
    <nav aria-label="Live session pages"><button disabled={loading || cursorTrail.length === 0} onClick={() => { setCursor(cursorTrail.at(-1)); setCursorTrail(values => values.slice(0, -1)); }}>Previous page</button><button disabled={loading || gap || !displayPage?.nextCursor} onClick={() => { setCursorTrail(values => [...values, cursor]); setCursor(displayPage?.nextCursor ?? undefined); }}>Next page</button></nav>
    {selection && <aside id="session-details" className="live-details" aria-label="Session details"><div className="health-heading-row"><h4>Session {selection.row.sessionId} / {selection.row.requestId ?? "idle"}</h4><button type="button" onClick={() => {
      queryRequest.current?.abort(); setSelection(undefined); setDetail(undefined); setDetailError(undefined); setQueryLoading(false);
      if (capture) publishCapture({ snapshotId: capture.snapshotId, observedUtc: capture.observedUtc, ...(capture.chain ? { chain: capture.chain } : {}) });
    }}>Close details</button></div>
      <p>Observed {timeLabel(selection.observed)} on {displayName}. This observation is preserved when filters change or the request disappears.</p>
      {selectedDatabaseId !== undefined && selection.row.databaseId !== selectedDatabaseId ? <p className="table-note">This preserved observation is outside the current database context.</p> : null}
      <p>Database: {selection.row.databaseName ?? selection.row.databaseId ?? "unknown"}; login: {selection.row.login ?? "unknown"}; client: {selection.row.clientHost ?? "unknown"} / {selection.row.application ?? "unknown"}.</p>
      <p>Engine startup: {selection.row.engineStartup}; session login: {selection.row.sessionLogin}; request start: {selection.row.requestStart ?? "idle"} (SQL Server local timestamps).</p>
      <p>Cumulative CPU {selection.row.cpuMs} ms · memory {selection.row.memoryBytes} bytes · logical reads {selection.row.logicalReads} · reads {selection.row.reads} · writes {selection.row.writes} · elapsed {selection.row.elapsedMs} ms.</p>
      <p>{selection.row.delta ? `Changes over ${selection.row.delta.seconds}s: CPU +${selection.row.delta.cpuMs} ms; reads +${selection.row.delta.reads}; writes +${selection.row.delta.writes}; logical reads +${selection.row.delta.logicalReads}.` : "Interval changes unavailable: no matching complete baseline, restarted lifetime or reset counters."}</p>
      <h4>Captured executing SQL</h4><p role={canReadText && detailError ? "alert" : "status"}>{selection.row.queryId && !canReadText
        ? "Captured SQL is hidden. Query Text Reader access is required for this server."
        : <>{selection.row.queryState === "truncated" ? "Statement truncated to 16 KiB. " : ""}{detailError ?? visibleDetail?.state ?? (queryLoading ? "Loading protected query…" : selection.row.queryId ? "Metadata restored. Captured SQL requires a deliberate request and Query Text Reader access." : selection.row.queryState)}</>}</p>
      {selection.row.queryId && canReadText && <button type="button" disabled={queryLoading} onClick={() => void loadCapturedSql(selection)}>Load captured SQL</button>}
      {visibleDetail?.text && <pre className="query-text" tabIndex={0} aria-label="Captured executing SQL">{visibleDetail.text}</pre>}
    </aside>}
  </section>;
}

function BlockingSnapshot({ page, groups, selectedGroupKey, onSelectGroup }: { readonly page: LivePage; readonly groups: readonly BlockingTreeGroup[]; readonly selectedGroupKey: string; readonly onSelectGroup: (key: string) => void }) {
  if (groups.length === 0) return <p className="table-note">{page.state === "unavailable"
    ? "Blocking relationships are unavailable for this observation."
    : "No blocking relationships occur in these loaded session rows. Filters, paging, and collection bounds can exclude other relationships."}</p>;
  return <section className="blocking-summary" aria-labelledby="blocking-summary-heading"><div className="section-heading"><div><h4 id="blocking-summary-heading">Blocking tree · loaded snapshot</h4><p>Chains use only the selected snapshot, filters, and loaded page. A missing blocker may be on another page or excluded by filters{page.truncated ? ", or absent from truncated collection" : ""}.</p></div></div>
    {groups.map(group => <div className="blocking-tree-group" key={group.key}>
      <div className="blocking-tree-group-heading"><h5>{group.rootLabel}{group.key.startsWith("session:") ? group.rootLoaded ? " · row loaded" : " · row not loaded" : ""}</h5>
        <button type="button" className="secondary-button blocking-chain-action" aria-label={`Filter chain: ${group.rootLabel}`} aria-pressed={selectedGroupKey === group.key} onClick={() => onSelectGroup(group.key)}>Filter chain</button>
      </div>
      {group.issue && <p>{group.issue}</p>}
      {group.nodes.length > 0 && <ul>{group.nodes.map(node => <BlockingTreeBranch key={node.sessionId} node={node} />)}</ul>}
      {group.unresolvedRows.length > 0 && <ul>{group.unresolvedRows.map(row => <li key={row.identity}>Session {row.sessionId}/{row.requestId ?? "idle"} waits on {formatBlockingTarget(row.blocker)}<span>{row.waitType ?? "Wait type unavailable"}</span></li>)}</ul>}
    </div>)}
  </section>;
}

function BlockingTreeBranch({ node }: { readonly node: BlockingTreeNode }) {
  return <li><strong>Session {node.sessionId}</strong><span>{node.rows.map(row => `${row.requestId ?? "idle"}: ${row.waitType ?? "wait unavailable"}`).join(" · ")}</span>
    {node.children.length > 0 && <ul>{node.children.map(child => <BlockingTreeBranch key={child.sessionId} node={child} />)}</ul>}
  </li>;
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
