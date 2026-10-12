import { useId,useLayoutEffect,useRef } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { lifetimeAverageStallMilliseconds } from "./resourceFileModel.ts";
import type { ResourceFileSelection } from "./resourceSelectionModel.ts";
import type { FileSnapshotSelection, HistoricalDatabaseFile } from "./databaseFileWindowHistoryModel.ts";
import type { DatabaseFileWindowHistoryRead } from "./useDatabaseFileWindowHistory.ts";
import "./databaseFileWindowHistory.css";

const labels:Readonly<Record<string,string>>={observed_files:"Retained file facts",observed_empty:"Complete empty snapshot",collection_failed:"Collection failed",collection_loss:"Collection lost evidence",collection_gap:"Collection gap",source_schema_unsupported:"Source schema unsupported",payload_missing:"File payload unavailable",stored_count_mismatch:"Stored file count mismatch",source_clock_skew:"Source clock differs from the receipt",snapshot_not_retained:"Exact snapshot is not retained",snapshot_outside_window:"Exact snapshot is outside the shared window"};
const bytes=(value:string|null)=>value===null?"Unlimited or unknown; source does not distinguish":`${value} bytes`;
function growth(file:HistoricalDatabaseFile){return file.growthMode==="disabled"?"Autogrowth disabled":file.growthMode==="percent"?`${file.growthPercent}% per growth event`:file.growthMode==="fixed_bytes"?`${file.growthBytes} bytes per growth event`:`Ambiguous retained values: ${file.growthBytes} bytes and ${file.growthPercent}%`;}
export function DatabaseFileWindowHistoryPanel({history,windowError,selectionInvalid=false,explicitSelection,selectedFile,databaseId,onSelectReceipt,onStartInvestigation,onSelectFile}:{
  readonly history:DatabaseFileWindowHistoryRead;readonly windowError?:string;readonly selectionInvalid?:boolean;
  readonly explicitSelection?:FileSnapshotSelection;readonly selectedFile?:ResourceFileSelection;readonly databaseId?:number;
  readonly onSelectReceipt:(selection:FileSnapshotSelection|undefined)=>void;
  readonly onStartInvestigation:(selection?:FileSnapshotSelection)=>void;
  readonly onSelectFile:(file:ResourceFileSelection,receipt:FileSnapshotSelection)=>void;
}){
  const formatTime=useTimeFormatter(),id=useId();
  const list=history.list,page=list.pages[list.index],snapshot=history.snapshot,filePage=snapshot.pages[snapshot.index],receipt=filePage?.receipt;
  const root=useRef<HTMLElement|null>(null),recoveryFocus=useRef<{source:"list"|"snapshot";context:string;selection:string;waiting:boolean}|undefined>(undefined);
  function recover(source:"list"|"snapshot",action:()=>void){recoveryFocus.current={source,context:history.contextKey,selection:history.selectionIdentity,waiting:false};action();}
  useLayoutEffect(()=>{
    const request=recoveryFocus.current;if(!request)return;
    if(request.context!==history.contextKey){recoveryFocus.current=undefined;return;}
    if(history.denied){root.current?.querySelector<HTMLButtonElement>('button[data-file-access-retry]')?.focus();recoveryFocus.current=undefined;return;}
    if(request.source==="snapshot"&&request.selection!==history.selectionIdentity){recoveryFocus.current=undefined;return;}
    const read=request.source==="list"?list:snapshot,cancel=root.current?.querySelector<HTMLButtonElement>(`button[data-file-source-cancel="${request.source}"]`);
    if(!request.waiting){
      if(read.loading){cancel?.focus();request.waiting=true;}
      else{root.current?.querySelector<HTMLButtonElement>(`button[data-file-source-refresh="${request.source}"]`)?.focus();recoveryFocus.current=undefined;}
    }else if(!read.loading){
      if(document.activeElement===cancel||document.activeElement===document.body)root.current?.querySelector<HTMLButtonElement>(`button[data-file-source-refresh="${request.source}"]`)?.focus();
      recoveryFocus.current=undefined;
    }
  },[list.loading,snapshot.loading,history.contextKey,history.selectionIdentity,history.denied]);
  const files=filePage?.items.filter(file=>databaseId===undefined||file.databaseId===databaseId)??[];
  const selected=selectedFile?snapshot.pages.flatMap(p=>p.items).find(f=>f.databaseId===selectedFile.databaseId&&f.fileId===selectedFile.fileId):undefined;
  const pin=()=>onStartInvestigation(history.selected??undefined);
  function controls(source:"list"|"snapshot",name:string,max:number){const read=source==="list"?list:snapshot,current=read.pages[read.index];return <div className="file-history-actions" role="group" aria-label={`${name} read controls`}>
    <button type="button" data-file-source-refresh={source} disabled={read.loading||Boolean(windowError)||selectionInvalid&&source==="snapshot"} onClick={()=>history.refresh(source)}>Refresh {name}</button>
    <button type="button" data-file-source-cancel={source} disabled={!read.loading} onClick={()=>recover(source,()=>history.cancel(source))}>Cancel {name} read</button>
    {read.error&&<button type="button" disabled={read.loading} onClick={()=>recover(source,()=>history.retry(source))}>Retry {name} read</button>}
    <button type="button" disabled={read.loading||read.index===0} onClick={()=>{pin();history.navigate(source,0);}}>First {name} page</button>
    <button type="button" disabled={read.loading||read.index===0} onClick={()=>{pin();history.navigate(source,read.index-1);}}>Previous {name} page</button>
    <button type="button" disabled={read.loading||!current||read.index+1>=read.pages.length&&(!current.nextCursor||read.pages.length>=max)} onClick={()=>{pin();history.navigate(source,read.index+1);}}>Next {name} page</button>
  </div>;}
  return <section ref={root} className="panel database-file-window-history" aria-labelledby={id}>
    <h3 id={id}>Database file allocation and autogrowth history</h3>
    <p>Receipts follow collection completion in the shared window. The most recent attempt is selected first, including failures and missing payloads. Configuration and cumulative I/O facts describe one received snapshot; they do not establish file incarnation, growth cause, free space or future capacity.</p>
    {windowError&&<p role="alert">{windowError}</p>}
    {history.denied?<div role="region" aria-label="Database file evidence access"><p role="alert">Database file evidence access was denied. Received receipts, file facts and cached pages were removed.</p><button type="button" data-file-access-retry onClick={()=>recover("list",history.retryAccess)}>Retry database file access</button></div>:<>
      <section aria-label="File collection receipts" aria-busy={list.loading}>
        <h4>File collection receipts</h4>
        {list.loading&&<p role="status">{page?"Refreshing file receipts; received investigation remains visible until a validated replacement arrives…":"Loading file collection receipts…"}</p>}
        {list.error&&<p role="alert">{list.error}</p>}
        {controls("list","file receipts",20)}
        {page&&<>
          <p className="file-history-receipt">Received window {formatTime(page.fromUtc)} to {formatTime(page.toUtc)} · cutoff {formatTime(page.cutoffUtc)} · checked {formatTime(page.repositoryTimeUtc)}. Page {list.index+1}; {list.pages.reduce((n,p)=>n+p.items.length,0)} received receipts.</p>
          <p role="status">{page.state} · {page.coverage.runCount} completed attempts; {page.coverage.completeRunCount} complete, {page.coverage.gapRunCount} gaps. {page.coverage.hasVisibilityGap?"Collection, revision, timing or retention gaps prevent a complete view.":"Coverage describes retained sampled evidence, not a health verdict."}</p>
          {page.items.length===0?<p role="status">No completed file collection attempts are retained in this window.</p>:<details className="file-history-browser"><summary>Browse file collection receipts ({page.items.length} on this page)</summary><div className="table-scroll" tabIndex={0} role="region" aria-label="File collection receipt table"><table><caption>Actual attempts, newest completion first; failed attempts are not skipped</caption><thead><tr><th scope="col">Completion</th><th scope="col">Source revision</th><th scope="col">Result</th><th scope="col">Retained / expected files</th><th scope="col">Inspect</th></tr></thead><tbody>{page.items.map(item=><tr key={item.runId}><td><time dateTime={item.completedAtUtc}>{formatTime(item.completedAtUtc)}</time><small>{item.runId}</small></td><td>{item.sampleTargetRevision}</td><td>{item.outcome} · {labels[item.reason]??item.reason}<small>Loss: {item.lossKind??"unknown"}; minimum lost: {item.minimumLostItems??"unknown"}</small></td><td>{item.storedFileCount} / {item.expectedFileCount??"unknown"}</td><td><button type="button" onClick={()=>onSelectReceipt(item)} aria-pressed={history.selected?.runId===item.runId&&history.selected?.sampleTargetRevision===item.sampleTargetRevision}>Inspect file receipt {item.runId}</button></td></tr>)}</tbody></table></div></details>}
        </>}
      </section>
      <section aria-label="Selected file snapshot" aria-busy={snapshot.loading}>
        <h4>Selected file snapshot</h4>
        {selectionInvalid&&<p role="alert">The requested file snapshot identity is invalid. No other receipt has been selected.</p>}
        {(explicitSelection||selectionInvalid)&&<button type="button" onClick={()=>onSelectReceipt(undefined)}>Clear file snapshot selection</button>}
        {history.selected&&<p className="file-history-receipt">Exact run {history.selected.runId} · sample revision {history.selected.sampleTargetRevision}. {explicitSelection?"Pinned investigation; polling does not choose another receipt.":"Default: the most recent received attempt, without skipping failures."}</p>}
        {snapshot.loading&&<p role="status">{filePage?"Reading file facts; the received snapshot remains visible until a validated response arrives…":"Loading exact file snapshot…"}</p>}
        {snapshot.error&&<p role="alert">{snapshot.error}</p>}
        {snapshot.errorStatus===422&&snapshot.attempt?.limit===100&&<button type="button" disabled={snapshot.loading} onClick={()=>recover("snapshot",history.smallerFilePages)}>Use smaller file pages</button>}
        {history.selected&&controls("snapshot","file snapshot",10000/(snapshot.limit??100))}
        {!history.selected&&!selectionInvalid&&!list.loading&&<p role="status">No receipt is available for exact file facts in this window.</p>}
        {filePage&&<>
          <p role="status">{filePage.state} · {labels[filePage.reason]??filePage.reason}. {receipt?`${receipt.storedFileCount} retained file rows; ${receipt.expectedFileCount??"unknown"} expected. Completion ${formatTime(receipt.completedAtUtc)}.`:"No exact receipt payload is available; the requested identity is retained without replacement."}</p>
          {filePage.state!=="available"&&<p>{["snapshot_not_retained","snapshot_outside_window","payload_missing"].includes(filePage.reason)?"Verify the pinned run and sample revision, select a retained completion window, or choose another receipt explicitly. Refresh cannot recreate missing historical payload.":"Ask a Target Administrator to check database.files permissions, SQL Server connectivity and collection loss. Refresh reads retained evidence; it does not repair a past collection attempt."}</p>}
          {receipt&&<details><summary>File source receipt and loss details</summary><dl className="health-facts"><div><dt>Collection result</dt><dd>{receipt.outcome}</dd></div><div><dt>Loss</dt><dd>{receipt.lossKind??"Unknown"} · minimum {receipt.minimumLostItems??"unknown"} items</dd></div><div><dt>Source observed range</dt><dd>{formatTime(receipt.firstObservedAtUtc)} to {formatTime(receipt.lastObservedAtUtc)}</dd></div><div><dt>Source collected range</dt><dd>{formatTime(receipt.firstCollectedAtUtc)} to {formatTime(receipt.lastCollectedAtUtc)}</dd></div></dl><p className="file-history-receipt">Cutoff {formatTime(filePage.cutoffUtc)} · checked {formatTime(filePage.repositoryTimeUtc)} · content epoch {filePage.readEpoch}. Epoch describes retained content, not physical file continuity.</p></details>}
          {selectedFile&&<><p>Selected database ID {selectedFile.databaseId}, file ID {selectedFile.fileId}: {selected?.logicalName??"Name unavailable in received exact pages"}.</p>{!selected&&<p role="status">This exact file is not in received pages. Its numeric identity is retained; no other file is selected.</p>}</>}
          <details className="supporting-evidence" aria-label="Selected file allocation and autogrowth"><summary>Selected file allocation and autogrowth{selected?receipt?.state!=="available"?" · Incomplete evidence":"":" · Unavailable"}</summary>
            <p>Allocation configuration from the exact retained receipt, in Live or Rewind. This is not used log space, free disk space, a forecast, or a guarantee growth will succeed.</p>
            {selected?<><p>{selected.logicalName} · database {selected.databaseId}, file {selected.fileId}. Receipt status: {receipt?.state} · {labels[receipt?.reason??""]??receipt?.reason}.</p><dl className="health-facts"><div><dt>Allocated bytes</dt><dd>{bytes(selected.sizeBytes)}</dd></div><div><dt>Reported maximum bytes</dt><dd>{bytes(selected.maximumSizeBytes)}</dd></div><div><dt>Autogrowth</dt><dd>{growth(selected)}</dd></div><div><dt>File type / state</dt><dd>{selected.fileType} / {selected.fileState}</dd></div><div><dt>Observed</dt><dd>{formatTime(selected.observedAtUtc)}</dd></div><div><dt>Collected</dt><dd>{formatTime(selected.collectedAtUtc)}</dd></div><div><dt>Completed</dt><dd>{formatTime(receipt!.completedAtUtc)}</dd></div></dl><p>Null maximum does not distinguish unlimited from unknown. Settings do not prove physical file continuity or why an allocation changed.</p></>:<p role="status">{selectedFile?"Selected file allocation is unavailable in received exact snapshot pages. No other file's configuration is substituted.":"Choose an exact file from the received snapshot to inspect its allocation and autogrowth."}</p>}
          </details>
          {databaseId!==undefined&&<p>Rows are filtered to database ID {databaseId} within this received page. Empty filtered pages do not mean the database has no files.</p>}
          <p className="file-history-receipt">Snapshot page {snapshot.index+1}; {snapshot.pages.reduce((n,p)=>n+p.items.length,0)} received file rows. Cumulative averages below are since the source counter reset, separate from selected-window latency.</p>
          {files.length?<div className="table-scroll" tabIndex={0} role="region" aria-label="Historical database file facts"><table><caption>Exact allocation, growth settings and cumulative I/O from the selected receipt</caption><thead><tr><th scope="col">File</th><th scope="col">Allocation</th><th scope="col">Autogrowth</th><th scope="col">Cumulative I/O averages</th><th scope="col">Evidence</th><th scope="col">Inspect</th></tr></thead><tbody>{files.map(file=><tr key={`${file.databaseId}:${file.fileId}`}><th scope="row">{file.logicalName}<small>Database {file.databaseId} · file {file.fileId} · {file.fileType} · {file.fileState}</small></th><td>{bytes(file.sizeBytes)}<small>Maximum: {bytes(file.maximumSizeBytes)}</small></td><td>{growth(file)}</td><td>Read: {lifetimeAverageStallMilliseconds(file.readStallMilliseconds,file.readCount)??"unavailable"} ms<small>Write: {lifetimeAverageStallMilliseconds(file.writeStallMilliseconds,file.writeCount)??"unavailable"} ms</small><details><summary>Exact cumulative counters</summary><dl><dt>Reads</dt><dd>{file.readCount}</dd><dt>Writes</dt><dd>{file.writeCount}</dd><dt>Bytes read / written</dt><dd>{file.bytesRead} / {file.bytesWritten}</dd><dt>Total stall milliseconds</dt><dd>{file.ioStallMilliseconds}</dd><dt>Read / write stalls</dt><dd>{file.readStallMilliseconds??"unknown"} / {file.writeStallMilliseconds??"unknown"}</dd></dl></details></td><td>Observed {formatTime(file.observedAtUtc)}<small>Collected {formatTime(file.collectedAtUtc)}</small></td><td><button type="button" className="file-history-inspect" aria-pressed={selectedFile?.databaseId===file.databaseId&&selectedFile.fileId===file.fileId} aria-label={`View read and write latency trend for database ${file.databaseId}, file ${file.logicalName}`} onClick={()=>onSelectFile(file,filePage)}>View latency trend</button></td></tr>)}</tbody></table></div>:<p role="status">{receipt?.reason==="observed_empty"?"The completed receipt contains zero file facts. This does not establish healthy allocation or available capacity.":"No file facts are present on this received page. Review the receipt state, loss and database filter."}</p>}
        </>}
        {!filePage&&selectedFile&&<details aria-label="Selected file allocation and autogrowth"><summary>Selected file allocation and autogrowth · Unavailable</summary><p>Selected file allocation is unavailable until this exact receipt is received. No other file's configuration is substituted.</p></details>}
      </section>
    </>}
  </section>;
}


