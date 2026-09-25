import { useEffect, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { fileSizeGib } from "./resourceFileModel";
import { getSqlVolumePage, type SqlVolumePage } from "./sqlVolumeApi";
import { volumeEvidenceMessage, volumeUsedPercent } from "./sqlVolumeModel";

export function SqlVolumePanel({ instanceId, refresh, isRewind }: {
  readonly instanceId: string;
  readonly refresh: number;
  readonly isRewind: boolean;
}) {
  const { mode } = useTimeDisplay();
  const [page, setPage] = useState<SqlVolumePage>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(false);
  const [pageIndex, setPageIndex] = useState(0);
  const [retry, setRetry] = useState(0);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const request = useRef<AbortController | undefined>(undefined);

  useEffect(() => {
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setPage(undefined);
    setError(undefined);
    setPageIndex(0);
    setCursors([undefined]);
    if (isRewind) {
      request.current = undefined;
      setLoading(false);
      return () => controller.abort();
    }
    setLoading(true);
    void getSqlVolumePage(instanceId, controller.signal)
      .then(next => {
        if (controller.signal.aborted) return;
        if (next.instanceId.toLowerCase() !== instanceId.toLowerCase())
          throw new Error("Volume evidence did not match the selected server.");
        setPage(next);
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Volume evidence is unavailable.");
      })
      .finally(() => {
        if (!controller.signal.aborted) { request.current = undefined; setLoading(false); }
      });
    return () => controller.abort();
  }, [instanceId, refresh, isRewind, retry]);

  async function navigate(index: number, cursor?: string) {
    if (request.current || !page || isRewind) return;
    const controller = new AbortController();
    request.current = controller;
    setLoading(true);
    setError(undefined);
    try {
      const next = await getSqlVolumePage(instanceId, controller.signal, cursor);
      if (controller.signal.aborted) return;
      if (next.instanceId.toLowerCase() !== instanceId.toLowerCase() ||
          next.snapshotRunId !== page.snapshotRunId || next.targetRevision !== page.targetRevision)
        throw new Error("The volume snapshot changed. Return to the first page.");
      setPage(next);
      setPageIndex(index);
      setCursors(previous => [...previous.slice(0, index), cursor]);
    } catch (failure: unknown) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Volume evidence is unavailable.");
    } finally {
      if (!controller.signal.aborted) { request.current = undefined; setLoading(false); }
    }
  }

  return <section className="panel" aria-label="SQL-reported target-host volume capacity" aria-busy={loading}>
    <h3>SQL-reported target-host volume capacity</h3>
    {isRewind ? <p>Historical volume capacity is not available in Rewind. Choose a live range to see the latest snapshot.</p> : <>
      <p>Capacity comes from SQL Server's view of volumes used by its database files. It describes the target host, not the collector host. Shared volumes appear once; paths are withheld.</p>
      {loading && <p role="status">Loading volume snapshot…</p>}
      {error && <p role="alert">{error} <button type="button" onClick={() => setRetry(value => value + 1)}>Load latest snapshot</button></p>}
      {page && <>
        <p className="activity-evidence">{volumeEvidenceMessage(page.state, page.reason)}
          {page.completedAtUtc ? ` Collected ${formatDisplayTime(page.completedAtUtc, mode)}.` : ""}
          {page.lossKind && page.lossKind !== "none" ? ` Loss: ${page.lossKind.replaceAll("_", " ")}.` : ""}
          {page.minimumLostItems ? ` At least ${page.minimumLostItems} items lost.` : ""}
          {` Page ${pageIndex + 1} · ${page.items.length} volumes`}
          {page.nextCursor ? " · More volumes available." : " · End of snapshot."}</p>
        {page.items.length ? <div className="table-scroll"><table>
          <caption>Latest SQL-reported volume capacity</caption>
          <thead><tr><th scope="col">Volume</th><th scope="col">Mapped files</th>
            <th scope="col">Total</th><th scope="col">Available</th>
            <th scope="col">Used</th><th scope="col">Observed</th></tr></thead>
          <tbody>{page.items.map(item => <tr key={item.volumeKey}>
            <td title={`Opaque volume key ${item.volumeKey}`}>
              {item.identityKind === "file_scoped_unknown" ? "Unidentified file volume" : "Volume"} {item.volumeKey.slice(0, 8)}</td>
            <td>{item.mappedFileCount}</td>
            <td title={item.totalBytes === null ? "Capacity unavailable" : `${item.totalBytes} bytes`}>
              {item.totalBytes === null ? "—" : fileSizeGib(item.totalBytes) ?? "—"}</td>
            <td title={item.availableBytes === null ? "Capacity unavailable" : `${item.availableBytes} bytes`}>
              {item.availableBytes === null ? "—" : fileSizeGib(item.availableBytes) ?? "—"}</td>
            <td>{volumeUsedPercent(item.totalBytes, item.availableBytes) ?? "—"}</td>
            <td>{formatDisplayTime(item.observedAtUtc, mode)}</td>
          </tr>)}</tbody>
        </table></div> : <p>No volume rows were returned for this snapshot.</p>}
        <nav aria-label="SQL volume pages" className="pager">
          <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(0)}>First volume page</button>{" "}
          <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(pageIndex - 1, cursors[pageIndex - 1])}>Previous volume page</button>{" "}
          <button type="button" disabled={loading || !page.nextCursor} onClick={() => void navigate(pageIndex + 1, page.nextCursor ?? undefined)}>Next volume page</button>
        </nav>
      </>}
    </>}
  </section>;
}
