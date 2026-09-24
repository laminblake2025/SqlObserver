import { useState, type PointerEvent } from 'react';
import { displayMetric } from './overviewModel';
import { chartSelection, chartUtcAtX, chartX } from './chartTimeModel';
import type { OverviewSeries } from './overviewTypes';

const colors = ['var(--series-1)','var(--series-2)','var(--series-3)','var(--series-4)','var(--series-5)','var(--series-6)','var(--series-7)','var(--series-8)','var(--series-9)','var(--series-10)'];

interface PlotPoint {
  readonly timeUtc: string;
  readonly value: number;
  readonly samples: number;
  readonly x: number;
  readonly y: number;
}

export function OverviewChart({series,previous,fromUtc,toUtc,markers=[],crosshairUtc,onCrosshairChange,onSelectWindow}: {
  series:readonly OverviewSeries[];previous?:readonly OverviewSeries[];fromUtc:string;toUtc:string;
  markers?:readonly {timeUtc:string;label:string}[];
  crosshairUtc?:string|null;
  onCrosshairChange?:(utc:string|null)=>void;
  onSelectWindow?:(window:{fromUtc:string;toUtc:string})=>void;
}) {
  const [dragStart,setDragStart]=useState<number|null>(null);
  const [dragEnd,setDragEnd]=useState<number|null>(null);
  const from=Date.parse(fromUtc), to=Date.parse(toUtc);
  const current=series.flatMap(s=>s.points.filter(p=>p.value!==null && Number.isFinite(p.value)));
  if (!current.length) return <p className="overview-empty">No comparable observations in this window. Missing samples are not plotted as zero.</p>;
  const max=Math.max(1,...current.map(p=>p.value!),...(previous??[]).flatMap(s=>s.points.map(p=>p.value??0)));
  const x=(time:string,shift=0)=>48+(Date.parse(time)+shift-from)/(to-from)*570;
  const y=(value:number)=>180-value/max*155;
  const ticks=[0,.25,.5,.75,1];
  const colorOrder=[...new Set([...series,...(previous??[])].map(seriesIdentity))].sort();
  const colorByIdentity=new Map(colorOrder.map((identity,index)=>[identity,index]));
  const colorFor=(s:OverviewSeries)=>colors[(colorByIdentity.get(seriesIdentity(s))??0)%colors.length];
  const all=[...series.map(s=>({s,shift:0})),...(previous??[]).map(s=>({s,shift:to-from}))];
  const crosshairTime=crosshairUtc===null||crosshairUtc===undefined?NaN:Date.parse(crosshairUtc);
  const crosshairX=Number.isFinite(crosshairTime)&&crosshairTime>=from&&crosshairTime<=to?x(crosshairUtc!):null;
  const pointX=(event:PointerEvent<SVGSVGElement>)=>{
    const bounds=event.currentTarget.getBoundingClientRect();
    return bounds.width>0?chartX(event.clientX,bounds.left,bounds.width):null;
  };
  return <figure><svg className={`chart overview-chart${onSelectWindow?' chart-interactive':''}`} viewBox="0 0 650 220" role="img" aria-label={`${series[0]?.unit} over the selected UTC window. Lines connect observations; missing samples remain gaps.${onSelectWindow?' Drag across the chart to select a shared UTC window.':''}`}
    onPointerMove={event=>{if(!onCrosshairChange&&!onSelectWindow)return;const value=pointX(event);if(value===null)return;onCrosshairChange?.(chartUtcAtX(value,fromUtc,toUtc));if(dragStart!==null)setDragEnd(value);}}
    onPointerDown={event=>{if(!onSelectWindow)return;const value=pointX(event);if(value===null)return;setDragStart(value);setDragEnd(value);event.currentTarget.setPointerCapture(event.pointerId);}}
    onPointerUp={event=>{if(dragStart===null)return;const value=pointX(event);setDragStart(null);setDragEnd(null);if(value!==null){const selected=chartSelection(dragStart,value,fromUtc,toUtc);if(selected)onSelectWindow?.(selected);}}}
    onPointerCancel={()=>{setDragStart(null);setDragEnd(null);}}
    onPointerLeave={()=>{if(dragStart===null)onCrosshairChange?.(null);}}>
    {ticks.map(f=><g key={f}><line className="chart-gridline" x1={48} x2={620} y1={y(max*f)} y2={y(max*f)}/><text x="0" y={y(max*f)+4}>{displayMetric(max*f)}</text><line className="chart-gridline chart-gridline-vertical" x1={48+f*570} x2={48+f*570} y1={25} y2={180}/></g>)}
    {all.map(({s,shift})=>{
      const color=colorFor(s);
      const points=plotPoints(s,shift,from,to,x,y);
      return <g key={`${s.targetId}:${s.metric}:${s.dimension}:${shift}`}>
        {lineSegments(points).map((d,index)=><path className="chart-series" d={d} fill="none" key={index} stroke={color} strokeDasharray={shift ? "5 5" : undefined} strokeLinecap="round" strokeLinejoin="round" strokeOpacity={shift ? .48 : .95} strokeWidth={shift ? 1.75 : 2.5}/>)}
        {points.filter((point): point is PlotPoint => point !== null).map(point=><circle className="chart-marker" cx={point.x} cy={point.y} fill={shift ? "var(--surface)" : color} key={`${point.timeUtc}:${shift}`} r={shift ? 1.5 : 2} stroke={color} strokeWidth={shift ? 1 : 1.25}>
          <title>{s.label} {s.dimension} · {point.timeUtc} · {displayMetric(point.value)} {s.unit} · {point.samples} samples{shift?' · previous period':''}</title>
        </circle>)}
      </g>;
    })}
    {markers.filter(marker=>Date.parse(marker.timeUtc)>=from&&Date.parse(marker.timeUtc)<to).map((marker,index)=><g key={`${marker.timeUtc}:${index}`} className="chart-event-marker"><line x1={x(marker.timeUtc)} x2={x(marker.timeUtc)} y1="25" y2="180"/><circle cx={x(marker.timeUtc)} cy="24" r="4"/><title>{marker.label} · {marker.timeUtc}</title></g>)}
    {dragStart!==null&&dragEnd!==null&&<rect className="chart-selection" x={Math.min(dragStart,dragEnd)} y="25" width={Math.abs(dragEnd-dragStart)} height="155" pointerEvents="none"/>}
    {crosshairX!==null&&<g className="chart-crosshair" pointerEvents="none"><line x1={crosshairX} x2={crosshairX} y1="25" y2="180"/><text x={crosshairX>500?crosshairX-5:crosshairX+5} y="18" textAnchor={crosshairX>500?'end':'start'}>{new Date(crosshairTime).toISOString().slice(5,19).replace('T',' ')} UTC</text></g>}
    <text x="48" y="210">{new Date(from).toISOString().slice(5,16).replace('T',' ')} UTC</text><text x="465" y="210">{new Date(to).toISOString().slice(5,16).replace('T',' ')} UTC</text>
  </svg><figcaption className="overview-legend">{series.map(s=><span key={`${s.targetId}:${s.metric}:${s.dimension}`}><i style={{background:colorFor(s)}}/>{s.label}{s.dimension?` · ${s.dimension}`:''} <small>{s.state}</small></span>)}</figcaption>
  {previous?.length ? <small>Dashed lines: previous equal-length window shifted for comparison. Observed bucket means; gaps and changing coverage can affect comparisons.</small> : null}
  {markers.length>0&&<small>Event lines mark ranked issues returned for this window; they are not a complete event history.</small>}
  <details className="chart-values"><summary>View chart values</summary><div className="table-scroll"><table><thead><tr><th>Server / resource</th><th>UTC</th><th>Value</th><th>Samples</th><th>Period</th></tr></thead><tbody>{all.flatMap(({s,shift})=>s.points.map(p=><tr key={`${s.targetId}:${s.metric}:${s.dimension}:${shift}:${p.timeUtc}`}><td>{s.label} {s.dimension}</td><td>{p.timeUtc}</td><td>{displayMetric(p.value)} {s.unit}</td><td>{p.samples}</td><td>{shift?'Previous':'Selected'}</td></tr>))}</tbody></table></div></details></figure>;
}

function seriesIdentity(series: OverviewSeries): string {
  return `${series.targetId}:${series.metric}:${series.dimension??''}`;
}

function plotPoints(series: OverviewSeries, shift: number, from: number, to: number, x: (time: string, shift?: number) => number, y: (value: number) => number): readonly (PlotPoint | null)[] {
  return series.points
    .filter(point => Date.parse(point.timeUtc) + shift >= from && Date.parse(point.timeUtc) + shift < to)
    .map(point => point.value === null ? null : {
      timeUtc: point.timeUtc,
      value: point.value,
      samples: point.samples,
      x: x(point.timeUtc, shift),
      y: y(point.value),
    });
}

function lineSegments(points: readonly (PlotPoint | null)[]): readonly string[] {
  const paths: string[] = [];
  let current: PlotPoint[] = [];
  const flush = () => {
    if (current.length) paths.push(current.map((point, index) => `${index === 0 ? 'M' : 'L'}${point.x.toFixed(2)} ${point.y.toFixed(2)}`).join(' '));
    current = [];
  };
  for (const point of points) {
    if (point === null) flush();
    else current.push(point);
  }
  flush();
  return paths;
}
