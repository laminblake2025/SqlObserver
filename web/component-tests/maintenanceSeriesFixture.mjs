import { createHash } from 'node:crypto';
export const alpha = '11111111-1111-4111-8111-111111111111', beta = '44444444-4444-4444-8444-444444444444';
export const seriesId = '77777777-7777-4777-8777-777777777777', auditId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
export const now = '2026-10-03T15:00:00.1234560+00:00';
export const nativeUtc = value => value.replace(/(?:\.(\d{1,6}))?Z$/u, (_, fraction = '') => '.' + fraction.padEnd(7, '0') + '+00:00');
export const definition = changes => ({ frequency: 'daily', interval: 1, startDate: '2026-11-01', endDate: '2026-11-02',
  localStartTime: '01:30', durationMinutes: 60, timeZoneId: 'America/New_York', weekdays: [], dayOfMonth: null,
  gapPolicy: 'skip', foldPolicy: 'earlier', reason: 'Synthetic overnight maintenance', ...changes });
export function digest(d) {
  return createHash('sha256').update([d.frequency, d.interval, d.startDate, d.endDate, d.localStartTime, d.durationMinutes,
    d.timeZoneId, [...d.weekdays].sort((a,b) => a-b).join(','), d.dayOfMonth ?? '<null>', d.gapPolicy, d.foldPolicy, d.reason].join('|')).digest('hex');
}
export function windowId(id, date) {
  const hex = createHash('sha256').update(id.toLowerCase() + '|' + date).digest('hex').slice(0,32);
  return [hex.slice(0,8),hex.slice(8,12),hex.slice(12,16),hex.slice(16,20),hex.slice(20)].join('-');
}
const dateNumber = value => Math.floor(Date.parse(value + 'T00:00:00Z') / 86400000);
const dayAt = value => new Date(value * 86400000).toISOString().slice(0,10);
const sunday = (year, month, nth) => { const first = new Date(Date.UTC(year,month-1,1)); return 1 + (7-first.getUTCDay())%7 + 7*(nth-1); };
/** Server-shaped deterministic fixture for UTC/New York civil expansion, never production browser expansion. */
export function expansion(id = seriesId, d = definition(), effective = null) {
  const occurrences = [], skippedDates = [], start = dateNumber(d.startDate), end = dateNumber(d.endDate);
  const weekAnchor = start - new Date(start * 86400000).getUTCDay();
  for (let day = start; day <= end; day++) {
    const date = dayAt(day), calendar = new Date(day * 86400000), year = calendar.getUTCFullYear(), month = calendar.getUTCMonth()+1, dom = calendar.getUTCDate();
    const match = d.frequency === 'daily' ? (day-start)%d.interval===0 : d.frequency === 'weekly'
      ? Math.floor((day-weekAnchor)/7)%d.interval===0 && d.weekdays.includes(calendar.getUTCDay())
      : ((year-Number(d.startDate.slice(0,4)))*12+month-Number(d.startDate.slice(5,7)))%d.interval===0 && dom===d.dayOfMonth;
    if (!match) continue;
    const hour = Number(d.localStartTime.slice(0,2)), gap = month===3 && dom===sunday(year,3,2), fold = month===11 && dom===sunday(year,11,1);
    if (d.timeZoneId==='America/New_York' && gap && hour===2) { skippedDates.push({ localDate:date,reason:'nonexistent_local_time' }); continue; }
    const ambiguous = d.timeZoneId==='America/New_York' && fold && hour===1;
    const march = Date.UTC(year,2,sunday(year,3,2)), november = Date.UTC(year,10,sunday(year,11,1));
    const offset = d.timeZoneId==='UTC' ? 0 : ambiguous ? d.foldPolicy==='earlier' ? -240 : -300
      : day*86400000>march && day*86400000<november || gap && hour>=3 || fold && hour<1 ? -240 : -300;
    const starts = Date.parse(date+'T'+d.localStartTime+':00Z') - offset*60000;
    if (effective && date<effective) continue;
    occurrences.push({ windowId:windowId(id,date),localDate:date,startsAtUtc:nativeUtc(new Date(starts).toISOString()),
      endsAtUtc:nativeUtc(new Date(starts+d.durationMinutes*60000).toISOString()),utcOffsetMinutes:offset,ambiguous,expansionVersion:'civil-v1' });
  }
  if (d.frequency==='monthly') {
    const [year,month] = d.startDate.split('-').map(Number), finish = Number(d.endDate.slice(0,4))*12+Number(d.endDate.slice(5,7))-1;
    for (let value=year*12+month-1;value<=finish;value+=d.interval) {
      const y=Math.floor(value/12),m=value%12+1,days=new Date(Date.UTC(y,m,0)).getUTCDate();
      const requested=String(y).padStart(4,'0')+'-'+String(m).padStart(2,'0')+'-'+String(d.dayOfMonth).padStart(2,'0');
      if (d.dayOfMonth>days && requested>=d.startDate && requested<=d.endDate) skippedDates.push({localDate:requested,reason:'missing_month_day'});
    }
  }
  return { occurrences, skippedDates };
}
export const status = (id=seriesId,d=definition(), changes={}) => ({ targetId:alpha,seriesId:id,revision:1,definition:d,
  cancelledFromDate:null,createdAtUtc:now,updatedAtUtc:now,expansionVersion:'civil-v1',serverNowUtc:now,
  occurrenceCount:expansion(id,d).occurrences.length,...changes });
