import { useEffect, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { getServerConfigurationSnapshot } from "./healthApi";
import type { ServerConfigurationSnapshotResult } from "./healthTypes";

export function ServerConfigurationSnapshot({ instanceId, refresh }: {
  readonly instanceId: string;
  readonly refresh: number;
}) {
  const formatTime = useTimeFormatter();
  const [retry, setRetry] = useState(0);
  const key = `${instanceId}:${refresh}:${retry}`;
  const [paging, setPaging] = useState<{ key: string; index: number;
    cursors: readonly (string | undefined)[] }>({ key: "", index: 0, cursors: [undefined] });
  const current = paging.key === key ? paging :
    { key, index: 0, cursors: [undefined] as readonly (string | undefined)[] };
  const cursor = current.cursors[current.index];
  const [page, setPage] = useState<{ key: string; index: number;
    result: ServerConfigurationSnapshotResult }>();
  const [error, setError] = useState<{ key: string; index: number }>();
  const result = page?.key === key && page.index === current.index ? page.result : undefined;
  const failed = error?.key === key && error.index === current.index;

  useEffect(() => {
    const controller = new AbortController();
    setPage(undefined);
    setError(undefined);
    void getServerConfigurationSnapshot(instanceId, controller.signal, cursor)
      .then(next => {
        if (next.targetId.toLowerCase() !== instanceId.toLowerCase())
          throw new Error("Configuration snapshot target does not match.");
        if (!controller.signal.aborted) setPage({ key, index: current.index, result: next });
      })
      .catch(() => { if (!controller.signal.aborted) setError({ key, index: current.index }); });
    return () => controller.abort();
  }, [instanceId, key, current.index, cursor]);

  return <section className="bounded-health-section" aria-labelledby="server-configuration-snapshot-heading">
    <div className="bounded-health-heading"><h4 id="server-configuration-snapshot-heading">Current server settings</h4></div>
    <p>Latest complete scan. This view follows the server now, even when the timeline is rewound.</p>
    {failed && <p role="alert">Current settings are unavailable or changed while paging. <button type="button" onClick={() => setRetry(value => value + 1)}>Restart from latest</button></p>}
    {!failed && !result && <p role="status">Loading current settings…</p>}
    {result && <>
      <p>{result.runId && result.observedAtUtc
        ? `Observed ${formatTime(result.observedAtUtc)}. ` : "No complete scan yet. "}
        {result.collectorEnabled ? "Collection enabled." : "Collection disabled."}
        {result.lastOutcome ? ` Last outcome: ${result.lastOutcome}.` : ""}</p>
      {result.items.length > 0 && <><div className="table-scroll"><table><thead><tr>
        <th>Setting</th><th>Configured</th><th>In use</th><th>Type</th>
      </tr></thead><tbody>{result.items.map(item => <tr key={item.configurationId}>
        <th scope="row">{item.configurationName}{item.isAdvanced ? " (advanced)" : ""}</th>
        <td>{item.configuredValue}</td><td>{item.inUseValue}</td><td>{item.sourceType}</td>
      </tr>)}</tbody></table></div><nav className="pager" aria-label="Current server settings pages">
        {current.index > 0 && <button type="button" onClick={() =>
          setPaging({ ...current, index: current.index - 1 })}>Previous 100</button>}
        <span>Page {current.index + 1}</span>
        {result.nextCursor && <button type="button" onClick={() =>
          setPaging({ key, index: current.index + 1,
            cursors: [...current.cursors.slice(0, current.index + 1), result.nextCursor!] })}>Next 100</button>}
      </nav></>}
    </>}
  </section>;
}
