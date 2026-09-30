import { useEffect, useRef, useState } from "react";
import { getSavedIncident, IncidentRequestError, saveIncident } from "./incidentApi.ts";
import { incidentSelection, prepareIncident, savedIncidentHref, validateIncidentWindow } from "./incidentModel.ts";
import type { IncidentWindow, SaveIncidentCommand, SavedIncident } from "./incidentModel.ts";
import { SavedIncidentView } from "./SavedIncidentView.tsx";
import { overviewHref } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";

export function IncidentWorkspace({ targetId, targetRevision, window, canSave, scope, selectionHash, formatTime }: {
  readonly targetId: string; readonly targetRevision: number; readonly window?: IncidentWindow;
  readonly canSave: boolean; readonly scope: OverviewScope; readonly selectionHash: string;
  readonly formatTime: (value: string) => string;
}) {
  const selectedId = incidentSelection(selectionHash);
  const [draft, setDraft] = useState<{ window: IncidentWindow; revision: number }>();
  const [title, setTitle] = useState(""); const [notes, setNotes] = useState("");
  const [command, setCommand] = useState<SaveIncidentCommand>();
  const [pending, setPending] = useState(false);
  const [saveError, setSaveError] = useState<{ message: string; retryable: boolean }>();
  const write = useRef<AbortController | null>(null);
  const [loaded, setLoaded] = useState<SavedIncident>();
  const [readError, setReadError] = useState<{ id: string; message: string }>();
  const [reload, setReload] = useState(0);
  const close = () => { location.hash = overviewHref(scope,"health",targetId); };

  useEffect(() => {
    if (write.current) { write.current.abort(); write.current=null; setPending(false); }
    return () => { write.current?.abort(); };
  },[targetId,selectedId]);
  useEffect(() => {
    setLoaded(undefined); setReadError(undefined);
    if (!selectedId) return;
    const controller = new AbortController();
    void getSavedIncident(targetId,selectedId,controller.signal)
      .then(value => { if (!controller.signal.aborted) setLoaded(value); })
      .catch(error => { if (!controller.signal.aborted) setReadError({ id: selectedId,
        message: error instanceof IncidentRequestError ? error.message : "This investigation is unavailable." }); });
    return () => controller.abort();
  },[targetId,selectedId,reload]);

  const open = () => {
    if (!window) return;
    try {
      validateIncidentWindow(window,Date.now());
      setDraft({ window: { ...window }, revision: targetRevision }); setTitle(""); setNotes("");
      setCommand(undefined); setSaveError(undefined);
    } catch (error) { setSaveError({ message: error instanceof Error ? error.message : "Select a past window.", retryable: false }); }
  };
  const submit = async () => {
    if (!draft || !canSave || write.current) return;
    let exact: SaveIncidentCommand;
    try { exact=command ?? prepareIncident(draft.window,draft.revision,title,notes); }
    catch (error) { setSaveError({ message: error instanceof Error ? error.message : "Check the investigation details.",retryable:false }); return; }
    // Set the controller before yielding so double clicks cannot dispatch a
    // second mutation. Retries retain this immutable command and operation ID.
    const controller=new AbortController(); write.current=controller;
    setCommand(exact); setPending(true); setSaveError(undefined);
    try {
      const result=await saveIncident(targetId,exact,controller.signal);
      if (!controller.signal.aborted) { setDraft(undefined); setCommand(undefined); location.hash=savedIncidentHref(result,scope); }
    } catch (error) {
      if (!controller.signal.aborted) setSaveError({ message: error instanceof Error ? error.message : "The save outcome is unknown.",
        retryable: error instanceof IncidentRequestError && error.retryable });
    } finally {
      if (write.current===controller) { write.current=null; setPending(false); }
    }
  };

  if (selectedId === null) return <section className="panel"><p role="alert">The incident link is invalid. Open a saved investigation using its original link.</p><button type="button" onClick={close}>Close investigation</button></section>;
  if (selectedId !== undefined) {
    const current=loaded?.targetId.toLowerCase()===targetId.toLowerCase() && loaded.threadId.toLowerCase()===selectedId ? loaded : undefined;
    if (current) return <SavedIncidentView incident={current} formatTime={formatTime} href={savedIncidentHref(current,scope)} onClose={close} />;
    return <section className="panel"><p role={readError?.id===selectedId ? "alert" : "status"}>{readError?.id===selectedId ? readError.message : "Loading saved investigation…"}</p>
      {readError?.id===selectedId && <button type="button" onClick={() => setReload(value => value+1)}>Retry loading</button>}
      <button type="button" onClick={close}>Close investigation</button></section>;
  }
  if (!canSave) return <p className="evidence-footer">Operator or Target Administrator access unlocks saving an incident for this server.</p>;
  if (!draft) return <div className="incident-actions"><button type="button" disabled={!window || targetRevision<1} onClick={open}>Open incident</button>
    <small>Save the selected window, notes and existing analytics evidence.</small>{saveError && <p role="alert">{saveError.message}</p>}</div>;
  const locked=pending || command!==undefined;
  return <form className="panel incident-form" onSubmit={event => { event.preventDefault(); void submit(); }}>
    <h4>Open incident</h4><p>Save window: {formatTime(draft.window.fromUtc)} to {formatTime(draft.window.toUtc)}. This draft stays on this window as live charts refresh.</p>
    <label>Incident title<input required value={title} disabled={locked} onChange={event => setTitle(event.target.value)} /></label>
    <label>Notes<textarea value={notes} disabled={locked} onChange={event => setNotes(event.target.value)} rows={4} /></label>
    <small>The capture includes existing analytics evidence packets. Empty or partial coverage stays visible.</small>
    {saveError && <p role="alert">{saveError.message}</p>}
    <div className="toolbar">
      <button type="submit" disabled={pending || command!==undefined && saveError?.retryable!==true}>{pending ? "Saving…" : command ? "Retry same save" : "Save incident"}</button>
      <button type="button" disabled={pending} onClick={() => { setDraft(undefined); setCommand(undefined); setSaveError(undefined); }}>Close draft</button>
    </div>
    {command && !pending && <p role="status">The request keeps the same window, notes and operation ID when retried. Closing an unverified draft does not undo a save that may have committed.</p>}
  </form>;
}
