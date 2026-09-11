export function ObservationChart({ items, label }: { readonly items: readonly {time: string; value: number | null}[]; readonly label: string }) {
  const boundedItems = items.slice(0, 1000);
  const points = boundedItems.filter((item): item is {time: string; value: number} => item.value !== null && Number.isFinite(item.value) && Number.isFinite(Date.parse(item.time)));
  if (!points.length) return <p className="empty-state">No numeric observations available.</p>;
  const start = Math.min(...points.map(p => Date.parse(p.time))), end = Math.max(...points.map(p => Date.parse(p.time)));
  const max = Math.max(1, ...points.map(p => p.value));
  const x = (time: string) => 45 + (Date.parse(time) - start) / Math.max(1, end - start) * 575;
  const y = (value: number) => 195 - value / max * 165;
  const plotted = boundedItems.map(item => item.value !== null && Number.isFinite(item.value) && Number.isFinite(Date.parse(item.time)) ? { ...item, x: x(item.time), y: y(item.value) } : null);
  const ticks = [0, .25, .5, .75, 1];
  return <figure><figcaption>{label} · {points.length} bounded observations · UTC</figcaption><svg className="chart" viewBox="0 0 640 240" role="img" aria-label={`${label}. Lines connect observations; missing samples remain gaps. Range 0 to ${max}.`}>
    {ticks.map(f => <g key={f}><line className="chart-gridline" x1={45} x2={620} y1={y(max*f)} y2={y(max*f)}/><text x="0" y={y(max*f)+4}>{(max*f).toLocaleString()}</text><line className="chart-gridline chart-gridline-vertical" x1={45+f*575} x2={45+f*575} y1={30} y2={195}/></g>)}
    {lineSegments(plotted).map((d,index) => <path className="chart-series" d={d} fill="none" key={index} stroke="var(--teal)" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5"/>)}
    {plotted.filter((point): point is {time: string; value: number; x: number; y: number} => point !== null).map((point, i) => <circle className="chart-marker" cx={point.x} cy={point.y} fill="var(--teal)" key={`${point.time}:${i}`} r="2" stroke="var(--teal)" strokeWidth="1.25"><title>{point.time}: {point.value} · {label}</title></circle>)}
    <text x="45" y="225">{new Date(start).toISOString().slice(11,19)}</text><text x="545" y="225">{new Date(end).toISOString().slice(11,19)}</text>
  </svg><small>Lines connect adjacent observations; separate observations preserve gaps, with no inferred continuity or rate.</small></figure>;
}

function lineSegments(points: readonly ({ x: number; y: number } | null)[]): readonly string[] {
  const paths: string[] = [];
  let current: { x: number; y: number }[] = [];
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
