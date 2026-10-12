export type TimeDisplayMode = "local" | "utc";

export function formatDisplayTime(
  value: string | number,
  mode: TimeDisplayMode,
  compact = false,
  localTimeZone?: string,
): string {
  const date = new Date(value);
  if (!Number.isFinite(date.valueOf())) return "Time unavailable";
  const options: Intl.DateTimeFormatOptions = {
    timeZone: mode === "utc" ? "UTC" : localTimeZone,
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
    timeZoneName: "short",
  };
  if (!compact) {
    options.year = "numeric";
    options.second = "2-digit";
  }
  return new Intl.DateTimeFormat("en-US", options).format(date);
}
