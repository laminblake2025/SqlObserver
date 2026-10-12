import { useEffect, useId, useRef, useState } from 'react';
import { customUtcInputValue, customUtcWindow, overviewWindow } from '../features/overview/overviewModel';
import { useTimeDisplay } from '../TimeDisplayContext';
import { displayRangeInput, editedRangeUtc } from './timeRangeEditorModel';
import type { OverviewRange, OverviewScope } from '../features/overview/overviewTypes';

export function TimeRangeControls({scope,onChange,maximumDays=31}: {
  scope:OverviewScope; onChange:(change:Partial<OverviewScope>)=>void; maximumDays?:number;
}) {
  const {mode}=useTimeDisplay();
  const timezone=mode==='utc'?'UTC':Intl.DateTimeFormat().resolvedOptions().timeZone;
  const inputValue=(value:string|undefined)=>displayRangeInput(value,mode);
  const [error,setError]=useState('');
  const [editing,setEditing]=useState(scope.range==='custom'&&(!scope.from||!scope.to));
  const editorId=useId();
  const editorToggle=useRef<HTMLButtonElement>(null);
  const editor=useRef<HTMLFormElement>(null);
  useEffect(()=>{
    if(editing&&scope.range==='custom') editor.current?.querySelector<HTMLInputElement>('input[name="from"]')?.focus();
  },[editing,scope.range,scope.from,scope.to]);
  const closeEditor=()=>{setError('');setEditing(false);editorToggle.current?.focus();};
  return <>
    <label>Time range<select className="time-range-select" aria-label="Time range" value={scope.range} onChange={event=>{
      setError('');
      const range=event.target.value as OverviewRange;
      if(range==='custom') {
        const window=overviewWindow(scope,Date.now());
        onChange({range,from:window.fromUtc,to:window.toUtc});
        setEditing(true);
      } else {
        setEditing(false);
        onChange({range});
      }
    }}>
      <option value="1h">Last hour</option><option value="6h">Last 6 hours</option><option value="24h">Last 24 hours</option><option value="7d">Last 7 days</option><option value="custom">Custom range</option>
    </select></label>
    {scope.range==='custom'&&<div className="time-range-editor">
      <button ref={editorToggle} type="button" aria-expanded={editing} aria-controls={editing?editorId:undefined} onClick={()=>{if(editing)closeEditor();else{setError('');setEditing(true);}}}>{editing?'Close editor':`Edit ${mode==='utc'?'UTC':'local'} range`}</button>
      {editing&&<form ref={editor} id={editorId} aria-label={`Custom ${timezone} time range`} className="overview-controls time-range-popover" key={`${scope.from}:${scope.to}:${mode}`} onKeyDown={event=>{
        if(event.key==='Escape'){event.preventDefault();event.stopPropagation();closeEditor();}
      }} onSubmit={event=>{
        event.preventDefault();
        const values=new FormData(event.currentTarget);
        try {
          const window=customUtcWindow(event.currentTarget.dataset.fromEdited==='true'?editedRangeUtc(String(values.get('from')??''),mode):customUtcInputValue(scope.from),event.currentTarget.dataset.toEdited==='true'?editedRangeUtc(String(values.get('to')??''),mode):customUtcInputValue(scope.to),Date.now(),maximumDays,
            {fromUtc:scope.from,toUtc:scope.to,
              fromEdited:event.currentTarget.dataset.fromEdited==='true',toEdited:event.currentTarget.dataset.toEdited==='true'});
          onChange({from:window.fromUtc,to:window.toUtc});closeEditor();
        } catch(failure) {setError(failure instanceof Error?failure.message:'Enter a valid UTC range.');}
      }}>
        <label>From ({timezone})<input name="from" required type="datetime-local" step="0.001"
          defaultValue={inputValue(scope.from)} onChange={event=>{
            if(event.currentTarget.form)event.currentTarget.form.dataset.fromEdited='true';
          }}/></label>
        <label>To ({timezone})<input name="to" required type="datetime-local" step="0.001"
          defaultValue={inputValue(scope.to)} onChange={event=>{
            if(event.currentTarget.form)event.currentTarget.form.dataset.toEdited='true';
          }}/></label>
        <small>Enter bounds in {timezone}. Untouched bounds retain exact UTC precision; edited bounds use milliseconds. Ambiguous daylight saving times require UTC.</small>
        {inputValue(scope.from)&&inputValue(scope.to)&&<small>Exact current bounds: <code>{scope.from}</code> to <code>{scope.to}</code>.</small>}
        <button type="submit">Apply range</button>
        {error&&<p role="alert">{error}</p>}
      </form>}
    </div>}
  </>;
}
