import { useLayoutEffect, useRef, useState, type PointerEvent } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext.tsx";
import { formatDisplayTime } from "../../timeDisplay.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import { chartViewportIdentity } from "../../components/chartViewportIdentity.ts";
import { ChartPlotViewport } from "../../components/ChartPlotViewport.tsx";
import { useChartGesture } from "../../components/useChartGesture.ts";
import { chartObservedPoints, chartUtcAtX } from "../overview/chartTimeModel.ts";
import { cpuObservationPoints, type CpuObservationReceiptPage } from "./cpuObservationHistoryModel.ts";

function lineSegments(points: ReturnType<typeof cpuObservationPoints>, fromUtc: string, toUtc: string) {
  const visible = chartObservedPoints(points.map(point => ({ ...point, timeUtc: point.receipt.completedAtUtc, value: point.value?.percent ?? null, component: point.value })), fromUtc, toUtc);
  const segments: (NonNullable<typeof visible[number]>[])[] = [];
  let current: NonNullable<typeof visible[number]>[] = [];
  const flush = () => { if (current.length) segments.push(current); current = []; };
  for (const point of visible) {
    if (point === null) { flush(); continue; }
    const previous = current.at(-1);
    if (previous && (previous.receipt.sampleTargetRevision !== point.receipt.sampleTargetRevision || previous.component?.quality !== point.component?.quality)) flush();
    current.push(point);
  }
  flush();
  return segments;
}

