import { type PointerEvent } from "react";
import { useResponsiveChart } from "../../components/useResponsiveChart";
import { useTimeDisplay } from "../../TimeDisplayContext.tsx";
import { formatDisplayTime } from "../../timeDisplay.ts";
import { chartSvgX, chartUtcAtX } from "../overview/chartTimeModel.ts";
import { useChartGesture } from "../../components/useChartGesture.ts";
import { chartViewportIdentity } from "../../components/chartViewportIdentity.ts";
import { ChartPlotViewport } from "../../components/ChartPlotViewport.tsx";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import { approximateVolumeAxisLabel, approximateVolumeGib, volumeHistoryCompletionFraction, volumeHistorySegments, volumeHistoryUtc, type VolumeHistoryPage, type VolumeHistorySample, type VolumeIdentityKind } from "./sqlVolumeHistoryModel.ts";
interface Point { readonly row: VolumeHistorySample; readonly fraction: number; readonly value: number }
function segments(rows: readonly VolumeHistorySample[], shift: bigint, kind: VolumeIdentityKind | null, fromUtc: string, toUtc: string): readonly (readonly Point[])[] {
  return volumeHistorySegments(rows,kind).map(segment => segment.map(row => ({ row, fraction: volumeHistoryCompletionFraction(row.completedAtUtc,fromUtc,toUtc,shift), value: approximateVolumeGib(row.availableBytes)! })));
}
export function SqlVolumeHistoryChart({ current, previous, fromUtc, toUtc, crosshairUtc, onCrosshairChange, onSelectWindow }: {
  readonly current: readonly VolumeHistoryPage[]; readonly previous?: readonly VolumeHistoryPage[]; readonly fromUtc: string; readonly toUtc: string;
  readonly crosshairUtc?: string | null; readonly onCrosshairChange?: (utc: string | null) => void;
  readonly onSelectWindow?: (window: { fromUtc: string; toUtc: string }) => void;
}) {
  const responsive = useResponsiveChart();
  const { mode } = useTimeDisplay(), from = utcMicroseconds(volumeHistoryUtc(fromUtc)), to = utcMicroseconds(volumeHistoryUtc(toUtc)), shift = to - from;
  const selected = segments(current.flatMap(page => page.items), 0n, current[0]?.identityKind ?? null,fromUtc,toUtc), earlier = previous ? segments(previous.flatMap(page => page.items), shift, previous[0]?.identityKind ?? null,fromUtc,toUtc) : [];
  const groups = [{ label: "Selected window", data: selected, previous: false }, { label: "Previous equal-length window", data: earlier, previous: true }];
  const all = groups.flatMap(group => group.data.flat()), values = all.map(point => point.value);
  const gesture = useChartGesture(JSON.stringify([fromUtc, toUtc, current[0]?.readEpoch, previous?.[0]?.readEpoch]), fromUtc, toUtc, onSelectWindow);
  if (!values.length) return <p>No complete known-capacity gauges were received. Missing values are not zero.</p>;
  const low = Math.min(...values), high = Math.max(...values), padding = Math.max(.01, (high - low) * .05), minimum = low - padding, maximum = high + padding;
  const viewLeft = Math.min(0, 42 - Math.max(...[minimum, maximum].map(value => approximateVolumeAxisLabel(value).length)) * responsive.fontSize * .65 - 12);
  const viewWidth = 650 - viewLeft;
  const x = (fraction: number) => 48 + fraction * 572, y = (value: number) => 180 - (value - minimum) / (maximum - minimum) * 150;
  const position = (event: PointerEvent<SVGSVGElement>) => chartSvgX(event.currentTarget, event.clientX, event.clientY);
  let crosshair: bigint | null = null;
  try { if (crosshairUtc) crosshair = utcMicroseconds(volumeHistoryUtc(crosshairUtc)); } catch { /* An invalid shared crosshair does not alter received evidence. */ }
  return <figure ref={responsive.ref} className="volume-history-chart"><figcaption>Available capacity · approximate GiB · collection completion time</figcaption>
    <ChartPlotViewport label="Volume history chart area" contextKey={JSON.stringify([fromUtc,toUtc,current[0]?.instanceId,current[0]?.targetRevision,current[0]?.sampleTargetRevision,current[0]?.volumeKey])} resetKey={chartViewportIdentity([String(current[0]?.instanceId),String(current[0]?.targetRevision),String(current[0]?.sampleTargetRevision),String(current[0]?.volumeKey)], typeof location === "undefined" ? "" : location.hash)} canSelectWindow={Boolean(onSelectWindow)} adaptWidth>
    <svg className="chart chart-interactive" style={{ fontSize: responsive.fontSize * viewWidth / 650 }} viewBox={`${viewLeft} 0 ${viewWidth} 225`} role="img" aria-label="SQL volume available capacity by collection completion"
      onPointerDown={event => { const p = position(event); if (event.button === 0 && event.isPrimary && p !== null && gesture.begin(event.pointerId, p)) event.currentTarget.setPointerCapture(event.pointerId); }}
      onPointerMove={event => { const p = position(event); if (event.isPrimary && gesture.acceptsPointer(event.pointerId) && p !== null) { onCrosshairChange?.(chartUtcAtX(p, fromUtc, toUtc)); gesture.move(event.pointerId, p); } }}
      onPointerUp={event => gesture.finish(event.pointerId, position(event))} onPointerCancel={event => gesture.cancel(event.pointerId)} onLostPointerCapture={event => gesture.cancel(event.pointerId)}
      onPointerLeave={event => { if (event.isPrimary && gesture.start === null) onCrosshairChange?.(null); }}>
      {[0,.5,1].map(f => <g key={f}><line className="chart-gridline" x1="48" x2="620" y1={y(minimum + f * (maximum - minimum))} y2={y(minimum + f * (maximum - minimum))} /><text x="42" textAnchor="end" y={y(minimum + f * (maximum - minimum)) + 4}>{approximateVolumeAxisLabel(minimum + f * (maximum - minimum))}</text></g>)}
      {groups.map(group => <g key={group.label}>{group.data.map((segment, i) => <path key={i} className="chart-series" fill="none" stroke={group.previous ? "var(--series-2)" : "var(--series-1)"} strokeWidth="2.5" strokeDasharray={group.previous ? "5 4" : undefined} d={segment.map((point,index) => (index ? "L" : "M") + x(point.fraction).toFixed(2) + " " + y(point.value).toFixed(2)).join(" ")} />)}
        {group.data.flatMap(segment => segment.map(point => <circle key={point.row.runId} className={`chart-marker${segment.length > 1 ? " chart-point-hit-target" : ""}`} cx={x(point.fraction)} cy={y(point.value)} r={segment.length > 1 ? 4 : 2} fill={group.previous ? "var(--series-2)" : "var(--series-1)"}><title>{group.label} · completed {formatDisplayTime(point.row.completedAtUtc, mode)} · {point.value.toLocaleString(undefined,{maximumFractionDigits:2})} approximate GiB</title></circle>))}</g>)}
      {crosshair !== null && crosshair >= from && crosshair < to && <g className="chart-crosshair"><line x1={x(Number(crosshair-from)/Number(shift))} x2={x(Number(crosshair-from)/Number(shift))} y1="30" y2="180" /></g>}
      {gesture.start !== null && gesture.end !== null && <rect className="chart-selection" x={Math.min(gesture.start,gesture.end)} y="30" width={Math.abs(gesture.end-gesture.start)} height="150" />}
      <text x="48" y="218">{formatDisplayTime(fromUtc,mode,true)}</text><text x="620" y="218" textAnchor="end">{formatDisplayTime(toUtc,mode,true)}</text>
    </svg></ChartPlotViewport><p className="table-note">Solid: selected window{previous ? "; dashed: previous equal-length window shifted for comparison" : ""}. Gauges are received observations, with no interpolation to the window edges. Mount-point and unknown file identities show individual observations without cross-run lines. Gaps, missing capacity, total-capacity changes and completion intervals over ten minutes break lines; this visual rule does not alter native coverage counts. Exact bytes and actual SQL observation times appear in the received evidence tables.</p>
  </figure>;
}
