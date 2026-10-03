import { useEffect, useLayoutEffect, useRef, useState } from "react";

interface Position<Input, Page> {
  readonly context: string; readonly input: Input; readonly page: Page; readonly first: Page;
  readonly autoKey: string; readonly index: number; readonly cursors: readonly (string | undefined)[]; readonly held: boolean;
}
interface Attempt<Input> { readonly input: Input; readonly index: number; readonly cursor?: string; readonly latest: boolean; }

// Current and anchor pages plus a bounded cursor trail. Polling may update the first page;
// once a user investigates another position, only an explicit latest read replaces it.
export function useRetainedOperationsPage<Input, Page>(options: {
  readonly context: string; readonly input?: Input; readonly inputKey: string; readonly refresh: number;
  readonly read: (input: Input, signal: AbortSignal, cursor?: string) => Promise<Page>;
  readonly validate: (first: Page, next: Page, index: number) => void;
  readonly discardOnError?: (error: unknown) => boolean;
}) {
  const [position, setPosition] = useState<Position<Input, Page>>();
  const [activity, setActivity] = useState<{ context: string; autoKey: string; loading: boolean; error?: unknown; attempt?: Attempt<Input> }>();
  const latest = useRef(options); latest.current = options;
  const received = useRef(position); received.current = position;
  const request = useRef<{ controller: AbortController; context: string } | undefined>(undefined);
  const context = options.context;
  const current = position?.context === context ? position : undefined;
  const autoKey = JSON.stringify([options.inputKey, options.refresh]);
  const currentActivity = activity?.context === context && (current?.held || activity.autoKey === autoKey) ? activity : undefined;

  useLayoutEffect(() => {
    if (request.current?.context !== context) { request.current?.controller.abort(); request.current = undefined; }
    return () => { request.current?.controller.abort(); request.current = undefined; };
  }, [context]);

  async function execute(attempt: Attempt<Input>, automatic = false) {
    if (latest.current.context !== context || request.current) return;
    const before = received.current?.context === context ? received.current : undefined;
    const owner = { controller: new AbortController(), context };
    request.current = owner;
    if (!automatic && before) setPosition({ ...before, held: true });
    const requestKey = JSON.stringify([latest.current.inputKey, latest.current.refresh]);
    setActivity({ context, autoKey: requestKey, loading: true, attempt });
    const read = latest.current.read, validate = latest.current.validate;
    const inputKey = latest.current.inputKey, refresh = latest.current.refresh;
    const isCurrent = () => !owner.controller.signal.aborted && request.current === owner && latest.current.context === context
      && (!automatic || latest.current.inputKey === inputKey && latest.current.refresh === refresh);
    try {
      const page = await read(attempt.input, owner.controller.signal, attempt.cursor);
      if (!isCurrent()) return;
      validate(attempt.latest || !before ? page : before.first, page, attempt.index);
      const cursors = attempt.latest || !before ? [undefined] : before.cursors.slice();
      if (!attempt.latest) cursors[attempt.index] = attempt.cursor;
      setPosition({ context, input: attempt.input, page, first: attempt.latest || !before ? page : before.first,
        autoKey: requestKey, index: attempt.index, cursors, held: !attempt.latest });
      setActivity({ context, autoKey: requestKey, loading: false });
    } catch (error) {
      if (!isCurrent()) return;
      if (latest.current.discardOnError?.(error)) setPosition(undefined);
      setActivity({ context, autoKey: requestKey, loading: false, error, attempt });
    } finally { if (request.current === owner) request.current = undefined; }
  }

  useEffect(() => {
    const before = received.current?.context === context ? received.current : undefined;
    if (options.input === undefined || before?.held || request.current || before?.autoKey === autoKey ||
      activity?.context === context && activity.autoKey === autoKey && activity.error !== undefined) return;
    void execute({ input: options.input, index: 0, latest: true }, true);
    // execute installs the controller synchronously before its first await.
    // TypeScript retains the earlier undefined narrowing across that call.
    const owner = request.current as { controller: AbortController; context: string } | undefined;
    return () => { if (request.current === owner) { owner?.controller.abort(); request.current = undefined; } };
  }, [context, options.inputKey, options.refresh, current?.held]);

  function navigate(index: number, cursor?: string) {
    if (!current || latest.current.context !== context || index < 0 || index > 2048 || (index > 0 && !cursor)) return;
    void execute({ input: current.input, index, cursor, latest: false });
  }
  return { page: current?.page, index: current?.index ?? 0, held: current?.held ?? false,
    loading: options.input !== undefined && (currentActivity?.loading ?? (!current || !current.held && current.autoKey !== autoKey)), error: currentActivity?.error,
    next: (cursor: string) => navigate((current?.index ?? 0) + 1, cursor),
    previous: () => navigate((current?.index ?? 0) - 1, current?.cursors[(current?.index ?? 0) - 1]),
    first: () => navigate(0),
    returnLatest: () => { if (latest.current.input !== undefined) void execute({ input: latest.current.input, index: 0, latest: true }); },
    retry: () => { if (currentActivity?.attempt) void execute(currentActivity.attempt); },
  };
}
