import { useId, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import "./chartPlotViewport.css";

interface PlotScrollState {
  readonly context: string;
  readonly overflowing: boolean;
  readonly earlier: boolean;
  readonly later: boolean;
}

/** Keep the full date and value axes readable without widening the page. */
export function ChartPlotViewport({ label, contextKey, canSelectWindow = false, minWidth = 650, children }: {
  readonly label: string;
  readonly contextKey: string;
  readonly canSelectWindow?: boolean;
  readonly minWidth?: number;
  readonly children: ReactNode;
}) {
  const viewport = useRef<HTMLDivElement>(null), viewportId = useId();
  const [received, setReceived] = useState<PlotScrollState>();
  const current = received?.context === contextKey ? received : undefined;

  useLayoutEffect(() => {
    const element = viewport.current;
    if (!element) return;
    let active = true;
    const measure = () => {
      if (!active) return;
      const maximum = Math.max(0, element.scrollWidth - element.clientWidth);
      const next = { context: contextKey, overflowing: maximum > 1,
        earlier: element.scrollLeft > 1, later: maximum - element.scrollLeft > 1 };
      setReceived(previous => previous?.context === next.context
        && previous.overflowing === next.overflowing && previous.earlier === next.earlier
        && previous.later === next.later ? previous : next);
    };
    element.scrollLeft = 0;
    element.addEventListener("scroll", measure, { passive: true });
    window.addEventListener("resize", measure);
    const observer = typeof ResizeObserver === "undefined" ? undefined : new ResizeObserver(measure);
    observer?.observe(element);
    if (element.firstElementChild) observer?.observe(element.firstElementChild);
    measure();
    return () => {
      active = false;
      observer?.disconnect();
      element.removeEventListener("scroll", measure);
      window.removeEventListener("resize", measure);
    };
  }, [contextKey]);

  const scroll = (direction: -1 | 1) => {
    const element = viewport.current;
    if (!element || !current?.overflowing) return;
    element.scrollBy({ left: direction * Math.max(100, element.clientWidth * .8), behavior: "instant" });
  };

  return <div className="chart-plot-viewport">
    <div ref={viewport} id={viewportId} className="chart-plot-scroll" role="region" aria-label={label} tabIndex={0}
      style={{ "--chart-plot-min-width": `${Math.max(650,minWidth)}px` } as CSSProperties}>
      {children}
    </div>
    {current?.overflowing && <>
      <div className="chart-plot-scroll-controls" role="group" aria-label={`${label} scroll controls`}>
        <button type="button" aria-label={`Scroll ${label} earlier`} aria-controls={viewportId}
          disabled={!current.earlier} onClick={() => scroll(-1)}>Earlier</button>
        <button type="button" aria-label={`Scroll ${label} later`} aria-controls={viewportId}
          disabled={!current.later} onClick={() => scroll(1)}>Later</button>
      </div>
      <p className="chart-plot-scroll-hint">Use Earlier and Later to reveal the rest of this plot.
        {canSelectWindow && " Drag within the plot to select a shared time window."}</p>
    </>}
  </div>;
}
