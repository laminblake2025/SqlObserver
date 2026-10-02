import { type PointerEvent } from 'react';
import { useTimeDisplay } from '../../TimeDisplayContext';
import { formatDisplayTime } from '../../timeDisplay';
import { chartObservedPoints, chartUtcAtX, chartSvgX } from './chartTimeModel';
import { useChartGesture } from '../../components/useChartGesture';
import { formatOverviewChartValue, overviewChartRange } from './overviewChartRange';
import type { OverviewSeries } from './overviewTypes';
import { matchingReferences, type ChartReferences } from './chartReferenceModel.ts';

const colors = ['var(--series-1)','var(--series-2)','var(--series-3)','var(--series-4)','var(--series-5)','var(--series-6)','var(--series-7)','var(--series-8)','var(--series-9)','var(--series-10)'];

interface PlotPoint {
  readonly timeUtc: string;
  readonly value: number;
  readonly samples: number;
  readonly x: number;
  readonly y: number;
}

export function OverviewChart({series,previous,fromUtc,toUtc,markers=[],crosshairUtc,onCrosshairChange,onSelectWindow,references}: {
  series:readonly OverviewSeries[];previous?:readonly OverviewSeries[];fromUtc:string;toUtc:string;
  markers?:readonly {timeUtc:string;label:string}[];
  crosshairUtc?:string|null;
  onCrosshairChange?:(utc:string|null)=>void;
  onSelectWindow?:(window:{fromUtc:string;toUtc:string})=>void;
  references?:ChartReferences;
}) {
  const {mode}=useTimeDisplay();
  const timeLabel=(value:string|number,compact=false)=>formatDisplayTime(value,mode,compact);
  const gesture=useChartGesture(JSON.stringify([fromUtc,toUtc,series.map(seriesIdentity)]),fromUtc,toUtc,onSelectWindow);
  const dragStart=gesture.start,dragEnd=gesture.end;
  const from=Date.parse(fromUtc), to=Date.parse(toUtc);
  const selectedReferences=matchingReferences(series,references);
  const bands=selectedReferences.bands.filter(band=>Date.parse(band.fromUtc)<to&&Date.parse(band.toUtc)>from&&Date.parse(band.fromUtc)<Date.parse(band.toUtc));
  const range=overviewChartRange(series,previous,fromUtc,toUtc,selectedReferences);
  if (!range) return <p className="overview-empty">No comparable observations in this window. Missing samples are not plotted as zero.</p>;
  const x=(time:string,shift=0)=>48+(Date.parse(time)+shift-from)/(to-from)*572;
  const y=(value:number)=>180-(value-range.minimum)/(range.maximum-range.minimum)*155;
  const ticks=[0,.25,.5,.75,1];
  const colorOrder=[...new Set([...series,...(previous??[])].map(seriesIdentity))].sort();
  const colorByIdentity=new Map(colorOrder.map((identity,index)=>[identity,index]));
  const colorFor=(s:OverviewSeries)=>colors[(colorByIdentity.get(seriesIdentity(s))??0)%colors.length];
  const all=[...series.map(s=>({s,shift:0})),...(previous??[]).map(s=>({s,shift:to-from}))];
  const crosshairTime=crosshairUtc===null||crosshairUtc===undefined?NaN:Date.parse(crosshairUtc);
  const crosshairX=Number.isFinite(crosshairTime)&&crosshairTime>=from&&crosshairTime<=to?x(crosshairUtc!):null;
  const pointX=(event:PointerEvent<SVGSVGElement>)=>{
    return chartSvgX(event.currentTarget,event.clientX,event.clientY);
  };
  return <figure><svg className={`chart overview-chart${onSelectWindow?' chart-interactive':''}`} viewBox="0 0 650 220" role="img" aria-label={`${series[0]?.unit} over the selected ${mode==='local'?'local-time display':'UTC'} window. Lines connect observations; missing samples remain gaps.${onSelectWindow?' Drag across the chart to select a shared time window.':''}`}
    onPointerMove={event=>{if(!onCrosshairChange&&!onSelectWindow)return;const value=pointX(event);if(value===null)return;onCrosshairChange?.(chartUtcAtX(value,fromUtc,toUtc));gesture.move(value);}}
    onPointerDown={event=>{if(!onSelectWindow)return;const value=pointX(event);if(value===null)return;gesture.begin(event.pointerId,value);event.currentTarget.setPointerCapture(event.pointerId);}}
    onPointerUp={event=>{gesture.finish(event.pointerId,pointX(event));}}
    onPointerCancel={gesture.cancel} onLostPointerCapture={gesture.cancel}
    onPointerLeave={()=>{if(dragStart===null)onCrosshairChange?.(null);}}>
    {ticks.map(f=>{const value=range.minimum+f*(range.maximum-range.minimum);return <g key={f}><line className="chart-gridline" x1={48} x2={620} y1={y(value)} y2={y(value)}/><text x="0" y={y(value)+4}>{formatOverviewChartValue(value,range)}</text><line className="chart-gridline chart-gridline-vertical" x1={48+f*572} x2={48+f*572} y1={25} y2={180}/></g>;})}
    {bands.map((band,index)=>{const left=x(new Date(Math.max(from,Date.parse(band.fromUtc))).toISOString()),right=x(new Date(Math.min(to,Date.parse(band.toUtc))).toISOString());return <g className="chart-baseline-band" key={`${band.metric}:${band.fromUtc}:${index}`}>
      <rect x={left} y={y(band.high)} width={right-left} height={y(band.low)-y(band.high)}/>
      <line x1={left} x2={right} y1={y(band.median)} y2={y(band.median)}/><title>{`${band.label} · ${formatOverviewChartValue(band.low,range)}–${formatOverviewChartValue(band.high,range)} ${band.unit}`}</title>
    </g>;})}
    {selectedReferences.thresholds.map((threshold,index)=><g className="chart-threshold" key={`${threshold.metric}:${index}`}>
      <line x1={48} x2={618} y1={y(threshold.value)} y2={y(threshold.value)}/><title>{`${threshold.label} · ${formatOverviewChartValue(threshold.value,range)} ${threshold.unit}`}</title>
    </g>)}
    {all.map(({s,shift})=>{
      const color=colorFor(s);
      const points=plotPoints(s,shift,from,to,x,y);
      return <g key={`${s.targetId}:${s.metric}:${s.dimension}:${shift}`}>
        {lineSegments(points).map((d,index)=><path className="chart-series" d={d} fill="none" key={index} stroke={color} strokeDasharray={shift ? "5 5" : undefined} strokeLinecap="round" strokeLinejoin="round" strokeOpacity={shift ? .48 : .95} strokeWidth={shift ? 1.75 : 2.5}/>)}
        {points.filter((point): point is PlotPoint => point !== null).map(point=><circle className="chart-marker" cx={point.x} cy={point.y} fill={shift ? "var(--surface)" : color} key={`${point.timeUtc}:${shift}`} r={shift ? 1.5 : 2} stroke={color} strokeWidth={shift ? 1 : 1.25}>
          <title>{`${s.label} ${s.dimension??''} · ${timeLabel(point.timeUtc)} · ${formatOverviewChartValue(point.value,range)} ${s.unit} · ${point.samples} samples${shift?' · previous period':''}`}</title>
        </circle>)}
      </g>;
    })}
    {markers.filter(marker=>Date.parse(marker.timeUtc)>=from&&Date.parse(marker.timeUtc)<to).map((marker,index)=><g key={`${marker.timeUtc}:${index}`} className="chart-event-marker"><line x1={x(marker.timeUtc)} x2={x(marker.timeUtc)} y1="25" y2="180"/><circle cx={x(marker.timeUtc)} cy="24" r="4"/><title>{`${marker.label} · ${timeLabel(marker.timeUtc)}`}</title></g>)}
    {dragStart!==null&&dragEnd!==null&&<rect className="chart-selection" x={Math.min(dragStart,dragEnd)} y="25" width={Math.abs(dragEnd-dragStart)} height="155" pointerEvents="none"/>}
    {crosshairX!==null&&<g className="chart-crosshair" pointerEvents="none"><line x1={crosshairX} x2={crosshairX} y1="25" y2="180"/><text x={crosshairX>500?crosshairX-5:crosshairX+5} y="18" textAnchor={crosshairX>500?'end':'start'}>{timeLabel(crosshairTime,true)}</text></g>}
    <text x="48" y="210">{timeLabel(from,true)}</text><text x="465" y="210">{timeLabel(to,true)}</text>
  </svg><figcaption className="overview-legend">{series.map(s=><span key={`${s.targetId}:${s.metric}:${s.dimension}`}><i style={{background:colorFor(s)}}/>{s.label}{s.dimension?` · ${s.dimension}`:''} <small>{s.state}</small></span>)}</figcaption>
  <small>Vertical scale includes observations and visible references and may not start at zero.</small>
  {bands.length>0&&<small>Shaded band: {[...new Set(bands.map(band=>band.label.split(' · ')[0]))].join(', ')} · p10–p90; center line: median. Missing reference hours remain gaps.</small>}
  {selectedReferences.thresholds.length>0&&<small>Dashed horizontal lines: current enabled metric rules. Open reference details for each rule and condition.</small>}
  {(bands.length>0||selectedReferences.thresholds.length>0)&&<details className="chart-values"><summary>View chart references</summary>
    <p>Baseline shading describes historical percentiles. Dashed threshold lines show current configured rules, not historical settings or an alert decision.</p>
    <ul>{selectedReferences.thresholds.map((threshold,index)=><li key={`threshold:${index}`}>{threshold.label} · {formatOverviewChartValue(threshold.value,range)} {threshold.unit}</li>)}</ul>
    {bands.length>0&&<div className="table-scroll" tabIndex={0} role="region" aria-label="Baseline reference hours"><table><caption>Baseline reference hours</caption><thead><tr><th>Window ({mode==='local'?'local':'UTC'})</th><th>p10–p90 / median</th><th>Source evidence</th></tr></thead><tbody>{bands.map((band,index)=><tr key={`band:${index}`}><td>{timeLabel(band.fromUtc)} to {timeLabel(band.toUtc)}</td><td>{formatOverviewChartValue(band.low,range)}–{formatOverviewChartValue(band.high,range)} / {formatOverviewChartValue(band.median,range)} {band.unit}</td><td>{band.label}</td></tr>)}</tbody></table></div>}
  </details>}
  {previous?.length ? <small>Dashed lines: previous equal-length window shifted for comparison. Observed bucket means; gaps and changing coverage can affect comparisons.</small> : null}
  {markers.length>0&&<small>Event lines mark available events in this window; they are not a complete event history.</small>}
  <details className="chart-values"><summary>View chart values</summary><div className="table-scroll" tabIndex={0} role="region" aria-label="Chart observation values"><table><thead><tr><th>Server / resource</th><th>{mode==='local'?'Local time':'UTC'}</th><th>Value</th><th>Samples</th><th>Period</th></tr></thead><tbody>{all.flatMap(({s,shift})=>s.points.map(p=><tr key={`${s.targetId}:${s.metric}:${s.dimension}:${shift}:${p.timeUtc}`}><td>{s.label} {s.dimension}</td><td title={p.timeUtc}>{timeLabel(p.timeUtc)}</td><td>{formatOverviewChartValue(p.value,range)} {s.unit}</td><td>{p.samples}</td><td>{shift?'Previous':'Selected'}</td></tr>))}</tbody></table></div></details></figure>;
}

function seriesIdentity(series: OverviewSeries): string {
  return `${series.targetId}:${series.metric}:${series.dimension??''}`;
}

function plotPoints(series: OverviewSeries, shift: number, from: number, to: number, x: (time: string, shift?: number) => number, y: (value: number) => number): readonly (PlotPoint | null)[] {
  const windowFrom = new Date(from - shift).toISOString();
  const windowTo = new Date(to - shift).toISOString();
  return chartObservedPoints(series.points, windowFrom, windowTo)
    .map(point => point === null ? null : {
      timeUtc: point.timeUtc,
      value: point.value!,
      samples: point.samples,
      x: x(point.timeUtc, shift),
      y: y(point.value!),
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
