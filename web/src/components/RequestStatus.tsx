import { useMemo, useState } from "react";
import { failureReference } from "../api/responseFailure.ts";

export function RequestStatus({ loading, error, updatedAt, hasData = false, label = "Evidence", onRetry }: {
  readonly loading: boolean; readonly error?: string; readonly updatedAt?: string;
  readonly hasData?: boolean; readonly label?: string; readonly onRetry?: () => void;
}) {
  const [copied, setCopied] = useState<string>();
  const failedAt = useMemo(() => error ? new Date().toISOString() : undefined, [error]);
  const reference = error ? failureReference(error) : undefined;
  return <div className="request-status">
    {loading || error || updatedAt ? <p role={error ? "alert" : "status"} className={error ? "status-message" : "table-note"}>
      {error ? `${label}: ${error}${hasData ? " Showing last-known evidence." : ""}` : loading ? `${hasData ? "Refreshing" : "Loading"} ${label.toLowerCase()}…` : `${label} updated.`}
      {failedAt ? ` Failed at: ${failedAt}.` : ""}
      {updatedAt ? ` Last successful update: ${updatedAt}.` : ""}
    </p> : null}
    {error && onRetry ? <button className="secondary-button" type="button" disabled={loading} onClick={onRetry}>Retry {label.toLowerCase()}</button> : null}
    {reference ? <button className="secondary-button" type="button" onClick={() => { void navigator.clipboard.writeText(reference).then(() => setCopied(reference)).catch(() => setCopied(undefined)); }}>{copied === reference ? "Reference copied" : "Copy error reference"}</button> : null}
  </div>;
}
