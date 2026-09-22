import { useEffect, useRef, useState } from "react";

export interface EvidenceResource<T> { readonly key: string; readonly data?: T; readonly loading: boolean; readonly error?: string; readonly updatedAt?: string; }

/** Keep evidence only within the same request scope; abort superseded work. */
export function useEvidenceResource<T>(key: string, refresh: number, load: (signal: AbortSignal) => Promise<T>, interval?: number) {
  const loader = useRef(load);
  loader.current = load;
  const [retry, setRetry] = useState(0);
  const [result, setResult] = useState<EvidenceResource<T>>({ key, loading: true });
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function read() {
      setResult(previous => previous.key === key ? { ...previous, loading: true, error: undefined } : { key, loading: true });
      try {
        const data = await loader.current(controller.signal);
        if (!controller.signal.aborted) setResult({ key, data, loading: false, updatedAt: new Date().toISOString() });
      } catch (error) {
        if (!controller.signal.aborted) setResult(previous => ({ ...(previous.key === key ? previous : { key }), loading: false, error: error instanceof Error ? error.message : "The request failed safely." }));
      } finally {
        if (!controller.signal.aborted && interval !== undefined) timer = setTimeout(() => void read(), interval);
      }
    }
    void read();
    return () => { controller.abort(); clearTimeout(timer); };
  }, [key, refresh, retry, interval]);
  return { ...(result.key === key ? result : { key, loading: true }), retry: () => setRetry(value => value + 1) };
}
