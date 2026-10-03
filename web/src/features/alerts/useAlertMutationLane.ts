import { useLayoutEffect, useRef, useState } from "react";
import { AlertMutationLane } from "./alertMutationLane.ts";
export function useAlertMutationLane(key: string): AlertMutationLane {
  const [, redraw] = useState(0), mounted = useRef(false), reference = useRef<AlertMutationLane | undefined>(undefined);
  if (!reference.current) reference.current = new AlertMutationLane(() => { if (mounted.current) redraw(value => value + 1); });
  reference.current.setContext(key);
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false; reference.current?.reset(); }; }, []);
  return reference.current;
}
