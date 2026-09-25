import { useState, type PointerEvent } from "react";
import { useTimeDisplay } from "../TimeDisplayContext";
import { formatDisplayTime } from "../timeDisplay";
import { chartSelection, chartUtcAtX, chartX } from "../features/overview/chartTimeModel";
import {
  nearestObservation, observationSegments, prepareObservationChart,
  type ObservationBand, type ObservationSeries, type ObservationThreshold,
  type PlottedObservation,
} from "./observationChartModel";

const colors = ["var(--series-1)", "var(--series-2)", "var(--series-3)", "var(--series-4)", "var(--series-5)",
  "var(--series-6)", "var(--series-7)", "var(--series-8)", "var(--series-9)", "var(--series-10)"];
const left = 48, right = 620, top = 30, bottom = 180;
const number = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 });

export function ObservationChart({ label, series, fromUtc, toUtc, baselineBand, threshold, crosshairUtc,
  onCrosshairChange, onSelectWindow }: {
  readonly label: string;
  readonly series: readonly ObservationSeries[];
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly baselineBand?: ObservationBand;
  readonly threshold?: ObservationThreshold;
  readonly crosshairUtc?: string | null;
  readonly onCrosshairChange?: (utc: string | null) => void;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
}) {
  const { mode } = useTimeDisplay();
  const timeLabel = (value: string | number, compact = false) => formatDisplayTime(value, mode, compact);
  const [hoverUtc, setHoverUtc] = useState<string | null>(null);
  const [dragStart, setDragStart] = useState<number | null>(null);
  const [dragEnd, setDragEnd] = useState<number | null>(null);
  const chart = prepareObservationChart(series, fromUtc, toUtc, baselineBand, threshold);
  if (chart === null) return <p className="empty-state">No numeric observations in this window. Missing values are not zero.</p>;

  const x = (timestamp: number) => left + (timestamp - chart.from) / (chart.to - chart.from) * (right - left);
  const y = (value: number) => bottom - (value - chart.minimum) / (chart.maximum - chart.minimum) * (bottom - top);
  const pointerX = (event: PointerEvent<SVGSVGElement>) => {
    const bounds = event.currentTarget.getBoundingClientRect();
    return bounds.width > 0 ? chartX(event.clientX, bounds.left, bounds.width) : null;
  };
  const activeCrosshair = crosshairUtc === undefined ? hoverUtc : crosshairUtc;
  const crosshairTime = activeCrosshair === null ? NaN : Date.parse(activeCrosshair);
  const crosshairX = Number.isFinite(crosshairTime) && crosshairTime >= chart.from && crosshairTime <= chart.to
    ? x(crosshairTime) : null;
  const referenceBand = baselineBand && Number.isFinite(baselineBand.lower) && Number.isFinite(baselineBand.upper)
    && baselineBand.lower <= baselineBand.upper ? baselineBand : undefined;
  const referenceThreshold = threshold && Number.isFinite(threshold.value) ? threshold : undefined;
  const announceCrosshair = (utc: string | null) => { setHoverUtc(utc); onCrosshairChange?.(utc); };

  return <figure className="observation-figure">
    <figcaption>{label} · {chart.pointCount} plotted values from loaded evidence · {mode === "local" ? "local time" : "UTC"}</figcaption>
    <svg className={`chart observation-chart${onSelectWindow ? " chart-interactive" : ""}`} viewBox="0 0 650 225"
      role="img" aria-label={`${label}. ${chart.series.length} series in the selected ${mode === "local" ? "local-time display" : "UTC"} window. Missing samples remain gaps.${onSelectWindow ? " Drag to select a shared time window." : ""}`}
      onPointerMove={event => {
        const position = pointerX(event);
        if (position === null) return;
        announceCrosshair(chartUtcAtX(position, fromUtc, toUtc));
        if (dragStart !== null) setDragEnd(position);
      }}
      onPointerDown={event => {
        if (!onSelectWindow) return;
        const position = pointerX(event);
        if (position === null) return;
        setDragStart(position); setDragEnd(position);
        event.currentTarget.setPointerCapture(event.pointerId);
      }}
      onPointerUp={event => {
        if (dragStart === null) return;
        const position = pointerX(event);
        setDragStart(null); setDragEnd(null);
        if (position !== null) {
          const selected = chartSelection(dragStart, position, fromUtc, toUtc);
          if (selected) onSelectWindow?.(selected);
        }
      }}
      onPointerCancel={() => { setDragStart(null); setDragEnd(null); }}
      onPointerLeave={() => { if (dragStart === null) announceCrosshair(null); }}>
      {[0, .5, 1].map(fraction => {
        const value = chart.minimum + fraction * (chart.maximum - chart.minimum);
        return <g key={fraction}><line className="chart-gridline" x1={left} x2={right} y1={y(value)} y2={y(value)} />
          <text x="0" y={y(value) + 4}>{number.format(value)}</text></g>;
      })}
      {[0, .5, 1].map(fraction => <line key={fraction} className="chart-gridline chart-gridline-vertical"
        x1={left + fraction * (right - left)} x2={left + fraction * (right - left)} y1={top} y2={bottom} />)}
      {referenceBand && <rect className="chart-baseline-band" x={left} y={y(referenceBand.upper)}
        width={right - left} height={Math.max(0, y(referenceBand.lower) - y(referenceBand.upper))}>
        <title>{referenceBand.label}: {number.format(referenceBand.lower)} to {number.format(referenceBand.upper)}</title>
      </rect>}
      {referenceThreshold && <line className="chart-threshold" x1={left} x2={right}
        y1={y(referenceThreshold.value)} y2={y(referenceThreshold.value)}>
        <title>{referenceThreshold.label}: {number.format(referenceThreshold.value)}</title>
      </line>}
      {chart.series.map((entry, index) => {
        const color = colors[index % colors.length];
        return <g key={entry.id}>
          {observationSegments(entry.items).map((segment, segmentIndex) =>
            <path className="chart-series" key={segmentIndex} d={segmentPath(segment, x, y)} fill="none"
              stroke={color} strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" />)}
          {entry.items.filter((item): item is PlottedObservation => item !== null).map((point, pointIndex) =>
            <circle className="chart-marker" key={`${point.time}:${pointIndex}`} cx={x(point.timestamp)} cy={y(point.value)}
              r="2" fill={color} stroke={color}><title>{entry.label} · {timeLabel(point.timestamp)} · {number.format(point.value)}</title></circle>)}
        </g>;
      })}
      {dragStart !== null && dragEnd !== null && <rect className="chart-selection" x={Math.min(dragStart, dragEnd)}
        y={top} width={Math.abs(dragEnd - dragStart)} height={bottom - top} pointerEvents="none" />}
      {crosshairX !== null && <g className="chart-crosshair" pointerEvents="none">
        <line x1={crosshairX} x2={crosshairX} y1={top} y2={bottom} />
        <text x={crosshairX > 500 ? crosshairX - 5 : crosshairX + 5} y="20"
          textAnchor={crosshairX > 500 ? "end" : "start"}>{timeLabel(crosshairTime, true)}</text>
      </g>}
      <text x={left} y="215">{timeLabel(chart.from, true)}</text>
      <text x={right} y="215" textAnchor="end">{timeLabel(chart.to, true)}</text>
    </svg>
    <div className="observation-legend">
      {chart.series.map((entry, index) => <span key={entry.id}><i style={{ background: colors[index % colors.length] }} />{entry.label}</span>)}
      {referenceBand && <span><i className="baseline-swatch" />{referenceBand.label}</span>}
      {referenceThreshold && <span><i className="threshold-swatch" />{referenceThreshold.label}</span>}
    </div>
    {crosshairX !== null && <p className="chart-nearest">Nearest observations at {timeLabel(crosshairTime)}: {chart.series.map((entry) => {
      const nearest = nearestObservation(entry.items, crosshairTime);
      return `${entry.label} ${nearest ? `${number.format(nearest.value)} at ${timeLabel(nearest.timestamp)}` : "unavailable"}`;
    }).join(" · ")}</p>}
    <small>Observed values only; lines do not imply samples between points.{onSelectWindow ? " Drag to narrow the workspace time range." : ""}</small>
    <details className="chart-values"><summary>View chart values</summary><div className="table-scroll"><table>
      <thead><tr><th>Series</th><th>{mode === "local" ? "Local time" : "UTC"}</th><th>Value</th></tr></thead><tbody>
        {chart.series.flatMap((entry) => entry.items.filter((item): item is PlottedObservation => item !== null)
          .map((point, index) => <tr key={`${entry.id}:${point.time}:${index}`}><td>{entry.label}</td><td title={point.time}>{timeLabel(point.timestamp)}</td><td>{number.format(point.value)}</td></tr>))}
      </tbody></table></div></details>
  </figure>;
}

function segmentPath(segment: readonly PlottedObservation[], x: (timestamp: number) => number,
  y: (value: number) => number): string {
  return segment.map((point, index) => `${index === 0 ? "M" : "L"}${x(point.timestamp).toFixed(2)} ${y(point.value).toFixed(2)}`).join(" ");
}
