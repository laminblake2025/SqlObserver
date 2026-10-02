import { useEffect, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { fileSizeGib } from "./resourceFileModel";
import { getSqlVolumePage, type SqlVolumePage } from "./sqlVolumeApi";
import { volumeDaysUntilFull, volumeEvidenceMessage, volumeUsedPercent } from "./sqlVolumeModel";

interface VolumeSnapshot {
  readonly context: string;
  readonly page: SqlVolumePage;
  readonly index: number;
  readonly cursors: readonly (string | undefined)[];
}
interface VolumeRequestState { readonly context: string; readonly loading: boolean; readonly error?: string; }
const supersededMessage = "A newer volume snapshot is available. Load the latest snapshot to retry.";

export function SqlVolumePanel({ instanceId, targetRevision, refresh, isRewind }: {
  readonly instanceId: string;
  readonly targetRevision: number;
  readonly refresh: number;
  readonly isRewind: boolean;
}) {
  const { mode } = useTimeDisplay();
  const [snapshot, setSnapshot] = useState<VolumeSnapshot>();
  const [requestState, setRequestState] = useState<VolumeRequestState>();
  const [retry, setRetry] = useState(0);
  const request = useRef<AbortController | undefined>(undefined);
  const context = JSON.stringify([instanceId.toLowerCase(), targetRevision, refresh, isRewind, retry]);
  const currentContext = useRef(context);
  currentContext.current = context;
  const loaded = snapshot?.context === context && !isRewind ? snapshot : undefined;
  const page = loaded?.page;
  const pageIndex = loaded?.index ?? 0;
  const cursors = loaded?.cursors ?? [undefined];
  const loading = !isRewind && (requestState?.context !== context || requestState.loading);
  const error = requestState?.context === context ? requestState.error : undefined;

  useEffect(() => {
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setSnapshot(undefined);
    if (isRewind) {
      request.current = undefined;
      setRequestState({ context, loading: false });
      return () => controller.abort();
    }
    setRequestState({ context, loading: true });
    const active = () => !controller.signal.aborted && currentContext.current === context && request.current === controller;
    void getSqlVolumePage(instanceId, controller.signal, undefined, { targetRevision })
      .then(next => {
        if (!active()) return;
        if (next.state === "superseded") throw new Error(supersededMessage);
        setSnapshot({ context, page: next, index: 0, cursors: [undefined] });
        setRequestState({ context, loading: false });
      })
      .catch((failure: unknown) => {
        if (active()) {
          setSnapshot(undefined);
          setRequestState({ context, loading: false, error: failure instanceof Error ? failure.message : "Volume evidence is unavailable." });
        }
      })
      .finally(() => {
        if (active()) request.current = undefined;
      });
    return () => controller.abort();
  }, [instanceId, targetRevision, refresh, isRewind, retry, context]);

  async function navigate(index: number, cursor?: string) {
    if (currentContext.current !== context || request.current || !page || !loaded || isRewind) return;
    const controller = new AbortController();
    request.current = controller;
    setRequestState({ context, loading: true });
    const active = () => !controller.signal.aborted && currentContext.current === context && request.current === controller;
    try {
      const next = await getSqlVolumePage(instanceId, controller.signal, cursor, {
        targetRevision, snapshotRunId: page.snapshotRunId,
        afterVolumeKey: index > pageIndex ? page.items.at(-1)?.volumeKey : undefined,
        beforeVolumeKey: index < pageIndex ? page.items[0]?.volumeKey : undefined,
      });
      if (!active()) return;
      if (next.state === "superseded") throw new Error(supersededMessage);
      if (index > pageIndex && next.nextCursor !== null && loaded.cursors.includes(next.nextCursor))
        throw new Error("The volume snapshot changed. Load the latest snapshot to retry.");
      setSnapshot({ context, page: next, index, cursors: [...loaded.cursors.slice(0, index), cursor] });
      setRequestState({ context, loading: false });
    } catch (failure: unknown) {
      if (active()) {
        setSnapshot(undefined);
        setRequestState({ context, loading: false, error: failure instanceof Error ? failure.message : "Volume evidence is unavailable." });
      }
    } finally {
      if (active()) request.current = undefined;
    }
  }

  return <section className="panel server-dashboard-chart" aria-label="SQL-reported target-host volume capacity" aria-busy={loading}>
    <h3>SQL-reported target-host volume capacity</h3>
    {isRewind ? <p>Historical volume capacity is not available in Rewind. Choose a live range to see the latest snapshot.</p> : <>
      <p>Capacity comes from SQL Server's view of volumes used by its database files. It describes the target host, not the collector host. Shared volumes appear once; paths are withheld. Days until full uses a 14-day trend of complete daily snapshots; changing or erratic capacity has no estimate.</p>
      {loading && <p role="status">{page ? "Loading another page of the displayed volume snapshot…" : "Loading volume snapshot…"}</p>}
      {error && <p role="alert">{error} <button type="button" onClick={() => setRetry(value => value + 1)}>Load latest snapshot</button></p>}
      {page && <>
        <p className="activity-evidence">{volumeEvidenceMessage(page.state, page.reason)}
          {page.completedAtUtc ? ` Collected ${formatDisplayTime(page.completedAtUtc, mode)}.` : ""}
          {` Repository checked ${formatDisplayTime(page.repositoryTimeUtc, mode)}.`}
          {page.lossKind && page.lossKind !== "none" ? ` Loss: ${page.lossKind.replaceAll("_", " ")}.` : ""}
          {page.minimumLostItems ? ` At least ${page.minimumLostItems} items lost.` : ""}
          {` Page ${pageIndex + 1} · ${page.items.length} volumes`}
          {page.nextCursor ? " · More volumes available." : " · End of snapshot."}</p>
        {page.items.length ? <div className="table-scroll" tabIndex={0} role="region" aria-label="Latest SQL-reported volume capacity"><table className="evidence-table">
          <caption>Latest SQL-reported volume capacity</caption>
          <thead><tr><th scope="col">Volume</th><th scope="col">Mapped files</th>
            <th scope="col">Total</th><th scope="col">Available</th>
            <th scope="col">Used</th><th scope="col">Days until full</th><th scope="col">Observed</th></tr></thead>
          <tbody>{page.items.map(item => <tr key={item.volumeKey}>
            <td title={`Opaque volume key ${item.volumeKey}`}>
              {item.identityKind === "file_scoped_unknown" ? "Unidentified file volume" : "Volume"} {item.volumeKey.slice(0, 8)}</td>
            <td>{item.mappedFileCount}</td>
            <td className="evidence-scalar" title={item.totalBytes === null ? "Capacity unavailable" : `${item.totalBytes} bytes`}>
              {item.totalBytes === null ? "—" : fileSizeGib(item.totalBytes) ?? "—"}</td>
            <td className="evidence-scalar" title={item.availableBytes === null ? "Capacity unavailable" : `${item.availableBytes} bytes`}>
              {item.availableBytes === null ? "—" : fileSizeGib(item.availableBytes) ?? "—"}</td>
            <td className="evidence-scalar">{volumeUsedPercent(item.totalBytes, item.availableBytes) ?? "—"}</td>
            <td title={`${item.trendSampleDays ?? 0} complete daily samples · ${(item.trendState ?? "evidence_unavailable").replaceAll("_", " ")}`}>
              {volumeDaysUntilFull(item.trendState ?? "evidence_unavailable", item.daysUntilFull ?? null)}</td>
            <td className="evidence-scalar">{formatDisplayTime(item.observedAtUtc, mode)}</td>
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
