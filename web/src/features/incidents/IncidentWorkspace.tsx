import { useEffect, useId, useRef, useState } from "react";
import { getSavedIncident, IncidentRequestError, saveIncident } from "./incidentApi.ts";
import { incidentSelection, prepareIncident, savedIncidentHref, validateIncidentWindow } from "./incidentModel.ts";
import type { IncidentWindow, SaveIncidentCommand, SavedIncident } from "./incidentModel.ts";
import { SavedIncidentView } from "./SavedIncidentView.tsx";
import { SavedIncidentList } from "./SavedIncidentList.tsx";
import { overviewHref } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";

interface IncidentDraft {
  readonly context: string; readonly id: number; readonly window: IncidentWindow; readonly revision: number;
}
interface IncidentWrite {
  readonly controller: AbortController; readonly identity: string; readonly hash: string; readonly draftId: number;
}
const byteLength = (value: string) => new TextEncoder().encode(value).length;

export function IncidentWorkspace({ targetId, targetRevision, window, canSave, canRead, scope, selectionHash, formatTime, compact = false }: {
  readonly targetId: string; readonly targetRevision: number; readonly window?: IncidentWindow;
  readonly canSave: boolean; readonly canRead: boolean; readonly scope: OverviewScope; readonly selectionHash: string;
  readonly formatTime: (value: string) => string; readonly compact?: boolean;
}) {
  const selectedId = incidentSelection(selectionHash);
  const binding = JSON.stringify([targetId.toLowerCase(), targetRevision]);
  const bindingRef = useRef({ value: binding, generation: 0 });
  if (bindingRef.current.value !== binding) bindingRef.current = { value: binding, generation: bindingRef.current.generation + 1 };
  const context = JSON.stringify([binding, bindingRef.current.generation]);
  // Fence a revoke/regrant as well as page/time/database changes. The moving
  // effective Live window is deliberately absent from this raw route identity.
  const readWriteAccess = JSON.stringify([canSave, canRead]);
  const role = useRef({ value: readWriteAccess, generation: 0 });
  if (role.current.value !== readWriteAccess) role.current = { value: readWriteAccess, generation: role.current.generation + 1 };
  const identity = JSON.stringify([context, selectionHash, role.current.generation]);
  const latest = useRef(identity); latest.current = identity;
  const [draftState, setDraft] = useState<IncidentDraft>();
  const draft = draftState?.context === context ? draftState : undefined;
  const draftRef = useRef(draft); draftRef.current = draft;
  const sequence = useRef(0);
  const [title, setTitle] = useState(""); const [notes, setNotes] = useState("");
  const [command, setCommand] = useState<SaveIncidentCommand>();
  const [pendingId, setPendingId] = useState<number>();
  const pending = !!draft && pendingId === draft.id;
  const [saveError, setSaveError] = useState<{ context: string; message: string; retryable: boolean }>();
  const currentError = saveError?.context === context ? saveError : undefined;
  const write = useRef<IncidentWrite | null>(null);
  const opener = useRef<HTMLButtonElement>(null);
  const titleInput = useRef<HTMLInputElement>(null);
  const restoreFocus = useRef(false);
  const headingId = useId(); const titleHelp = useId(); const notesHelp = useId();
  const Heading = compact ? "h2" : "h4";
  const [loaded, setLoaded] = useState<{ key: string; value: SavedIncident }>();
  const [readError, setReadError] = useState<{ key: string; message: string }>();
  const [reload, setReload] = useState(0);
  const readKey = JSON.stringify([identity, selectedId, reload]);
  const latestRead = useRef(readKey); latestRead.current = readKey;
  const stillHere = () => latest.current === identity && (typeof location === "undefined" || location.hash === selectionHash);
  const mutationHere = () => {
    const parameters = new URLSearchParams(selectionHash.split("?")[1]);
    return canRead && stillHere() && window !== undefined && parameters.getAll("target").length === 1 &&
      parameters.get("target")?.toLowerCase() === targetId.toLowerCase() &&
      !["range", "from", "to", "databaseId", "compare"].some(key => parameters.getAll(key).length > 1);
  };
  const close = () => { if (stillHere()) location.hash = overviewHref(scope, "health", targetId); };

  useEffect(() => {
    setDraft(value => value?.context === context ? value : undefined);
    setCommand(undefined); setSaveError(undefined); setPendingId(undefined);
  }, [context]);
  useEffect(() => {
    const active = write.current;
    if (active && active.identity !== identity) {
      active.controller.abort(); write.current = null; setPendingId(undefined);
      if (draftRef.current?.id === active.draftId) setSaveError({ context,
        message: "The workspace changed before the save was verified. Retry the same save to check its outcome, or close this frozen draft.", retryable: true });
    }
  }, [identity, context]);
  useEffect(() => () => write.current?.controller.abort(), []);
  useEffect(() => {
    if (!selectedId || !canRead) return;
    const controller = new AbortController();
    void getSavedIncident(targetId, selectedId, controller.signal)
      .then(value => { if (!controller.signal.aborted && latestRead.current === readKey) setLoaded({ key: readKey, value }); })
      .catch(error => { if (!controller.signal.aborted && latestRead.current === readKey) setReadError({ key: readKey,
        message: error instanceof IncidentRequestError ? error.message : "This investigation is unavailable." }); });
    return () => controller.abort();
  }, [targetId, selectedId, readKey, canRead]);
  useEffect(() => {
    if (draft && canSave && selectedId === undefined) titleInput.current?.focus();
    else if (restoreFocus.current) { restoreFocus.current = false; opener.current?.focus(); }
  }, [draft?.id]);

  const open = () => {
    if (!mutationHere() || !canSave || !window || !Number.isSafeInteger(targetRevision) || targetRevision < 1) return;
    try {
      validateIncidentWindow(window, Date.now());
      setDraft({ context, id: ++sequence.current, window: { ...window }, revision: targetRevision }); setTitle(""); setNotes("");
      setCommand(undefined); setSaveError(undefined);
    } catch (error) { setSaveError({ context, message: error instanceof Error ? error.message : "Select a past window.", retryable: false }); }
  };
  const closeDraft = () => {
    if (!stillHere() || pending) return;
    restoreFocus.current = true; setDraft(undefined); setCommand(undefined); setSaveError(undefined);
  };
  const submit = async () => {
    if (!mutationHere() || !draft || !canSave || write.current || draftRef.current?.id !== draft.id) return;
    let exact: SaveIncidentCommand;
    try { exact = command ?? prepareIncident(draft.window, draft.revision, title, notes); }
    catch (error) { setSaveError({ context, message: error instanceof Error ? error.message : "Check the investigation details.", retryable: false }); return; }
    // Dispatch once; retries keep the exact immutable command and operation ID.
    const active: IncidentWrite = { controller: new AbortController(), identity, hash: selectionHash, draftId: draft.id };
    write.current = active; setCommand(exact); setPendingId(draft.id); setSaveError(undefined);
    const validCompletion = () => !active.controller.signal.aborted && latest.current === active.identity &&
      draftRef.current?.id === active.draftId && (typeof location === "undefined" || location.hash === active.hash);
    try {
      const result = await saveIncident(targetId, exact, active.controller.signal);
      if (validCompletion()) { setDraft(undefined); setCommand(undefined); location.hash = savedIncidentHref(result, scope); }
    } catch (error) {
      if (validCompletion()) setSaveError({ context, message: error instanceof Error ? error.message : "The save outcome is unknown.",
        retryable: error instanceof IncidentRequestError && error.retryable });
    } finally {
      if (write.current === active) { write.current = null; setPendingId(undefined); }
    }
  };
  const className = `incident-workspace${compact ? " incident-workspace-compact" : ""}`;

  if (!canRead) return <section className={`${className} panel`}>
    <p role="status">Viewer, Operator or Target Administrator access is required to read and export this saved investigation.</p>
    {selectedId !== undefined ? <button type="button" onClick={close}>Close investigation</button> : null}
  </section>;
  if (selectedId === null) return <section className={`${className} panel`}><p role="alert">The incident link is invalid. Open a saved investigation using its original link.</p><button type="button" onClick={close}>Close investigation</button></section>;
  if (selectedId !== undefined) {
    const current = loaded?.key === readKey ? loaded.value : undefined;
    if (current) return <SavedIncidentView incident={current} canRead={canRead} compact={compact} formatTime={formatTime} href={savedIncidentHref(current, scope)} onClose={close} />;
    const error = readError?.key === readKey ? readError : undefined;
    return <section className={`${className} panel`}><p role={error ? "alert" : "status"}>{error ? error.message : "Loading saved investigation…"}</p>
      {error && <button type="button" onClick={() => { if (stillHere()) setReload(value => value + 1); }}>Retry loading</button>}
      <button type="button" onClick={close}>Close investigation</button></section>;
  }
  if (!draft || !canSave) return <div className={className}>
    {canSave ? <div className="incident-actions"><button ref={opener} type="button" disabled={!window || !Number.isSafeInteger(targetRevision) || targetRevision < 1} onClick={open}>Open incident</button>
      <small>Save the selected window, notes and existing server-wide analytics evidence.</small>{currentError && <p role="alert">{currentError.message}</p>}</div>
      : <p className="evidence-footer">Operator or Target Administrator access unlocks saving an incident for this server.</p>}
    <SavedIncidentList key={context} targetId={targetId} targetRevision={targetRevision} compact={compact} scope={scope} formatTime={formatTime}/>
  </div>;
  const locked = pending || command !== undefined;
  return <form className={`${className} panel incident-form`} aria-labelledby={headingId} onSubmit={event => { event.preventDefault(); void submit(); }}>
    <Heading id={headingId}>Open incident</Heading><p>Save window: {formatTime(draft.window.fromUtc)} to {formatTime(draft.window.toUtc)}. This draft stays frozen on this window as live charts refresh or the workspace time changes.</p>
    <label>Incident title<input ref={titleInput} required value={title} disabled={locked} aria-describedby={titleHelp} onChange={event => setTitle(event.target.value)} /></label>
    <small id={titleHelp}>{byteLength(title)} / 200 UTF-8 bytes. Use a plain, single-line title.</small>
    <label>Notes<textarea value={notes} disabled={locked} aria-describedby={notesHelp} onChange={event => setNotes(event.target.value)} rows={4} /></label>
    <small id={notesHelp}>{byteLength(notes)} / 8192 UTF-8 bytes.</small>
    <p className="evidence-footer">The capture includes existing server-wide analytics evidence packets. A selected database controls navigation, not the captured evidence. Empty or partial coverage stays visible.</p>
    {currentError && <p role="alert">{currentError.message}</p>}
    {!window && <p role="status">Choose an unambiguous server and past time context before saving this frozen draft.</p>}
    <div className="toolbar">
      <button type="submit" disabled={!window || pending || command !== undefined && currentError?.retryable !== true}>{pending ? "Saving…" : command ? "Retry same save" : "Save incident"}</button>
      <button type="button" disabled={pending} onClick={closeDraft}>Close draft</button>
    </div>
    {command && !pending && <p role="status">The request keeps the same window, notes and operation ID when retried. Closing an unverified draft does not undo a save that may have committed.</p>}
  </form>;
}
