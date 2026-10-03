import { useEffect, useRef, useState } from "react";

/** A synchronous command owner. Context changes fence receipts before effect cleanup. */
export function useHealthPageRequest(key: string) {
  const liveKey = useRef(key);
  liveKey.current = key;
  const owner = useRef<{ key: string; controller: AbortController } | undefined>(undefined);
  const [pendingKey, setPendingKey] = useState<string>();
  useEffect(() => {
    if (owner.current?.key !== key) {
      owner.current?.controller.abort();
      owner.current = undefined;
    }
    return () => { if (owner.current?.key === key) { owner.current.controller.abort(); owner.current = undefined; } };
  }, [key]);
  function begin() {
    if (liveKey.current !== key || owner.current?.key === key) return undefined;
    owner.current?.controller.abort();
    const command = { key, controller: new AbortController() };
    owner.current = command;
    setPendingKey(key);
    return {
      signal: command.controller.signal,
      current: () => liveKey.current === key && owner.current === command && !command.controller.signal.aborted,
      finish: () => {
        if (owner.current === command) { owner.current = undefined; setPendingKey(undefined); }
      },
    };
  }
  return { begin, pending: pendingKey === key && owner.current?.key === key };
}

export const healthInvestigationMaximumPages = 400;
export const healthInvestigationMaximumRows = 10_000;
export function canAppendHealthPage(pages: readonly { readonly items: readonly unknown[] }[], newRows = 1) {
  return pages.length < healthInvestigationMaximumPages &&
    pages.reduce((count, page) => count + page.items.length, 0) + newRows <= healthInvestigationMaximumRows;
}
