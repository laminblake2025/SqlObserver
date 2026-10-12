import { useRef, useState } from "react";
import { chartInspectionInWindow } from "./chartInspectionModel.ts";

export function useChartInspection(context: string, fromUtc?: string, toUtc?: string, requestedUtc?: string | null, fallbackUtc?: string | null) {
  const identity = useRef({ context, generation: 0 });
  if (identity.current.context !== context) identity.current = { context, generation: identity.current.generation + 1 };
  const generation = identity.current.generation;
  const [hover, setHover] = useState<{ generation: number; utc: string | null }>();
  const setCrosshairUtc = (utc: string | null) => {
    if (identity.current.generation === generation) setHover({ generation, utc });
  };
  const hovered = hover?.generation === generation ? hover.utc : null;
  const crosshairUtc = chartInspectionInWindow(hovered, fromUtc, toUtc) ? hovered!
    : chartInspectionInWindow(requestedUtc, fromUtc, toUtc) ? requestedUtc!
      : chartInspectionInWindow(fallbackUtc, fromUtc, toUtc) ? fallbackUtc! : null;
  return { crosshairUtc, setCrosshairUtc };
}