export const preview = (id=seriesId,d=definition(),effective=null) => ({targetId:alpha,seriesId:id,serverNowUtc:now,
  definitionDigest:digest(d),expansionVersion:'civil-v1',...expansion(id,d,effective),
  occurrences:expansion(id,d,effective).occurrences.filter(item=>Date.parse(item.startsAtUtc)>Date.parse(now))});
export const receipt = (command,changes={}) => ({operationId:command.body.operationId,auditId,recordedAtUtc:now,
  seriesId:command.kind==='create'?command.body.seriesId:command.seriesId,revision:command.kind==='create'?1:command.body.expectedRevision+1,
  appliedAtUtc:now,createdOccurrences:2,updatedOccurrences:0,cancelledOccurrences:0,preservedExceptions:0,...changes});
export function seriesBackend() {
  const series=new Map(),windows=new Map(),operations=new Map(); const key=(target,id)=>target+':'+id;
  return {series,windows,operations,
    read(target,id) { const value=series.get(key(target,id)); if(!value)throw Object.assign(new Error('Missing series'),{status:404});return {...value,serverNowUtc:now}; },
    list(target,before) {const values=[...series.values()].filter(item=>item.targetId===target).sort((a,b)=>b.seriesId.localeCompare(a.seriesId));
      const start=before?values.findIndex(item=>item.seriesId===before)+1:0,items=values.slice(start,start+50);
      return {targetId:target,serverNowUtc:now,items,nextBeforeSeriesId:start+items.length<values.length?items.at(-1).seriesId:null};},
    occurrences(target,id,before) {const values=[...windows.values()].filter(item=>item.targetId===target&&item.seriesId===id).sort((a,b)=>b.localDate.localeCompare(a.localDate));
      const start=before?values.findIndex(item=>item.windowId===before)+1:0,items=values.slice(start,start+50).map(({targetId,seriesId,...item})=>item);
      return {targetId:target,seriesId:id,serverNowUtc:now,items,nextBeforeWindowId:start+items.length<values.length?items.at(-1).windowId:null};},
    oneOff(target) {const items=[...windows.values()].filter(item=>item.targetId===target).sort((a,b)=>b.startsAtUtc.localeCompare(a.startsAtUtc)).slice(0,100)
      .map(({windowId,startsAtUtc,endsAtUtc,reason,revision,cancelledAtUtc})=>({windowId,startsAtUtc,endsAtUtc,reason,revision,cancelledAtUtc}));
      return {targetId:target,listingStartedUtc:now,items,hasMore:false,nextCursor:null};},
    changeWindow(target,kind,id,body) {
      const row=windows.get(id),opkey=key(target,body.operationId),replay=operations.get(opkey),raw=JSON.stringify(body);
      if(replay){if(replay.raw!==raw||replay.kind!==kind||replay.id!==id)throw Object.assign(new Error('Changed replay'),{status:409});return replay.receipt;}
      if(kind!=='oneoff-create'&&(!row||row.targetId!==target||row.revision!==body.expectedRevision))throw Object.assign(new Error('Changed window'),{status:409});
      if(kind==='oneoff-create')windows.set(id,{...body,targetId:target,seriesId:null,localDate:body.startsAtUtc.slice(0,10),revision:1,cancelledAtUtc:null});
      else if(kind==='oneoff-edit')Object.assign(row,{startsAtUtc:body.startsAtUtc,endsAtUtc:body.endsAtUtc,reason:body.reason,revision:row.revision+1,exception:true});
      else Object.assign(row,{cancelledAtUtc:now,revision:row.revision+1,exception:true});
      const result={auditId,recordedAtUtc:now};operations.set(opkey,{kind,id,raw,receipt:result});return result;
    },
    change(target,command,raw=JSON.stringify(command.body)) {
      const id=command.kind==='create'?command.body.seriesId:command.seriesId,opkey=key(target,command.body.operationId),replay=operations.get(opkey);
      if(replay){if(replay.raw!==raw||replay.kind!==command.kind||replay.id!==id)throw Object.assign(new Error('Changed replay'),{status:409});return replay.receipt;}
      const previous=series.get(key(target,id)),revision=command.kind==='create'?1:previous?.revision+1;
      if(command.kind!=='create'&&(!previous||previous.revision!==command.body.expectedRevision)||command.kind==='create'&&previous)
        throw Object.assign(new Error('Revision changed'),{status:409});
      let created=0,updated=0,cancelled=0,preserved=0;
      const d=command.kind==='cancel'?previous.definition:command.body.definition, cutoff=command.kind==='create'?null:command.body.effectiveFromDate;
      const fence=command.kind==='cancel'?(previous.cancelledFromDate&&previous.cancelledFromDate<cutoff?previous.cancelledFromDate:cutoff):previous?.cancelledFromDate??null;
      const generated=command.kind==='cancel'?[]:expansion(id,d,cutoff).occurrences;
      const dates=new Set(generated.map(item=>item.localDate));
      for(const row of windows.values()) if(row.targetId===target&&row.seriesId===id&&(!cutoff||row.localDate>=cutoff)){
        if(row.exception){preserved++;continue;}if(row.cancelledAtUtc||Date.parse(row.startsAtUtc)<=Date.parse(now))continue;
        if(command.kind==='cancel'||!dates.has(row.localDate)){row.cancelledAtUtc=now;row.revision++;cancelled++;}
      }
      for(const item of generated) {
        const existing=windows.get(item.windowId);
        if(existing?.exception){continue;}if(existing?.cancelledAtUtc||fence&&item.localDate>=fence||Date.parse(item.startsAtUtc)<=Date.parse(now))continue;
        const next={...item,targetId:target,seriesId:id,reason:d.reason,revision:existing?existing.revision+1:1,cancelledAtUtc:null,seriesRevision:revision,exception:false};
        windows.set(item.windowId,next);existing?updated++:created++;
      }
      const value=status(id,d,{targetId:target,revision,cancelledFromDate:fence,occurrenceCount:[...windows.values()].filter(item=>item.targetId===target&&item.seriesId===id).length});
      series.set(key(target,id),value);
      const result=receipt(command,{revision,createdOccurrences:created,updatedOccurrences:updated,cancelledOccurrences:cancelled,preservedExceptions:preserved});
      operations.set(opkey,{raw,kind:command.kind,id,receipt:result});return result;
    } };
}
