import { useEffect, useLayoutEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { useRetainedResourceFiles } from "../src/features/resources/useRetainedResourceFiles";
import { readResourceSelection } from "../src/features/resources/resourceSelectionModel";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import { readOverviewScope } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "./harness.css";

declare global { interface Window { resourceFileCommits: { context: string; text: string }[]; } }
const alpha = "11111111-1111-4111-8111-000000000001";
// This projection qualifies the legacy current-files API and its retained cursor
// contract only. The dashboard now uses the completion-window history source.
function LegacyFileProjection({scope,revision,refresh}:{scope:OverviewScope;revision:number;refresh:number}) {
  const selected=readResourceSelection(location.hash,scope.target||alpha).file;
  const context=JSON.stringify([scope.target||alpha,revision,scope.range,scope.from,scope.to,scope.databaseId]);
  const result=useRetainedResourceFiles(scope.target||alpha,context,refresh,Boolean(selected));
  const page=result.files,rows=page?.items.filter(file=>scope.databaseId===undefined||file.databaseId===scope.databaseId)??[];
  const selectedRow=rows.find(file=>file.databaseId===selected?.databaseId&&file.fileId===selected.fileId);
  function inspect(databaseId:number,fileId:number){const params=new URLSearchParams(location.hash.split('?')[1]);params.set('resourceDatabaseId',String(databaseId));params.set('resourceFileId',String(fileId));location.hash=`resources?${params}`;}
  return <section className="panel" aria-label="Database file I/O"><h2>Legacy database file I/O</h2>
    <p>This test-only view qualifies the legacy current-files endpoint, not shared-window dashboard history.</p>
    {result.filesLoading&&<p role="status">Reading file counters; received evidence remains visible.</p>}
    {result.fileError&&<p role="alert">{result.fileError} The investigation has not advanced; retry navigation or return to latest.</p>}
    {(result.retained||result.fileError)&&<button type="button" disabled={result.filesLoading} onClick={result.returnToLatestFiles}>Return to latest file snapshot</button>}
    {page&&<><p>Page {result.filePageIndex+1} · {rows.length} loaded files. Repository time: {page.repositoryTimeUtc}</p>
      <p>Repository check times may differ between pages. Received counters do not establish continuous evidence.</p>
      {selected&&<p>{selectedRow?`Selected file: database ID ${selected.databaseId}, file ID ${selected.fileId} · ${selectedRow.logicalName}`:`File ID ${selected.fileId} (name unavailable in the loaded snapshot). Its identity is retained; no other file has been selected.`}</p>}
      <div className="screen-actions"><button type="button" disabled={result.filesLoading||result.filePageIndex===0} onClick={()=>void result.navigateFilePage(0)}>First file page</button>
        <button type="button" disabled={result.filesLoading||result.filePageIndex===0} onClick={()=>void result.navigateFilePage(result.filePageIndex-1)}>Previous file page</button>
        <button type="button" disabled={result.filesLoading||!page.nextCursor} onClick={()=>void result.navigateFilePage(result.filePageIndex+1,page.nextCursor??undefined)}>Next file page</button></div>
      {rows.length?<div className="table-scroll" tabIndex={0} role="region" aria-label="Loaded database file counters"><table style={{minWidth:1500}}><caption>Retained SQL Server file counters</caption><thead><tr><th scope="col">File</th><th scope="col">Size bytes</th><th scope="col">Read operations</th><th scope="col">Write operations</th><th scope="col">Inspect</th></tr></thead><tbody>{rows.map(file=><tr key={`${file.databaseId}:${file.fileId}`}><th scope="row">{file.logicalName}</th><td>{file.sizeBytes}</td><td>{file.readCount}</td><td>{file.writeCount}</td><td><button type="button" aria-label={`View read and write latency trend for database ${file.databaseId}, file ${file.logicalName}`} onClick={()=>inspect(file.databaseId,file.fileId)}>View read and write latency trend</button></td></tr>)}</tbody></table></div>:<p>No file rows for the selected database</p>}
    </>}
  </section>;
}
function Harness() {
  const [hash, setHash] = useState(location.hash);
  const [revision, setRevision] = useState(1);
  const [refresh, setRefresh] = useState(0);
  const [mounted, setMounted] = useState(true);
  const { mode, setMode } = useTimeDisplay();
  const scope = readOverviewScope(hash);
  useEffect(() => { const changed = () => setHash(location.hash); window.addEventListener("hashchange", changed); return () => window.removeEventListener("hashchange", changed); }, []);
  useLayoutEffect(() => {
    window.resourceFileCommits ??= [];
    window.resourceFileCommits.push({ context: JSON.stringify([scope.target, revision, scope.from, scope.to, scope.databaseId]),
      text: document.querySelector('[aria-label="Database file I/O"]')?.textContent ?? "" });
  });
  function change(key: string, value: string) {
    const parameters = new URLSearchParams(location.hash.split("?")[1]);
    if (value) parameters.set(key, value); else parameters.delete(key);
    if (key === "target") { parameters.delete("resourceDatabaseId"); parameters.delete("resourceFileId"); }
    location.hash = `resources?${parameters}`; setHash(location.hash);
  }
  return <main className="component-harness">
    <h1>Retained resource-file evidence harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={scope.target || alpha} onChange={event => change("target", event.target.value)}>
        <option value={alpha}>Alpha</option><option value="11111111-1111-4111-8111-000000000002">Beta</option>
      </select></label>
      <label>Target revision<select value={revision} onChange={event => setRevision(Number(event.target.value))}><option value={1}>Revision 1</option><option value={2}>Revision 2</option></select></label>
      <label>Database scope<select value={scope.databaseId ?? ""} onChange={event => change("databaseId", event.target.value)}><option value="">All databases</option><option value={5}>Database 5</option><option value={6}>Database 6</option></select></label>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}><option value="utc">UTC</option><option value="local">Local</option></select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Advance workspace poll</button>
      <button type="button" onClick={() => change("to", "2026-10-02T11:00:00.000Z")}>Change shared window</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount resources" : "Mount resources"}</button>
    </div>
    {mounted && <LegacyFileProjection scope={scope} revision={revision} refresh={refresh} />}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
