import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { formatDisplayTime, type TimeDisplayMode } from "./timeDisplay";

const preferenceKey = "sqlobserver.timeDisplayMode";
const TimeDisplayContext = createContext<{
  readonly mode: TimeDisplayMode;
  readonly setMode: (mode: TimeDisplayMode) => void;
}>({ mode: "local", setMode: () => undefined });

export function TimeDisplayProvider({ children }: { readonly children: ReactNode }) {
  const [mode, setMode] = useState<TimeDisplayMode>(() => {
    try { return localStorage.getItem(preferenceKey) === "utc" ? "utc" : "local"; }
    catch { return "local"; }
  });
  useEffect(() => {
    try { localStorage.setItem(preferenceKey, mode); }
    catch { /* Display preference remains available for this tab. */ }
  }, [mode]);
  return <TimeDisplayContext value={{ mode, setMode }}>{children}</TimeDisplayContext>;
}

export function useTimeDisplay() { return useContext(TimeDisplayContext); }

export function useTimeFormatter() {
  const { mode } = useTimeDisplay();
  return (value: string | null | undefined) => value ? formatDisplayTime(value, mode) : "Not observed";
}
