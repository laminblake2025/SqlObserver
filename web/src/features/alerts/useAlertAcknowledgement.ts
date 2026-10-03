import { useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { acknowledgeAlert } from "./alertApi.ts";
import { AcknowledgementAttempts } from "./acknowledgementAttempts.ts";
import type { FleetAlert } from "./alertTypes.ts";

const attempts = new AcknowledgementAttempts();
interface Attempt { readonly targetId: string; readonly alertId: string; readonly firstObservedUtc: string; readonly operationId: string; }
interface Owner { readonly key: string; readonly controller: AbortController; readonly attempt: Attempt; }
interface State { readonly key: string; readonly busy: boolean; readonly message?: string; readonly attempt?: Attempt; }
export function useAlertAcknowledgement(context: string, denial: number, selected: FleetAlert | undefined, allowed: boolean,
  hold: () => void, deny: () => void, recorded: (item: FleetAlert) => void) {
  const key = JSON.stringify([context, denial, selected?.targetId.toLowerCase(), selected?.alertId.toLowerCase(), selected?.firstObservedUtc]);
  const latest = useRef({ key, allowed }); latest.current = { key, allowed };
  const owner = useRef<Owner | undefined>(undefined);
  const [state, setState] = useState<State>();
  useLayoutEffect(() => {
    if (owner.current && owner.current.key !== key) { owner.current.controller.abort(); owner.current = undefined; }
    if (!allowed && owner.current) {
      owner.current.controller.abort(); owner.current = undefined;
      setState({ key, busy: false, message: "Acknowledgement authority is unavailable; any server completion remains uncertain. Recheck access and current server state." });
    }
    return () => {
      const request = owner.current;
      if (!request || request.key !== key) return;
      request.controller.abort(); owner.current = undefined;
      setState(previous => previous?.key === request.key ? { key: request.key, busy: false, attempt: request.attempt,
        message: "Acknowledgement request stopped after selection or authority changed; server completion is uncertain. An authorized retry for this same received cycle keeps its operation ID." } : previous);
    };
  }, [key, allowed]);
  async function send(attempt: Attempt) {
    if (!selected || latest.current.key !== key || !latest.current.allowed || selected.state !== "firing" || owner.current ||
      attempt.firstObservedUtc !== selected.firstObservedUtc || attempt.targetId.toLowerCase() !== selected.targetId.toLowerCase() || attempt.alertId.toLowerCase() !== selected.alertId.toLowerCase()) return;
    const request = { key, controller: new AbortController(), attempt };
    owner.current = request; hold(); setState({ key, busy: true, attempt });
    const current = () => !request.controller.signal.aborted && owner.current === request && latest.current.key === key && latest.current.allowed;
    try {
      await acknowledgeAlert(attempt.targetId, attempt.alertId, attempt.operationId, request.controller.signal);
      if (!current()) return;
      attempts.complete(attempt.targetId, attempt.alertId, attempt.firstObservedUtc);
      recorded(selected);
      setState({ key, busy: false, message: "Acknowledgement recorded. The received row is marked locally; return to latest for the server-provided timestamp." });
    } catch (failure) {
      if (!current()) return;
      if (failure instanceof ApiRequestError && [401, 403].includes(failure.status)) { deny(); return; }
      setState({ key, busy: false, attempt,
        message: "Acknowledgement completion is uncertain. Retry uses the same operation ID for this received first-observed cycle; return to latest to inspect current server state." });
    } finally { if (owner.current === request) owner.current = undefined; }
  }
  function cancel() {
    const request = owner.current;
    if (!request || latest.current.key !== key) return;
    request.controller.abort(); owner.current = undefined;
    setState({ key, busy: false, attempt: request.attempt,
      message: "Acknowledgement request cancelled; server completion is uncertain. Retry uses the same operation ID while this received cycle remains selected." });
  }
  const current = state?.key === key ? state : undefined;
  return { busy: current?.busy ?? false, message: current?.message, cancel, retry: () => { if (current?.attempt) void send(current.attempt); },
    canRetry: Boolean(current?.attempt && !current.busy && selected?.state === "firing" && allowed),
    acknowledge: () => { if (selected) void send({ targetId: selected.targetId, alertId: selected.alertId, firstObservedUtc: selected.firstObservedUtc,
      operationId: attempts.tokenFor(selected.targetId, selected.alertId, selected.firstObservedUtc) }); } };
}
