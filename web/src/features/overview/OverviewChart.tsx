import { displayMetric } from './overviewModel';
import type { OverviewSeries } from './overviewTypes';

const colors = ['#77d4c2','#82b8ff','#e5b96c','#cba8ed','#f198a3','#a9ce7c','#69cbdc','#d3c998','#d997d0','#91aabd'];
export function OverviewChart({series,previous,fromUtc,toUtc}: {series:readonly OverviewSeries[];previous?:readonly OverviewSeries[];fromUtc:string;toUtc:string}) {
  const from=Date.parse(fromUtc), to=Date.parse(toUtc);
  const current=series.flatMap(s=>s.points.filter(p=>p.value!==null));
  if (!current.length) return <p className="overview-empty">No comparable observations in this window. Missing samples are not plotted as zero.</p>;
  const max=Math.max(1,...current.map(p=>p.value!),...(previous??[]).flatMap(s=>s.points.map(p=>p.value??0)));
  const x=(time:string,shift=0)=>48+(Date.parse(time)+shift-from)/(to-from)*570;
  const y=(value:number)=>180-value/max*155;
  const all=[...series.map((s,i)=>({s,i,shift:0})),...(previous??[]).map((s,i)=>({s,i,shift:to-from}))];
  return <figure><svg className="chart overview-chart" viewBox="0 0 650 220" role="img" aria-label={`${series[0]?.unit} over the selected UTC window. Individual bucket observations; missing buckets are gaps.`}>
    {[0,.5,1].map(f=><g key={f}><path d={`M48 ${y(max*f)}H620`} stroke="#33424e"/><text x="0" y={y(max*f)+4}>{displayMetric(max*f)}</text></g>)}
    {all.map(({s,i,shift})=><g key={`${s.targetId}:${s.dimension}:${shift}`}>{s.points.filter(p=>p.value!==null && Date.parse(p.timeUtc)+shift>=from && Date.parse(p.timeUtc)+shift<to).map(p=><circle key={p.timeUtc} cx={x(p.timeUtc,shift)} cy={y(p.value!)} r={shift?2:3.5} style={{fill:shift?'none':colors[i%colors.length],stroke:colors[i%colors.length],opacity:shift?.55:1}}><title>{s.label} {s.dimension} · {p.timeUtc} · {displayMetric(p.value)} {s.unit} · {p.samples} samples{shift?' · previous period':''}</title></circle>)}</g>)}
    <text x="48" y="210">{new Date(from).toISOString().slice(5,16).replace('T',' ')} UTC</text><text x="465" y="210">{new Date(to).toISOString().slice(5,16).replace('T',' ')} UTC</text>
  </svg><figcaption className="overview-legend">{series.map((s,i)=><span key={`${s.targetId}:${s.dimension}`}><i style={{background:colors[i%colors.length]}}/>{s.label}{s.dimension?` · ${s.dimension}`:''} <small>{s.state}</small></span>)}</figcaption>
  {previous?.length ? <small>Hollow points: previous equal-length window shifted for comparison. Observed bucket means; gaps and changing coverage can affect comparisons.</small> : null}
  <details className="chart-values"><summary>View chart values</summary><div className="table-scroll"><table><thead><tr><th>Server / resource</th><th>UTC</th><th>Value</th><th>Samples</th><th>Period</th></tr></thead><tbody>{all.flatMap(({s,shift})=>s.points.map(p=><tr key={`${s.targetId}:${s.dimension}:${shift}:${p.timeUtc}`}><td>{s.label} {s.dimension}</td><td>{p.timeUtc}</td><td>{displayMetric(p.value)} {s.unit}</td><td>{p.samples}</td><td>{shift?'Previous':'Selected'}</td></tr>))}</tbody></table></div></details></figure>;
}