export function CpuObservationChart({ current, previous, component, fromUtc, toUtc, crosshairUtc, onCrosshairChange, onSelectWindow }: {
  readonly current: readonly CpuObservationReceiptPage[]; readonly previous?: readonly CpuObservationReceiptPage[];
  readonly component: "sqlProcess" | "operatingSystem"; readonly fromUtc: string; readonly toUtc: string;
  readonly crosshairUtc?: string | null; readonly onCrosshairChange?: (utc: string | null) => void;
  readonly onSelectWindow?: (window: { fromUtc: string; toUtc: string }) => void;
}) {
  const frame = useRef<HTMLElement>(null), [width, setWidth] = useState(650);
  useLayoutEffect(() => { const element = frame.current; if (!element) return; const measure = () => setWidth(Math.max(100, element.clientWidth)); measure(); const observer = new ResizeObserver(measure); observer.observe(element); return () => observer.disconnect(); }, []);
  const left = 42, right = width - 12, plotWidth = right - left;
  const { mode } = useTimeDisplay(), from = utcMicroseconds(fromUtc), span = utcMicroseconds(toUtc) - from;
  const label = component === "sqlProcess" ? "SQL-process CPU" : "Target-host CPU";
  const groups = [{ title: "Selected window", points: cpuObservationPoints(current, component), shift: 0n, previous: false },
    { title: "Previous window", points: cpuObservationPoints(previous ?? [], component), shift: span, previous: true }];
  const gesture = useChartGesture(JSON.stringify([fromUtc, toUtc, current[0]?.readEpoch, previous?.[0]?.readEpoch]), fromUtc, toUtc, onSelectWindow);
  const x = (utc: string, shift = 0n) => left + Number(utcMicroseconds(utc) + shift - from) / Number(span) * plotWidth;
  const position = (event: PointerEvent<SVGSVGElement>) => {
    const svg = event.currentTarget, matrix = svg.getScreenCTM(); if (!matrix) return null;
    const point = svg.createSVGPoint(); point.x = event.clientX; point.y = event.clientY;
    const local = point.matrixTransform(matrix.inverse()).x;
    return Number.isFinite(local) ? 48 + (Math.min(right, Math.max(left, local)) - left) / plotWidth * 572 : null;
  };
  const fromCanonical = (value: number) => left + (value - 48) / 572 * plotWidth;
  let marker: number | undefined;
  try { if (crosshairUtc && utcMicroseconds(crosshairUtc) >= from && utcMicroseconds(crosshairUtc) < from + span) marker = x(crosshairUtc); } catch { /* Invalid alignment cannot change observations. */ }
  return <figure ref={frame} className="cpu-observation-chart"><figcaption>{label} · percent · collection completion time</figcaption>
    <ChartPlotViewport label={`${label} chart area`} contextKey={JSON.stringify([fromUtc, toUtc, current[0]?.targetId, current[0]?.targetRevision])} resetKey={chartViewportIdentity([component,String(current[0]?.targetId),String(current[0]?.targetRevision)], typeof location === "undefined" ? "" : location.hash)} canSelectWindow={Boolean(onSelectWindow)} adaptWidth>
      <svg className="chart chart-interactive" viewBox={`0 0 ${width} 215`} role="img" aria-label={`${label} independent observations and gaps`}
        onPointerDown={event => { const p = position(event); if (event.button === 0 && event.isPrimary && p !== null && gesture.begin(event.pointerId, p)) event.currentTarget.setPointerCapture(event.pointerId); }}
        onPointerMove={event => { const p = position(event); if (event.isPrimary && gesture.acceptsPointer(event.pointerId) && p !== null) { onCrosshairChange?.(chartUtcAtX(p, fromUtc, toUtc)); gesture.move(event.pointerId, p); } }}
        onPointerUp={event => gesture.finish(event.pointerId, position(event))} onPointerCancel={event => gesture.cancel(event.pointerId)} onLostPointerCapture={event => gesture.cancel(event.pointerId)}
        onPointerLeave={event => { if (event.isPrimary && gesture.start === null) onCrosshairChange?.(null); }}>
        {[0, 50, 100].map(value => <g key={value}><line className="chart-gridline" x1={left} x2={right} y1={180-value*1.5} y2={180-value*1.5}/><text x={left-6} textAnchor="end" y={184-value*1.5}>{value}%</text></g>)}
        {groups.map(group => {
          const windowFrom = new Date(Number((from - group.shift) / 1000n)).toISOString(), windowTo = new Date(Number((from + span - group.shift) / 1000n)).toISOString();
          const segments = lineSegments(group.points, windowFrom, windowTo);
          const connectedRuns = new Set(segments.filter(segment => segment.length > 1).flatMap(segment => segment.map(point => point.receipt.runId)));
          return <g key={group.title}>
          {segments.filter(segment => segment.length > 1).map((segment, index) => <path key={index} className={`chart-series ${segment[0]?.component?.quality === "provider_adjusted" ? "cpu-provider-adjusted" : "cpu-observation-point"}`} fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" strokeDasharray={group.previous ? "5 4" : undefined} opacity={group.previous ? .65 : 1} d={segment.map((point, i) => `${i ? "L" : "M"}${x(point.receipt.completedAtUtc, group.shift).toFixed(2)} ${(180 - point.value! * 1.5).toFixed(2)}`).join(" ")}/>)}
          {group.points.map(({ receipt, value }) => {
          const px = x(receipt.completedAtUtc, group.shift), py = value?.percent == null ? 198 : 180-value.percent*1.5;
          const title = `${group.title} · ${formatDisplayTime(receipt.completedAtUtc, mode)} · ${value?.percent == null ? "Gap: " + (value?.reason ?? receipt.reason) : `${value.percent}% · ${value.quality === "provider_adjusted" ? "ProviderAdjusted" : "Available"} · native interval ${value.fromNative100ns} to ${value.toNative100ns} (100 ns)`}`;
          return <g key={receipt.runId} className={value?.quality === "provider_adjusted" ? "cpu-provider-adjusted" : "cpu-observation-point"} opacity={group.previous ? .65 : 1}>
            <title>{title}</title>{value?.percent == null ? <path data-cpu-gap="true" d={`M${px-3} ${py-3}l6 6m-6 0l6-6`} stroke="currentColor"/> : value.quality === "provider_adjusted" ?
              <rect data-quality="provider_adjusted" x={px-4} y={py-4} width="8" height="8" fill={group.previous ? "none" : "currentColor"} stroke="currentColor"/> :
              <circle className={`chart-marker${connectedRuns.has(receipt.runId) ? " chart-point-hit-target" : ""}`} data-quality="available" cx={px} cy={py} r={connectedRuns.has(receipt.runId) ? 4 : 3.5} fill={group.previous ? "none" : "currentColor"} stroke="currentColor"/>}</g>;
        })}</g>;})}
        <text x={left-6} y="202" textAnchor="end">Gap</text>
        {marker !== undefined && <g className="chart-crosshair"><line x1={marker} x2={marker} y1="30" y2="205"/></g>}
        {gesture.start !== null && gesture.end !== null && <rect className="chart-selection" x={fromCanonical(Math.min(gesture.start,gesture.end))} y="30" width={Math.abs(gesture.end-gesture.start)/572*plotWidth} height="175"/>}

      </svg>
    </ChartPlotViewport>
    <p className="cpu-observation-axis"><time dateTime={fromUtc}>{formatDisplayTime(fromUtc, mode, true)}</time><time dateTime={toUtc}>{formatDisplayTime(toUtc, mode, true)}</time></p>
    <p className="table-note">Solid lines: selected window; dashed: previous equal-length window shifted for comparison. Lines connect received observations of the same quality and revision; missing observations and long completion gaps break lines. Hover for individual values. Isolated circles: Available. Squares: ProviderAdjusted. Crosses on the separate gap row: unavailable. Each observation retains its own native interval; lines do not imply a shared CPU denominator or continuous collection.</p>
  </figure>;
}
