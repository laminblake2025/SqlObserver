import { createContext, useContext, useLayoutEffect, useRef, useSyncExternalStore, type ComponentProps } from "react";
import { ObservationChart } from "../../components/ObservationChart.tsx";
import { chartInspectionHref, chartInspectionInWindow, readChartInspection } from "../../components/chartInspectionModel.ts";
import { useChartInspection } from "../../components/useChartInspection.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { QueryReadAccess } from "./useQueryReadAccess.tsx";

const subscribeHash = (changed: () => void) => {
  window.addEventListener("hashchange", changed);
  return () => window.removeEventListener("hashchange", changed);
};
const currentHash = () => location.hash;

export function useQueryChartInspection(context: string, targetId: string, fromUtc: string, toUtc: string, access: QueryReadAccess, databaseId?: number) {
  const hash = useSyncExternalStore(subscribeHash, currentHash, () => "");
  const identity = JSON.stringify([context, access.revision, access.denied]);
  const ownerKey = JSON.stringify([identity, hash]);
  const current = useRef({ key: ownerKey, generation: 0 });
  const mounted = useRef(false);
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  if (current.current.key !== ownerKey) current.current = { key: ownerKey, generation: current.current.generation + 1 };
  const generation = current.current.generation;
  const routeMatches = () => {
    const [path, query] = hash.split("?");
    const parameters = new URLSearchParams(query), targets = parameters.getAll("target"), databases = parameters.getAll("databaseId");
    if (path !== "#/queries" || targets.length !== 1 || targets[0]?.toLowerCase() !== targetId.toLowerCase() ||
        databases.length !== (databaseId === undefined ? 0 : 1) || databaseId !== undefined && databases[0] !== String(databaseId)) return false;
    if (parameters.get("range") === "custom") {
      const microseconds = (utc: string) => utcMicroseconds(utc.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
      try {
        if (parameters.getAll("from").length !== 1 || parameters.getAll("to").length !== 1 ||
            microseconds(parameters.get("from")!) !== microseconds(fromUtc) || microseconds(parameters.get("to")!) !== microseconds(toUtc)) return false;
      } catch { return false; }
    }
    return true;
  };
  const requestedUtc = routeMatches() ? readChartInspection(hash) : null;
  const inspection = useChartInspection(ownerKey, fromUtc, toUtc, requestedUtc);
  const owns = () => {
    return mounted.current && current.current.generation === generation && location.hash === hash && routeMatches() && access.allowed();
  };
  const setCrosshairUtc = (utc: string | null) => { if (owns()) inspection.setCrosshairUtc(utc); };
  const align = (utc?: string) => {
    if (!owns()) return;
    inspection.setCrosshairUtc(null);
    location.hash = chartInspectionHref(hash, utc);
  };
  return { context: identity, gestureContext: ownerKey, requestedUtc, crosshairUtc: access.denied ? null : inspection.crosshairUtc,
    setCrosshairUtc, owns, align };
}
export type QueryChartInspection = ReturnType<typeof useQueryChartInspection>;
export const QueryChartInspectionContext = createContext<QueryChartInspection | undefined>(undefined);
export const useSharedQueryChartInspection = () => useContext(QueryChartInspectionContext);

/** Every loaded query plot shares inspection; standalone subpanels retain their local controls. */
export function QueryObservationChart(props: ComponentProps<typeof ObservationChart>) {
  const inspection = useSharedQueryChartInspection();
  const crosshairUtc = inspection ? inspection.requestedUtc !== undefined
    ? inspection.crosshairUtc : inspection.crosshairUtc ?? props.crosshairUtc : props.crosshairUtc;
  const plottedCrosshair = crosshairUtc === undefined && !inspection ? undefined
    : chartInspectionInWindow(crosshairUtc, props.fromUtc, props.toUtc) ? crosshairUtc : null;
  return <ObservationChart key={inspection?.gestureContext} {...props} crosshairUtc={plottedCrosshair}
    onCrosshairChange={utc => {
      if (inspection && !inspection.owns()) return;
      inspection?.setCrosshairUtc(utc); props.onCrosshairChange?.(utc);
    }} onSelectWindow={props.onSelectWindow ? window => {
      if (!inspection || inspection.owns()) props.onSelectWindow?.(window);
    } : undefined} />;
}
