import { useEffect, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { fileSizeGib } from "./resourceFileModel";
import { getSqlVolumePage, type SqlVolumePage } from "./sqlVolumeApi";
import { volumeDaysUntilFull, volumeEvidenceMessage, volumeUsedPercent } from "./sqlVolumeModel";

interface VolumeSnapshot {
  readonly page: SqlVolumePage;
  readonly index: number;
  readonly cursors: readonly (string | undefined)[];
}
interface VolumeRequestState { readonly loading: boolean; readonly error?: string; }
const supersededMessage = "A newer volume snapshot is available. Load the latest snapshot to retry.";
// The native collector accepts at most 1,000 volumes, with 25 per page.
const maximumPages = 40;
const maximumRows = 1000;

export function SqlVolumePanel({ instanceId, targetRevision, refresh, isRewind }: {
  readonly instanceId: string;
  readonly targetRevision: number;
  readonly refresh: number;
  readonly isRewind: boolean;
}) {
  return isRewind ? <section className="panel server-dashboard-chart" aria-label="SQL-reported target-host volume capacity">
    <h3>SQL-reported target-host volume capacity</h3>
    <p>Historical volume capacity is not available in Rewind. Choose a live range to see the latest snapshot.</p>
  </section> : <LiveSqlVolumePanel key={JSON.stringify([instanceId.toLowerCase(), targetRevision])}
    instanceId={instanceId} targetRevision={targetRevision} refresh={refresh} />;
}

function LiveSqlVolumePanel({ instanceId, targetRevision, refresh }: {
  readonly instanceId: string; readonly targetRevision: number; readonly refresh: number;
}) {
  const { mode } = useTimeDisplay();
  const [snapshot, setSnapshot] = useState<VolumeSnapshot>();
  const [requestState, setRequestState] = useState<VolumeRequestState>();
  const [retry, setRetry] = useState(0);
  const request = useRef<AbortController | undefined>(undefined);
  const pages = useRef(new Map<number, SqlVolumePage>());
  const browsing = useRef(false);
  const [retained, setRetained] = useState(false);
  const loaded = snapshot;
  const page = loaded?.page;
  const pageIndex = loaded?.index ?? 0;
  const cursors = loaded?.cursors ?? [undefined];
  const loading = !requestState || requestState.loading;
  const error = requestState?.error;

  useEffect(() => () => request.current?.abort(), []);

  useEffect(() => {
    // A workspace tick must not replace browsed pages or abort their continuation.
    if (browsing.current) return;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setSnapshot(undefined);
    pages.current.clear();
    setRequestState({ loading: true });
    const active = () => !controller.signal.aborted && request.current === controller;
    void getSqlVolumePage(instanceId, controller.signal, undefined, { targetRevision })
      .then(next => {
        if (!active()) return;
        if (next.state === "superseded") throw new Error(supersededMessage);
        pages.current.set(0, next);
        setSnapshot({ page: next, index: 0, cursors: [undefined] });
        setRequestState({ loading: false });
      })
      .catch((failure: unknown) => {
        if (active()) {
          setSnapshot(undefined);
          setRequestState({ loading: false, error: failure instanceof Error ? failure.message : "Volume evidence is unavailable." });
        }
      })
      .finally(() => {
        if (active()) request.current = undefined;
      });
    return () => controller.abort();
  }, [instanceId, targetRevision, refresh, retry]);

  function returnToLatest() {
    request.current?.abort();
    request.current = undefined;
    browsing.current = false;
    setRetained(false);
    pages.current.clear();
    setSnapshot(undefined);
    setRequestState({ loading: true });
    setRetry(value => value + 1);
  }

  async function navigate(index: number, cursor?: string) {
    if (request.current || !page || !loaded) return;
    browsing.current = true;
    setRetained(true);
    // A cursorless GET means newest, not this snapshot's first page. Already
    // validated pages also remain readable after their native cursors expire.
    const cached = pages.current.get(index);
    if (cached) {
      setSnapshot({ ...loaded, page: cached, index });
      setRequestState({ loading: false });
      return;
    }
    if (cursor === undefined || index < 1) return;
    const controller = new AbortController();
    request.current = controller;
    setRequestState({ loading: true });
    const active = () => !controller.signal.aborted && request.current === controller;
    try {
      if (index >= maximumPages) throw new Error("The volume snapshot exceeds its supported size. Load the latest snapshot to retry.");
      const next = await getSqlVolumePage(instanceId, controller.signal, cursor, {
        targetRevision, snapshotRunId: page.snapshotRunId,
        afterVolumeKey: index > pageIndex ? page.items.at(-1)?.volumeKey : undefined,
        beforeVolumeKey: index < pageIndex ? page.items[0]?.volumeKey : undefined,
      });
      if (!active()) return;
      if (next.state === "superseded") throw new Error(supersededMessage);
      if (index > pageIndex && next.nextCursor !== null && loaded.cursors.includes(next.nextCursor))
        throw new Error("The volume snapshot changed. Load the latest snapshot to retry.");
      const totalRows = [...pages.current.values()].reduce((count, value) => count + value.items.length, next.items.length);
      if (totalRows > maximumRows || index === maximumPages - 1 && next.nextCursor !== null)
        throw new Error("The volume snapshot exceeds its supported size. Load the latest snapshot to retry.");
      pages.current.set(index, next);
      setSnapshot({ page: next, index, cursors: [...loaded.cursors.slice(0, index), cursor] });
      setRequestState({ loading: false });
    } catch (failure: unknown) {
      if (active()) {
        setSnapshot(undefined);
        pages.current.clear();
        setRequestState({ loading: false, error: failure instanceof Error ? failure.message : "Volume evidence is unavailable." });
      }
    } finally {
      if (active()) request.current = undefined;
    }
  }

  return <section className="panel server-dashboard-chart" aria-label="SQL-reported target-host volume capacity" aria-busy={loading}>
    <h3>SQL-reported target-host volume capacity</h3>
    <>
      <p>Capacity comes from SQL Server's view of volumes used by its database files. It describes the target host, not the collector host. Shared volumes appear once; paths are withheld. Days until full uses a 14-day trend of complete daily snapshots; changing or erratic capacity has no estimate.</p>
      {loading && <p role="status">{page ? "Loading another page of the retained volume snapshot…" : "Loading volume snapshot…"}</p>}
      {error && <p role="alert">{error} <button type="button" onClick={returnToLatest}>Load latest snapshot</button></p>}
      {retained && <p role="status">Browsing retained volume snapshot · automatic volume refresh paused. Already-read pages keep this snapshot; unread pages require a valid native continuation.
        {" "}<button type="button" onClick={returnToLatest}>Return to latest volume snapshot</button></p>}
      {page && <>
        <p className="activity-evidence">{retained && page.state === "current"
          ? "Retained SQL-reported volume snapshot; complete when read."
          : `${retained ? "Retained snapshot. " : ""}${volumeEvidenceMessage(page.state, page.reason)}`}
          {page.completedAtUtc ? ` Collected ${formatDisplayTime(page.completedAtUtc, mode)}.` : ""}
          {` Repository checked ${formatDisplayTime(page.repositoryTimeUtc, mode)}.`}
          {page.lossKind && page.lossKind !== "none" ? ` Loss: ${page.lossKind.replaceAll("_", " ")}.` : ""}
          {page.minimumLostItems ? ` At least ${page.minimumLostItems} items lost.` : ""}
          {` Page ${pageIndex + 1} · ${page.items.length} volumes`}
          {page.nextCursor ? " · More volumes available." : " · End of snapshot."}</p>
        {page.items.length ? <div className="table-scroll" tabIndex={0} role="region" aria-label="SQL-reported volume capacity table"><table className="evidence-table">
          <caption>{retained ? "Retained" : "Latest"} SQL-reported volume capacity</caption>
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
    </>
  </section>;
}
