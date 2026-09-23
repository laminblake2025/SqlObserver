import { useState } from 'react';
import { customUtcWindow } from '../features/overview/overviewModel';
import type { OverviewRange, OverviewScope } from '../features/overview/overviewTypes';

export function TimeRangeControls({scope,onChange,maximumDays=31,fixed=false}: {
  scope:OverviewScope; onChange:(change:Partial<OverviewScope>)=>void; maximumDays?:number; fixed?:boolean;
}) {
  const [error,setError]=useState('');
  return <>
    <label>Time range<select aria-label="Time range" value={scope.range} onChange={event=>{setError('');onChange({range:event.target.value as OverviewRange});}}>
      <option value="1h">{fixed ? '1 hour (fixed)' : 'Last hour'}</option><option value="6h">{fixed ? '6 hours (fixed)' : 'Last 6 hours'}</option><option value="24h">{fixed ? '24 hours (fixed)' : 'Last 24 hours'}</option><option value="7d">{fixed ? '7 days (fixed)' : 'Last 7 days'}</option><option value="custom">Custom UTC range</option>
    </select></label>
    {scope.range==='custom'&&<form className="overview-controls" key={`${scope.from}:${scope.to}`} onSubmit={event=>{
      event.preventDefault();
      const values=new FormData(event.currentTarget);
      try {
        const window=customUtcWindow(String(values.get('from')??''),String(values.get('to')??''),Date.now(),maximumDays);
        setError('');onChange({from:window.fromUtc,to:window.toUtc});
      } catch(failure) {setError(failure instanceof Error?failure.message:'Enter a valid UTC range.');}
    }}>
      <label>From (UTC)<input name="from" required type="datetime-local" step="0.001" defaultValue={scope.from?.replace(/Z$/,'')??''}/></label>
      <label>To (UTC)<input name="to" required type="datetime-local" step="0.001" defaultValue={scope.to?.replace(/Z$/,'')??''}/></label>
      <button type="submit">Apply range</button>
    </form>}
    {error&&<p role="alert">{error}</p>}
  </>;
}
