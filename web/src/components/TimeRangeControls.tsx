import { useState } from 'react';
import { customUtcWindow, overviewWindow } from '../features/overview/overviewModel';
import type { OverviewRange, OverviewScope } from '../features/overview/overviewTypes';

export function TimeRangeControls({scope,onChange,maximumDays=31}: {
  scope:OverviewScope; onChange:(change:Partial<OverviewScope>)=>void; maximumDays?:number;
}) {
  const [error,setError]=useState('');
  const [editing,setEditing]=useState(scope.range==='custom'&&(!scope.from||!scope.to));
  return <>
    <label>Time range<select aria-label="Time range" value={scope.range} onChange={event=>{
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
      <option value="1h">Last hour</option><option value="6h">Last 6 hours</option><option value="24h">Last 24 hours</option><option value="7d">Last 7 days</option><option value="custom">Custom UTC range</option>
    </select></label>
    {scope.range==='custom'&&<div className="time-range-editor">
      <button type="button" aria-expanded={editing} onClick={()=>{setError('');setEditing(value=>!value);}}>{editing?'Close editor':'Edit UTC range'}</button>
      {editing&&<form className="overview-controls time-range-popover" key={`${scope.from}:${scope.to}`} onSubmit={event=>{
        event.preventDefault();
        const values=new FormData(event.currentTarget);
        try {
          const window=customUtcWindow(String(values.get('from')??''),String(values.get('to')??''),Date.now(),maximumDays);
          setError('');onChange({from:window.fromUtc,to:window.toUtc});setEditing(false);
        } catch(failure) {setError(failure instanceof Error?failure.message:'Enter a valid UTC range.');}
      }}>
        <label>From (UTC)<input name="from" required type="datetime-local" step="1" defaultValue={scope.from?.slice(0,19)??''}/></label>
        <label>To (UTC)<input name="to" required type="datetime-local" step="1" defaultValue={scope.to?.slice(0,19)??''}/></label>
        <small>Enter custom bounds in UTC. The display toggle changes timestamps without changing the selected instants.</small>
        <button type="submit">Apply range</button>
        {error&&<p role="alert">{error}</p>}
      </form>}
    </div>}
  </>;
}
