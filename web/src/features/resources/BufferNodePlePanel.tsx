import { useEffect, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { OverviewChart } from "../overview/OverviewChart";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { getBufferNodePleWindow, type BufferNodePleWindow } from "./bufferNodePleApi";
import { bufferManagerPleSeries, bufferNodePleSeries } from "./bufferNodePleModel";

export function BufferNodePlePanel({ instanceId, scope, refresh, onSelectWindow, crosshairUtc, onCrosshairChange }: {
  readonly instanceId: string;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly crosshairUtc?: string | null;
  readonly onCrosshairChange?: (utc: string | null) => void;
}) {
  const { mode } = useTimeDisplay();
  const [selectedNode, setSelectedNode] = useState<number | null>(null);
  const [receivedData, setData] = useState<{ key: string; value: BufferNodePleWindow }>();
  const [receivedError, setError] = useState<{ key: string; message: string }>();
  const [receivedLoading, setLoading] = useState<{ key: string; value: boolean }>();
  const key = JSON.stringify([instanceId, scope, refresh, selectedNode]);
  const data = receivedData?.key === key ? receivedData.value : undefined;
  const error = receivedError?.key === key ? receivedError.message : undefined;
  const loading = receivedLoading?.key === key ? receivedLoading.value : true;

  useEffect(() => { setSelectedNode(null); }, [instanceId]);
  useEffect(() => {
    const controller = new AbortController();
    setData(undefined);
    setError(undefined);
    setLoading({ key, value: false });
    let window: { fromUtc: string; toUtc: string };
    try {
      window = overviewWindow(scope, Date.now());
      if (Date.parse(window.toUtc) - Date.parse(window.fromUtc) > 7 * 86_400_000)
        throw new Error("Buffer Node PLE history supports windows of seven days or less.");
    } catch (failure) {
      setError({ key, message: failure instanceof Error ? failure.message : "The selected window is invalid." });
      return () => controller.abort();
    }
    setLoading({ key, value: true });
    void getBufferNodePleWindow(instanceId, window.fromUtc, window.toUtc,
      selectedNode, controller.signal)
      .then(value => {
        if (controller.signal.aborted) return;
        if (selectedNode !== null && !value.nodes.some(node => node.nodeId === selectedNode)) {
          setSelectedNode(null);
          return;
        }
        setData({ key, value });
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted)
          setError({ key, message: failure instanceof Error ? failure.message : "Buffer Node PLE is unavailable." });
      })
      .finally(() => { if (!controller.signal.aborted) setLoading({ key, value: false }); });
    return () => controller.abort();
  }, [key]);

  return <section className="panel server-dashboard-chart" aria-label="Per-NUMA page life expectancy" aria-busy={loading}>
    <h3>Buffer Node page life expectancy</h3>
    <p className="activity-evidence">Each NUMA node has its own PLE gauge in seconds. The chart shows hourly minimum,
      average and maximum values for one node in the shared time window. It is separate from the Buffer Manager aggregate.</p>
    {loading && <p role="status">Loading Buffer Node history…</p>}
    {error && <p role="alert">{error}</p>}
    {data && <>
      <p className="activity-evidence">{formatDisplayTime(data.fromUtc, mode)} to {formatDisplayTime(data.toUtc, mode)} ·
        {data.nodes.length} observed nodes · {data.visibilityGapCount} collector gaps.</p>
      {data.nodes.length ? <label>NUMA node <select value={data.selectedNodeId ?? ""}
        onChange={event => setSelectedNode(Number(event.target.value))}>
        {data.nodes.map(node => <option key={node.nodeId} value={node.nodeId}>
          {node.nodeId} · {node.sampleCount} samples
        </option>)}
      </select></label> : <p>No Buffer Node samples were stored in this window. Check the collector schedule and SQL Server permissions.</p>}
      {data.points.length ? <OverviewChart series={bufferNodePleSeries(data)}
        crosshairUtc={crosshairUtc} onCrosshairChange={onCrosshairChange}
        fromUtc={data.fromUtc} toUtc={data.toUtc} onSelectWindow={onSelectWindow} />
        : data.nodes.length > 0 && <p>No samples were stored for the selected node in this window.</p>}
      <h4>Buffer Manager aggregate PLE</h4>
      <p className="activity-evidence">The SQL Server Buffer Manager counter covers the instance overall. Its hourly values
        are charted separately from each NUMA node.</p>
      {data.aggregatePoints.length ? <OverviewChart series={bufferManagerPleSeries(data)}
        crosshairUtc={crosshairUtc} onCrosshairChange={onCrosshairChange}
        fromUtc={data.fromUtc} toUtc={data.toUtc} onSelectWindow={onSelectWindow} />
        : <p>No Buffer Manager PLE samples were stored in this window.</p>}
    </>}
  </section>;
}
