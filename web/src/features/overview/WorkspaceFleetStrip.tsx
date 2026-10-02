import { useEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import { FleetServerStrip } from "./FleetServerStrip";
import { getOverview } from "./overviewApi";
import { overviewWindow } from "./overviewModel";
import type { OverviewScope, OverviewSnapshot } from "./overviewTypes";

interface FleetResult {
  readonly key: string;
  readonly selectionKey: string;
  readonly pending: boolean;
  readonly snapshot?: OverviewSnapshot;
  readonly error?: string;
}
const clock = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));

/** Keep authorized fleet navigation available while investigating a selected server. */
export function WorkspaceFleetStrip({ scope, refresh }: {
  readonly scope: OverviewScope;
  readonly refresh: number;
}) {
  const formatTime = useTimeFormatter();
  // Changing the inspected server/database does not change the authorized fleet request.
  const selectionKey = JSON.stringify([scope.range, scope.from, scope.to]);
  const key = JSON.stringify([selectionKey, refresh]);
  const requestKey = useRef(key);
  requestKey.current = key;
  const [result, setResult] = useState<FleetResult>();
  const [stickyOffset, setStickyOffset] = useState(0);
  const current = result?.selectionKey === selectionKey ? result : undefined;
  const pending = current === undefined || current.key !== key || current.pending;
  useEffect(() => {
    const sidebar = document.querySelector<HTMLElement>(".sidebar");
    const measure = () => setStickyOffset(window.matchMedia("(max-width: 960px)").matches
      ? Math.ceil(sidebar?.getBoundingClientRect().height ?? 0) : 0);
    const observer = new ResizeObserver(measure);
    if (sidebar) observer.observe(sidebar);
    window.addEventListener("resize", measure);
    measure();
    return () => { observer.disconnect(); window.removeEventListener("resize", measure); };
  }, []);
  useEffect(() => {
    const strip = document.querySelector<HTMLElement>(".workspace-fleet-strip");
    if (!strip) return;
    const measure = () => document.documentElement.style.setProperty("--workspace-fleet-stack-height",
      `${stickyOffset + Math.ceil(strip.getBoundingClientRect().height)}px`);
    const observer = new ResizeObserver(measure);
    observer.observe(strip);
    measure();
    return () => { observer.disconnect(); document.documentElement.style.removeProperty("--workspace-fleet-stack-height"); };
  }, [current?.snapshot, stickyOffset]);
  useEffect(() => {
    const controller = new AbortController();
    setResult(previous => ({ key, selectionKey, pending: true,
      snapshot: previous?.selectionKey === selectionKey ? previous.snapshot : undefined }));
    void (async () => {
      try {
        const window = overviewWindow(scope, Date.now());
        const snapshot = await getOverview("", window, controller.signal);
        if (controller.signal.aborted || requestKey.current !== key) return;
        if (snapshot.targetId !== null || clock(snapshot.fromUtc) !== clock(window.fromUtc) ||
            clock(snapshot.toUtc) !== clock(window.toUtc))
          throw new Error("Fleet evidence did not match the selected UTC window.");
        setResult(previous => previous?.key === key && previous.selectionKey === selectionKey
          ? { key, selectionKey, pending: false, snapshot } : previous);
      } catch (failure) {
        if (!controller.signal.aborted && requestKey.current === key) setResult(previous => previous?.key === key && previous.selectionKey === selectionKey
          ? { key, selectionKey, pending: false,
            error: failure instanceof Error ? failure.message : "Authorized fleet evidence could not be loaded." } : previous);
      }
    })();
    return () => controller.abort();
  }, [key]);
  if (current?.key === key && current.error) return <p className="workspace-fleet-status table-note" role="status">
    Fleet status is unavailable: {current.error} Missing fleet evidence is not an all-clear signal.
  </p>;
  if (!current?.snapshot) return <p className="workspace-fleet-status table-note" role="status">
    Loading authorized fleet status for the selected window…
  </p>;
  const snapshot = current.snapshot;
  return <>
    <FleetServerStrip snapshot={snapshot} scope={scope} stickyOffset={stickyOffset} busy={pending} />
    {pending && <p className="workspace-fleet-status table-note" role="status">
      Refreshing fleet status; showing previous snapshot refreshed {formatTime(snapshot.refreshedAtUtc)}
      {" "}for {formatTime(snapshot.fromUtc)} to {formatTime(snapshot.toUtc)}. Snapshot remains visible until fresh evidence arrives; navigation retains workspace time mode.
    </p>}
    <p className="workspace-fleet-status table-note">
      Fleet CPU and memory window: {formatTime(snapshot.fromUtc)} to {formatTime(snapshot.toUtc)}.
      {" "}Other dots use their latest available source evidence. {snapshot.excludedTargets} disabled/retired servers excluded.
      {" "}Navigation preserves the workspace Live/Rewind range; database context follows only the same server. Missing evidence is not a healthy verdict.
    </p>
  </>;
}
